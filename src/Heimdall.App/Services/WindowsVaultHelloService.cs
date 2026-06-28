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

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Heimdall.App.Services;

/// <summary>
/// Windows Hello implementation of the secondary vault DEK wrapper. The
/// KeyCredential is relied-upon-not-attested: enrollment is gated on TPM 2.0
/// presence, while KeyCredential attestation is not enforced.
/// </summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WindowsVaultHelloService : IVaultHelloService
{
    private readonly ITpmPresenceService _tpmPresence;

    /// <summary>Create the service.</summary>
    public WindowsVaultHelloService(ITpmPresenceService tpmPresence)
    {
        _tpmPresence = tpmPresence;
    }

    /// <inheritdoc />
    public async Task<bool> IsEnrollmentAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            bool keyCredentialSupported = await KeyCredentialManager.IsSupportedAsync().AsTask(ct)
                .ConfigureAwait(false);
            bool tpmPresent = await _tpmPresence.IsTpm2PresentAsync(ct).ConfigureAwait(false);
            bool available = keyCredentialSupported && tpmPresent;
            FileLogger.Info($"WindowsVaultHelloService: enrollment availability returned {available}.");
            return available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WindowsVaultHelloService: availability check failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<VaultHelloEnrollment> EnrollAsync(ReadOnlyMemory<byte> dek, string vaultId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (dek.Length != VaultCipher.KeySizeBytes)
        {
            throw new ArgumentException($"DEK must be {VaultCipher.KeySizeBytes} bytes.", nameof(dek));
        }

        var credentialName = VaultHelloProtector.CreateCredentialName(vaultId);
        var create = await KeyCredentialManager
            .RequestCreateAsync(credentialName, KeyCredentialCreationOption.ReplaceExisting)
            .AsTask(ct)
            .ConfigureAwait(false);
        ThrowIfFailed(create.Status.ToString());

        var credential = create.Credential ?? throw new VaultHelloException(VaultHelloFailureReason.Unavailable);
        var publicKey = ToArray(credential.RetrievePublicKey());
        var publicKeyHash = Convert.ToHexString(SHA256.HashData(publicKey));
        var challenge = RandomNumberGenerator.GetBytes(VaultHelloProtector.ChallengeSizeBytes);
        var salt = RandomNumberGenerator.GetBytes(VaultHelloProtector.SaltSizeBytes);
        byte[]? signature = null;
        byte[]? helloKek = null;

        try
        {
            signature = await SignAsync(credential, challenge, ct).ConfigureAwait(false);
            helloKek = VaultHelloProtector.DeriveHelloKek(signature, salt);
            var binding = new VaultHelloBinding(vaultId, publicKeyHash, challenge, salt);
            var rawWrapped = VaultHelloProtector.WrapDek(dek.Span, helloKek, binding);
            var dpapiWrapped = DpapiProvider.Protect(rawWrapped);

            FileLogger.Info("WindowsVaultHelloService: Windows Hello vault enrollment succeeded.");
            return new VaultHelloEnrollment(
                vaultId,
                dpapiWrapped,
                Convert.ToBase64String(challenge),
                Convert.ToBase64String(salt),
                credentialName,
                publicKeyHash);
        }
        finally
        {
            Zero(signature);
            Zero(helloKek);
        }
    }

    /// <inheritdoc />
    public async Task<VaultDekHolder> UnlockAsync(VaultHelloEnrollment stored, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stored);

        var open = await KeyCredentialManager.OpenAsync(stored.CredentialName).AsTask(ct).ConfigureAwait(false);
        ThrowIfFailed(open.Status.ToString());
        var credential = open.Credential ?? throw new VaultHelloException(VaultHelloFailureReason.NotFound);

        var binding = stored.ToBinding();
        byte[]? signature = null;
        byte[]? helloKek = null;
        string rawWrapped;

        try
        {
            signature = await SignAsync(credential, binding.Challenge, ct).ConfigureAwait(false);
            helloKek = VaultHelloProtector.DeriveHelloKek(signature, binding.Salt);
            try
            {
                rawWrapped = DpapiProvider.Unprotect(stored.WrappedDek);
            }
            catch
            {
                throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
            }

            var holder = VaultHelloProtector.UnwrapDek(rawWrapped, helloKek, binding);
            FileLogger.Info("WindowsVaultHelloService: Windows Hello vault unlock succeeded.");
            return holder;
        }
        finally
        {
            Zero(signature);
            Zero(helloKek);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string credentialName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
        {
            return;
        }

        try
        {
            await KeyCredentialManager.DeleteAsync(credentialName).AsTask(ct).ConfigureAwait(false);
            FileLogger.Info("WindowsVaultHelloService: Windows Hello vault credential removed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x8009000D)
        {
            FileLogger.Info("WindowsVaultHelloService: Windows Hello vault credential was already absent.");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WindowsVaultHelloService: credential removal failed: {ex.Message}");
        }
    }

    private static async Task<byte[]> SignAsync(KeyCredential credential, byte[] challenge, CancellationToken ct)
    {
        var sign = await credential.RequestSignAsync(ToBuffer(challenge)).AsTask(ct).ConfigureAwait(false);
        ThrowIfFailed(sign.Status.ToString());
        if (sign.Result is null || sign.Result.Length == 0)
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }

        return ToArray(sign.Result);
    }

    private static void ThrowIfFailed(string statusName)
    {
        var reason = VaultHelloStatusMapper.MapKeyCredentialStatus(statusName);
        if (reason is not null)
        {
            throw new VaultHelloException(reason.Value);
        }
    }

    private static IBuffer ToBuffer(byte[] bytes) => CryptographicBuffer.CreateFromByteArray(bytes);

    private static byte[] ToArray(IBuffer buffer)
    {
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] bytes);
        return bytes;
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
