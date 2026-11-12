/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Buffers.Binary;
using System.IO;
using System.Text;
using Heimdall.App.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services.Handlers;

/// <summary>Why a session that names one agent key cannot be connected.</summary>
internal enum SshAgentKeyRefusal
{
    /// <summary>Nothing prevents the connection.</summary>
    None,

    /// <summary>No agent Heimdall may use holds the key, or the stored fingerprint is malformed.</summary>
    NotLoaded,

    /// <summary>The connection needs Pageant and Pageant does not hold the key.</summary>
    NotInPageant,

    /// <summary>Plink cannot read a public key file of this type, so it cannot be limited to the key.</summary>
    PlinkKeyType,

    /// <summary>The session also names a key file and Plink takes one <c>-i</c> only.</summary>
    PlinkKeyFile,

    /// <summary>The Plink in use is not the bundled build whose behaviour was measured.</summary>
    PlinkNotBundled,

    /// <summary>An external PuTTY window cannot be told which agent key to use.</summary>
    LauncherUnsupported,
}

/// <summary>
/// The decisions a handler takes before dialling when a session names one agent key. Pure, so the
/// refusals can be pinned without a process, a socket or a prompt.
/// </summary>
internal static class SshAgentKeyPreflight
{
    private const int BlobLengthPrefixBytes = sizeof(uint);
    private const char FirstPrintableAscii = '!';
    private const char LastPrintableAscii = '~';

