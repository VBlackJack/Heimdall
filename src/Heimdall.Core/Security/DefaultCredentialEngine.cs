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

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Heimdall.Core.Localization;

namespace Heimdall.Core.Security;

/// <summary>
/// Credential test result status.
/// </summary>
public enum CredTestStatus
{
    /// <summary>Default or factory credential was accepted.</summary>
    Default,

    /// <summary>Credential was rejected or changed.</summary>
    Changed,

    /// <summary>Credential test failed due to an operational error.</summary>
    Error,

    /// <summary>
    /// The check cannot log in to this service from here, so nothing was tested. Neither a
    /// default credential nor a rejection.
    /// </summary>
    NotSupported
}

/// <summary>
/// Immutable result of a single default-credential test.
/// </summary>
public sealed class CredTestResultDto
{
    public required string Service { get; init; }

    public required int Port { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    public required CredTestStatus Status { get; init; }

    public string? ErrorDetail { get; init; }
}

/// <summary>
/// Pure helpers for default credential scanning.
/// </summary>
public static class DefaultCredentialEngine
{
    public const int ConnectTimeoutMs = 5000;
    public const int RateLimitDelayMs = 1000;
    public const int MaxConcurrentServices = 3;
    public const int PortProbeTimeoutMs = 2000;

    /// <summary>
    /// The services whose login cannot be tested over a direct connection: MySQL and MSSQL are
    /// tested only through a gateway's own client. A greeting alone says nothing about a credential.
    /// </summary>
    private static readonly string[] DirectCheckUnsupportedServices = ["MySQL", "MSSQL"];

    private const string HttpVersionPrefix = "HTTP/";
    private const int HttpStatusDigits = 3;
    private const int HttpFirstSuccessStatus = 200;
    private const int HttpFirstClientErrorStatus = 400;

    private const string FtpLoginAcceptedCode = "230";
    private const int FtpReplyCodeLength = 3;

    private const byte PostgresAuthenticationMessage = (byte)'R';
    private const int PostgresAuthenticationMessageLength = 8;
    private const int PostgresAuthenticationOk = 0;
    private const int PostgresMinReplyBytes = 9;

    private const int VncBannerLength = 12;
    private const string VncBannerPrefix = "RFB ";
    private const int VncSupportedMajor = 3;
    private const int VncMajorStart = 4;
    private const int VncVersionDigits = 3;
    private const int VncDotIndex = 7;
    private const int VncMinorStart = 8;
    private const int VncFirstSecurityTypeListMinor = 7;
    private const int VncFirstVersion38Minor = 8;
    private const byte VncSecurityTypeNone = 1;

