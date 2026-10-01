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
    Error
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
    /// Checks if an HTTP status line indicates successful authentication.
    /// </summary>
    public static bool IsHttpSuccessResponse(string responseLine)
    {
        if (string.IsNullOrEmpty(responseLine))
        {
            return false;
        }

        var parts = responseLine.Split(' ', 3);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var statusCode))
        {
            return statusCode is >= 200 and < 400;
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

        if (defaultCount == 0)
        {
            return L(localize, "ToolDefCredNoDefaults");
        }

        ICountLocalizer counts = countLocalizer ?? DelegateCountLocalizer.English(key => L(localize, key));
        return string.Format(
            L(localize, "ToolDefCredSummary"),
            counts.FormatCount(
                defaultCount,
                "ToolDefCredSummaryCredentialsOne",
                "ToolDefCredSummaryCredentials",
                defaultCount),
            counts.FormatCount(serviceCount, "ToolDefCredSummaryServicesOne", "ToolDefCredSummaryServices", serviceCount));
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
