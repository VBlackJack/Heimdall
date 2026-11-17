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
using System.Security.Cryptography;
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
/// TelnetPasswordEncrypted, WebPasswordEncrypted, VncPassword (all read via CredentialProtector), and
/// CitrixLaunchCommandLine (PLAINTEXT at rest -> brought under v2).</item>
/// <item>settings.json: CredentialProviderUnlockSecretEncrypted and
/// CmdLibGitSyncToken (both read + written through CredentialProtector, so their
/// non-vault form is the CredentialProtector legacy format). The SSH gateways'
/// password and key passphrase follow the same rule in both directions.</item>
/// </list>
/// Plaintext is materialized transiently as managed strings through the
/// CredentialProtector string API (the same non-zeroable boundary as the rest of
/// the app); key material stays in the pinned/zeroable holders of Lot 1a/1b.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class VaultMigrationEngine
{
    private static int s_undecryptableReported;

    /// <summary>Forward-migrate the whole confidential set (legacy -> v2). Idempotent.</summary>
    public static async Task MigrateForwardAsync(IConfigManager configManager)
    {
        await configManager.MutateServersAsync(servers =>
        {
            foreach (ServerProfileDto profile in servers)
            {
                ForwardProfile(profile);
            }

            return servers.Count;
        }).ConfigureAwait(false);

        await ForwardSettingsAsync(configManager).ConfigureAwait(false);
    }

    private static Task ForwardSettingsAsync(IConfigManager configManager) =>
        configManager.MergeSettingAsync(settings =>
        {
            Apply(settings.CredentialProviderUnlockSecretEncrypted,
                v => settings.CredentialProviderUnlockSecretEncrypted = v, ForwardEncrypted);
            Apply(settings.CmdLibGitSyncToken,
                v => settings.CmdLibGitSyncToken = v, ForwardEncrypted);

            // Assigning only on change keeps the passphrase setter from declaring a field the
            // gateway never had.
            foreach (SshGatewayDto gateway in settings.SshGateways)
            {
                Apply(gateway.SshPasswordEncrypted,
                    v => gateway.SshPasswordEncrypted = v, ForwardEncrypted);
                Apply(gateway.SshKeyPassphraseEncrypted,
                    v => gateway.SshKeyPassphraseEncrypted = v, ForwardEncrypted);
            }
        });

    /// <summary>
    /// Seals, in the server inventory, every secret that was persisted in a legacy or plaintext
    /// form after the vault's initial migration completed (a profile imported or edited by an
    /// older build, a Citrix launch token). Idempotent: the inventory is rewritten only when a
    /// value changed.
    /// </summary>
    /// <returns>Whether the inventory was changed.</returns>
    public static async Task<bool> ReconcileServersAsync(IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);

        return await configManager.MutateServersAsync(servers =>
        {
            var changed = false;
            foreach (ServerProfileDto profile in servers)
            {
                changed |= ForwardProfile(profile);
            }

            return changed;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="settings"/> hold a secret that a forward pass would seal: a value
    /// the legacy reader can open and that is not a vault blob yet. A value nothing can open is
    /// left alone by the pass and is not counted, so it does not cause a write at every unlock.
    /// </summary>
    internal static bool SettingsHoldUnsealedSecrets(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (NeedsSealing(settings.CredentialProviderUnlockSecretEncrypted)
            || NeedsSealing(settings.CmdLibGitSyncToken))
        {
            return true;
        }

        return settings.SshGateways.Any(gateway =>
            NeedsSealing(gateway.SshPasswordEncrypted) || NeedsSealing(gateway.SshKeyPassphraseEncrypted));
    }

    /// <summary>
    /// Seals the secrets of the settings that are still in a legacy form. Idempotent; the settings
    /// are written only when <see cref="SettingsHoldUnsealedSecrets"/> finds something to seal.
    /// </summary>
    /// <returns>Whether the settings were rewritten.</returns>
    public static async Task<bool> ReconcileSettingsAsync(IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);

        if (!SettingsHoldUnsealedSecrets(await configManager.LoadSettingsAsync().ConfigureAwait(false)))
        {
            return false;
        }

        await ForwardSettingsAsync(configManager).ConfigureAwait(false);
        return true;
    }

    private static bool NeedsSealing(string? value)
    {
        if (string.IsNullOrEmpty(value) || VaultSecretBlob.IsSecretBlob(value))
        {
            return false;
        }

        try
        {
            return CredentialProtector.Unprotect(value) is not null;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Reverse-migrate the whole confidential set (v2 -> legacy). Idempotent.</summary>
    public static Task MigrateReverseAsync(IConfigManager configManager) =>
        MigrateReverseAsync(configManager, onUnreadable: null);

    /// <summary>
    /// Reverse-migrate the whole confidential set. A value sealed with a key that cannot open it is
    /// left untouched and reported through <paramref name="onUnreadable"/>; without the callback it
    /// throws, as it always did.
    /// </summary>
    /// <param name="configManager">The configuration manager.</param>
    /// <param name="onUnreadable">Called once per value that cannot be opened.</param>
    public static Task MigrateReverseAsync(IConfigManager configManager, Action? onUnreadable)
    {
        Func<string?, string?> legacy = onUnreadable is null
            ? ReverseToCredentialProtectorLegacy
            : value => ReverseTolerant(value, onUnreadable);
        Func<string?, string?> plain = onUnreadable is null
            ? ReverseToPlaintext
            : value => ReversePlaintextTolerant(value, onUnreadable);
        return ReverseCoreAsync(configManager, legacy, plain);
    }

    /// <summary>
    /// Clears every confidential value that is sealed with a key that cannot open it. A value that
    /// can be read is never cleared.
    /// </summary>
    /// <returns>How many values were cleared.</returns>
    public static async Task<int> MigrateDropUnreadableAsync(IConfigManager configManager)
    {
        var dropped = 0;
        string? Drop(string? value)
        {
            if (!IsUnreadableVaultSecret(value))
            {
                return value;
            }

            dropped++;
            return null;
        }

        await ReverseCoreAsync(configManager, Drop, Drop).ConfigureAwait(false);
        return dropped;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a vault blob that the current key cannot authenticate.
    /// A readable blob, a legacy value and an empty one are not.
    /// </summary>
    internal static bool IsUnreadableVaultSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || !VaultSecretBlob.IsSecretBlob(value))
        {
            return false;
        }

        try
        {
            CredentialProtector.Unprotect(value);
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    /// <summary>
    /// The reverse transform that leaves a value it cannot open untouched and counts it. Used for
    /// the stores outside the configuration files.
    /// </summary>
    internal static string? ReverseTolerant(string? value, Action onUnreadable) =>
        ReverseWith(value, onUnreadable, reProtect: plain => CredentialProtector.ProtectLegacy(plain));

    /// <summary>The tolerant reverse for a value that was plaintext at rest.</summary>
    internal static string? ReversePlaintextTolerant(string? value, Action onUnreadable) =>
        ReverseWith(value, onUnreadable, reProtect: plain => plain);

    /// <summary>
    /// Opens a vault blob and hands the plaintext to <paramref name="reProtect"/>. Only a failure to
    /// OPEN the blob makes it unreadable; a failure to protect the result again is not a reason to
    /// drop the value and is left to propagate.
    /// </summary>
    private static string? ReverseWith(string? value, Action onUnreadable, Func<string, string?> reProtect)
    {
        if (string.IsNullOrEmpty(value) || !VaultSecretBlob.IsSecretBlob(value))
        {
            return value;
        }

        string? plain;
        try
        {
            plain = CredentialProtector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            onUnreadable();
            return value;
        }

        return reProtect(plain!);
    }

    private static async Task ReverseCoreAsync(
        IConfigManager configManager,
        Func<string?, string?> legacy,
        Func<string?, string?> plain)
    {
        await configManager.MutateServersAsync(servers =>
        {
            foreach (ServerProfileDto profile in servers)
            {
                ReverseProfile(profile, legacy, plain);
            }

            return servers.Count;
        }).ConfigureAwait(false);

        await configManager.MergeSettingAsync(settings =>
        {
            // CredentialProviderUnlockSecret is read through CredentialProtector,
            // so its legacy form is the CredentialProtector legacy format.
            Apply(settings.CredentialProviderUnlockSecretEncrypted,
                v => settings.CredentialProviderUnlockSecretEncrypted = v, legacy);

            // CmdLibGitSyncToken is now written + read through CredentialProtector
            // (vault-aware), so disable returns it to the CredentialProtector legacy
            // form, consistent with CredentialProviderUnlockSecretEncrypted.
            Apply(settings.CmdLibGitSyncToken,
                v => settings.CmdLibGitSyncToken = v, legacy);

            // Gateway secrets are written through CredentialProtector too, so a gateway saved
            // while the vault was unlocked holds a vault blob. Assigning only on change keeps the
            // passphrase setter from declaring a field the gateway never had.
            foreach (SshGatewayDto gateway in settings.SshGateways)
            {
                Apply(gateway.SshPasswordEncrypted,
                    v => gateway.SshPasswordEncrypted = v, legacy);
                Apply(gateway.SshKeyPassphraseEncrypted,
                    v => gateway.SshKeyPassphraseEncrypted = v, legacy);
            }
        }).ConfigureAwait(false);
    }

    private static bool ForwardProfile(ServerProfileDto profile)
    {
        var changed = false;
        changed |= Apply(profile.RdpPasswordEncrypted, v => profile.RdpPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.SshPasswordEncrypted, v => profile.SshPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.SshKeyPassphraseEncrypted, v => profile.SshKeyPassphraseEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.WinRmPasswordEncrypted, v => profile.WinRmPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.FtpPasswordEncrypted, v => profile.FtpPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.TelnetPasswordEncrypted, v => profile.TelnetPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.WebPasswordEncrypted, v => profile.WebPasswordEncrypted = v, ForwardEncrypted);
        changed |= Apply(profile.VncPassword, v => profile.VncPassword = v, ForwardEncrypted);
        changed |= Apply(profile.CitrixLaunchCommandLine, v => profile.CitrixLaunchCommandLine = v, ForwardPlaintext);
        return changed;
    }

    private static void ReverseProfile(
        ServerProfileDto profile,
        Func<string?, string?> legacy,
        Func<string?, string?> plain)
    {
        Apply(profile.RdpPasswordEncrypted, v => profile.RdpPasswordEncrypted = v, legacy);
        Apply(profile.SshPasswordEncrypted, v => profile.SshPasswordEncrypted = v, legacy);
        Apply(profile.SshKeyPassphraseEncrypted, v => profile.SshKeyPassphraseEncrypted = v, legacy);
        Apply(profile.WinRmPasswordEncrypted, v => profile.WinRmPasswordEncrypted = v, legacy);
        Apply(profile.FtpPasswordEncrypted, v => profile.FtpPasswordEncrypted = v, legacy);
        Apply(profile.TelnetPasswordEncrypted, v => profile.TelnetPasswordEncrypted = v, legacy);
        Apply(profile.WebPasswordEncrypted, v => profile.WebPasswordEncrypted = v, legacy);
        Apply(profile.VncPassword, v => profile.VncPassword = v, legacy);
        Apply(profile.CitrixLaunchCommandLine, v => profile.CitrixLaunchCommandLine = v, plain);
    }

    /// <summary>Read the current value, transform it, and write back only when it changed.</summary>
    private static bool Apply(string? current, Action<string?> setter, Func<string?, string?> transform)
    {
        var next = transform(current);
        if (string.Equals(next, current, StringComparison.Ordinal))
        {
            return false;
        }

        setter(next);
        return true;
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
            // the original bytes - never lose data or write null. No value logged.
            // Once per process: the pass also runs at every unlock, and the same secret would be
            // reported each time.
            if (Interlocked.Exchange(ref s_undecryptableReported, 1) == 0)
            {
                FileLogger.Warn("Vault migration: skipped an undecryptable legacy secret field.");
            }

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
