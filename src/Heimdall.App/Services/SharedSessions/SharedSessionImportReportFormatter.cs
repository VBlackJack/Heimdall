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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Words an import report in the interface language: the label of each check, the explanation of
/// each entry, and the plain text and CSV forms the report dialog copies and exports.
/// </summary>
public sealed class SharedSessionImportReportFormatter
{
    private const string CsvLineEnd = "\r\n";
    private const string CsvSeparator = ",";
    private const string Quote = "\"";

    private readonly LocalizationManager _localizer;

    public SharedSessionImportReportFormatter(LocalizationManager localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>The checks, in the order the report lists them.</summary>
    public static IReadOnlyList<SharedSessionReportCheck> Checks { get; } = Enum.GetValues<SharedSessionReportCheck>();

    /// <summary>The name of <paramref name="check"/>.</summary>
    public string CheckLabel(SharedSessionReportCheck check) => _localizer[check switch
    {
        SharedSessionReportCheck.SkippedSession => "SharedReportCheckSkipped",
        SharedSessionReportCheck.UnresolvedVariable => "SharedReportCheckUnresolved",
        SharedSessionReportCheck.NameHostMismatch => "SharedReportCheckNameHost",
        SharedSessionReportCheck.GatewayOutlier => "SharedReportCheckGatewayOutlier",
        SharedSessionReportCheck.DuplicateHost => "SharedReportCheckDuplicateHost",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    }];

    /// <summary>The folder of an entry as the report shows it; the top of the file has a name of its own.</summary>
    public string FolderLabel(string folderPath) =>
        string.IsNullOrWhiteSpace(folderPath) ? _localizer["SharedReportTopFolder"] : folderPath;

    /// <summary>What <paramref name="entry"/> found, in one sentence.</summary>
    public string Explain(SharedSessionReportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Check switch
        {
            SharedSessionReportCheck.SkippedSession => ExplainSkip(entry),
            SharedSessionReportCheck.UnresolvedVariable => _localizer.Format("SharedReportUnresolvedExplain", entry.Detail),
            SharedSessionReportCheck.NameHostMismatch => _localizer.Format("SharedReportNameHostExplain", entry.SessionName, entry.Detail),
            SharedSessionReportCheck.GatewayOutlier => _localizer.Format(
                "SharedReportGatewayOutlierExplain",
                RouteLabel(entry.Detail),
                RouteLabel(entry.Context)),
            SharedSessionReportCheck.DuplicateHost => _localizer.Format("SharedReportDuplicateHostExplain", entry.Detail, entry.Context),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Check, null),
        };
    }

    /// <summary>"3 findings": the total of a report, worded by its number.</summary>
    public string TotalText(SharedSessionImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return _localizer.FormatCount(report.Count, "SharedReportTotalOne", "SharedReportTotal", report.Count);
    }

    /// <summary>The report as plain text: the title, the counts per check, then every entry by check.</summary>
    public string ToPlainText(SharedSessionImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        text.AppendLine(_localizer.Format("SharedReportDialogTitle", report.SourceName));
        text.AppendLine(TotalText(report));
        foreach (SharedSessionReportCheck check in Checks)
        {
            text.AppendLine(_localizer.Format("SharedReportSummaryLine", CheckLabel(check), report.CountOf(check)));
        }

        foreach (SharedSessionReportCheck check in Checks)
        {
            List<SharedSessionReportEntry> entries = [.. report.Entries.Where(entry => entry.Check == check)];
            if (entries.Count == 0)
            {
                continue;
            }

            text.AppendLine();
            text.AppendLine(_localizer.Format("SharedReportSummaryLine", CheckLabel(check), entries.Count));
            foreach (SharedSessionReportEntry entry in entries)
            {
                text.AppendLine(_localizer.Format(
                    "SharedReportPlainTextEntry",
                    FolderLabel(entry.FolderPath),
                    entry.SessionName,
                    Explain(entry)));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The report as CSV: one row per entry with its check, folder, session, detail and
    /// explanation. Every cell is neutralized against formula injection before it is quoted, since
    /// the file names and hosts come from a file other people edit.
    /// </summary>
    public string ToCsv(SharedSessionImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var csv = new StringBuilder();
        AppendRow(
            csv,
            _localizer["SharedReportColumnCheck"],
            _localizer["SharedReportColumnFolder"],
            _localizer["SharedReportColumnSession"],
            _localizer["SharedReportColumnDetail"],
            _localizer["SharedReportColumnExplanation"]);
        foreach (SharedSessionReportEntry entry in report.Entries)
        {
            AppendRow(csv, CheckLabel(entry.Check), FolderLabel(entry.FolderPath), entry.SessionName, entry.Detail, Explain(entry));
        }

        return csv.ToString();
    }

    private static void AppendRow(StringBuilder csv, params string[] cells)
    {
        csv.Append(string.Join(CsvSeparator, cells.Select(cell => Quoted(InputValidator.SanitizeCsvCell(cell)))));
        csv.Append(CsvLineEnd);
    }

    private static string Quoted(string value) =>
        Quote + value.Replace(Quote, Quote + Quote, StringComparison.Ordinal) + Quote;

    private string RouteLabel(string route) =>
        string.IsNullOrEmpty(route) ? _localizer["SharedReportDirectConnection"] : route;

    private string ExplainSkip(SharedSessionReportEntry entry) => entry.SkipReason switch
    {
        MobaXtermImportWarningKind.MalformedSessionLine => _localizer["SharedReportSkipMalformed"],
        MobaXtermImportWarningKind.UnsupportedSessionType => _localizer.Format("SharedReportSkipUnsupportedType", entry.Detail),
        MobaXtermImportWarningKind.MissingHost => _localizer["SharedReportSkipMissingHost"],
        MobaXtermImportWarningKind.InvalidGatewayHost => _localizer.Format("SharedReportSkipInvalidGateway", entry.Detail),
        MobaXtermImportWarningKind.DuplicateSession => _localizer["SharedReportSkipDuplicate"],
        MobaXtermImportWarningKind.ParseError => _localizer.Format("SharedReportSkipParseError", entry.Detail),
        _ => _localizer["SharedReportSkipRefused"],
    };
}
