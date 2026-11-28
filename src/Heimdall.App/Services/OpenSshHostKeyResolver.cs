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
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Captures the host key a server presents, without authenticating, for the native OpenSSH
/// backend. Stateless: every call is a fresh, throwaway handshake.
/// </summary>
internal interface IOpenSshHostKeyProbe
{
    /// <summary>
    /// Returns the presented key (algorithm, SHA256 fingerprint and raw blob as base64), or null
    /// when the server could not be probed. Only a cancellation of <paramref name="ct"/> throws.
    /// </summary>
    Task<PlinkHostKeyPresentation?> ProbeAsync(
        string host,
        int port,
        SshCryptoPolicy cryptoPolicy,
        int timeoutMs,
        CancellationToken ct);
}

/// <summary>
/// Probes through SSH.NET only, with the same host-key algorithm set as the embedded and Plink
/// modes (certificate algorithms included, so a certificate-presenting host yields the same
/// pin everywhere) and the effective crypto policy applied. It never starts Plink and never
/// offers a profile user name: the factory supplies its own probe user.
/// </summary>
internal sealed class SshNetOpenSshHostKeyProbe : IOpenSshHostKeyProbe
{
    /// <summary>Lower bound of the probe's connect timeout, whatever the setting says.</summary>
    internal const int MinimumProbeTimeoutMs = 1000;

