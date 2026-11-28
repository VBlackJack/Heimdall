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

using System.Security.Cryptography;
using System.Text;
using Heimdall.App.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

internal sealed record PlinkHostKeyDecision(
    bool ShouldProceed,
    string? Fingerprint,
    SshFailureCode? FailureCode,
    string? FailureMessageKey,
    string? StoredFingerprint,
    string? PresentedFingerprint);

internal static class PlinkHostKeyDecider
{
    /// <summary>
    /// Resolves the host-key fingerprint Heimdall will pass to plink via
    /// -hostkey. When <paramref name="plinkPath"/> is absent, the probe is
    /// skipped: an existing stored fingerprint proceeds, while first-use
    /// returns a structured fail-closed decision.
    /// </summary>
    internal static async Task<PlinkHostKeyDecision> DecideAsync(
        string host,
        int port,
        string? username,
        string? plinkPath,
        int probeTimeoutMs,
        string? storedFingerprint,
        IPlinkHostKeyProbe probe,
        IHostKeyVerifier verifier,
        IHostKeyTrustService trustService,
        CancellationToken ct)
    {
        return await DecideAsync(
                transportHost: host,
                transportPort: port,
                verificationHost: host,
                verificationPort: port,
                username: username,
                plinkPath: plinkPath,
                probeTimeoutMs: probeTimeoutMs,
                storedFingerprint: storedFingerprint,
                probe: probe,
                verifier: verifier,
                trustService: trustService,
                ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the host-key fingerprint Heimdall will pass to plink via
    /// -hostkey, while separating the transport endpoint from the logical
    /// host-key trust identity.
    /// </summary>
    internal static async Task<PlinkHostKeyDecision> DecideAsync(
        string transportHost,
        int transportPort,
        string verificationHost,
        int verificationPort,
        string? username,
        string? plinkPath,
        int probeTimeoutMs,
        string? storedFingerprint,
        IPlinkHostKeyProbe probe,
        IHostKeyVerifier verifier,
        IHostKeyTrustService trustService,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationHost);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(trustService);
        ct.ThrowIfCancellationRequested();

        string? normalizedStored = string.IsNullOrWhiteSpace(storedFingerprint)
            ? null
            : storedFingerprint;
        bool isViaGateway = !string.Equals(transportHost, verificationHost, StringComparison.OrdinalIgnoreCase)
            || transportPort != verificationPort;
        PlinkHostKeyPresentation? presentation = string.IsNullOrWhiteSpace(plinkPath)
            ? null
            : await probe.ProbeAsync(
                    plinkPath,
                    transportHost,
                    transportPort,
                    username,
                    probeTimeoutMs,
                    ct)
                .ConfigureAwait(false);

        if (normalizedStored is not null)
        {
            return await DecideWithStoredFingerprintAsync(
                    verificationHost,
                    verificationPort,
                    normalizedStored,
                    presentation,
                    isViaGateway,
                    verifier,
                    trustService,
                    ct)
                .ConfigureAwait(false);
        }

        return await DecideFirstUseAsync(
                verificationHost,
                verificationPort,
                presentation,
                isViaGateway,
                verifier,
                trustService,
                ct)
            .ConfigureAwait(false);
    }

    private static async Task<PlinkHostKeyDecision> DecideWithStoredFingerprintAsync(
        string host,
        int port,
        string storedFingerprint,
        PlinkHostKeyPresentation? presentation,
        bool isViaGateway,
        IHostKeyVerifier verifier,
        IHostKeyTrustService trustService,
        CancellationToken ct)
    {
        if (presentation is null)
        {
            if (trustService.IsRevoked(host, port, storedFingerprint))
            {
                return Reject(
                    SshFailureCode.HostKeyRevoked,
                    SshLocalizationKeys.ErrorSshHostKeyRevoked,
                    storedFingerprint,
                    storedFingerprint);
            }

            Core.Logging.FileLogger.Warn(
                $"Plink host-key probe returned no presentation for {host}:{port}; proceeding with stored Heimdall fingerprint.");
            return Proceed(storedFingerprint);
        }

        if (trustService.IsRevoked(host, port, presentation.Fingerprint))
        {
            return Reject(
                SshFailureCode.HostKeyRevoked,
                SshLocalizationKeys.ErrorSshHostKeyRevoked,
                storedFingerprint,
                presentation.Fingerprint);
        }

        if (ConstantTimeEquals(presentation.Fingerprint, storedFingerprint))
        {
            return Proceed(storedFingerprint);
        }

        // Multi-algorithm match: if Heimdall already pinned a key for this host under the exact
        // algorithm presented by Plink (e.g. ssh-ed25519 vs rsa-sha2-512), accept without a
        // false-positive MITM mismatch prompt.
        string? algorithmStored = trustService.GetEntry(host, port, presentation.Algorithm)?.Fingerprint;
        if (!string.IsNullOrWhiteSpace(algorithmStored)
            && ConstantTimeEquals(presentation.Fingerprint, algorithmStored))
        {
            return Proceed(algorithmStored);
        }

        HostKeyDecision decision = await verifier.VerifyAsync(
                host,
                port,
                presentation.Algorithm,
                presentation.Fingerprint,
                storedFingerprint,
                ReadOnlyMemory<byte>.Empty,
                isViaGateway,
                ct)
            .ConfigureAwait(false);

        return ApplyVerifierDecision(
            host,
            port,
            presentation,
            storedFingerprint,
            decision,
            trustService);
    }

    private static async Task<PlinkHostKeyDecision> DecideFirstUseAsync(
        string host,
        int port,
        PlinkHostKeyPresentation? presentation,
        bool isViaGateway,
        IHostKeyVerifier verifier,
        IHostKeyTrustService trustService,
        CancellationToken ct)
    {
        if (presentation is null)
        {
            return Reject(
                SshFailureCode.HostKeyUnavailable,
                SshLocalizationKeys.ErrorSshHostKeyUnavailable,
                null,
                null);
        }

        if (trustService.IsRevoked(host, port, presentation.Fingerprint))
        {
            return Reject(
                SshFailureCode.HostKeyRevoked,
                SshLocalizationKeys.ErrorSshHostKeyRevoked,
                null,
                presentation.Fingerprint);
        }

        HostKeyDecision decision = await verifier.VerifyAsync(
                host,
                port,
                presentation.Algorithm,
                presentation.Fingerprint,
                storedFingerprint: null,
                ReadOnlyMemory<byte>.Empty,
                isViaGateway,
                ct)
            .ConfigureAwait(false);

        return ApplyVerifierDecision(
            host,
            port,
            presentation,
            storedFingerprint: null,
            decision,
            trustService);
    }

    private static PlinkHostKeyDecision ApplyVerifierDecision(
        string host,
        int port,
        PlinkHostKeyPresentation presentation,
        string? storedFingerprint,
        HostKeyDecision decision,
        IHostKeyTrustService trustService)
    {
        if (decision == HostKeyDecision.Accept)
        {
            trustService.Trust(
                host,
                port,
                presentation.Fingerprint,
                presentation.Algorithm,
                HostKeySource.UserConfirmed,
                presentation.PublicKeyBase64);
            return Proceed(presentation.Fingerprint, storedFingerprint, presentation.Fingerprint);
        }

        if (decision == HostKeyDecision.TrustOnce)
        {
            trustService.TrustForSession(
                host,
                port,
                presentation.Fingerprint,
                presentation.Algorithm,
                presentation.PublicKeyBase64);
            return Proceed(presentation.Fingerprint, storedFingerprint, presentation.Fingerprint);
        }

        return storedFingerprint is null
            ? Reject(
                SshFailureCode.Cancelled,
                SshLocalizationKeys.ErrorSshCancelled,
                null,
                presentation.Fingerprint)
            : Reject(
                SshFailureCode.HostKeyMismatch,
                SshLocalizationKeys.ErrorHostKeyMismatch,
                storedFingerprint,
                presentation.Fingerprint);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        byte[] aBytes = Encoding.UTF8.GetBytes(a);
        byte[] bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static PlinkHostKeyDecision Proceed(
        string fingerprint,
        string? storedFingerprint = null,
        string? presentedFingerprint = null)
    {
        return new PlinkHostKeyDecision(
            ShouldProceed: true,
            fingerprint,
            FailureCode: null,
            FailureMessageKey: null,
            storedFingerprint,
            presentedFingerprint);
    }

    private static PlinkHostKeyDecision Reject(
        SshFailureCode failureCode,
        string failureMessageKey,
        string? storedFingerprint,
        string? presentedFingerprint)
    {
        return new PlinkHostKeyDecision(
            ShouldProceed: false,
            Fingerprint: null,
            failureCode,
            failureMessageKey,
            storedFingerprint,
            presentedFingerprint);
    }
}