    /// <summary>Whether a direct (no gateway) check of the service has no login behind it.</summary>
    public static bool IsDirectCheckUnsupported(string service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return DirectCheckUnsupportedServices.Contains(service, StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks if an HTTP status line indicates successful authentication.
    /// </summary>
    /// <remarks>
    /// The text comes from the scanned host. Only a real status line counts: it starts with
    /// <c>HTTP/</c> and carries a three-digit code. Before, any line whose second word parsed as
    /// 200 to 399 passed, so an SSH or FTP banner reading <c>... 200 ...</c>, or a signed number,
    /// was reported as accepted credentials.
    /// </remarks>
    public static bool IsHttpSuccessResponse(string responseLine)
    {
        if (string.IsNullOrEmpty(responseLine))
        {
            return false;
        }

        // A buffered read holds the status line, the headers and the start of the body.
        var lineEnd = responseLine.IndexOfAny(['\r', '\n']);
        var statusLine = lineEnd < 0 ? responseLine : responseLine[..lineEnd];
        if (!statusLine.StartsWith(HttpVersionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2
            || parts[1].Length != HttpStatusDigits
            || !parts[1].All(char.IsAsciiDigit))
        {
            return false;
        }

        var statusCode = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return statusCode is >= HttpFirstSuccessStatus and < HttpFirstClientErrorStatus;
    }

    /// <summary>
    /// Checks whether FTP output holds a "230" (user logged in) reply: a line that starts with the
    /// code and is followed by a space, a hyphen or the end of the line.
    /// </summary>
    /// <remarks>
    /// A substring search for "230" also matched a refusal that happened to contain the digits
    /// ("530 Login incorrect ... 2300").
    /// </remarks>
    public static bool IsFtpLoginAccepted(string reply)
    {
        if (string.IsNullOrEmpty(reply))
        {
            return false;
        }

        foreach (var rawLine in reply.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(FtpLoginAcceptedCode, StringComparison.Ordinal)
                && (line.Length == FtpReplyCodeLength || line[FtpReplyCodeLength] is ' ' or '-'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the first bytes a PostgreSQL server sent are an AuthenticationOk message:
    /// <c>R</c>, a length of 8 and an authentication code of 0.
    /// </summary>
    public static bool IsPostgresAuthenticationOk(byte[] reply, int length)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (length < PostgresMinReplyBytes || length > reply.Length || reply[0] != PostgresAuthenticationMessage)
        {
            return false;
        }

        var messageLength = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(1, sizeof(int)));
        var code = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(1 + sizeof(int), sizeof(int)));
        return messageLength == PostgresAuthenticationMessageLength && code == PostgresAuthenticationOk;
    }

    /// <summary>
    /// Reads a VNC protocol banner (<c>RFB 003.008</c> and a line feed). Only the first 12 bytes of
    /// the read are looked at; whatever follows is not part of the banner.
    /// </summary>
    /// <param name="reply">The bytes read from the server.</param>
    /// <param name="length">How many of them are valid.</param>
    /// <param name="minor">The minor version when the banner is valid.</param>
    public static bool TryParseVncBanner(byte[] reply, int length, out int minor)
    {
        ArgumentNullException.ThrowIfNull(reply);
        minor = 0;
        if (length < VncBannerLength || length > reply.Length)
        {
            return false;
        }

        var banner = Encoding.ASCII.GetString(reply, 0, VncBannerLength);
        if (!banner.StartsWith(VncBannerPrefix, StringComparison.Ordinal)
            || banner[VncDotIndex] != '.'
            || banner[VncBannerLength - 1] != '\n'
            || banner.AsSpan(VncMajorStart, VncVersionDigits).ContainsAnyExceptInRange('0', '9')
            || banner.AsSpan(VncMinorStart, VncVersionDigits).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        var major = int.Parse(banner.AsSpan(VncMajorStart, VncVersionDigits), CultureInfo.InvariantCulture);
        if (major != VncSupportedMajor)
        {
            return false;
        }

        minor = int.Parse(banner.AsSpan(VncMinorStart, VncVersionDigits), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// The version line a client answers with: the highest one this scanner speaks that the server
    /// offered, so a server announcing a version it has not been told about is still answered.
    /// </summary>
    public static byte[] BuildVncVersionReply(int serverMinor)
    {
        var minor = serverMinor >= VncFirstVersion38Minor
            ? VncFirstVersion38Minor
            : serverMinor >= VncFirstSecurityTypeListMinor ? VncFirstSecurityTypeListMinor : 3;
        return Encoding.ASCII.GetBytes($"{VncBannerPrefix}00{VncSupportedMajor}.00{minor}\n");
    }

    /// <summary>
    /// Checks whether the security handshake offers "None" (no password). From 3.7 the server sends a
    /// count and a list of types; 3.3 sends the single chosen type as four bytes.
    /// </summary>
    public static bool IsVncNoAuthOffered(byte[] reply, int length, int serverMinor)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (length < 1 || length > reply.Length)
        {
            return false;
        }

        if (serverMinor < VncFirstSecurityTypeListMinor)
        {
            return length >= sizeof(uint)
                && BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(0, sizeof(uint))) == VncSecurityTypeNone;
        }

        var count = reply[0];
        for (var index = 1; index <= count && index < length; index++)
        {
            if (reply[index] == VncSecurityTypeNone)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a summary line for the current results.
    /// </summary>
    /// <param name="results">The scan results.</param>
    /// <param name="localize">Maps a key to its text; keys are returned as-is when omitted.</param>
    /// <param name="countLocalizer">
    /// Words the counts in the language of <paramref name="localize"/>; when omitted, through
    /// <paramref name="localize"/> under the English rule.
    /// </param>
    public static string BuildSummaryText(
        IReadOnlyList<CredTestResultDto> results,
        Func<string, string>? localize = null,
        ICountLocalizer? countLocalizer = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var defaultCount = results.Count(r => r.Status == CredTestStatus.Default);
        var serviceCount = results
            .Where(r => r.Status == CredTestStatus.Default)
            .Select(r => $"{r.Service}:{r.Port}")
            .Distinct(StringComparer.Ordinal)
            .Count();

        bool anyNotTested = results.Any(r => r.Status == CredTestStatus.NotSupported);
        if (defaultCount == 0)
        {
            // "No defaults" would claim more than was tested when some checks could not run.
            return anyNotTested
                ? L(localize, "ToolDefCredNoDefaultsPartial")
                : L(localize, "ToolDefCredNoDefaults");
        }

        ICountLocalizer counts = countLocalizer ?? DelegateCountLocalizer.English(key => L(localize, key));
        string summary = string.Format(
            L(localize, "ToolDefCredSummary"),
            counts.FormatCount(
                defaultCount,
                "ToolDefCredSummaryCredentialsOne",
                "ToolDefCredSummaryCredentials",
                defaultCount),
            counts.FormatCount(serviceCount, "ToolDefCredSummaryServicesOne", "ToolDefCredSummaryServices", serviceCount));
        return anyNotTested ? summary + " " + L(localize, "ToolDefCredSummaryNotTested") : summary;
    }

    /// <summary>
    /// Builds a plain-text tabular report for clipboard export.
    /// </summary>
    public static string BuildReportText(
        IReadOnlyList<CredTestResultDto> results,
        Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine(string.Format(
            "{0,-12}{1,-8}{2,-16}{3,-16}{4,-12}{5}",
            L(localize, "ToolDefCredColService"),
            L(localize, "ToolDefCredColPort"),
            L(localize, "ToolDefCredColUser"),
            L(localize, "ToolDefCredColPass"),
            L(localize, "ToolDefCredColStatus"),
            L(localize, "ToolDefCredColDetail")));
        builder.AppendLine(new string('-', 80));

        foreach (var result in results)
        {
            var statusLabel = StatusToLabel(result.Status, localize);
            var detail = result.Status == CredTestStatus.Error
                ? result.ErrorDetail ?? string.Empty
                : StatusToDetail(result.Status, result.Service, localize);
            builder.AppendLine(string.Format(
                "{0,-12}{1,-8}{2,-16}{3,-16}{4,-12}{5}",
                result.Service,
                result.Port,
                result.Username,
                result.Password,
                statusLabel,
                detail));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds CSV content for export.
    /// </summary>
    public static string BuildCsvExport(
        IReadOnlyList<CredTestResultDto> results,
        Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",",
            L(localize, "ToolDefCredColService"),
            L(localize, "ToolDefCredColPort"),
            L(localize, "ToolDefCredColUser"),
            L(localize, "ToolDefCredColPass"),
            L(localize, "ToolDefCredColStatus"),
            L(localize, "ToolDefCredColDetail")));

        foreach (var result in results)
        {
            var detail = result.Status == CredTestStatus.Error
                ? result.ErrorDetail ?? string.Empty
                : StatusToDetail(result.Status, result.Service, localize);

            builder.AppendLine(string.Join(",",
                EscapeCsv(result.Service),
                result.Port.ToString(),
                EscapeCsv(result.Username),
                EscapeCsv(result.Password),
                EscapeCsv(StatusToLabel(result.Status, localize)),
                EscapeCsv(detail)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the localized label for a result status.
    /// </summary>
    public static string StatusToLabel(
        CredTestStatus status,
        Func<string, string>? localize = null)
    {
        return status switch
        {
            CredTestStatus.Default => L(localize, "ToolDefCredStatusDefault"),
            CredTestStatus.Changed => L(localize, "ToolDefCredStatusChanged"),
            CredTestStatus.Error => L(localize, "ToolDefCredStatusError"),
            CredTestStatus.NotSupported => L(localize, "ToolDefCredStatusNotSupported"),
            _ => status.ToString()
        };
    }

    /// <summary>
    /// Returns the localized detail text for a non-error status.
    /// </summary>
    public static string StatusToDetail(
        CredTestStatus status,
        string service,
        Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        return status switch
        {
            CredTestStatus.Default => string.Format(
                L(localize, "ToolDefCredDetailAccepted"),
                service),
            CredTestStatus.Changed => string.Format(
                L(localize, "ToolDefCredDetailRejected"),
                service),
            CredTestStatus.NotSupported => string.Format(
                L(localize, "ToolDefCredDetailNotSupported"),
                service),
            _ => string.Empty
        };
    }

    private static string EscapeCsv(string value)
    {
        var sanitized = InputValidator.SanitizeCsvCell(value)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{sanitized}\"";
    }

    private static string L(Func<string, string>? localize, string key)
        => localize?.Invoke(key) ?? key;
}
