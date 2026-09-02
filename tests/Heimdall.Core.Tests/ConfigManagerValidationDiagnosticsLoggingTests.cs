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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

/// <summary>
/// A settings refresh over a large inventory must not spend one log line on
/// five hundred identically shaped diagnostics, and must not lose any of them.
/// </summary>
public sealed class ConfigManagerValidationDiagnosticsLoggingTests
{
    private const string DocumentName = "servers.json";

    private static List<ValidationDiagnostic> Diagnostics(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new ValidationDiagnostic(
            ValidationSeverity.Warning,
            $"Servers[{index}].RemoteServer: invalid hostname or IP address"))];

    [Fact]
    public void ManyDiagnostics_TheWarningLineCountsThemAndQuotesOnlyAFewOfThem()
    {
        List<ValidationDiagnostic> diagnostics = Diagnostics(500);

        string summary = ConfigManager.BuildValidationDiagnosticsSummary(DocumentName, diagnostics);

        Assert.Contains("500", summary, StringComparison.Ordinal);
        Assert.Contains("Servers[0].RemoteServer", summary, StringComparison.Ordinal);
        Assert.Contains("Servers[4].RemoteServer", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Servers[5].RemoteServer", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Servers[499].RemoteServer", summary, StringComparison.Ordinal);

        // The old single line ran to tens of thousands of characters; the point
        // of the change is that the line stays readable next to its neighbours.
        Assert.True(
            summary.Length < 1000,
            $"The warning line is still too long to read ({summary.Length} characters).");
    }

    [Fact]
    public void ManyDiagnostics_TheDebugLineStillCarriesEveryOne()
    {
        List<ValidationDiagnostic> diagnostics = Diagnostics(500);

        string detail = ConfigManager.BuildValidationDiagnosticsDetail(DocumentName, diagnostics);

        Assert.Contains("Servers[0].RemoteServer", detail, StringComparison.Ordinal);
        Assert.Contains("Servers[250].RemoteServer", detail, StringComparison.Ordinal);
        Assert.Contains("Servers[499].RemoteServer", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FewDiagnostics_AreAllQuotedInTheWarningLine()
    {
        List<ValidationDiagnostic> diagnostics = Diagnostics(ConfigManager.MaxQuotedValidationDiagnostics);

        string summary = ConfigManager.BuildValidationDiagnosticsSummary(DocumentName, diagnostics);

        for (int index = 0; index < ConfigManager.MaxQuotedValidationDiagnostics; index++)
        {
            Assert.Contains($"Servers[{index}].RemoteServer", summary, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("more at Debug", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySummarizedDiagnosticKeepsItsSeverity()
    {
        List<ValidationDiagnostic> diagnostics =
        [
            new ValidationDiagnostic(ValidationSeverity.Error, "Servers[0].Id: missing"),
            new ValidationDiagnostic(ValidationSeverity.Warning, "Servers[1].RemoteServer: invalid")
        ];

        string summary = ConfigManager.BuildValidationDiagnosticsSummary(DocumentName, diagnostics);

        Assert.Contains("[Error] Servers[0].Id: missing", summary, StringComparison.Ordinal);
        Assert.Contains("[Warning] Servers[1].RemoteServer: invalid", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnostics arrive in document order, so the one that blocks the next
    /// write can sit behind five harmless warnings. Quoting by position buries
    /// it in the Debug line, which is the burial this warning line was shortened
    /// to prevent.
    /// </summary>
    [Fact]
    public void AnErrorBehindFiveWarnings_IsStillQuotedInTheWarningLine()
    {
        List<ValidationDiagnostic> diagnostics =
        [
            .. Diagnostics(ConfigManager.MaxQuotedValidationDiagnostics),
            new ValidationDiagnostic(
                ValidationSeverity.Error,
                "SshGateways[5].ParentGatewayId: gateway cannot be its own parent.")
        ];

        string summary = ConfigManager.BuildValidationDiagnosticsSummary("settings.json", diagnostics);

        Assert.Contains(
            "[Error] SshGateways[5].ParentGatewayId: gateway cannot be its own parent.",
            summary,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The quoted slice is the most severe few, not the first few. Calling them
    /// "first" tells the reader that the Error the line quotes was the first
    /// diagnostic in the document and that the ones omitted came after it -
    /// both of which the severity sort makes untrue.
    /// </summary>
    [Fact]
    public void TheWarningLine_NamesTheQuotedSliceBySeverityNotByPosition()
    {
        List<ValidationDiagnostic> diagnostics =
        [
            .. Diagnostics(ConfigManager.MaxQuotedValidationDiagnostics),
            new ValidationDiagnostic(
                ValidationSeverity.Error,
                "SshGateways[5].ParentGatewayId: gateway cannot be its own parent.")
        ];

        string summary = ConfigManager.BuildValidationDiagnosticsSummary("settings.json", diagnostics);

        // The Error the line quotes sat at position 6 of the document and the
        // five it dropped came before it, so "first" is a claim this line
        // cannot make.
        Assert.Contains(
            $"{ConfigManager.MaxQuotedValidationDiagnostics} most severe:",
            summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("first", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Severity decides which diagnostics survive the trim; within one severity
    /// the quoted ones stay in document order, so the line still reads as a
    /// prefix of the file rather than a reshuffle.
    /// </summary>
    [Fact]
    public void WithinOneSeverity_TheQuotedDiagnosticsKeepTheirDocumentOrder()
    {
        List<ValidationDiagnostic> diagnostics = Diagnostics(10);

        string summary = ConfigManager.BuildValidationDiagnosticsSummary(DocumentName, diagnostics);

        int first = summary.IndexOf("Servers[0].RemoteServer", StringComparison.Ordinal);
        int second = summary.IndexOf("Servers[1].RemoteServer", StringComparison.Ordinal);

        Assert.True(first >= 0 && second > first, $"Document order was not preserved: {summary}");
        Assert.DoesNotContain("Servers[5].RemoteServer", summary, StringComparison.Ordinal);
    }
}
