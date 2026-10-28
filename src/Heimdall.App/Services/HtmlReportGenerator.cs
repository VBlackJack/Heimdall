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
using System.Net;
using System.Text;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Generates a standalone HTML report for SecNumCloud compliance audits.
/// The output is a single self-contained .html file with embedded CSS,
/// suitable for presentation to auditors and print export.
/// </summary>
public static class HtmlReportGenerator
{
    /// <summary>
    /// Generates the full HTML report from an <see cref="AuditReport"/>.
    /// All user-supplied data is HTML-encoded to prevent XSS.
    /// </summary>
    /// <param name="report">The completed audit report.</param>
    /// <param name="localize">Optional localization delegate; keys pass through when null.</param>
    /// <returns>A complete HTML document as a string.</returns>
    public static string Generate(AuditReport report, Func<string, string>? localize = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var l = localize ?? (key => key);
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        AppendHead(sb, report, l);
        sb.AppendLine("<body>");
        AppendHeader(sb, report, l);
        AppendSummary(sb, report, l);

        foreach (var chapter in report.Chapters)
        {
            AppendChapter(sb, chapter, l);
        }

        AppendFooter(sb, report, l);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendHead(StringBuilder sb, AuditReport report, Func<string, string> l)
    {
        var scope = Encode(FormatScope(report.Scope, l));
        var date = report.StartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(Encode(l("AuditReportTitle"))).Append(" &#8212; ").Append(scope)
          .Append(" &#8212; ").Append(date).AppendLine("</title>");
        AppendStyles(sb);
        sb.AppendLine("</head>");
    }

    private static void AppendStyles(StringBuilder sb)
    {
        sb.AppendLine("<style>");
        sb.AppendLine("""
:root {
  --pass: #28a745;
  --warn: #ffc107;
  --fail: #dc3545;
  --error: #6f42c1;
  --skipped: #6c757d;
  --accent: #1a73e8;
  --bg-primary: #ffffff;
  --bg-secondary: #f8f9fa;
  --border: #dee2e6;
  --text-primary: #212529;
  --text-secondary: #6c757d;
}

*, *::before, *::after {
  box-sizing: border-box;
}

body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  max-width: 1000px;
  margin: 0 auto;
  padding: 2rem 1.5rem;
  color: var(--text-primary);
  background: var(--bg-secondary);
  line-height: 1.6;
}

h1 { font-size: 1.75rem; margin: 0 0 0.5rem 0; }
h2 { font-size: 1.35rem; margin: 0 0 0.75rem 0; color: var(--accent); }
h3 { font-size: 1rem; margin: 0 0 0.5rem 0; }

.header {
  border-bottom: 3px solid var(--accent);
  padding-bottom: 1rem;
  margin-bottom: 2rem;
}

.header p {
  margin: 0.25rem 0 0 0;
  color: var(--text-secondary);
  font-size: 0.95rem;
}

.summary {
  display: flex;
  gap: 1.5rem;
  margin-bottom: 2.5rem;
  flex-wrap: wrap;
}

.score-card {
  background: var(--bg-primary);
  text-align: center;
  padding: 1.5rem 2rem;
  border-radius: 8px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  min-width: 160px;
}

.score-value {
  font-size: 3rem;
  font-weight: 700;
  line-height: 1.1;
}

.score-label {
  font-size: 0.9rem;
  color: var(--text-secondary);
  margin-top: 0.25rem;
}

.summary-table {
  background: var(--bg-primary);
  border-radius: 8px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  padding: 1rem 1.5rem;
  flex: 1;
  min-width: 200px;
}

.summary-table table {
  width: 100%;
  border-collapse: collapse;
}

.summary-table td {
  padding: 6px 4px;
  border: none;
}

.summary-table td:last-child {
  text-align: right;
  font-weight: 600;
}

.chapter-bars {
  background: var(--bg-primary);
  border-radius: 8px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  padding: 1rem 1.5rem;
  flex: 1;
  min-width: 220px;
}

.chapter-bars h3 {
  margin-bottom: 0.75rem;
  font-size: 0.9rem;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.bar-row {
  display: flex;
  align-items: center;
  margin-bottom: 0.5rem;
  font-size: 0.85rem;
}

.bar-label {
  width: 120px;
  flex-shrink: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.bar-track {
  flex: 1;
  height: 14px;
  background: #e9ecef;
  border-radius: 7px;
  overflow: hidden;
  display: flex;
}

.bar-segment {
  height: 100%;
  transition: width 0.3s;
}

.chapter {
  background: var(--bg-primary);
  border-radius: 8px;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  padding: 1.5rem;
  margin-bottom: 2rem;
}

.chapter-stats {
  font-size: 0.9rem;
  color: var(--text-secondary);
  margin-bottom: 1rem;
}

.check {
  border: 1px solid var(--border);
  border-radius: 8px;
  margin-bottom: 1rem;
  padding: 1rem 1rem 1rem 1.25rem;
}

.check:last-child { margin-bottom: 0; }
.check.pass { border-left: 4px solid var(--pass); }
.check.warning { border-left: 4px solid var(--warn); }
.check.fail { border-left: 4px solid var(--fail); }
.check.error { border-left: 4px solid var(--error); }
.check.skipped { border-left: 4px solid var(--skipped); }

.check h3 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.check p {
  margin: 0.5rem 0 0 0;
  color: var(--text-secondary);
  word-break: break-word;
}

.badge {
  display: inline-block;
  padding: 2px 10px;
  border-radius: 12px;
  font-size: 0.75em;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  white-space: nowrap;
}

.badge-pass { background: var(--pass); color: #fff; }
.badge-warning { background: var(--warn); color: #333; }
.badge-fail { background: var(--fail); color: #fff; }
.badge-error { background: var(--error); color: #fff; }
.badge-skipped { background: var(--skipped); color: #fff; }

details {
  margin-top: 0.75rem;
}

summary {
  cursor: pointer;
  font-weight: 600;
  font-size: 0.9rem;
  color: var(--accent);
  user-select: none;
}

summary:hover { text-decoration: underline; }

table {
  width: 100%;
  border-collapse: collapse;
  margin: 0.5rem 0 0 0;
  font-size: 0.85rem;
}

thead th {
  background: #f1f3f4;
  text-align: left;
  padding: 8px 10px;
  font-weight: 600;
  white-space: nowrap;
}

tbody td {
  padding: 6px 10px;
  border-bottom: 1px solid #e8eaed;
  word-break: break-word;
  overflow-wrap: anywhere;
  max-width: 350px;
}

tbody tr:nth-child(even) { background: var(--bg-secondary); }

code {
  font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
  font-size: 0.85em;
  background: #f1f3f4;
  padding: 1px 4px;
  border-radius: 3px;
  word-break: break-all;
  overflow-wrap: anywhere;
}

.footer {
  margin-top: 3rem;
  padding-top: 1rem;
  border-top: 1px solid var(--border);
  color: var(--text-secondary);
  font-size: 0.85rem;
}

.footer p { margin: 0.25rem 0; }

@media print {
  body { padding: 0; background: #fff; }
  .no-print { display: none !important; }
  details[open] summary { display: none; }
  details { display: block !important; }
  details > *:not(summary) { display: block !important; }
  .chapter, .score-card, .summary-table, .chapter-bars {
    box-shadow: none;
    break-inside: avoid;
  }
  .check { break-inside: avoid; }
}

@media (max-width: 640px) {
  .summary { flex-direction: column; }
  .bar-label { width: 80px; }
  body { padding: 1rem; }
}
""");
        sb.AppendLine("</style>");
    }

    private static void AppendHeader(StringBuilder sb, AuditReport report, Func<string, string> l)
    {
        var scope = Encode(FormatScope(report.Scope, l));
        var date = report.StartTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var duration = DurationFormatter.Format(report.EndTime - report.StartTime, l);

        sb.AppendLine("<div class=\"header\">");
        sb.Append("<h1>").Append(Encode(l("AuditReportHeading"))).AppendLine("</h1>");
        sb.Append("<p>").Append(Encode(l("AuditReportScope"))).Append(": ").Append(scope)
          .Append(" | ").Append(Encode(l("AuditReportDate"))).Append(": ").Append(Encode(date))
          .Append(" | ").Append(Encode(l("AuditReportDuration"))).Append(": ").Append(Encode(duration))
          .AppendLine("</p>");
        sb.AppendLine("</div>");
    }

    private static void AppendSummary(StringBuilder sb, AuditReport report, Func<string, string> l)
    {
        var allChecks = report.Chapters.SelectMany(c => c.Checks).ToList();
        var totalChecks = allChecks.Count;
        var passCount = allChecks.Count(c => c.Status == AuditStatus.Pass);
        var warnCount = allChecks.Count(c => c.Status == AuditStatus.Warning);
        var failCount = allChecks.Count(c => c.Status == AuditStatus.Fail);
        var errorCount = allChecks.Count(c => c.Status == AuditStatus.Error);
        var skippedCount = allChecks.Count(c => c.Status == AuditStatus.Skipped);

        var compliancePercent = totalChecks > 0
            ? (int)Math.Round(100.0 * passCount / totalChecks)
            : 0;
        var complianceColor = compliancePercent >= 80 ? "var(--pass)"
            : compliancePercent >= 50 ? "var(--warn)"
            : "var(--fail)";

        sb.AppendLine("<div class=\"summary\">");

        // Score card
        sb.AppendLine("<div class=\"score-card\">");
        sb.Append("<div class=\"score-value\" style=\"color: ").Append(complianceColor).Append(";\">")
          .Append(compliancePercent.ToString(CultureInfo.InvariantCulture)).AppendLine("%</div>");
        sb.Append("<div class=\"score-label\">").Append(Encode(l("AuditReportCompliance"))).AppendLine("</div>");
        sb.AppendLine("</div>");

        // Status counts table
        sb.AppendLine("<div class=\"summary-table\">");
        sb.AppendLine("<table>");
        AppendSummaryRow(sb, "pass", l("AuditStatusPass"), passCount, l("AuditReportChecks"));
        AppendSummaryRow(sb, "warning", l("AuditStatusWarn"), warnCount, l("AuditReportChecks"));
        AppendSummaryRow(sb, "fail", l("AuditStatusFail"), failCount, l("AuditReportChecks"));
        AppendSummaryRow(sb, "error", l("AuditStatusError"), errorCount, l("AuditReportChecks"));
        AppendSummaryRow(sb, "skipped", l("AuditStatusSkip"), skippedCount, l("AuditReportChecks"));
        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        // Per-chapter progress bars
        sb.AppendLine("<div class=\"chapter-bars\">");
        sb.Append("<h3>").Append(Encode(l("AuditReportChapters"))).AppendLine("</h3>");
        foreach (var chapter in report.Chapters)
        {
            AppendChapterBar(sb, chapter);
        }
        sb.AppendLine("</div>");

        sb.AppendLine("</div>");
    }

    private static void AppendSummaryRow(StringBuilder sb, string cssClass, string label, int count, string checksLabel)
    {
        sb.Append("<tr><td><span class=\"badge badge-").Append(cssClass).Append("\">")
          .Append(Encode(label)).Append("</span></td><td>")
          .Append(count.ToString(CultureInfo.InvariantCulture))
          .Append(' ').Append(Encode(checksLabel)).AppendLine("</td></tr>");
    }

    private static void AppendChapterBar(StringBuilder sb, AuditChapter chapter)
    {
        var total = chapter.Checks.Count;
        if (total == 0)
        {
            return;
        }

        var passPercent = 100.0 * chapter.PassCount / total;
        var warnPercent = 100.0 * chapter.WarnCount / total;
        var failPercent = 100.0 * chapter.FailCount / total;
        var errorCount = chapter.Checks.Count(c => c.Status == AuditStatus.Error);
        var errorPercent = 100.0 * errorCount / total;
        var skippedCount = chapter.Checks.Count(c => c.Status == AuditStatus.Skipped);
        var skippedPercent = 100.0 * skippedCount / total;

        sb.AppendLine("<div class=\"bar-row\">");
        sb.Append("<span class=\"bar-label\" title=\"").Append(Encode(chapter.Name)).Append("\">")
          .Append(Encode(chapter.Id)).AppendLine("</span>");
        sb.AppendLine("<div class=\"bar-track\">");

        AppendBarSegment(sb, "var(--pass)", passPercent);
        AppendBarSegment(sb, "var(--warn)", warnPercent);
        AppendBarSegment(sb, "var(--fail)", failPercent);
        AppendBarSegment(sb, "var(--error)", errorPercent);
        AppendBarSegment(sb, "var(--skipped)", skippedPercent);

        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void AppendBarSegment(StringBuilder sb, string color, double percent)
    {
        if (percent <= 0)
        {
            return;
        }

        sb.Append("<div class=\"bar-segment\" style=\"width:")
          .Append(percent.ToString("F1", CultureInfo.InvariantCulture))
          .Append("%;background:").Append(color).AppendLine(";\"></div>");
    }

    private static void AppendChapter(StringBuilder sb, AuditChapter chapter, Func<string, string> l)
    {
        sb.AppendLine("<div class=\"chapter\">");
        sb.Append("<h2>").Append(Encode(chapter.Name))
          .Append(" &#8212; ").Append(Encode(chapter.SecNumCloudRef)).AppendLine("</h2>");

        sb.Append("<div class=\"chapter-stats\">").Append(Encode(l("AuditReportPassLabel"))).Append(' ')
          .Append(chapter.PassCount.ToString(CultureInfo.InvariantCulture))
          .Append(" | ").Append(Encode(l("AuditReportWarnLabel"))).Append(' ')
          .Append(chapter.WarnCount.ToString(CultureInfo.InvariantCulture))
          .Append(" | ").Append(Encode(l("AuditReportFailLabel"))).Append(' ')
          .Append(chapter.FailCount.ToString(CultureInfo.InvariantCulture))
          .AppendLine("</div>");

        foreach (var check in chapter.Checks)
        {
            AppendCheck(sb, check, l);
        }

        sb.AppendLine("</div>");
    }

    private static void AppendCheck(StringBuilder sb, AuditCheck check, Func<string, string> l)
    {
        var statusCss = StatusToCssClass(check.Status);
        var statusLabel = StatusToLabel(check.Status, l);

        sb.Append("<div class=\"check ").Append(statusCss).AppendLine("\">");
        sb.AppendLine("<h3>");
        sb.Append("<span class=\"badge badge-").Append(statusCss).Append("\">")
          .Append(Encode(statusLabel)).Append("</span> ");
        sb.Append(Encode(check.Id)).Append(": ").Append(Encode(check.Name))
          .Append(" (").Append(Encode(check.SecNumCloudClause)).Append(')');
        sb.AppendLine("</h3>");

        if (!string.IsNullOrWhiteSpace(check.Summary))
        {
            sb.Append("<p>").Append(Encode(check.Summary)).AppendLine("</p>");
        }

        if (check.Evidence.Count > 0)
        {
            AppendEvidenceTable(sb, check.Evidence, l);
        }

        sb.AppendLine("</div>");
    }

    private static void AppendEvidenceTable(StringBuilder sb, List<AuditEvidence> evidence, Func<string, string> l)
    {
        sb.AppendLine("<details>");
        sb.Append("<summary>").Append(Encode(l("AuditReportEvidence"))).Append(" (")
          .Append(evidence.Count.ToString(CultureInfo.InvariantCulture))
          .Append(' ').Append(Encode(l("AuditReportItems"))).AppendLine(")</summary>");
        sb.AppendLine("<table>");
        sb.Append("<thead><tr><th>").Append(Encode(l("AuditReportHost")))
          .Append("</th><th>").Append(Encode(l("AuditReportDetail")))
          .Append("</th><th>").Append(Encode(l("AuditReportRawData")))
          .AppendLine("</th></tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var item in evidence)
        {
            sb.AppendLine("<tr>");
            sb.Append("<td>").Append(Encode(item.Host)).AppendLine("</td>");
            sb.Append("<td>").Append(Encode(item.Detail)).AppendLine("</td>");
            sb.Append("<td><code>").Append(Encode(item.RawData)).AppendLine("</code></td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</details>");
    }

    private static void AppendFooter(StringBuilder sb, AuditReport report, Func<string, string> l)
    {
        var allChecks = report.Chapters.SelectMany(c => c.Checks).ToList();
        var hostCount = allChecks.SelectMany(c => c.Evidence)
            .Select(e => e.Host)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var duration = DurationFormatter.Format(report.EndTime - report.StartTime, l);
        var timestamp = report.EndTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        sb.AppendLine("<div class=\"footer\">");
        sb.Append("<p>").Append(Encode(l("AuditReportGenerated"))).Append(" &#8212; ")
          .Append(Encode(timestamp)).AppendLine("</p>");
        sb.Append("<p>").Append(Encode(l("AuditReportAuditDuration"))).Append(": ").Append(Encode(duration))
          .Append(" | ").Append(Encode(l("AuditReportHostsScanned"))).Append(": ").Append(hostCount.ToString(CultureInfo.InvariantCulture))
          .Append(" | ").Append(Encode(l("AuditReportChecksPerformed"))).Append(": ").Append(allChecks.Count.ToString(CultureInfo.InvariantCulture))
          .AppendLine("</p>");
        sb.AppendLine("</div>");
    }

    private static string FormatScope(AuditScope scope, Func<string, string> l)
    {
        var parts = new List<string>();

        if (scope.Targets.Count > 0)
        {
            parts.Add(string.Join(", ", scope.Targets));
        }

        if (!string.IsNullOrEmpty(scope.Subnet))
        {
            parts.Add(scope.Subnet);
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : l("ValueNotAvailable");
    }

    private static string StatusToCssClass(AuditStatus status) => status switch
    {
        AuditStatus.Pass => "pass",
        AuditStatus.Warning => "warning",
        AuditStatus.Fail => "fail",
        AuditStatus.Error => "error",
        AuditStatus.Skipped => "skipped",
        _ => "skipped",
    };

    private static string StatusToLabel(AuditStatus status, Func<string, string> l) => status switch
    {
        AuditStatus.Pass => l("AuditStatusPass"),
        AuditStatus.Warning => l("AuditStatusWarn"),
        AuditStatus.Fail => l("AuditStatusFail"),
        AuditStatus.Error => l("AuditStatusError"),
        AuditStatus.Skipped => l("AuditStatusSkip"),
        _ => status.ToString().ToUpperInvariant(),
    };

    private static string Encode(string value) =>
        WebUtility.HtmlEncode(value ?? string.Empty);
}