    /// <summary>
    /// Exactly the key type names measured against the bundled Plink 0.83: Plink reports that it
    /// cannot load the public key file of any other type and then tries every key of Pageant,
    /// which would offer more than the one chosen, so such a key is refused on the Plink path.
    /// Only the Ed25519 certificate line was measured among the certificate types.
    /// </summary>
    private static readonly HashSet<string> PlinkParseableKeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-ed448",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa",
        "ssh-dss",
        "ssh-ed25519-cert-v01@openssh.com",
    };

    /// <summary>
    /// Finds the chosen key in the reachable agents, bounded in time and off the calling thread.
    /// </summary>
    /// <param name="fingerprint">The stored fingerprint.</param>
    /// <param name="registry">The agents to search.</param>
    /// <param name="agentName">Restrict the search to this agent, or null for all of them.</param>
    /// <param name="timeout">Longest wait for the agents; the shared default when null.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>
    /// The identity and its agent, or null when it is malformed, not loaded, or the agents did not
    /// answer in time: an agent that cannot be asked holds nothing that can be offered.
    /// </returns>
    internal static async Task<SshAgentIdentityMatch?> LocateAsync(
        string fingerprint,
        SshAgentRegistry registry,
        string? agentName = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(registry);

        if (!SshAgentKeyFingerprints.IsWellFormed(fingerprint))
        {
            return null;
        }

        Task<SshAgentIdentityMatch?> lookup = Task.Run(
            () => registry.FindIdentity(fingerprint, agentName),
            CancellationToken.None);
        try
        {
            return await lookup
                .WaitAsync(timeout ?? TimeSpan.FromMilliseconds(SshAgentRegistry.IdentityLookupTimeoutMs), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Core.Logging.FileLogger.Warn("SSH agent key lookup timed out; the key is treated as not loaded.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Core.Logging.FileLogger.Warn($"SSH agent key lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether Plink can honour the chosen key, given where the key lives and what is launched.
    /// </summary>
    /// <param name="pageantMatch">The key as Pageant holds it, or null when Pageant does not.</param>
    /// <param name="keyFileConfigured">The session also names a private key file.</param>
    /// <param name="plinkAttested">The launcher is the bundled build whose key selection is relied on.</param>
    /// <remarks>
    /// Plink can only reach Pageant, so the agent is judged first: that is the problem the user can
    /// fix by loading the key. Every other refusal has its own cause and its own message.
    /// </remarks>
    internal static SshAgentKeyRefusal RefusalForPlink(
        SshAgentIdentityMatch? pageantMatch,
        bool keyFileConfigured,
        bool plinkAttested)
    {
        if (pageantMatch is null)
        {
            return SshAgentKeyRefusal.NotInPageant;
        }

        if (!IsPlinkParseable(pageantMatch.Key))
        {
            return SshAgentKeyRefusal.PlinkKeyType;
        }

        if (keyFileConfigured)
        {
            return SshAgentKeyRefusal.PlinkKeyFile;
        }

        return plinkAttested ? SshAgentKeyRefusal.None : SshAgentKeyRefusal.PlinkNotBundled;
    }

    /// <summary>Whether Plink can read the public key file written for this key.</summary>
    internal static bool IsPlinkParseable(ISshAgentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string? type = ReadBlobKeyType(key.PublicKeyBlob);
        if (type is null)
        {
            return false;
        }

        return PlinkParseableKeyTypes.Contains(type);
    }

    /// <summary>
    /// The type name the blob itself begins with, as a length-prefixed string. The key's
    /// <c>KeyType</c> property is the agent's own reading of the same bytes, with a placeholder
    /// when it failed; the blob is what Plink parses, so the blob decides.
    /// </summary>
    internal static string? ReadBlobKeyType(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        if (blob.Length < BlobLengthPrefixBytes)
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length == 0 || length > blob.Length - BlobLengthPrefixBytes)
        {
            return null;
        }

        string type = Encoding.ASCII.GetString(blob, BlobLengthPrefixBytes, (int)length);
        return type.All(static c => c is >= FirstPrintableAscii and <= LastPrintableAscii) ? type : null;
    }

    /// <summary>The locale key that explains a refusal.</summary>
    internal static string MessageKey(SshAgentKeyRefusal refusal) => refusal switch
    {
        SshAgentKeyRefusal.NotInPageant => SshLocalizationKeys.ErrorSshAgentKeyNotInPageant,
        SshAgentKeyRefusal.PlinkKeyType => SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyType,
        SshAgentKeyRefusal.PlinkKeyFile => SshLocalizationKeys.ErrorSshAgentKeyPlinkKeyFile,
        SshAgentKeyRefusal.PlinkNotBundled => SshLocalizationKeys.ErrorSshAgentKeyPlinkNotBundled,
        SshAgentKeyRefusal.LauncherUnsupported => SshLocalizationKeys.ErrorSshAgentKeyLauncherUnsupported,
        _ => SshLocalizationKeys.ErrorSshAgentKeyNotLoaded,
    };

    /// <summary>Whether the message for a refusal names the key by its fingerprint.</summary>
    internal static bool NamesFingerprint(SshAgentKeyRefusal refusal) => refusal is
        SshAgentKeyRefusal.NotLoaded
        or SshAgentKeyRefusal.NotInPageant
        or SshAgentKeyRefusal.PlinkKeyType;

    /// <summary>
    /// The one-line OpenSSH form of a public key, newline included: key type, a space, the
    /// base64 of the public key blob. No comment, so nothing but public material is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The blob does not begin with a type name.</exception>
    internal static string BuildPublicKeyLine(ISshAgentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string type = ReadBlobKeyType(key.PublicKeyBlob)
            ?? throw new InvalidOperationException("The agent key blob does not start with a key type name.");
        return $"{type} {Convert.ToBase64String(key.PublicKeyBlob)}\n";
    }
}

/// <summary>
/// The short-lived public key file handed to Plink with <c>-i</c> so that Pageant offers that key.
/// </summary>
/// <remarks>
/// The file holds public material only, but it still lives as short as the session needs it and is
/// created private to the user like the password file. It reuses the password file prefix on
/// purpose: the janitor that already sweeps stale files of that prefix at startup then also
/// removes a leftover from a crash, with no second sweep to maintain.
/// </remarks>
internal static class PlinkAgentKeyFile
{
    private const string NameInfix = "agentkey_";
    private const string Extension = ".pub";

    /// <summary>
    /// A fresh path in the temp folder, nothing written yet. Separate from <see cref="Write"/> so
    /// the command line can be built, and the release armed, before the file exists: a failure
    /// between the two then has nothing left behind.
    /// </summary>
    internal static string NewPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"{PlinkPasswordFileNaming.Prefix}{NameInfix}{Guid.NewGuid():N}{Extension}");

    /// <summary>Writes the one-line public key to <paramref name="path"/>, private to the user.</summary>
    internal static void Write(string path, ISshAgentKey key)
    {
        string line = SshAgentKeyPreflight.BuildPublicKeyLine(key);

        if (OperatingSystem.IsWindows())
        {
            SecureFileWriter.WriteAndProtect(path, line);
        }
        else
        {
            File.WriteAllText(path, line, Encoding.ASCII);
        }
    }
}
