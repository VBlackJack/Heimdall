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

namespace Heimdall.Sftp;

/// <summary>Why an FTPS server certificate was refused.</summary>
public enum FtpsCertificateRejectionReason
{
    /// <summary>The server presented no certificate at all.</summary>
    NotPresented,

    /// <summary>A certificate is pinned for this endpoint and the presented one differs.</summary>
    Mismatch,

    /// <summary>
    /// The presented certificate is the pinned one, but it no longer passes the checks a pin
    /// cannot override: expired, revoked, or a chain whose revocation status cannot be
    /// determined for a pin the system validated.
    /// </summary>
    PinnedCertificateInvalid,

    /// <summary>The user answered the first-use prompt with Reject.</summary>
    RejectedByUser,
}

public sealed class FtpsCertificateRejectedException : Exception
{
    public FtpsCertificateRejectedException(
        string host,
        int port,
        string presentedFingerprint,
        string? storedFingerprint,
        FtpsCertificateRejectionReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Host = host;
        Port = port;
        PresentedFingerprint = presentedFingerprint;
        StoredFingerprint = storedFingerprint;
        Reason = reason;
    }

    public string Host { get; }

    public int Port { get; }

    public string PresentedFingerprint { get; }

    public string? StoredFingerprint { get; }

    /// <summary>
    /// Why the certificate was refused. A pin that expired or was revoked used to be reported
    /// with the same words as a Reject click, and the user could not tell a decision of theirs
    /// from a certificate that had gone bad.
    /// </summary>
    public FtpsCertificateRejectionReason Reason { get; }

    public bool IsMismatch => Reason == FtpsCertificateRejectionReason.Mismatch;
}