    public async Task<PlinkHostKeyPresentation?> ProbeAsync(
        string host,
        int port,
        SshCryptoPolicy cryptoPolicy,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            var probeParams = new SshConnectionParams
            {
                Host = host,
                Port = port,
                Username = string.Empty,
                CryptoPolicy = cryptoPolicy,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(MinimumProbeTimeoutMs, timeoutMs)),
            };
            SshConnectionFactory.HostKeyPresentation presentation = await SshConnectionFactory
                .ProbeHostKeyAsync(probeParams, host, port, ct)
                .ConfigureAwait(false);
            return new PlinkHostKeyPresentation(
                presentation.Algorithm,
                presentation.Fingerprint,
                Convert.ToBase64String(presentation.HostKey));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Debug($"Native OpenSSH host key probe failed for {host}:{port}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Why the native OpenSSH host-key resolution refused to launch.</summary>
internal enum OpenSshHostKeyFailure
{
    None = 0,
    Revoked,
    CertificateInvalid,
    Mismatch,
    Cancelled,
    Unavailable,
    UnsupportedKeyType,
}

/// <summary>
/// The outcome of resolving the host key for a native OpenSSH launch: the keys to write to the
/// ephemeral <c>known_hosts</c> file (empty on failure) or the reason for the refusal.
/// </summary>
internal sealed record OpenSshHostKeyResolution(
    IReadOnlyList<OpenSshKnownHostsKey> Keys,
    bool CertificatePresented,
    OpenSshHostKeyFailure Failure,
    string? StoredFingerprint = null,
    string? PresentedFingerprint = null)
{
    public bool Succeeded => Failure == OpenSshHostKeyFailure.None;

    internal static OpenSshHostKeyResolution Success(IReadOnlyList<OpenSshKnownHostsKey> keys, bool certificatePresented)
        => new(keys, certificatePresented, OpenSshHostKeyFailure.None);

    internal static OpenSshHostKeyResolution Rejected(
        OpenSshHostKeyFailure failure,
        string? storedFingerprint = null,
        string? presentedFingerprint = null)
        => new([], false, failure, storedFingerprint, presentedFingerprint);
}

/// <summary>
/// Decides the host key native <c>ssh.exe</c> will be allowed to accept. Heimdall runs the same
/// decision as for Plink (revocation, stored pin, mismatch prompt, first-use prompt) plus the
/// certificate-authority rules, then hands ssh only the key it decided, so ssh never asks about
/// host keys itself.
/// </summary>
internal sealed class OpenSshHostKeyResolver
{
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyTrustService _trustService;
    private readonly IHostKeyVerifier _verifier;
    private readonly IOpenSshHostKeyProbe _probe;

    public OpenSshHostKeyResolver(
        HostKeyStore hostKeyStore,
        IHostKeyTrustService trustService,
        IHostKeyVerifier verifier,
        IOpenSshHostKeyProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(trustService);
        ArgumentNullException.ThrowIfNull(verifier);
        _hostKeyStore = hostKeyStore;
        _trustService = trustService;
        _verifier = verifier;
        _probe = probe ?? new SshNetOpenSshHostKeyProbe();
    }

    /// <summary>
    /// Resolves the host key of a server. <paramref name="transportHost"/> is the endpoint ssh
    /// dials (a loopback port through a gateway); trust is looked up, asked and stored under the
    /// logical <paramref name="logicalHost"/> and <paramref name="logicalPort"/>.
    /// </summary>
    public async Task<OpenSshHostKeyResolution> ResolveAsync(
        string transportHost,
        int transportPort,
        string logicalHost,
        int logicalPort,
        SshCryptoPolicy cryptoPolicy,
        int probeTimeoutMs,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalHost);
        ct.ThrowIfCancellationRequested();

        PlinkHostKeyPresentation? presentation = await ProbeAsync(
                transportHost,
                transportPort,
                cryptoPolicy,
                probeTimeoutMs,
                ct)
            .ConfigureAwait(false);

        byte[]? presentedBlob = null;
        if (presentation is not null)
        {
            presentedBlob = DecodeBlob(presentation.PublicKeyBase64);
            if (presentedBlob is null || !FingerprintMatches(presentedBlob, presentation.Fingerprint))
            {
                FileLogger.Warn(
                    $"Native OpenSSH host key probe for {logicalHost}:{logicalPort} returned a blob that does not match its fingerprint; refusing.");
                return OpenSshHostKeyResolution.Rejected(OpenSshHostKeyFailure.Unavailable);
            }

            OpenSshHostKeyResolution? early = EvaluatePresentedKey(
                logicalHost,
                logicalPort,
                presentation,
                presentedBlob);
            if (early is not null)
            {
                return early;
            }
        }

        string? storedFingerprint = _trustService.GetEffectiveEntry(logicalHost, logicalPort)?.Fingerprint;
        bool isViaGateway = !string.Equals(transportHost, logicalHost, StringComparison.OrdinalIgnoreCase)
            || transportPort != logicalPort;
        PlinkHostKeyDecision decision = await PlinkHostKeyDecider.DecideAsync(
                logicalHost,
                logicalPort,
                isViaGateway,
                storedFingerprint,
                _ => Task.FromResult(presentation),
                _verifier,
                _trustService,
                ct)
            .ConfigureAwait(false);
        if (!decision.ShouldProceed || decision.Fingerprint is null)
        {
            return MapRejection(decision);
        }

        if (presentation is not null && presentedBlob is not null)
        {
            return BuildFromPresentedKey(logicalHost, logicalPort, presentation, presentedBlob, decision.Fingerprint);
        }

        return BuildFromStoredPins(logicalHost, logicalPort, decision.Fingerprint);
    }

    /// <summary>
    /// Revocation, key type and certificate checks on a presented key, before any dialog.
    /// Returns a terminal resolution, or null when the decision must go on to the trust decider.
    /// </summary>
    private OpenSshHostKeyResolution? EvaluatePresentedKey(
        string host,
        int port,
        PlinkHostKeyPresentation presentation,
        byte[] blob)
    {
        if (_trustService.IsRevoked(host, port, presentation.Fingerprint))
        {
            return OpenSshHostKeyResolution.Rejected(
                OpenSshHostKeyFailure.Revoked,
                presentedFingerprint: presentation.Fingerprint);
        }

        string? pinnedType = OpenSshHostKeyTypes.ResolvePinnedKeyType(blob);
        if (!OpenSshHostKeyTypes.IsSupported(pinnedType))
        {
            return OpenSshHostKeyResolution.Rejected(
                OpenSshHostKeyFailure.UnsupportedKeyType,
                presentedFingerprint: presentation.Fingerprint);
        }

        HostCertificateVerificationStatus certStatus = OpenSshCertificateVerifier.VerifyHostCertificate(
            blob,
            host,
            port,
            _hostKeyStore);
        switch (certStatus)
        {
            case HostCertificateVerificationStatus.Revoked:
                return OpenSshHostKeyResolution.Rejected(
                    OpenSshHostKeyFailure.Revoked,
                    presentedFingerprint: presentation.Fingerprint);
            case HostCertificateVerificationStatus.InvalidForTrustedCertAuthority:
                return OpenSshHostKeyResolution.Rejected(
                    OpenSshHostKeyFailure.CertificateInvalid,
                    presentedFingerprint: presentation.Fingerprint);
            case HostCertificateVerificationStatus.TrustedByCertAuthority:
                // Trusted through a configured CA: no dialog, and the key written is the subject key.
                return BuildFromPresentedKey(host, port, presentation, blob, presentation.Fingerprint);
            default:
                return null;
        }
    }

    private OpenSshHostKeyResolution BuildFromPresentedKey(
        string host,
        int port,
        PlinkHostKeyPresentation presentation,
        byte[] blob,
        string decidedFingerprint)
    {
        KeyBuild build = TryBuildKey(blob, decidedFingerprint);
        if (build.Failure != OpenSshHostKeyFailure.None || build.Key is null)
        {
            return OpenSshHostKeyResolution.Rejected(
                build.Failure,
                presentedFingerprint: presentation.Fingerprint);
        }

        BackFillBlob(host, port, decidedFingerprint, presentation.PublicKeyBase64);
        return OpenSshHostKeyResolution.Success([build.Key], build.IsCertificate);
    }

    /// <summary>
    /// The probe failed and the decider proceeded on the stored pin: every non-revoked pinned
    /// entry of the logical host that carries a blob is written, one line each. The decided
    /// fingerprint must be one of them, so ssh never trusts less or more than Heimdall decided.
    /// </summary>
    private OpenSshHostKeyResolution BuildFromStoredPins(string host, int port, string decidedFingerprint)
    {
        var keys = new List<OpenSshKnownHostsKey>();
        bool certificatePresented = false;
        bool decidedCovered = false;
        foreach (HostKeyEntry entry in CollectPinnedEntries(host, port))
        {
            if (string.IsNullOrWhiteSpace(entry.PublicKeyBase64)
                || _trustService.IsRevoked(host, port, entry.Fingerprint))
            {
                continue;
            }

            bool isDecided = FingerprintsEqual(entry.Fingerprint, decidedFingerprint);
            byte[]? blob = DecodeBlob(entry.PublicKeyBase64);
            if (blob is null)
            {
                FileLogger.Warn($"Pinned host key blob for {host}:{port} is not valid base64; refusing.");
                return OpenSshHostKeyResolution.Rejected(OpenSshHostKeyFailure.Unavailable);
            }

            KeyBuild build = TryBuildKey(blob, entry.Fingerprint);
            if (build.Failure == OpenSshHostKeyFailure.UnsupportedKeyType && !isDecided)
            {
                // An extra pinned key of a type ssh cannot be pinned to is simply not offered.
                continue;
            }

            if (build.Failure != OpenSshHostKeyFailure.None || build.Key is null)
            {
                return OpenSshHostKeyResolution.Rejected(build.Failure, presentedFingerprint: null);
            }

            keys.Add(build.Key);
            certificatePresented |= build.IsCertificate;
            decidedCovered |= isDecided;
        }

        if (keys.Count == 0 || !decidedCovered)
        {
            // A legacy pin that never stored its blob: nothing to give ssh, so nothing to launch.
            FileLogger.Warn(
                $"No stored host key blob for {host}:{port}; refusing to launch native OpenSSH on a fingerprint alone.");
            return OpenSshHostKeyResolution.Rejected(OpenSshHostKeyFailure.Unavailable);
        }

        return OpenSshHostKeyResolution.Success(keys, certificatePresented);
    }

    private List<HostKeyEntry> CollectPinnedEntries(string host, int port)
    {
        var entries = new List<HostKeyEntry>();

        void AddDistinct(HostKeyEntry? entry)
        {
            if (entry is not null
                && !entries.Exists(existing => FingerprintsEqual(existing.Fingerprint, entry.Fingerprint)))
            {
                entries.Add(entry);
            }
        }

        AddDistinct(_trustService.GetEffectiveEntry(host, port));
        AddDistinct(_trustService.GetEntry(host, port));
        string hostPortKey = HostKeyFormats.MakeKey(host, port);
        foreach ((string hostPort, HostKeyEntry entry, bool isAdditional) in _trustService.GetAllAlgorithmEntries())
        {
            if (isAdditional && string.Equals(hostPort, hostPortKey, StringComparison.Ordinal))
            {
                AddDistinct(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// When the decider matched a stored pin that never kept its blob, stores the blob the server
    /// just presented under the same fingerprint, so a later probe failure can still launch.
    /// </summary>
    private void BackFillBlob(string host, int port, string fingerprint, string? blobBase64)
    {
        if (string.IsNullOrWhiteSpace(blobBase64))
        {
            return;
        }

        HostKeyEntry? primary = _trustService.GetEntry(host, port);
        if (primary is not null
            && string.IsNullOrWhiteSpace(primary.PublicKeyBase64)
            && FingerprintsEqual(primary.Fingerprint, fingerprint))
        {
            _trustService.Trust(host, port, primary.Fingerprint, primary.Algorithm, primary.Source, blobBase64);
        }

        string hostPortKey = HostKeyFormats.MakeKey(host, port);
        foreach ((string hostPort, HostKeyEntry entry, bool isAdditional) in _trustService.GetAllAlgorithmEntries())
        {
            if (isAdditional
                && string.Equals(hostPort, hostPortKey, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(entry.PublicKeyBase64)
                && FingerprintsEqual(entry.Fingerprint, fingerprint))
            {
                _trustService.TrustAdditionalAlgorithm(
                    host,
                    port,
                    entry.Fingerprint,
                    entry.Algorithm,
                    entry.Source,
                    blobBase64);
            }
        }
    }

    private readonly record struct KeyBuild(
        OpenSshKnownHostsKey? Key,
        bool IsCertificate,
        OpenSshHostKeyFailure Failure);

    /// <summary>
    /// Turns a blob into the line ssh will be given: checks the blob against the fingerprint
    /// Heimdall decided, takes the subject key out of a certificate, and enforces the key-type
    /// allow-list on what is actually written.
    /// </summary>
    private static KeyBuild TryBuildKey(byte[] blob, string decidedFingerprint)
    {
        if (!FingerprintMatches(blob, decidedFingerprint))
        {
            FileLogger.Warn("A host key blob does not match the fingerprint Heimdall decided; refusing to write it.");
            return new KeyBuild(null, false, OpenSshHostKeyFailure.Unavailable);
        }

        string? blobType = OpenSshHostKeyTypes.ReadBlobKeyType(blob);
        if (blobType is null)
        {
            return new KeyBuild(null, false, OpenSshHostKeyFailure.UnsupportedKeyType);
        }

        if (!OpenSshCertificateParser.IsCertificateKeyType(blobType))
        {
            return OpenSshHostKeyTypes.IsSupported(blobType)
                ? new KeyBuild(new OpenSshKnownHostsKey(blobType, Convert.ToBase64String(blob)), false, OpenSshHostKeyFailure.None)
                : new KeyBuild(null, false, OpenSshHostKeyFailure.UnsupportedKeyType);
        }

        if (!OpenSshCertificateParser.TryParseBlob(blob, out OpenSshCertificateInfo? cert)
            || cert?.SubjectPublicKeyBlob is not { } subjectBlob
            || !OpenSshHostKeyTypes.IsSupported(cert.UnderlyingKeyType))
        {
            return new KeyBuild(null, true, OpenSshHostKeyFailure.UnsupportedKeyType);
        }

        if (cert.SubjectPublicKeyFingerprint is null
            || !FingerprintMatches(subjectBlob, cert.SubjectPublicKeyFingerprint))
        {
            FileLogger.Warn("A host certificate subject key does not match its parsed fingerprint; refusing.");
            return new KeyBuild(null, true, OpenSshHostKeyFailure.Unavailable);
        }

        return new KeyBuild(
            new OpenSshKnownHostsKey(cert.UnderlyingKeyType, Convert.ToBase64String(subjectBlob)),
            true,
            OpenSshHostKeyFailure.None);
    }

    private static OpenSshHostKeyResolution MapRejection(PlinkHostKeyDecision decision)
    {
        OpenSshHostKeyFailure failure = decision.FailureCode switch
        {
            SshFailureCode.HostKeyRevoked => OpenSshHostKeyFailure.Revoked,
            SshFailureCode.HostKeyMismatch => OpenSshHostKeyFailure.Mismatch,
            SshFailureCode.Cancelled => OpenSshHostKeyFailure.Cancelled,
            _ => OpenSshHostKeyFailure.Unavailable,
        };
        return OpenSshHostKeyResolution.Rejected(failure, decision.StoredFingerprint, decision.PresentedFingerprint);
    }

    private async Task<PlinkHostKeyPresentation?> ProbeAsync(
        string host,
        int port,
        SshCryptoPolicy cryptoPolicy,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            return await _probe.ProbeAsync(host, port, cryptoPolicy, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Debug($"Native OpenSSH host key probe threw for {host}:{port}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? DecodeBlob(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool FingerprintMatches(byte[] blob, string expectedFingerprint)
        => FingerprintsEqual(HostKeyFormats.ComputeSha256Fingerprint(blob), expectedFingerprint);

    private static bool FingerprintsEqual(string a, string b)
    {
        byte[] aBytes = Encoding.UTF8.GetBytes(a);
        byte[] bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
