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

using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Headless lifecycle for the master-password vault: enable, unlock, change the
/// master password, disable, and lock. Owns the unlocked DEK holder for the
/// session and drives the resumable migration engine.
/// </summary>
/// <remarks>
/// Migration state machine (both passes are idempotent; <c>InProgress</c> marks either direction):
/// <list type="number">
/// <item><b>Enable</b>: <c>None</c> -&gt; persist (wrapped DEK + Enabled=true +
/// <c>InProgress</c>) -&gt; set DEK -&gt; forward-migrate -&gt; <c>Complete</c>.
/// The state and wrapped DEK are persisted BEFORE migrating, so a crash is
/// recoverable.</item>
/// <item><b>Unlock</b> while <c>InProgress</c>: unwrap -&gt; set DEK -&gt; resume
/// forward-migrate (idempotent) -&gt; <c>Complete</c>.</item>
/// <item><b>Disable</b>: unwrap (authorization) -&gt; set DEK -&gt; mark
/// <c>InProgress</c> -&gt; reverse-migrate the configuration and every store while
/// still enabled -&gt; withdraw the key and check that nothing is still sealed -&gt;
/// persist (clear wrapped DEK + Enabled=false + <c>None</c>) -&gt; lock. When the
/// check fails the forward pass runs again and the vault stays on; after a crash
/// the next unlock treats <c>InProgress</c> as a forward resume.</item>
/// </list>
/// Rollback policy: enable never rolls back to disabled on failure. A failed or
/// interrupted forward pass leaves Enabled=true + <c>InProgress</c> + the wrapped
/// DEK, a consistent RESUMABLE state - re-unlock finishes it. A failed or
/// interrupted disable leaves the vault enabled with a mixed-but-readable set;
/// re-disable finishes it. Neither path ever loses or silently downgrades data.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VaultLifecycleService : IDisposable
{
    /// <summary>The locale key naming the configuration files in a residue report.</summary>
    public const string ConfigurationStoreNameKey = "VaultStoreConfiguration";

    private readonly IConfigManager _configManager;
    private readonly IVaultHelloService? _vaultHelloService;
    private readonly IReadOnlyList<IVaultSecretStore> _secretStores;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultDekHolder? _unlockedDek;
    private int _lockDeferred;
    private int _unsealedNotified;
    private Task _postUnlockSealing = Task.CompletedTask;

    /// <summary>Create the service over a configuration manager.</summary>
    /// <param name="configManager">The settings + servers persistence manager.</param>
    /// <param name="vaultHelloService">Optional Windows Hello DEK-wrapper service.</param>
    /// <param name="secretStores">
    /// Optional stores outside the configuration files. They are sealed when the vault is enabled
    /// or unlocked and returned to the legacy form when it is disabled, which is refused while any
    /// of them still holds a secret sealed with the vault key.
    /// </param>
    public VaultLifecycleService(
        IConfigManager configManager,
        IVaultHelloService? vaultHelloService = null,
        IEnumerable<IVaultSecretStore>? secretStores = null)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        _configManager = configManager;
        _vaultHelloService = vaultHelloService;
        _secretStores = secretStores is null ? [] : [.. secretStores];
    }

    /// <summary>
    /// The locale keys of the stores that stayed unsealed after the pass that follows an unlock,
    /// empty when none did. Set at most once per session.
    /// </summary>
    public IReadOnlyList<string> UnsealedStoreNameKeys { get; private set; } = [];

    /// <summary>
    /// Raised once per session, from a background thread, when a store stays unsealed after the
    /// pass that follows an unlock. Never blocks the unlock.
    /// </summary>
    public event Action<IReadOnlyList<string>>? UnsealedStoresDetected;

    /// <summary>The stores this service seals and unseals.</summary>
    internal IReadOnlyList<IVaultSecretStore> SecretStores => _secretStores;

    /// <summary>The pass that follows the latest unlock; completed when none is pending.</summary>
    internal Task PostUnlockSealingTask => _postUnlockSealing;

    /// <summary>Whether the vault is currently unlocked (a usable DEK is held).</summary>
    public bool IsUnlocked => _unlockedDek is { IsDisposed: false };

    /// <summary>
    /// Enable the vault: generate a DEK, wrap it under the master password,
    /// persist the resumable state, then forward-migrate the confidential set.
    /// Leaves the vault unlocked.
    /// </summary>
    /// <param name="masterPassword">The new master password (caller zeroes after).</param>
    /// <param name="parameters">Argon2id cost parameters for the KEK.</param>
    /// <exception cref="MasterPasswordPolicyException">When the password is too weak.</exception>
    /// <exception cref="InvalidOperationException">When the vault is already enabled.</exception>
    public async Task EnableAsync(char[] masterPassword, Argon2idParameters parameters)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnableCoreAsync(masterPassword, parameters).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task EnableCoreAsync(char[] masterPassword, Argon2idParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);
        RequirePolicy(masterPassword);

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (settings.VaultEnabled)
        {
            throw new InvalidOperationException("The vault is already enabled.");
        }

        var dek = VaultKeyManager.GenerateDek();
        string wrapped;
        try
        {
            wrapped = WrapWithChars(masterPassword, dek.Key, parameters);
        }
        catch
        {
            dek.Dispose();
            throw;
        }

        var createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var vaultId = Guid.NewGuid().ToString("N");

        // Persist the wrapped DEK + InProgress BEFORE migrating: crash-recoverable.
        await _configManager.MergeSettingAsync(s =>
        {
            s.VaultWrappedDek = wrapped;
            s.VaultEnabled = true;
            s.VaultMigrationState = VaultMigrationState.InProgress;
            s.VaultCreatedAt = createdAt;
            s.VaultId = vaultId;
        }).ConfigureAwait(false);

        // Activate the DEK (Protect now writes v2), then run the forward pass.
        SetUnlockedDek(dek);
        await VaultMigrationEngine.MigrateForwardAsync(_configManager).ConfigureAwait(false);
        await ForwardStoresStrictAsync().ConfigureAwait(false);

        // The last good copies still hold the pre-vault documents: a fall-back to one must not
        // bring them back.
        await _configManager.RefreshLastGoodCopiesAsync().ConfigureAwait(false);

        await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.Complete)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Unlock the vault with the master password. Resumes an interrupted forward
    /// migration if one is pending.
    /// </summary>
    /// <param name="masterPassword">The master password (caller zeroes after).</param>
    /// <exception cref="InvalidOperationException">When the vault is not enabled.</exception>
    /// <exception cref="VaultUnlockException">When the password is wrong or the vault is corrupted.</exception>
    public async Task UnlockAsync(char[] masterPassword)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnlockCoreAsync(masterPassword).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }

        // After the gate is released: sealing the stores must not hold the unlock up.
        StartPostUnlockSealing();
    }

    private async Task UnlockCoreAsync(char[] masterPassword)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled)
        {
            throw new InvalidOperationException("The vault is not enabled.");
        }

        if (string.IsNullOrEmpty(settings.VaultWrappedDek))
        {
            throw new VaultUnlockException(); // enabled but no wrapped DEK -> corrupt, fail-closed
        }

        var dek = UnwrapWithChars(masterPassword, settings.VaultWrappedDek);
        SetUnlockedDek(dek);

        await ResumeOrReconcileForwardMigrationAsync(settings).ConfigureAwait(false);

        await StampLastMasterUnlockAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Enroll a Windows Hello-wrapped copy of the current vault DEK. Requires the
    /// vault to already be unlocked via the master password.
    /// </summary>
    public async Task EnrollHelloAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnrollHelloCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task EnrollHelloCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_vaultHelloService is null)
        {
            throw new VaultHelloException(VaultHelloFailureReason.Unavailable);
        }

        if (_unlockedDek is not { IsDisposed: false })
        {
            throw new InvalidOperationException("The vault must be unlocked before Windows Hello enrollment.");
        }

        if (!await _vaultHelloService.IsEnrollmentAvailableAsync(ct).ConfigureAwait(false))
        {
            throw new VaultHelloException(VaultHelloFailureReason.Unavailable);
        }

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled)
        {
            throw new InvalidOperationException("The vault is not enabled.");
        }

        var vaultId = string.IsNullOrWhiteSpace(settings.VaultId)
            ? Guid.NewGuid().ToString("N")
            : settings.VaultId;

        var dekCopy = GC.AllocateArray<byte>(VaultCipher.KeySizeBytes, pinned: true);
        try
        {
            _unlockedDek.Key.CopyTo(dekCopy);
            var enrollment = await _vaultHelloService.EnrollAsync(dekCopy, vaultId, ct).ConfigureAwait(false);

            await _configManager.MergeSettingAsync(s =>
            {
                s.VaultId = vaultId;
                s.VaultHelloEnrolled = true;
                s.VaultHelloWrappedDek = enrollment.WrappedDek;
                s.VaultHelloChallenge = enrollment.Challenge;
                s.VaultHelloSalt = enrollment.Salt;
                s.VaultHelloCredentialName = enrollment.CredentialName;
                s.VaultHelloPublicKeyHash = enrollment.PublicKeyHash;
            }).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dekCopy);
        }
    }

    /// <summary>
    /// Try to unlock the vault with the persisted Windows Hello enrollment. Any
    /// failure leaves the vault locked and returns false so callers can fall back
    /// to the master password path.
    /// </summary>
    public async Task<bool> UnlockWithHelloAsync(CancellationToken ct = default)
    {
        return (await UnlockWithHelloDetailedAsync(ct).ConfigureAwait(false)).Succeeded;
    }

    /// <summary>
    /// Try to unlock with Windows Hello and return a coarse failure reason without
    /// installing a DEK on failure.
    /// </summary>
    public async Task<VaultHelloUnlockResult> UnlockWithHelloDetailedAsync(CancellationToken ct = default)
    {
        VaultHelloUnlockResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await UnlockWithHelloCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }

        if (result.Succeeded)
        {
            StartPostUnlockSealing();
        }

        return result;
    }

    private async Task<VaultHelloUnlockResult> UnlockWithHelloCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_vaultHelloService is null)
        {
            return VaultHelloUnlockResult.Failure(VaultHelloFailureReason.Unavailable);
        }

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled || !settings.VaultHelloEnrolled)
        {
            return VaultHelloUnlockResult.Failure(VaultHelloFailureReason.Unavailable);
        }

        var enrollment = CreateHelloEnrollment(settings);
        if (enrollment is null)
        {
            return VaultHelloUnlockResult.Failure(VaultHelloFailureReason.CryptoFailure);
        }

        VaultDekHolder dek;
        try
        {
            dek = await _vaultHelloService.UnlockAsync(enrollment, ct).ConfigureAwait(false);
        }
        catch (VaultHelloException ex)
        {
            return VaultHelloUnlockResult.Failure(ex.Reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return VaultHelloUnlockResult.Failure(VaultHelloFailureReason.CryptoFailure);
        }

        SetUnlockedDek(dek);

        await ResumeOrReconcileForwardMigrationAsync(settings).ConfigureAwait(false);

        return VaultHelloUnlockResult.Success;
    }

    /// <summary>Whether this machine can enroll a Windows Hello wrapper now.</summary>
    public async Task<bool> IsHelloEnrollmentAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _vaultHelloService is not null
            && await _vaultHelloService.IsEnrollmentAvailableAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Remove the Windows Hello credential and clear Hello enrollment metadata.</summary>
    public async Task RemoveHelloAsync(CancellationToken ct = default)
    {
        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        await RemoveHelloCredentialIfPresentAsync(settings, ct).ConfigureAwait(false);
        await _configManager.MergeSettingAsync(ClearHelloSettings).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-wrap the DEK under a new master password. The DEK is unchanged, so no
    /// secret is re-encrypted.
    /// </summary>
    /// <param name="oldPassword">The current master password (caller zeroes after).</param>
    /// <param name="newPassword">The replacement master password (caller zeroes after).</param>
    /// <param name="parameters">Argon2id cost parameters for the new wrap.</param>
    /// <exception cref="MasterPasswordPolicyException">When the new password is too weak.</exception>
    /// <exception cref="InvalidOperationException">When the vault is not enabled.</exception>
    /// <exception cref="VaultUnlockException">When the old password is wrong or the vault is corrupted.</exception>
    public async Task ChangeMasterPasswordAsync(char[] oldPassword, char[] newPassword, Argon2idParameters parameters)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ChangeMasterPasswordCoreAsync(oldPassword, newPassword, parameters).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task ChangeMasterPasswordCoreAsync(char[] oldPassword, char[] newPassword, Argon2idParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(oldPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        RequirePolicy(newPassword);

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled || string.IsNullOrEmpty(settings.VaultWrappedDek))
        {
            throw new InvalidOperationException("The vault is not enabled.");
        }

        var newWrapped = ChangeWithChars(oldPassword, newPassword, settings.VaultWrappedDek, parameters);
        await _configManager.MergeSettingAsync(s => s.VaultWrappedDek = newWrapped).ConfigureAwait(false);
    }

    /// <summary>
    /// Disable the vault: authorize with the master password, return the configuration and every
    /// store to the legacy at-rest forms, check that nothing still holds a secret sealed with the
    /// vault key, then clear the vault state and lock.
    /// </summary>
    /// <remarks>
    /// <para>The key is erased by a successful disable, so a secret still sealed with it would be
    /// lost for good. The migration is marked <see cref="VaultMigrationState.InProgress"/> first,
    /// so a crash is detected at the next unlock, which seals everything again. After the reverse
    /// pass the in-memory key is withdrawn while the vault stays flagged enabled: a concurrent
    /// write fails closed instead of sealing a new secret, and the check that follows sees stores
    /// that no longer change.</para>
    /// <para>A value sealed before the key was withdrawn can still be written after the check, so
    /// the stores are asked again once the erase is on disk. If anything shows up, the wrapped key
    /// and the vault flags are written back from the copy held in memory before the secrets are
    /// sealed again, so nothing is ever sealed with a key that is not on disk.</para>
    /// <para>When anything remains, or any step throws, the key is put back and the forward pass
    /// runs again on the configuration and on every store, so the vault ends as protective as it
    /// began. Windows Hello is removed last, once the vault is gone for good.</para>
    /// </remarks>
    /// <param name="masterPassword">The master password (caller zeroes after).</param>
    /// <exception cref="InvalidOperationException">When the vault is not enabled.</exception>
    /// <exception cref="VaultUnlockException">When the password is wrong or the vault is corrupted.</exception>
    /// <exception cref="VaultResidueException">
    /// When a store still holds secrets sealed with the vault key, or a step failed and the vault
    /// was put back. The vault stays on.
    /// </exception>
    /// <exception cref="VaultStoreException">When a store could not be read or updated.</exception>
    public async Task DisableAsync(char[] masterPassword)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisableCoreAsync(masterPassword).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task DisableCoreAsync(char[] masterPassword)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        // The snapshot keeps the wrapped key and the Hello enrolment in memory for the whole
        // operation, so they can be written back if the erase has to be undone.
        var snapshot = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!snapshot.VaultEnabled || string.IsNullOrEmpty(snapshot.VaultWrappedDek))
        {
            throw new InvalidOperationException("The vault is not enabled.");
        }

        var dek = UnwrapWithChars(masterPassword, snapshot.VaultWrappedDek); // authorization
        SetUnlockedDek(dek);

        await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.InProgress)
            .ConfigureAwait(false);

        var residue = new List<string>();
        try
        {
            // Reverse while still enabled so v2 fields stay readable.
            await VaultMigrationEngine.MigrateReverseAsync(_configManager).ConfigureAwait(false);
            await ReverseStoresAsync().ConfigureAwait(false);

            // Withdraw the key but keep the vault flagged enabled: Protect now fails closed.
            CredentialProtector.ClearVaultKey();
            await CollectResidueAsync(residue).ConfigureAwait(false);

            if (residue.Count == 0)
            {
                await CommitDisableAsync(snapshot, residue).ConfigureAwait(false);
                if (residue.Count == 0)
                {
                    return;
                }
            }
        }
        catch (VaultResidueException found)
        {
            bool restored = await RestoreAfterFailureAsync(dek, snapshot).ConfigureAwait(false);
            throw new VaultResidueException(found.StoreNameKeys, restored);
        }
        catch (Exception failure)
        {
            bool restored = await RestoreAfterFailureAsync(dek, snapshot).ConfigureAwait(false);
            if (!restored)
            {
                throw new VaultResidueException([], protectionRestored: false, failure);
            }

            throw;
        }

        bool sealedAgain = await RestoreAfterFailureAsync(dek, snapshot).ConfigureAwait(false);
        throw new VaultResidueException(residue, sealedAgain);
    }

    /// <summary>
    /// The steps after the check: last good copies, the erase, a second look at the stores, and
    /// only then Windows Hello. Leaves names in <paramref name="residue"/> when the second look
    /// finds a vault secret; the caller then undoes the erase.
    /// </summary>
    private async Task CommitDisableAsync(AppSettings snapshot, List<string> residue)
    {
        // A later fall-back to a last good copy must not bring the vault blobs back.
        await _configManager.RefreshLastGoodCopiesAsync().ConfigureAwait(false);

        await _configManager.MergeSettingAsync(s =>
        {
            // A write already in flight when the key was withdrawn may have landed after the
            // check; looking again here, under the write lock, closes that window.
            if (SettingsHoldVaultSecrets(s))
            {
                throw new VaultResidueException([ConfigurationStoreNameKey]);
            }

            s.VaultWrappedDek = null;
            s.VaultEnabled = false;
            s.VaultMigrationState = VaultMigrationState.None;
            s.VaultCreatedAt = null;
            s.VaultId = null;
            ClearHelloSettings(s);
        }).ConfigureAwait(false);

        // A value sealed before the key was withdrawn can still land after the first check, in a
        // file or store that is now unreadable for good. Looking once more, with the erase on disk,
        // finds it while the key can still be put back.
        await CollectResidueAsync(residue).ConfigureAwait(false);
        if (residue.Count > 0)
        {
            return;
        }

        // The credential goes last: removing it earlier left the vault on, with the enrolment
        // flag set and no credential, whenever a later step failed.
        try
        {
            await RemoveHelloCredentialIfPresentAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Vault: the Windows Hello credential could not be removed ({ex.GetType().Name}).");
        }

        CredentialProtector.SetVaultEnabled(false);
        LockCore();
    }

    /// <summary>
    /// Undoes a disable that did not complete, in the only safe order: the wrapped key is on disk
    /// again first, then the key is put back and everything is sealed again. When the wrapped key
    /// is gone and cannot be written back, nothing is sealed: a secret sealed with a key that is
    /// not on disk is lost, whereas one left in the legacy form is merely less protected.
    /// </summary>
    /// <returns>Whether everything converted is sealed again (or the vault is, for good, off).</returns>
    private async Task<bool> RestoreAfterFailureAsync(VaultDekHolder dek, AppSettings snapshot)
    {
        bool keyOnDisk;
        try
        {
            keyOnDisk = await EnsureWrappedKeyOnDiskAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            keyOnDisk = false;
            FileLogger.Warn($"Vault: the wrapped key could not be confirmed on disk ({ex.GetType().Name}).");
        }

        if (!keyOnDisk)
        {
            CredentialProtector.SetVaultEnabled(false);
            LockCore();
            return true;
        }

        return await RestoreProtectionAsync(dek).ConfigureAwait(false);
    }

    private async Task<bool> EnsureWrappedKeyOnDiskAsync(AppSettings snapshot)
    {
        AppSettings current = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (current.VaultEnabled && !string.IsNullOrEmpty(current.VaultWrappedDek))
        {
            return true;
        }

        await _configManager.MergeSettingAsync(s =>
        {
            s.VaultWrappedDek = snapshot.VaultWrappedDek;
            s.VaultEnabled = true;
            s.VaultMigrationState = VaultMigrationState.InProgress;
            s.VaultCreatedAt = snapshot.VaultCreatedAt;
            s.VaultId = snapshot.VaultId;
            s.VaultHelloEnrolled = snapshot.VaultHelloEnrolled;
            s.VaultHelloWrappedDek = snapshot.VaultHelloWrappedDek;
            s.VaultHelloChallenge = snapshot.VaultHelloChallenge;
            s.VaultHelloSalt = snapshot.VaultHelloSalt;
            s.VaultHelloCredentialName = snapshot.VaultHelloCredentialName;
            s.VaultHelloPublicKeyHash = snapshot.VaultHelloPublicKeyHash;
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Puts the key back, then seals the configuration and every store again. The marker is
    /// cleared only when all of that worked, so the next unlock finishes it otherwise.
    /// </summary>
    /// <returns>Whether everything was sealed again.</returns>
    private async Task<bool> RestoreProtectionAsync(VaultDekHolder dek)
    {
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);

        var restored = true;
        try
        {
            await VaultMigrationEngine.MigrateForwardAsync(_configManager).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            restored = false;
            FileLogger.Warn($"Vault: the configuration could not be sealed again ({ex.GetType().Name}).");
        }

        if (!await TryForwardStoresAsync().ConfigureAwait(false))
        {
            restored = false;
        }

        if (!restored)
        {
            return false;
        }

        try
        {
            await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.Complete)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Vault: the migration marker could not be cleared ({ex.GetType().Name}).");
        }

        return true;
    }

    private static bool SettingsHoldVaultSecrets(AppSettings settings) =>
        VaultConfigurationScan.ContainsSecretBlob(JsonSerializer.SerializeToElement(settings));

    /// <summary>Names every store that still holds a secret sealed with the vault key.</summary>
    private async Task CollectResidueAsync(List<string> residue)
    {
        residue.Clear();
        if (await VaultConfigurationScan.HoldsVaultSecretsAsync(_configManager.SettingsPath, CancellationToken.None)
                .ConfigureAwait(false)
            || await VaultConfigurationScan.HoldsVaultSecretsAsync(_configManager.ServersPath, CancellationToken.None)
                .ConfigureAwait(false))
        {
            residue.Add(ConfigurationStoreNameKey);
        }

        foreach (IVaultSecretStore store in _secretStores)
        {
            bool holds;
            try
            {
                holds = await store.HoldsVaultSecretsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new VaultStoreException(store.StoreNameKey, ex);
            }

            if (holds)
            {
                residue.Add(store.StoreNameKey);
            }
        }
    }

    /// <summary>
    /// Lock the vault: clear and zero the in-memory DEK. The vault stays
    /// configured, so subsequent writes fail closed until the next unlock.
    /// </summary>
    /// <remarks>
    /// Never waits: it is called from the UI thread by the idle lock. While an operation (enable,
    /// unlock, disable, change of password) holds the key, the request is deferred and carried out
    /// the moment that operation ends, so the key is not disposed under it and the lock is not lost.
    /// </remarks>
    public void Lock()
    {
        if (!_gate.Wait(0))
        {
            Volatile.Write(ref _lockDeferred, 1);

            // The operation may have ended between the failed attempt and the flag being set.
            if (_gate.Wait(0))
            {
                ReleaseGate();
            }

            return;
        }

        ReleaseGate(lockNow: true);
    }

    /// <summary>Lock and release the held DEK.</summary>
    public void Dispose() => Lock();

    /// <summary>Releases the gate, first carrying out a lock that was requested while it was held.</summary>
    private void ReleaseGate(bool lockNow = false)
    {
        if (lockNow || Interlocked.Exchange(ref _lockDeferred, 0) == 1)
        {
            LockCore();
        }

        _gate.Release();
    }

    private void LockCore()
    {
        CredentialProtector.ClearVaultKey();
        _unlockedDek?.Dispose();
        _unlockedDek = null;
    }

    private void SetUnlockedDek(VaultDekHolder dek)
    {
        var previous = _unlockedDek;
        _unlockedDek = dek;
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);

        if (!ReferenceEquals(previous, dek))
        {
            previous?.Dispose();
        }
    }

    /// <summary>
    /// Finishes the configuration half of what a crash left half done.
    /// <see cref="VaultMigrationState.InProgress"/> covers both an interrupted enable and an
    /// interrupted disable: either way the key is intact and the answer is the same, run the
    /// forward pass again. The stores and the marker are handled after the unlock, by
    /// <see cref="PostUnlockSealingAsync"/>.
    /// </summary>
    private async Task ResumeOrReconcileForwardMigrationAsync(AppSettings settings)
    {
        if (settings.VaultMigrationState == VaultMigrationState.InProgress)
        {
            await VaultMigrationEngine.MigrateForwardAsync(_configManager).ConfigureAwait(false);

            // With stores, the marker is cleared once they are sealed, after the unlock; with none
            // there is nothing left to wait for.
            if (_secretStores.Count == 0)
            {
                await _configManager.MergeSettingAsync(
                        current => current.VaultMigrationState = VaultMigrationState.Complete)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (settings.VaultMigrationState != VaultMigrationState.Complete)
        {
            return;
        }

        try
        {
            await VaultMigrationEngine.ReconcileCitrixLaunchTokensAsync(_configManager)
                .ConfigureAwait(false);
        }
        catch
        {
            LockCore();
            throw;
        }
    }

    private void StartPostUnlockSealing()
    {
        if (_secretStores.Count > 0)
        {
            _postUnlockSealing = Task.Run(PostUnlockSealingAsync);
        }
    }

    /// <summary>
    /// Seals what older versions or an interrupted disable left in the legacy form, clears the
    /// marker once every store is sealed, and reports a store that stays unsealed (once a session).
    /// Runs after the unlock has returned and never throws.
    /// </summary>
    private async Task PostUnlockSealingAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsUnlocked)
            {
                return;
            }

            var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
            bool allSealed = await TryForwardStoresAsync().ConfigureAwait(false);
            if (allSealed && settings.VaultMigrationState == VaultMigrationState.InProgress)
            {
                await _configManager.MergeSettingAsync(
                        current => current.VaultMigrationState = VaultMigrationState.Complete)
                    .ConfigureAwait(false);
            }

            var unsealed = await FindUnsealedStoresAsync().ConfigureAwait(false);
            if (unsealed.Count > 0 && Interlocked.Exchange(ref _unsealedNotified, 1) == 0)
            {
                UnsealedStoreNameKeys = unsealed;
                UnsealedStoresDetected?.Invoke(unsealed);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Vault: the pass after unlock failed ({ex.GetType().Name}).");
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task<IReadOnlyList<string>> FindUnsealedStoresAsync()
    {
        var unsealed = new List<string>();
        foreach (IVaultSecretStore store in _secretStores)
        {
            try
            {
                if (await store.HasUnsealedSecretsAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    unsealed.Add(store.StoreNameKey);
                }
            }
            catch (Exception)
            {
                unsealed.Add(store.StoreNameKey);
            }
        }

        return unsealed;
    }

    private async Task ForwardStoresStrictAsync()
    {
        foreach (IVaultSecretStore store in _secretStores)
        {
            try
            {
                await store.ForwardAsync(VaultMigrationEngine.ForwardEncrypted, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new VaultStoreException(store.StoreNameKey, ex);
            }
        }
    }

    private async Task ReverseStoresAsync()
    {
        foreach (IVaultSecretStore store in _secretStores)
        {
            try
            {
                await store.ReverseAsync(VaultMigrationEngine.ReverseToCredentialProtectorLegacy, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new VaultStoreException(store.StoreNameKey, ex);
            }
        }
    }

    /// <summary>Seals every store, logging the ones that fail. No value is logged.</summary>
    /// <returns>Whether every store was sealed.</returns>
    private async Task<bool> TryForwardStoresAsync()
    {
        var allSealed = true;
        foreach (IVaultSecretStore store in _secretStores)
        {
            try
            {
                await store.ForwardAsync(VaultMigrationEngine.ForwardEncrypted, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                allSealed = false;
                FileLogger.Warn($"Vault: the store '{store.StoreNameKey}' could not be sealed ({ex.GetType().Name}).");
            }
        }

        return allSealed;
    }

    private Task StampLastMasterUnlockAsync()
    {
        var now = DateTimeOffset.UtcNow;
        return _configManager.MergeSettingAsync(s => s.VaultLastMasterUnlockUtc = now);
    }

    private async Task RemoveHelloCredentialIfPresentAsync(AppSettings settings, CancellationToken ct)
    {
        if (_vaultHelloService is null || string.IsNullOrWhiteSpace(settings.VaultHelloCredentialName))
        {
            return;
        }

        await _vaultHelloService.RemoveAsync(settings.VaultHelloCredentialName, ct).ConfigureAwait(false);
    }

    private static VaultHelloEnrollment? CreateHelloEnrollment(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.VaultId) ||
            string.IsNullOrWhiteSpace(settings.VaultHelloWrappedDek) ||
            string.IsNullOrWhiteSpace(settings.VaultHelloChallenge) ||
            string.IsNullOrWhiteSpace(settings.VaultHelloSalt) ||
            string.IsNullOrWhiteSpace(settings.VaultHelloCredentialName) ||
            string.IsNullOrWhiteSpace(settings.VaultHelloPublicKeyHash))
        {
            return null;
        }

        return new VaultHelloEnrollment(
            settings.VaultId,
            settings.VaultHelloWrappedDek,
            settings.VaultHelloChallenge,
            settings.VaultHelloSalt,
            settings.VaultHelloCredentialName,
            settings.VaultHelloPublicKeyHash);
    }

    private static void ClearHelloSettings(AppSettings settings)
    {
        settings.VaultHelloEnrolled = false;
        settings.VaultHelloWrappedDek = null;
        settings.VaultHelloChallenge = null;
        settings.VaultHelloSalt = null;
        settings.VaultHelloCredentialName = null;
        settings.VaultHelloPublicKeyHash = null;
    }

    private static void RequirePolicy(char[] password)
    {
        var result = MasterPasswordPolicy.Validate(password);
        if (!result.IsAcceptable)
        {
            throw new MasterPasswordPolicyException(result.Error!.Value);
        }
    }

    private static string WrapWithChars(char[] password, ReadOnlySpan<byte> dek, Argon2idParameters parameters)
    {
        var bytes = ToPinnedUtf8(password);
        try
        {
            return VaultKeyManager.WrapDek(bytes, dek, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static VaultDekHolder UnwrapWithChars(char[] password, string wrapped)
    {
        var bytes = ToPinnedUtf8(password);
        try
        {
            return VaultKeyManager.UnwrapDek(bytes, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ChangeWithChars(char[] oldPassword, char[] newPassword, string wrapped, Argon2idParameters parameters)
    {
        var oldBytes = ToPinnedUtf8(oldPassword);
        var newBytes = ToPinnedUtf8(newPassword);
        try
        {
            return VaultKeyManager.ChangeMasterPassword(oldBytes, newBytes, wrapped, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldBytes);
            CryptographicOperations.ZeroMemory(newBytes);
        }
    }

    private static byte[] ToPinnedUtf8(char[] password)
    {
        var count = Encoding.UTF8.GetByteCount(password);
        var bytes = GC.AllocateArray<byte>(count, pinned: true);
        Encoding.UTF8.GetBytes(password, 0, password.Length, bytes, 0);
        return bytes;
    }
}
