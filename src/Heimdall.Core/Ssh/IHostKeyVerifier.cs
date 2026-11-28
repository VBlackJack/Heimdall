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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Decision returned by <see cref="IHostKeyVerifier"/>.
/// </summary>
public enum HostKeyDecision
{
    Accept,
    TrustOnce,
    Reject
}

/// <summary>
/// Asks the user to verify an SSH host key before it is trusted.
/// Implementations may marshal to the UI thread internally; callers must invoke
/// them before SSH.NET's synchronous host key callback.
/// </summary>
public interface IHostKeyVerifier
{
    /// <summary>
    /// Requests a trust decision for the presented SSH host key.
    /// </summary>
    /// <param name="host">Remote host.</param>
    /// <param name="port">Remote port.</param>
    /// <param name="algorithm">Presented key algorithm.</param>
    /// <param name="presentedFingerprint">Presented SHA256 fingerprint.</param>
    /// <param name="storedFingerprint">
    /// Previously trusted fingerprint, or null on first use.
    /// </param>
    /// <param name="ct">Cancellation support.</param>
    /// <returns>The user's trust decision.</returns>
    Task<HostKeyDecision> VerifyAsync(
        string host,
        int port,
        string algorithm,
        string presentedFingerprint,
        string? storedFingerprint,
        CancellationToken ct = default);

    /// <summary>
    /// Requests a trust decision with optional access to the raw host-key blob (for DNS SSHFP
    /// SHA-1/SHA-256 verification) and gateway routing indicator (to suppress split-horizon DNS leaks).
    /// </summary>
    Task<HostKeyDecision> VerifyAsync(
        string host,
        int port,
        string algorithm,
        string presentedFingerprint,
        string? storedFingerprint,
        ReadOnlyMemory<byte> hostKeyBlob,
        bool isViaGateway = false,
        CancellationToken ct = default)
        => VerifyAsync(host, port, algorithm, presentedFingerprint, storedFingerprint, ct);
}
