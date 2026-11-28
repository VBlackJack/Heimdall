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

using System.Text;
using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh;

/// <summary>
/// Machine-readable error classification for <see cref="SshPublicKeyDeployResult"/>.
/// </summary>
public enum SshPublicKeyDeployErrorCode
{
    None,
    InvalidPublicKey,
    RemoteCommandFailed,
    ConfirmationMissing,
    SymlinkRefused,
    Exception
}

/// <summary>
/// Outcome of deploying an SSH public key to a remote server's <c>~/.ssh/authorized_keys</c>.
/// </summary>
public sealed record SshPublicKeyDeployResult(
    bool Success,
    bool AlreadyInstalled,
    string? ErrorMessage = null,
    SshPublicKeyDeployErrorCode ErrorCode = SshPublicKeyDeployErrorCode.None,
    int? ExitStatus = null);

/// <summary>
/// Deploys an OpenSSH public key line to a remote POSIX host's <c>~/.ssh/authorized_keys</c>
/// (equivalent to <c>ssh-copy-id</c>), preserving permissions (<c>700</c>/<c>600</c>) and
/// avoiding duplicates. The key travels as strict Base64 so no shell metacharacters are
/// ever interpolated into the remote command.
/// </summary>
public static class SshPublicKeyDeployer
{
    internal const string MarkerAdded = "HEIMDALL_KEY_ADDED";
    internal const string MarkerExists = "HEIMDALL_KEY_EXISTS";
    internal const string MarkerSymlinkRefused = "HEIMDALL_AK_SYMLINK";
    internal static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> SupportedKeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com"
    };

    /// <summary>
    /// Validates and trims an OpenSSH public key line.
    /// Returns false when the line contains newlines, control characters, an unsupported key
    /// type, or an invalid base64 key blob.
    /// </summary>
    public static bool TryValidatePublicKeyLine(string? publicKeyLine, out string normalizedLine)
    {
        normalizedLine = string.Empty;
        if (string.IsNullOrWhiteSpace(publicKeyLine))
        {
            return false;
        }

        var trimmed = publicKeyLine.Trim();
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !SupportedKeyTypes.Contains(parts[0]))
        {
            return false;
        }

        try
        {
            var blob = Convert.FromBase64String(parts[1]);
            if (blob.Length < 16)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        normalizedLine = parts.Length == 3
            ? $"{parts[0]} {parts[1]} {parts[2].Trim()}"
            : $"{parts[0]} {parts[1]}";
        return true;
    }

    /// <summary>
    /// Builds a POSIX-sh idempotent command that installs <paramref name="validatedPublicKeyLine"/>
    /// into <c>~/.ssh/authorized_keys</c>. The payload is transmitted strictly as Base64.
    /// </summary>
    internal static string BuildDeployCommand(string validatedPublicKeyLine)
    {
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(validatedPublicKeyLine));
        var parts = validatedPublicKeyLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var keyIdentity = parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : validatedPublicKeyLine;
        var base64Identity = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyIdentity));

        return $"key=$(printf '%s' '{base64Payload}' | base64 -d) && " +
               $"key_id=$(printf '%s' '{base64Identity}' | base64 -d) && " +
               "mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\" && " +
               "ak=\"$HOME/.ssh/authorized_keys\" && " +
               $"if [ -L \"$ak\" ]; then echo {MarkerSymlinkRefused}; exit 1; fi && " +
               "touch \"$ak\" && chmod 600 \"$ak\" && " +
               $"((grep -Ev '^[[:space:]]*(#|$)' \"$ak\" | grep -Fq -- \"$key_id\") && echo {MarkerExists} || " +
               $"((! [ -s \"$ak\" ] || [ -z \"$(tail -c 1 \"$ak\")\" ] || printf '\\n' >> \"$ak\") && " +
               $"printf '%s\\n' \"$key\" >> \"$ak\" && echo {MarkerAdded}))";
    }

    /// <summary>
    /// Connects to the target SSH server with strict host-key verification and installs
    /// <paramref name="publicKeyLine"/> into <c>~/.ssh/authorized_keys</c>.
    /// </summary>
    public static async Task<SshPublicKeyDeployResult> DeployAsync(
        SshConnectionParams connectionParams,
        string publicKeyLine,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!TryValidatePublicKeyLine(publicKeyLine, out var normalizedLine))
        {
            return new SshPublicKeyDeployResult(
                false,
                false,
                "Invalid OpenSSH public key line.",
                SshPublicKeyDeployErrorCode.InvalidPublicKey);
        }

        try
        {
            var pinned = await SshConnectionFactory.ResolveHostKeyAsync(
                    connectionParams,
                    hostKeyStore,
                    verifier,
                    cancellationToken)
                .ConfigureAwait(false);

            using var client = SshConnectionFactory.CreateSshClient(connectionParams);
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, connectionParams, pinned);

            await SshConnectionFactory.ConnectWithTransportBoundAsync(
                    client,
                    connectionParams,
                    ct => SshConnectionFactory.ConnectWithCancellationAsync(client, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            var commandText = BuildDeployCommand(normalizedLine);
            using var cmd = client.CreateCommand(commandText);
            cmd.CommandTimeout = DefaultCommandTimeout;
            var asyncResult = cmd.BeginExecute();
            using var reg = cancellationToken.Register(static state =>
            {
                try { ((SshCommand)state!).CancelAsync(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, cmd);

            var output = await Task<string>.Factory.FromAsync(asyncResult, cmd.EndExecute)
                .ConfigureAwait(false);

            if (output.Contains(MarkerSymlinkRefused, StringComparison.Ordinal))
            {
                return new SshPublicKeyDeployResult(
                    false,
                    false,
                    "Remote authorized_keys is a symbolic link.",
                    SshPublicKeyDeployErrorCode.SymlinkRefused,
                    cmd.ExitStatus);
            }

            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error)
                    ? $"Remote command exited with status {cmd.ExitStatus}."
                    : cmd.Error.Trim();
                return new SshPublicKeyDeployResult(
                    false,
                    false,
                    err,
                    SshPublicKeyDeployErrorCode.RemoteCommandFailed,
                    cmd.ExitStatus);
            }

            if (output.Contains(MarkerExists, StringComparison.Ordinal))
            {
                return new SshPublicKeyDeployResult(true, AlreadyInstalled: true);
            }

            if (output.Contains(MarkerAdded, StringComparison.Ordinal))
            {
                return new SshPublicKeyDeployResult(true, AlreadyInstalled: false);
            }

            return new SshPublicKeyDeployResult(
                false,
                false,
                "Remote host did not confirm key installation.",
                SshPublicKeyDeployErrorCode.ConfirmationMissing);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshPublicKeyDeployResult(
                false,
                false,
                ex.Message,
                SshPublicKeyDeployErrorCode.Exception);
        }
    }
}
