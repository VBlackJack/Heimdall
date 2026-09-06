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

namespace Heimdall.Core.Certificates;

public enum FtpsCertificateDecision
{
    Accept,
    TrustOnce,
    Reject
}

public sealed record FtpsCertificatePrompt(
    string Host,
    int Port,
    string PresentedFingerprint,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string ValidationErrors);

/// <summary>
/// Asks the user to verify an FTPS server certificate before it is trusted.
/// Implementations may marshal to the UI thread internally.
/// </summary>
public interface IFtpsCertificateVerifier
{
    Task<FtpsCertificateDecision> VerifyAsync(
        FtpsCertificatePrompt prompt,
        CancellationToken ct = default);
}

public sealed class RejectingFtpsCertificateVerifier : IFtpsCertificateVerifier
{
    public static RejectingFtpsCertificateVerifier Instance { get; } = new();

    private RejectingFtpsCertificateVerifier()
    {
    }

    public Task<FtpsCertificateDecision> VerifyAsync(
        FtpsCertificatePrompt prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return Task.FromResult(FtpsCertificateDecision.Reject);
    }
}
