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
using System.Security;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.Core.Ssh;

/// <summary>Proactive expiry assessment for an OpenSSH user certificate on connect.</summary>
public enum SshCertificateExpiryStatus
{
    None = 0,
    Valid = 1,
    ExpiringSoon = 2,
    Expired = 3,
    NotYetValid = 4
}

/// <summary>Result of evaluating a local OpenSSH user certificate's validity window.</summary>
public sealed record SshCertificateExpiryAssessment(
    SshCertificateExpiryStatus Status,
    DateTimeOffset? ValidBeforeUtc,
    int DaysRemaining,
    string? ResolvedPath,
    DateTimeOffset? ValidAfterUtc = null)
{
    public static readonly SshCertificateExpiryAssessment Empty =
        new(SshCertificateExpiryStatus.None, null, 0, null);
}

/// <summary>
/// Evaluates local OpenSSH user certificates (explicit or sibling <c>-cert.pub</c>) to warn users
/// proactively when a certificate is expired or nearing expiration.
/// </summary>
public static class SshCertificateExpiryEvaluator
{
    public const int ExpiringSoonDays = 30;
    public const long MaxCertificateBytes = 64 * 1024;

    /// <summary>
    /// Resolves and evaluates a local OpenSSH user certificate without touching network shares or device paths.
    /// </summary>
    public static SshCertificateExpiryAssessment Evaluate(
        string? keyPath,
        string? explicitCertPath,
        DateTimeOffset? nowUtc = null)
    {
        string? candidate = ResolveLocalCertificatePath(keyPath, explicitCertPath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return SshCertificateExpiryAssessment.Empty;
        }

        if (ImportedPathPolicy.IsNetworkOrDevicePath(candidate))
        {
            return SshCertificateExpiryAssessment.Empty;
        }

        try
        {
            var info = new FileInfo(candidate);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxCertificateBytes)
            {
                return SshCertificateExpiryAssessment.Empty;
            }

            string content = File.ReadAllText(candidate, Encoding.UTF8);
            return EvaluateContent(content, candidate, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return SshCertificateExpiryAssessment.Empty;
        }
    }

    /// <summary>Pure parser/evaluator for OpenSSH certificate text content.</summary>
    public static SshCertificateExpiryAssessment EvaluateContent(
        string? certContent,
        string? resolvedPath = null,
        DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(certContent)
            || !OpenSshCertificateParser.TryParsePublicLine(certContent, out OpenSshCertificateInfo? cert)
            || cert is null
            || cert.Type != OpenSshCertificateType.User)
        {
            return SshCertificateExpiryAssessment.Empty;
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        if (cert.ValidBefore is { } validBefore && validBefore <= now)
        {
            return new SshCertificateExpiryAssessment(SshCertificateExpiryStatus.Expired, validBefore, 0, resolvedPath, cert.ValidAfter);
        }

        if (cert.ValidAfter is { } validAfter && validAfter > now)
        {
            return new SshCertificateExpiryAssessment(SshCertificateExpiryStatus.NotYetValid, cert.ValidBefore, 0, resolvedPath, validAfter);
        }

        if (cert.ValidBefore is not { } validBeforeEnd)
        {
            return new SshCertificateExpiryAssessment(SshCertificateExpiryStatus.Valid, null, int.MaxValue, resolvedPath, cert.ValidAfter);
        }

        if (validBeforeEnd <= now.AddDays(ExpiringSoonDays))
        {
            int daysLeft = Math.Max(1, (int)Math.Ceiling((validBeforeEnd - now).TotalDays));
            return new SshCertificateExpiryAssessment(SshCertificateExpiryStatus.ExpiringSoon, validBeforeEnd, daysLeft, resolvedPath, cert.ValidAfter);
        }

        int remaining = Math.Max(ExpiringSoonDays + 1, (int)Math.Ceiling((validBeforeEnd - now).TotalDays));
        return new SshCertificateExpiryAssessment(SshCertificateExpiryStatus.Valid, validBeforeEnd, remaining, resolvedPath, cert.ValidAfter);
    }

    /// <summary>Formats a localized non-blocking status warning when the certificate is expired, not yet valid, or expiring soon.</summary>
    public static string? FormatLocalizedWarning(
        SshCertificateExpiryAssessment assessment,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(localizer);

        if (assessment.Status == SshCertificateExpiryStatus.NotYetValid
            && assessment.ValidAfterUtc is { } validAfter)
        {
            string startDateText = validAfter.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return localizer.Format("SshCertWarningNotYetValid", startDateText);
        }

        if (assessment.ValidBeforeUtc is not { } validBefore)
        {
            return null;
        }

        string dateText = validBefore.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return assessment.Status switch
        {
            SshCertificateExpiryStatus.Expired => localizer.Format("SshCertWarningExpired", dateText),
            SshCertificateExpiryStatus.ExpiringSoon => localizer.FormatCount(
                assessment.DaysRemaining,
                "SshCertWarningExpiringSoonOne",
                "SshCertWarningExpiringSoon",
                assessment.DaysRemaining,
                dateText),
            _ => null
        };
    }

    private static string? ResolveLocalCertificatePath(string? keyPath, string? explicitCertPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitCertPath))
        {
            return explicitCertPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return null;
        }

        string trimmedKey = keyPath.Trim();
        return trimmedKey.EndsWith("-cert.pub", StringComparison.OrdinalIgnoreCase)
            ? trimmedKey
            : trimmedKey + "-cert.pub";
    }
}
