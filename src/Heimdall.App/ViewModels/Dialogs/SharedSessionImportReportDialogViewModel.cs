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

using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// The read-only import report of one shared source: the count of each check, then the findings
/// grouped by check, with a copy to the clipboard and an export to CSV.
/// </summary>
public sealed partial class SharedSessionImportReportDialogViewModel : ObservableObject
{
    /// <summary>The file name the CSV export proposes, before the source's identifier.</summary>
    public const string CsvFileNamePrefix = "import-report-";

    /// <summary>The extension of the CSV export.</summary>
    public const string CsvExtension = ".csv";

    private readonly LocalizationManager _localizer;
    private readonly SharedSessionImportReportFormatter _formatter;
    private readonly IClipboardService _clipboard;
    private readonly Func<string, string?> _pickCsvPath;

    /// <param name="report">The report shown.</param>
    /// <param name="localizer">Words the dialog.</param>
    /// <param name="clipboard">Receives the plain text copy.</param>
    /// <param name="pickCsvPath">Asks where to export, given the proposed file name; null when cancelled.</param>
    public SharedSessionImportReportDialogViewModel(
        SharedSessionImportReport report,
        LocalizationManager localizer,
        IClipboardService clipboard,
        Func<string, string?> pickCsvPath)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _pickCsvPath = pickCsvPath ?? throw new ArgumentNullException(nameof(pickCsvPath));
        _formatter = new SharedSessionImportReportFormatter(localizer);

        Summary = [.. SharedSessionImportReportFormatter.Checks.Select(check =>
            new SharedSessionReportSummaryRow(_formatter.CheckLabel(check), report.CountOf(check)))];
        Groups = [.. SharedSessionImportReportFormatter.Checks
            .Select(check => (Check: check, Entries: report.Entries.Where(entry => entry.Check == check).ToList()))
            .Where(group => group.Entries.Count > 0)
            .Select(group => new SharedSessionReportGroup(
                _localizer.Format("SharedReportSummaryLine", _formatter.CheckLabel(group.Check), group.Entries.Count),
                [.. group.Entries.Select(entry => new SharedSessionReportRow(
                    _formatter.FolderLabel(entry.FolderPath),
                    entry.SessionName,
                    _formatter.Explain(entry)))]))];
    }

    /// <summary>The report shown.</summary>
    public SharedSessionImportReport Report { get; }

    public string DialogTitle => _localizer.Format("SharedReportDialogTitle", Report.SourceName);

    public string TotalText => _formatter.TotalText(Report);

    public bool HasEntries => !Report.IsEmpty;

    public string EmptyText => _localizer["SharedReportNoEntries"];

    public string CloseLabel => _localizer["SharedReportClose"];

    /// <summary>One line per check with how many findings it has, zeros included.</summary>
    public IReadOnlyList<SharedSessionReportSummaryRow> Summary { get; }

    /// <summary>The findings, grouped by check; a check with none has no group.</summary>
    public IReadOnlyList<SharedSessionReportGroup> Groups { get; }

    /// <summary>What the last copy or export came to.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The task of the last export; tests await it.</summary>
    internal Task LastExport { get; private set; } = Task.CompletedTask;

    /// <summary>The report as the plain text the copy puts on the clipboard.</summary>
    public string PlainText => _formatter.ToPlainText(Report);

    /// <summary>The report as the CSV the export writes.</summary>
    public string Csv => _formatter.ToCsv(Report);

    [RelayCommand]
    private void Copy()
    {
        _clipboard.SetText(PlainText);
        StatusText = _localizer["SharedReportCopied"];
    }

    [RelayCommand]
    private Task ExportCsvAsync()
    {
        LastExport = ExportCsvCoreAsync();
        return LastExport;
    }

    private async Task ExportCsvCoreAsync()
    {
        string? path = _pickCsvPath(CsvFileNamePrefix + Report.SourceId + CsvExtension);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // With its byte order mark, so a spreadsheet opens the accented names as UTF-8.
            await File.WriteAllTextAsync(path, Csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText = _localizer.Format("SharedReportExported", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLogger.Warn($"Shared sessions: the import report of {Report.SourceId} could not be exported: {ex.Message}");
            StatusText = _localizer.Format("SharedReportExportFailed", ex.Message);
        }
    }

    /// <summary>
    /// Asks where to save the CSV with the system dialog; null when the user cancels.
    /// </summary>
    public static string? PickCsvPathWithDialog(LocalizationManager localizer, string proposedFileName)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = proposedFileName,
            DefaultExt = CsvExtension,
            Filter = localizer["SharedReportCsvFilter"],
            Title = localizer["SharedReportExportCsv"],
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

/// <summary>One check of the report summary.</summary>
public sealed record SharedSessionReportSummaryRow(string Label, int Count);

/// <summary>The findings of one check.</summary>
public sealed record SharedSessionReportGroup(string Header, IReadOnlyList<SharedSessionReportRow> Rows);

/// <summary>One finding, as the dialog lists it.</summary>
public sealed record SharedSessionReportRow(string Folder, string Session, string Explanation);
