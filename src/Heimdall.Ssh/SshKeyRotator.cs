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

/// <summary>Execution phases of a guided SSH key rotation.</summary>
public enum SshKeyRotationPhase
{
    Generating,
    Deploying,
    Verifying,
    SavingProfile,
    RemovingOldKey,
    Completed,
    PartialSuccess,
    Failed
}

/// <summary>Structured error categories returned by <see cref="SshKeyRotator"/> steps.</summary>
public enum SshKeyRotationErrorCode
{
    None,
    InvalidParameters,
    VerificationFailed,
    SymlinkRefused,
    InvariantFailed,
    RemoteCommandFailed,
    ConnectionFailed
}

/// <summary>Outcome of verifying a newly deployed SSH key or removing an old key.</summary>
public sealed record SshKeyRotationStepResult(
    bool Success,
    string? ErrorMessage = null,
    SshKeyRotationErrorCode ErrorCode = SshKeyRotationErrorCode.None,
    int? ExitStatus = null);

/// <summary>
/// Safe transactional primitives for guided SSH key rotation:
/// isolated new-key-only verification (<see cref="SshConnectionParams.DisableAgentAuthentication"/> = true)
/// and atomic, symlink-safe, invariant-guarded removal of the replaced key from <c>~/.ssh/authorized_keys</c>.
/// </summary>
public static class SshKeyRotator
{
    internal const string VerifyOkMarker = "HEIMDALL_ROTATE_VERIFY_OK";
    internal const string OldKeyRemovedMarker = "HEIMDALL_OLD_KEY_REMOVED";
    internal const string SymlinkRefusedMarker = "HEIMDALL_AK_SYMLINK";
    internal const string InvariantFailedMarker = "HEIMDALL_ROTATE_INVARIANT_FAILED";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds a POSIX-sh command that atomically removes <paramref name="validatedOldPublicKeyLine"/>
    /// from <c>~/.ssh/authorized_keys</c> ONLY if:
    /// 1) <c>authorized_keys</c> is not a symlink,
    /// 2) a temporary file is staged in <c>$HOME/.ssh</c> with mode 600, and
    /// 3) the staged file is non-empty AND still contains <paramref name="validatedNewPublicKeyLine"/>.
    /// </summary>
    internal static string BuildRemoveOldKeyCommand(
        string validatedOldPublicKeyLine,
        string validatedNewPublicKeyLine)
    {
        if (!TryExtractKeyIdentity(validatedOldPublicKeyLine, out string oldIdentity)
            || !TryExtractKeyIdentity(validatedNewPublicKeyLine, out string newIdentity))
        {
            throw new ArgumentException("Both old and new public keys must be valid OpenSSH public key lines.");
        }

        if (string.Equals(oldIdentity, newIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("Old and new public keys must be distinct.");
        }

        string oldB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(oldIdentity));
        string newB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(newIdentity));

