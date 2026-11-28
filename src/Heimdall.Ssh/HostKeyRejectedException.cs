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

namespace Heimdall.Ssh;

/// <summary>
/// Categorizes why a presented SSH host key or certificate was rejected.
/// </summary>
public enum HostKeyRejectionReason
{
    UserRejected,
    Mismatch,
    Revoked,
    CertificateInvalid
}

/// <summary>
/// Raised when an SSH host key is explicitly rejected by the verifier or policy.
/// </summary>
public sealed class HostKeyRejectedException : Exception
{
    public HostKeyRejectedException(
        string host,
        int port,
        string algorithm,
        string presentedFingerprint,
        string? storedFingerprint,
        HostKeyRejectionReason? reason = null)
        : base(CreateMessage(
            host,
            port,
            presentedFingerprint,
            storedFingerprint,
            ResolveReason(storedFingerprint, reason)))
    {
        Host = host;
        Port = port;
        Algorithm = algorithm;
        PresentedFingerprint = presentedFingerprint;
        StoredFingerprint = storedFingerprint;
        Reason = ResolveReason(storedFingerprint, reason);
    }

    public string Host { get; }

    public int Port { get; }

    public string Algorithm { get; }

    public string PresentedFingerprint { get; }

    public string? StoredFingerprint { get; }

    public HostKeyRejectionReason Reason { get; }

    public bool IsMismatch => Reason == HostKeyRejectionReason.Mismatch;

    private static HostKeyRejectionReason ResolveReason(
        string? storedFingerprint,
        HostKeyRejectionReason? reason)
    {
        return reason ?? (!string.IsNullOrWhiteSpace(storedFingerprint)
            ? HostKeyRejectionReason.Mismatch
            : HostKeyRejectionReason.UserRejected);
    }

    private static string CreateMessage(
        string host,
        int port,
        string presentedFingerprint,
        string? storedFingerprint,
        HostKeyRejectionReason reason)
    {
        return reason switch
        {
            HostKeyRejectionReason.Revoked =>
                $"SSH host key for {host}:{port} is explicitly revoked by policy. Presented fingerprint: {presentedFingerprint}.",
            HostKeyRejectionReason.CertificateInvalid =>
                $"SSH host certificate for {host}:{port} failed validation for a trusted CA. Presented fingerprint: {presentedFingerprint}.",
            HostKeyRejectionReason.Mismatch =>
                $"SSH host key mismatch for {host}:{port}. Stored fingerprint {storedFingerprint} differs from presented fingerprint {presentedFingerprint}.",
            _ =>
                $"SSH host key was rejected for {host}:{port}. Presented fingerprint: {presentedFingerprint}."
        };
    }
}
