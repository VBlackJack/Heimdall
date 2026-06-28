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
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Headless lifecycle for the master-password vault: enable, unlock, change the
/// master password, disable, and lock. Owns the unlocked DEK holder for the
/// session and drives the resumable migration engine.
/// </summary>
/// <remarks>
/// Migration state machine (forward only; reverse is idempotent and stateless):
/// <list type="number">
/// <item><b>Enable</b>: <c>None</c> -&gt; persist (wrapped DEK + Enabled=true +
/// <c>InProgress</c>) -&gt; set DEK -&gt; forward-migrate -&gt; <c>Complete</c>.
/// The state and wrapped DEK are persisted BEFORE migrating, so a crash is
/// recoverable.</item>
/// <item><b>Unlock</b> while <c>InProgress</c>: unwrap -&gt; set DEK -&gt; resume
/// forward-migrate (idempotent) -&gt; <c>Complete</c>.</item>
/// <item><b>Disable</b>: unwrap (authorization) -&gt; set DEK -&gt;
/// reverse-migrate while still enabled -&gt; persist (clear wrapped DEK +
/// Enabled=false + <c>None</c>) -&gt; lock.</item>
/// </list>
/// Rollback policy: enable never rolls back to disabled on failure. A failed or
/// interrupted forward pass leaves Enabled=true + <c>InProgress</c> + the wrapped
/// DEK, a consistent RESUMABLE state — re-unlock finishes it. A failed or
/// interrupted disable leaves the vault enabled with a mixed-but-readable set;
/// re-disable finishes it. Neither path ever loses or silently downgrades data.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VaultLifecycleService : IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly IVaultHelloService? _vaultHelloService;
    private VaultDekHolder? _unlockedDek;

    /// <summary>Create the service over a configuration manager.</summary>
    /// <param name="configManager">The settings + servers persistence manager.</param>
    /// <param name="vaultHelloService">Optional Windows Hello DEK-wrapper service.</param>
    public VaultLifecycleService(IConfigManager configManager, IVaultHelloService? vaultHelloService = null)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        _configManager = configManager;
        _vaultHelloService = vaultHelloService;
    }

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

        if (settings.VaultMigrationState == VaultMigrationState.InProgress)
        {
            await VaultMigrationEngine.MigrateForwardAsync(_configManager).ConfigureAwait(false);
            await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.Complete)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enroll a Windows Hello-wrapped copy of the current vault DEK. Requires the
    /// vault to already be unlocked via the master password.
    /// </summary>
    public async Task EnrollHelloAsync(CancellationToken ct = default)
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
        ct.ThrowIfCancellationRequested();
        if (_vaultHelloService is null)
        {
            return false;
        }

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled || !settings.VaultHelloEnrolled)
        {
            return false;
        }

        var enrollment = CreateHelloEnrollment(settings);
        if (enrollment is null)
        {
            return false;
        }

        VaultDekHolder dek;
        try
        {
            dek = await _vaultHelloService.UnlockAsync(enrollment, ct).ConfigureAwait(false);
        }
        catch (VaultHelloException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }

        SetUnlockedDek(dek);

        if (settings.VaultMigrationState == VaultMigrationState.InProgress)
        {
            await VaultMigrationEngine.MigrateForwardAsync(_configManager).ConfigureAwait(false);
            await _configManager.MergeSettingAsync(s => s.VaultMigrationState = VaultMigrationState.Complete)
                .ConfigureAwait(false);
        }

        return true;
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
    /// Disable the vault: authorize with the master password, reverse-migrate the
    /// confidential set back to the legacy at-rest forms, then clear the vault
    /// state and lock.
    /// </summary>
    /// <param name="masterPassword">The master password (caller zeroes after).</param>
    /// <exception cref="InvalidOperationException">When the vault is not enabled.</exception>
    /// <exception cref="VaultUnlockException">When the password is wrong or the vault is corrupted.</exception>
    public async Task DisableAsync(char[] masterPassword)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (!settings.VaultEnabled || string.IsNullOrEmpty(settings.VaultWrappedDek))
        {
            throw new InvalidOperationException("The vault is not enabled.");
        }

        var dek = UnwrapWithChars(masterPassword, settings.VaultWrappedDek); // authorization
        SetUnlockedDek(dek);

        // Reverse-migrate while still enabled so v2 fields stay readable; flip the
        // flags only after the reverse pass completes.
        await VaultMigrationEngine.MigrateReverseAsync(_configManager).ConfigureAwait(false);
        await RemoveHelloCredentialIfPresentAsync(settings, CancellationToken.None).ConfigureAwait(false);

        await _configManager.MergeSettingAsync(s =>
        {
            s.VaultWrappedDek = null;
            s.VaultEnabled = false;
            s.VaultMigrationState = VaultMigrationState.None;
            s.VaultCreatedAt = null;
            s.VaultId = null;
            ClearHelloSettings(s);
        }).ConfigureAwait(false);

        CredentialProtector.SetVaultEnabled(false);
        Lock();
    }

    /// <summary>
    /// Lock the vault: clear and zero the in-memory DEK. The vault stays
    /// configured, so subsequent writes fail closed until the next unlock.
    /// </summary>
    public void Lock()
    {
        CredentialProtector.ClearVaultKey();
        _unlockedDek?.Dispose();
        _unlockedDek = null;
    }

    /// <summary>Lock and release the held DEK.</summary>
    public void Dispose() => Lock();

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
