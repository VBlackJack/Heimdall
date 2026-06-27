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

using System.Runtime.Versioning;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Migrates the confidential set between the legacy at-rest forms and the v2
/// DEK-encrypted form (HMS1). All passes are idempotent and direction-pure, so a
/// crash mid-migration leaves a mixed-but-readable vault that the next pass
/// finishes cleanly. The caller MUST have set the vault DEK on
/// <see cref="CredentialProtector"/> before invoking forward or reverse.
/// </summary>
/// <remarks>
/// The confidential set:
/// <list type="bullet">
/// <item>servers.json per profile: RdpPasswordEncrypted, SshPasswordEncrypted,
/// SshKeyPassphraseEncrypted, WinRmPasswordEncrypted, FtpPasswordEncrypted,
/// TelnetPasswordEncrypted, VncPassword (all read via CredentialProtector), and
/// CitrixLaunchCommandLine (PLAINTEXT at rest -> brought under v2).</item>
/// <item>settings.json: CredentialProviderUnlockSecretEncrypted and
/// CmdLibGitSyncToken (both read + written through CredentialProtector, so their
/// non-vault form is the CredentialProtector legacy format).</item>
/// </list>
/// Plaintext is materialized transiently as managed strings through the
/// CredentialProtector string API (the same non-zeroable boundary as the rest of
/// the app); key material stays in the pinned/zeroable holders of Lot 1a/1b.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class VaultMigrationEngine
{
    /// <summary>Forward-migrate the whole confidential set (legacy -> v2). Idempotent.</summary>
    public static async Task MigrateForwardAsync(IConfigManager configManager)
    {
        var servers = await configManager.LoadServersAsync().ConfigureAwait(false);
        foreach (var profile in servers)
        {
            ForwardProfile(profile);
        }

        await configManager.SaveServersAsync(servers).ConfigureAwait(false);

        await configManager.MergeSettingAsync(settings =>
        {
            Apply(settings.CredentialProviderUnlockSecretEncrypted,
                v => settings.CredentialProviderUnlockSecretEncrypted = v, ForwardEncrypted);
            Apply(settings.CmdLibGitSyncToken,
                v => settings.CmdLibGitSyncToken = v, ForwardEncrypted);
        }).ConfigureAwait(false);
    }

    /// <summary>Reverse-migrate the whole confidential set (v2 -> legacy). Idempotent.</summary>
    public static async Task MigrateReverseAsync(IConfigManager configManager)
    {
        var servers = await configManager.LoadServersAsync().ConfigureAwait(false);
        foreach (var profile in servers)
        {
            ReverseProfile(profile);
        }

        await configManager.SaveServersAsync(servers).ConfigureAwait(false);

        await configManager.MergeSettingAsync(settings =>
        {
            // CredentialProviderUnlockSecret is read through CredentialProtector,
            // so its legacy form is the CredentialProtector legacy format.
            Apply(settings.CredentialProviderUnlockSecretEncrypted,
                v => settings.CredentialProviderUnlockSecretEncrypted = v, ReverseToCredentialProtectorLegacy);

            // CmdLibGitSyncToken is now written + read through CredentialProtector
            // (vault-aware), so disable returns it to the CredentialProtector legacy
            // form, consistent with CredentialProviderUnlockSecretEncrypted.
            Apply(settings.CmdLibGitSyncToken,
                v => settings.CmdLibGitSyncToken = v, ReverseToCredentialProtectorLegacy);
        }).ConfigureAwait(false);
    }

    private static void ForwardProfile(ServerProfileDto profile)
    {
        Apply(profile.RdpPasswordEncrypted, v => profile.RdpPasswordEncrypted = v, ForwardEncrypted);
        Apply(profile.SshPasswordEncrypted, v => profile.SshPasswordEncrypted = v, ForwardEncrypted);
        Apply(profile.SshKeyPassphraseEncrypted, v => profile.SshKeyPassphraseEncrypted = v, ForwardEncrypted);
        Apply(profile.WinRmPasswordEncrypted, v => profile.WinRmPasswordEncrypted = v, ForwardEncrypted);
        Apply(profile.FtpPasswordEncrypted, v => profile.FtpPasswordEncrypted = v, ForwardEncrypted);
        Apply(profile.TelnetPasswordEncrypted, v => profile.TelnetPasswordEncrypted = v, ForwardEncrypted);
        Apply(profile.VncPassword, v => profile.VncPassword = v, ForwardEncrypted);
        Apply(profile.CitrixLaunchCommandLine, v => profile.CitrixLaunchCommandLine = v, ForwardPlaintext);
    }

    private static void ReverseProfile(ServerProfileDto profile)
    {
        Apply(profile.RdpPasswordEncrypted, v => profile.RdpPasswordEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.SshPasswordEncrypted, v => profile.SshPasswordEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.SshKeyPassphraseEncrypted, v => profile.SshKeyPassphraseEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.WinRmPasswordEncrypted, v => profile.WinRmPasswordEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.FtpPasswordEncrypted, v => profile.FtpPasswordEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.TelnetPasswordEncrypted, v => profile.TelnetPasswordEncrypted = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.VncPassword, v => profile.VncPassword = v, ReverseToCredentialProtectorLegacy);
        Apply(profile.CitrixLaunchCommandLine, v => profile.CitrixLaunchCommandLine = v, ReverseToPlaintext);
    }

    /// <summary>Read the current value, transform it, and write back only when it changed.</summary>
    private static void Apply(string? current, Action<string?> setter, Func<string?, string?> transform)
    {
        var next = transform(current);
        if (!string.Equals(next, current, StringComparison.Ordinal))
        {
            setter(next);
        }
    }

    // ── Forward transforms (legacy -> v2) ───────────────────────────────────

    /// <summary>An encrypted field: decrypt with the legacy reader, re-encrypt as v2.</summary>
    internal static string? ForwardEncrypted(string? current)
    {
        if (string.IsNullOrEmpty(current) || VaultSecretBlob.IsSecretBlob(current))
        {
            return current; // empty or already v2 -> idempotent no-op
        }

        var plaintext = CredentialProtector.Unprotect(current);
        if (plaintext is null)
        {
            // Undecryptable legacy blob (e.g. DPAPI from another user). Preserve
            // the original bytes — never lose data or write null. No value logged.
            FileLogger.Warn("Vault migration: skipped an undecryptable legacy secret field.");
            return current;
        }

        return CredentialProtector.Protect(plaintext); // DEK set -> v2
    }

    /// <summary>A plaintext-at-rest field (CitrixLaunchCommandLine): encrypt as v2.</summary>
    internal static string? ForwardPlaintext(string? current)
    {
        if (string.IsNullOrEmpty(current) || VaultSecretBlob.IsSecretBlob(current))
        {
            return current;
        }

        return CredentialProtector.Protect(current); // DEK set -> v2
    }

    // ── Reverse transforms (v2 -> legacy) ───────────────────────────────────

    /// <summary>A v2 field whose consumer reads via CredentialProtector: back to the legacy format.</summary>
    internal static string? ReverseToCredentialProtectorLegacy(string? current)
    {
        if (string.IsNullOrEmpty(current) || !VaultSecretBlob.IsSecretBlob(current))
        {
            return current; // empty or already legacy -> idempotent no-op
        }

        var plaintext = CredentialProtector.Unprotect(current); // v2 read (DEK set)
        return CredentialProtector.ProtectLegacy(plaintext!);
    }

    /// <summary>A v2 field that was plaintext at rest (CitrixLaunchCommandLine): back to plaintext.</summary>
    internal static string? ReverseToPlaintext(string? current)
    {
        if (string.IsNullOrEmpty(current) || !VaultSecretBlob.IsSecretBlob(current))
        {
            return current;
        }

        return CredentialProtector.Unprotect(current);
    }
}