        return
            $"old_id=$(printf '%s' '{oldB64}' | base64 -d) && " +
            $"new_id=$(printf '%s' '{newB64}' | base64 -d) && " +
            "ak=\"$HOME/.ssh/authorized_keys\" && " +
            $"if [ -L \"$ak\" ]; then echo {SymlinkRefusedMarker}; exit 1; fi && " +
            "[ -f \"$ak\" ] && " +
            "tmp=$(mktemp \"$HOME/.ssh/.authorized_keys.heimdall.XXXXXX\") && " +
            "if chmod 600 \"$tmp\" && " +
            "(grep -Fv -- \"$old_id\" \"$ak\" > \"$tmp\"; rc=$?; [ \"$rc\" -le 1 ]) && " +
            "[ -s \"$tmp\" ] && (grep -Ev '^[[:space:]]*(#|$)' \"$tmp\" | grep -Fq -- \"$new_id\"); then " +
            $"(mv -f \"$tmp\" \"$ak\" && chmod 600 \"$ak\" && echo {OldKeyRemovedMarker}) || {{ rm -f \"$tmp\"; exit 1; }}; " +
            $"else rm -f \"$tmp\"; echo {InvariantFailedMarker}; exit 1; fi";
    }

    internal static bool TryExtractKeyIdentity(string? publicKeyLine, out string identity)
    {
        identity = string.Empty;
        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out var normalized))
        {
            return false;
        }

        var parts = normalized.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        identity = $"{parts[0]} {parts[1]}";
        return true;
    }

    /// <summary>
    /// Builds strict isolated connection parameters for verifying the newly generated private key
    /// without any password, keyboard-interactive, certificate, or SSH agent fallback.
    /// </summary>
    public static SshConnectionParams BuildIsolatedVerificationParams(
        SshConnectionParams baseParams,
        string newPrivateKeyPath,
        string? newKeyPassphrase)
    {
        ArgumentNullException.ThrowIfNull(baseParams);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPrivateKeyPath);

        return new SshConnectionParams
        {
            Host = baseParams.Host,
            Port = baseParams.Port,
            LogicalHost = baseParams.LogicalHost,
            LogicalPort = baseParams.LogicalPort,
            Username = baseParams.Username,
            KeyPath = newPrivateKeyPath,
            CertificatePath = null,
            Password = null,
            KeyPassphrase = string.IsNullOrEmpty(newKeyPassphrase) ? null : newKeyPassphrase,
            UseLegacyPasswordAsKeyPassphrase = false,
            DisableAgentAuthentication = true,
            AgentKeyFingerprint = null,
            CryptoPolicy = baseParams.CryptoPolicy,
            ProxyType = baseParams.ProxyType,
            ProxyHost = baseParams.ProxyHost,
            ProxyPort = baseParams.ProxyPort,
            ProxyUsername = baseParams.ProxyUsername,
            ProxyPassword = baseParams.ProxyPassword,
            ConnectTimeout = baseParams.ConnectTimeout,
            AuthenticationTimeout = TimeSpan.FromSeconds(30),
            KeyboardInteractiveResponder = null
        };
    }

    /// <summary>
    /// Verifies that the target server accepts authentication with ONLY the new private key.
    /// </summary>
    public static async Task<SshKeyRotationStepResult> VerifyNewKeyAsync(
        SshConnectionParams isolatedNewKeyParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isolatedNewKeyParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!isolatedNewKeyParams.DisableAgentAuthentication
            || !string.IsNullOrEmpty(isolatedNewKeyParams.Password))
        {
            return new SshKeyRotationStepResult(
                false,
                "Verification parameters must disable agent and password fallback.",
                SshKeyRotationErrorCode.InvalidParameters);
        }

        try
        {
            var pinned = await SshConnectionFactory.ResolveHostKeyAsync(
                    isolatedNewKeyParams,
                    hostKeyStore,
                    verifier,
                    ct)
                .ConfigureAwait(false);

            using var client = SshConnectionFactory.CreateSshClient(isolatedNewKeyParams);
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, isolatedNewKeyParams, pinned);

            await SshConnectionFactory.ConnectWithTransportBoundAsync(
                    client,
                    isolatedNewKeyParams,
                    token => SshConnectionFactory.ConnectWithCancellationAsync(client, token),
                    ct)
                .ConfigureAwait(false);

            using var cmd = client.CreateCommand($"echo {VerifyOkMarker}");
            cmd.CommandTimeout = CommandTimeout;
            var ar = cmd.BeginExecute();
            using var reg = ct.Register(static state =>
            {
                try { ((SshCommand)state!).CancelAsync(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, cmd);
            string output = await Task<string>.Factory.FromAsync(ar, cmd.EndExecute).ConfigureAwait(false);

            if (cmd.ExitStatus == 0 && output.Contains(VerifyOkMarker, StringComparison.Ordinal))
            {
                return new SshKeyRotationStepResult(true);
            }

            return new SshKeyRotationStepResult(
                false,
                $"New-key verification failed (exit {cmd.ExitStatus}).",
                SshKeyRotationErrorCode.VerificationFailed,
                cmd.ExitStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshKeyRotationStepResult(false, ex.Message, SshKeyRotationErrorCode.ConnectionFailed);
        }
    }

    /// <summary>
    /// Connects using the verified new key and atomically removes the old public key from
    /// <c>~/.ssh/authorized_keys</c>, preserving all other keys and refusing to empty the file.
    /// </summary>
    public static async Task<SshKeyRotationStepResult> RemoveOldKeyAsync(
        SshConnectionParams isolatedNewKeyParams,
        string oldPublicKeyLine,
        string newPublicKeyLine,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(isolatedNewKeyParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        string commandText;
        try
        {
            commandText = BuildRemoveOldKeyCommand(oldPublicKeyLine, newPublicKeyLine);
        }
        catch (ArgumentException ex)
        {
            return new SshKeyRotationStepResult(false, ex.Message, SshKeyRotationErrorCode.InvalidParameters);
        }

        try
        {
            var pinned = await SshConnectionFactory.ResolveHostKeyAsync(
                    isolatedNewKeyParams,
                    hostKeyStore,
                    verifier,
                    ct)
                .ConfigureAwait(false);

            using var client = SshConnectionFactory.CreateSshClient(isolatedNewKeyParams);
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, isolatedNewKeyParams, pinned);

            await SshConnectionFactory.ConnectWithTransportBoundAsync(
                    client,
                    isolatedNewKeyParams,
                    token => SshConnectionFactory.ConnectWithCancellationAsync(client, token),
                    ct)
                .ConfigureAwait(false);

            using var cmd = client.CreateCommand(commandText);
            cmd.CommandTimeout = CommandTimeout;
            var ar = cmd.BeginExecute();
            using var reg = ct.Register(static state =>
            {
                try { ((SshCommand)state!).CancelAsync(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, cmd);
            string output = await Task<string>.Factory.FromAsync(ar, cmd.EndExecute).ConfigureAwait(false);

            if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
            {
                return new SshKeyRotationStepResult(
                    false,
                    "Remote authorized_keys is a symbolic link; left untouched.",
                    SshKeyRotationErrorCode.SymlinkRefused);
            }

            if (output.Contains(InvariantFailedMarker, StringComparison.Ordinal))
            {
                return new SshKeyRotationStepResult(
                    false,
                    "Safety check failed: new key not confirmed in staged authorized_keys.",
                    SshKeyRotationErrorCode.InvariantFailed);
            }

            if (cmd.ExitStatus == 0 && output.Contains(OldKeyRemovedMarker, StringComparison.Ordinal))
            {
                return new SshKeyRotationStepResult(true);
            }

            string err = string.IsNullOrWhiteSpace(cmd.Error)
                ? $"Old-key removal exited with status {cmd.ExitStatus}."
                : cmd.Error.Trim();
            return new SshKeyRotationStepResult(
                false,
                err,
                SshKeyRotationErrorCode.RemoteCommandFailed,
                cmd.ExitStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshKeyRotationStepResult(false, ex.Message, SshKeyRotationErrorCode.ConnectionFailed);
        }
    }
}
