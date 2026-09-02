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

using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Generic wrapper view that launches a detected external CLI tool
/// (NirSoft/Sysinternals), captures its stdout, and displays the output
/// as a DataGrid (CSV mode) or TextBox (text mode).
/// </summary>
public partial class ExternalToolWrapperView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private ExternalToolInfo? _toolInfo;
    private CancellationTokenSource? _cts;
    private Process? _runningProcess;
    private const int DefaultTimeoutMs = 60_000;
    private readonly ToolAsyncStateController _viewState;

    public ExternalToolWrapperView()
    {
        InitializeComponent();
        _viewState = new ToolAsyncStateController(
            null, LoadingBar, TxtError, null, null, TxtStatus,
            BtnRun, TxtArguments);
        TxtArguments.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) OnRunClick(s, e);
        };
    }

    /// <summary>
    /// Configures the wrapper with the external tool metadata.
    /// Must be called before <see cref="Initialize"/>.
    /// </summary>
    public void SetToolInfo(ExternalToolInfo toolInfo)
    {
        _toolInfo = toolInfo;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        ApplyLocalization();

        if (_toolInfo is null) return;

        HeaderTitle.Text = _toolInfo.Name;
        TxtProviderBadge.Text = _toolInfo.ProviderName;
        TxtExePath.Text = _toolInfo.ExecutablePath;

        // Build default arguments, replacing placeholders with context values
        var args = _toolInfo.Arguments;
        if (context is not null)
        {
            args = args
                .Replace("{Host}", context.TargetHost ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{Port}", context.TargetPort?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            args = args
                .Replace("{Host}", "", StringComparison.OrdinalIgnoreCase)
                .Replace("{Port}", "", StringComparison.OrdinalIgnoreCase);
        }
        TxtArguments.Text = args.Trim();

        // Warn upfront if tool requires elevation (output won't be captured)
        if (_toolInfo.RequiresElevation)
        {
            TxtStatus.Text = L("ExtToolElevationWarning");
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtArguments.Focus();
        });
    }

    public bool CanClose() => _runningProcess is null || _runningProcess.HasExited;

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_toolInfo is null) return;

        _cts?.Cancel();
        _cts?.Dispose();
        var timeoutMs = GetConfiguredTimeoutMs();
        _cts = new CancellationTokenSource(timeoutMs);

        SetRunningState(true);
        _viewState.Begin();
        TxtOutput.Text = string.Empty;
        ResultsGrid.ItemsSource = null;

        try
        {
            var (exitCode, stdout, stderr) = await RunToolAsync(
                _toolInfo.ExecutablePath, TxtArguments.Text, _cts.Token);

            if (_cts.Token.IsCancellationRequested) return;

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                _viewState.ShowError(stderr.Trim(), showEmptyState: false, keepResultsVisible: true);
            }

            var output = stdout;
            if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(stderr))
                output = stderr;

            if (_toolInfo.OutputFormat == OutputFormat.Csv)
            {
                DisplayCsvOutput(output);
            }
            else if (_toolInfo.OutputFormat == OutputFormat.Json)
            {
                DisplayJsonOutput(output);
            }
            else
            {
                DisplayTextOutput(output);
            }

            TxtStatus.Text = string.Format(CultureInfo.InvariantCulture,
                L("ExtToolStatusComplete"), exitCode,
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            _viewState.ShowError(L("ExtToolErrorTimeout"), L("ExtToolStatusTimeout"), showEmptyState: false);
        }
        catch (Exception ex)
        {
            _viewState.ShowError(ex.Message, L("ExtToolStatusError"), showEmptyState: false);
        }
        finally
        {
            SetRunningState(false);
            _viewState.End();
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { if (_runningProcess is { HasExited: false }) _runningProcess.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunToolAsync(
        string exePath, string arguments, CancellationToken ct)
    {
        // Elevated processes require UseShellExecute=true, which prevents stdout redirect.
        // Fallback: wrap in cmd /c with output redirected to a temp file.
        if (_toolInfo?.RequiresElevation == true)
            return await RunElevatedToolAsync(exePath, arguments, ct);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        _runningProcess = process;

        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            // Timeout or manual cancel - kill the process tree so it does not
            // continue running in the background after the tab shows a timeout.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* process may have already exited */ }
            throw;
        }
        finally
        {
            _runningProcess = null;
        }
    }

    /// <summary>
    /// Runs an elevated tool directly via UseShellExecute + Verb=runas.
    /// Cannot capture stdout (Windows limitation), so the tool opens in its own window.
    /// No shell interpolation - arguments are passed directly to the process, not via cmd.exe.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunElevatedToolAsync(
        string exePath, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
            };

            using var process = new Process { StartInfo = psi };
            _runningProcess = process;

            process.Start();
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, L("ExtToolElevatedNoCapture"), "");
        }
        catch (OperationCanceledException)
        {
            // Timeout or manual cancel - kill the elevated process tree so it does not
            // continue running in the background after the tab shows a timeout.
            try { if (_runningProcess is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { /* process may have already exited */ }
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - user declined UAC
            return (-1, "", L("ExtToolElevationCancelled"));
        }
        finally
        {
            _runningProcess = null;
        }
    }

    private void DisplayCsvOutput(string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            DisplayTextOutput(csvText);
            return;
        }

        try
        {
            var table = ParseCsv(csvText);
            if (table.Rows.Count == 0)
            {
                DisplayTextOutput(csvText);
                return;
            }

            ResultsGrid.ItemsSource = table.DefaultView;
            ResultsGrid.Visibility = Visibility.Visible;
            TxtOutput.Visibility = Visibility.Collapsed;

            TxtStatus.Text = string.Format(CultureInfo.InvariantCulture,
                L("ExtToolStatusRows"), table.Rows.Count);
        }
        catch
        {
            // Fallback to text if CSV parsing fails
            DisplayTextOutput(csvText);
        }
    }

    /// <summary>
    /// Parses JSON array output (NirSoft /sjson) into a DataTable for DataGrid display.
    /// Falls back to formatted text if parsing fails.
    /// </summary>
    private void DisplayJsonOutput(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            DisplayTextOutput(jsonText);
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                // Pretty-print non-array JSON
                var pretty = System.Text.Json.JsonSerializer.Serialize(
                    doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                DisplayTextOutput(pretty);
                return;
            }

            var table = new DataTable();
            var firstRow = true;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                if (firstRow)
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        var colName = prop.Name;
                        var uniqueName = colName;
                        var suffix = 1;
                        while (table.Columns.Contains(uniqueName))
                            uniqueName = $"{colName}_{suffix++}";
                        table.Columns.Add(uniqueName);
                    }
                    firstRow = false;
                }

                var row = table.NewRow();
                foreach (var prop in element.EnumerateObject())
                {
                    // Match by property name, not position - handles missing/reordered fields
                    if (table.Columns.Contains(prop.Name))
                        row[prop.Name] = prop.Value.ToString();
                    else if (!firstRow)
                    {
                        // Late-discovered column: add it and backfill with empty
                        table.Columns.Add(prop.Name);
                        row[prop.Name] = prop.Value.ToString();
                    }
                }
                table.Rows.Add(row);
            }

            if (table.Rows.Count == 0)
            {
                DisplayTextOutput(jsonText);
                return;
            }

            ResultsGrid.ItemsSource = table.DefaultView;
            ResultsGrid.Visibility = Visibility.Visible;
            TxtOutput.Visibility = Visibility.Collapsed;

            TxtStatus.Text = string.Format(CultureInfo.InvariantCulture,
                L("ExtToolStatusRows"), table.Rows.Count);
        }
        catch
        {
            DisplayTextOutput(jsonText);
        }
    }

    private void DisplayTextOutput(string text)
    {
        TxtOutput.Text = text?.Trim() ?? string.Empty;
        TxtOutput.Visibility = Visibility.Visible;
        ResultsGrid.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Simple RFC 4180-compatible CSV parser. Handles quoted fields with embedded
    /// commas and double-quotes. First row = headers → DataTable column names.
    /// </summary>
    private static DataTable ParseCsv(string csv)
    {
        var table = new DataTable();
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return table;

        var headers = ParseCsvLine(headerLine);
        foreach (var header in headers)
        {
            var colName = header.Trim();
            // DataTable requires unique column names
            var baseName = string.IsNullOrWhiteSpace(colName) ? "Column" : colName;
            var uniqueName = baseName;
            var suffix = 1;
            while (table.Columns.Contains(uniqueName))
                uniqueName = $"{baseName}_{suffix++}";
            table.Columns.Add(uniqueName);
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            var row = table.NewRow();
            for (var i = 0; i < Math.Min(fields.Count, table.Columns.Count); i++)
                row[i] = fields[i];
            table.Rows.Add(row);
        }

        return table;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = TxtOutput.Visibility == Visibility.Visible
            ? TxtOutput.Text
            : DataGridToText();

        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                Clipboard.SetText(text);
                CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
    }

    private string DataGridToText()
    {
        if (ResultsGrid.ItemsSource is not DataView view) return string.Empty;
        var table = view.Table;
        if (table is null) return string.Empty;

        var sb = new StringBuilder();
        // Headers
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            sb.Append(table.Columns[i].ColumnName);
        }
        sb.AppendLine();
        // Rows
        foreach (DataRow row in table.Rows)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(row[i]);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void SetRunningState(bool running)
    {
        BtnRun.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        BtnStop.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLocalization()
    {
        BtnRun.Content = L("ExtToolBtnRun");
        BtnStop.Content = L("ExtToolBtnStop");
        BtnCopy.Content = L("ToolBtnCopyToClipboard");
        TxtStatus.Text = L("ExtToolStatusReady");

        System.Windows.Automation.AutomationProperties.SetName(BtnRun, L("ExtToolBtnRun"));
        System.Windows.Automation.AutomationProperties.SetName(BtnStop, L("ExtToolBtnStop"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopy, L("ToolBtnCopyToClipboard"));
        System.Windows.Automation.AutomationProperties.SetName(TxtArguments, L("ExtToolArgsLabel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtOutput, L("ExtToolOutputLabel"));
        System.Windows.Automation.AutomationProperties.SetName(ResultsGrid, L("ExtToolOutputLabel"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(LoadingBar, L("ExtToolA11yLoading"));
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var desc = _toolInfo?.DescriptionKey is not null ? L(_toolInfo.DescriptionKey) : "";
        var exePath = _toolInfo?.ExecutablePath ?? "";
        var provider = _toolInfo?.ProviderName ?? "";

        TxtHelpContent.Text = string.Format(CultureInfo.InvariantCulture,
            L("ExtToolHelpTemplate"), _toolInfo?.Name ?? "", desc, provider, exePath);
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private string L(string key) => _localizer?[key] ?? key;

    private int GetConfiguredTimeoutMs()
    {
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow?.DataContext is ViewModels.MainViewModel vm)
            return vm.CurrentSettings?.ExternalToolTimeoutMs ?? DefaultTimeoutMs;
        return DefaultTimeoutMs;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        try { if (_runningProcess is { HasExited: false }) _runningProcess.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
