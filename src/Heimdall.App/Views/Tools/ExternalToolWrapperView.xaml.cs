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
using Heimdall.Core.Security;

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
        TabularGridCopy.Attach(ResultsGrid);
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

        // Build default arguments, replacing placeholders with context values, by the same rule
        // as a launch from the server menu: a host that would not stay one plain argument is not
        // written into the box, and the user is told why.
        if (!ExternalToolDefinition.TryResolveDetectedArguments(
                _toolInfo.Arguments,
                _toolInfo.ExecutablePath,
                context?.TargetHost,
                context?.TargetPort?.ToString(CultureInfo.InvariantCulture),
                user: null,
                out string args,
                out ArgumentTemplateRejection? rejection))
        {
            ExternalToolDefinition.TryResolveDetectedArguments(
                _toolInfo.Arguments,
                _toolInfo.ExecutablePath,
                host: null,
                port: null,
                user: null,
                out args,
                out _);
            TxtError.Text = string.Format(
                CultureInfo.CurrentCulture,
                L(ExternalToolLaunchService.RejectionMessageKey(rejection!.Reason)),
                rejection.Token);
            TxtError.Visibility = Visibility.Visible;
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

    private void DisplayCsvOutput(string csvText) => DisplayTabularOutput(TabularOutput.FromCsv(csvText));

    /// <summary>
    /// Shows JSON array output (NirSoft /sjson) in the DataGrid.
    /// Falls back to formatted text if parsing fails.
    /// </summary>
    private void DisplayJsonOutput(string jsonText) => DisplayTabularOutput(TabularOutput.FromJson(jsonText));

    private void DisplayTabularOutput(TabularOutput output)
    {
        if (output.Table is not { } table)
        {
            DisplayTextOutput(output.Text);
            return;
        }

        ResultsGrid.ItemsSource = table.DefaultView;
        ResultsGrid.Visibility = Visibility.Visible;
        TxtOutput.Visibility = Visibility.Collapsed;

        TxtStatus.Text = DescribeRowCount(_localizer, table.Rows.Count);
    }

    private void DisplayTextOutput(string text)
    {
        TxtOutput.Text = text?.Trim() ?? string.Empty;
        TxtOutput.Visibility = Visibility.Visible;
        ResultsGrid.Visibility = Visibility.Collapsed;
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
        return table is null ? string.Empty : TabularOutput.ToTabSeparated(table);
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

    /// <summary>The status line once the tool's output was read as a table.</summary>
    internal static string DescribeRowCount(LocalizationManager? localizer, int count) =>
        localizer?.FormatCount(count, "ExtToolStatusRowsOne", "ExtToolStatusRows", count) ?? "ExtToolStatusRows";

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
