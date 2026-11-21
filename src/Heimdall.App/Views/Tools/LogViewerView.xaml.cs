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
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.Core.Localization;
using Heimdall.Core.LogFiles;
using Heimdall.Core.Logging;
using Heimdall.Core.Matching;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Log file viewer with tail mode, regex filtering, and auto-scroll.
/// Replaces <c>Get-Content -Tail -Wait</c> for environments where
/// PowerShell execution is restricted.
/// </summary>
public partial class LogViewerView : UserControl, IToolView
{
    private const int MaxDisplayLines = LogFileScanner.MaxLines;
    private const string HighlightBrushKey = "AccentBrush";
    private const string HighlightForegroundBrushKey = "TextOnAccentBrush";
    private const string MutedBrushKey = "TextSecondaryBrush";
    private const double BytesPerKilobyte = 1024.0;
    private const int TailIntervalMs = 500;
    private const int EncodingIndexUtf8 = 0;
    private const int EncodingIndexAscii = 1;
    private const int EncodingIndexUtf16 = 2;
    private const int EncodingIndexWindows1252 = 3;
    private const int Windows1252CodePage = 1252;

    private LocalizationManager? _localizer;
    private string? _filePath;
    private CancellationTokenSource? _scanCts;
    private int _generation;
    private LogCursor? _cursor;
    private bool _tailBusy;
    private bool _loading;
    private bool _disposed;
    private Paragraph? _provisional;
    private DispatcherTimer? _tailTimer;
    private Encoding _encoding = Encoding.UTF8;
    private Regex? _filterRegex;
    private bool _autoScroll = true;

    private readonly record struct RenderStyle(Brush Highlight, Brush? HighlightForeground, Brush? Muted, string TruncatedMarker);

    public LogViewerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the view with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        _localizer = localizer;
        if (_localizer is not null) _localizer.LocaleChanged += OnLocaleChanged;
        ApplyLocalization();
        ClearErrorState();
        UpdateViewerState();

        if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            FilePathInput.Text = context.Argument;
            _ = LoadAsync(context.Argument);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FilePathInput.Focus());
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolLogViewTitle");
        BtnBrowse.Content = L("ToolLogViewBtnBrowse");
        BtnTail.Content = L("ToolLogViewBtnTail");
        BtnApplyFilter.Content = L("ToolLogViewBtnFilter");
        BtnClearFilter.Content = L("ToolLogViewBtnClear");
        ChkCaseSensitive.Content = L("ToolLogViewCaseSensitive");
        EncodingLabel.Text = L("ToolLogViewEncoding");
        EncUtf8Item.Content = L("EncodingUtf8");
        EncAsciiItem.Content = L("EncodingAscii");
        EncUtf16Item.Content = L("EncodingUtf16");
        EncWin1252Item.Content = L("EncodingWindows1252");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowse, L("ToolLogViewBtnBrowse"));
        System.Windows.Automation.AutomationProperties.SetName(BtnTail, L("A11yLogViewerTailToggle"));
        System.Windows.Automation.AutomationProperties.SetName(BtnApplyFilter, L("ToolLogViewBtnFilter"));
        System.Windows.Automation.AutomationProperties.SetName(BtnClearFilter, L("ToolLogViewBtnClear"));
        System.Windows.Automation.AutomationProperties.SetName(FilePathInput, L("ToolLogViewFilePathHint"));
        System.Windows.Automation.AutomationProperties.SetName(FilterInput, L("ToolLogViewBtnFilter"));
        System.Windows.Automation.AutomationProperties.SetName(ChkCaseSensitive, L("ToolLogViewCaseSensitive"));
        System.Windows.Automation.AutomationProperties.SetName(EncodingCombo, L("ToolLogViewEncoding"));
        System.Windows.Automation.AutomationProperties.SetName(LogViewer, L("ToolLogViewTitle"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        FilePathInput.Tag = L("ToolWatermarkLogFilePath");
        FilterInput.Tag = L("ToolWatermarkFilterText");
        TxtEmptyState.Text = L("ToolLogViewEmptyState");

        BtnCopyLog.Content = L("ToolBtnCopyToClipboard");
        BtnCopyLog.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyLog, L("ToolBtnCopyToClipboard"));
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("ToolLogViewFileFilter"),
            Title = L("ToolLogViewBtnBrowse")
        };

        if (dialog.ShowDialog() == true)
        {
            FilePathInput.Text = dialog.FileName;
            _ = LoadAsync(dialog.FileName);
        }
    }

    private void OnFilePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var path = FilePathInput.Text.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                _ = LoadAsync(path);
            }
        }
    }

    private bool IsCurrent(int generation) => !_disposed && generation == _generation;

    private void CancelScan()
    {
        var cts = _scanCts;
        _scanCts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// Scans the file off the UI thread and renders the last lines once. Never throws: failures
    /// are shown in the error banner and a superseded load is dropped silently.
    /// </summary>
    private async Task LoadAsync(string filePath)
    {
        CancelScan();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var generation = ++_generation;

        StopTail();
        ClearErrorState();
        _cursor = null;
        _filePath = filePath;
        _loading = true;
        ResetDisplay();
        StatsText.Text = L("ToolLogViewLoading");

        var encoding = _encoding;
        var filter = new LogFilter(_filterRegex);
        LogScanResult result;

        try
        {
            result = await Task.Run(() => LogFileScanner.ReadInitial(filePath, encoding, filter, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation)) FailLoad(ex);
            return;
        }

        if (!IsCurrent(generation)) return;

        try
        {
            _loading = false;
            if (result.Status == LogScanStatus.FilterTimeout)
            {
                ResetDisplay();
                ShowErrorState(L("ToolLogViewErrorRegexTimeout"));
                return;
            }

            _cursor = result.Cursor;
            _autoScroll = true;
            RenderAll(result);

            if (BtnTail.IsChecked == true)
            {
                StartTail();
            }
        }
        catch (Exception ex)
        {
            FailLoad(ex);
        }
    }

    private void FailLoad(Exception ex)
    {
        _loading = false;
        _filePath = null;
        _cursor = null;
        ResetDisplay();

        var notFound = ex is FileNotFoundException or DirectoryNotFoundException
            or ArgumentException or NotSupportedException;
        ShowErrorState(notFound
            ? L("ToolLogViewErrorFileNotFound")
            : string.Format(L("ToolLogViewErrorOpen"), ex.Message));
    }

    private void OnTailChecked(object sender, RoutedEventArgs e)
    {
        if (_cursor is not null && _filePath is not null)
        {
            StartTail();
        }
    }

    private void OnTailUnchecked(object sender, RoutedEventArgs e)
    {
        StopTail();
    }

    private void StartTail()
    {
        if (_tailTimer is not null) return;

        _autoScroll = true;
        _tailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TailIntervalMs) };
        _tailTimer.Tick += OnTailTick;
        _tailTimer.Start();

        TailStatusText.Text = L("ToolLogViewTailActive");
        TailStatusText.Visibility = Visibility.Visible;
    }

    private void StopTail()
    {
        _tailTimer?.Stop();
        _tailTimer = null;

        TailStatusText.Visibility = Visibility.Collapsed;
    }

    private async void OnTailTick(object? sender, EventArgs e)
    {
        var path = _filePath;
        var cursor = _cursor;
        if (_tailBusy || _loading || _disposed || path is null || cursor is null) return;

        _tailBusy = true;
        var generation = _generation;
        var filter = new LogFilter(_filterRegex);
        var token = _scanCts?.Token ?? CancellationToken.None;

        try
        {
            var result = await Task.Run(() => LogFileScanner.ReadAppended(path, cursor, filter, token), token);
            if (!IsCurrent(generation)) return;

            if (_tailTimer is null)
            {
                // Tail was switched off while this scan ran: its cursor is spent, so resync by rescanning.
                ReloadCurrentFile();
                return;
            }

            ApplyTailResult(result, cursor);
        }
        catch (OperationCanceledException)
        {
            // A newer load superseded this tick.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may be locked or mid-rotation; the next tick retries.
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Log viewer tail tick failed: {ex.Message}");
        }
        finally
        {
            _tailBusy = false;
        }
    }

    private void ApplyTailResult(LogScanResult result, LogCursor previous)
    {
        if (result.Status == LogScanStatus.FilterTimeout)
        {
            _cursor = null;
            StopTail();
            ShowErrorState(L("ToolLogViewErrorRegexTimeout"));
            return;
        }

        _cursor = result.Cursor;
        if (result.Status == LogScanStatus.Ok && !result.Overflowed && result.Cursor!.Position == previous.Position)
        {
            return;
        }

        if (result.Status == LogScanStatus.Reset || result.Overflowed)
        {
            RenderAll(result);
            return;
        }

        var style = ResolveStyle();
        LogViewer.BeginChange();
        try
        {
            RemoveProvisional();
            AppendLines(result, style);
            TrimToMaxLines();
        }
        finally
        {
            LogViewer.EndChange();
        }

        FinishRender(result);
    }

    private void RenderAll(LogScanResult result)
    {
        var style = ResolveStyle();
        LogViewer.BeginChange();
        try
        {
            LogDocument.Blocks.Clear();
            _provisional = null;
            AppendLines(result, style);
            TrimToMaxLines();
        }
        finally
        {
            LogViewer.EndChange();
        }

        FinishRender(result);
    }

    private void AppendLines(LogScanResult result, RenderStyle style)
    {
        foreach (var line in result.Lines)
        {
            LogDocument.Blocks.Add(BuildParagraph(line, style));
        }

        if (result.PendingLine is not null)
        {
            _provisional = BuildParagraph(result.PendingLine, style);
            LogDocument.Blocks.Add(_provisional);
        }
    }

    private void RemoveProvisional()
    {
        if (_provisional is null) return;

        LogDocument.Blocks.Remove(_provisional);
        _provisional = null;
    }

    private void FinishRender(LogScanResult result)
    {
        UpdateViewerState();
        UpdateStats(result);

        if (_autoScroll)
        {
            LogViewer.ScrollToEnd();
        }
    }

    private RenderStyle ResolveStyle() => new(
        TryFindResource(HighlightBrushKey) as Brush ?? Brushes.Yellow,
        TryFindResource(HighlightForegroundBrushKey) as Brush,
        TryFindResource(MutedBrushKey) as Brush,
        L("ToolLogViewLineTruncated"));

    private static Paragraph BuildParagraph(LogLine line, RenderStyle style)
    {
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };
        var lastIndex = 0;

        foreach (var highlight in line.Highlights)
        {
            if (highlight.Start > lastIndex)
            {
                para.Inlines.Add(new Run(line.Text[lastIndex..highlight.Start]));
            }

            var run = new Run(line.Text.Substring(highlight.Start, highlight.Length))
            {
                Background = style.Highlight
            };
            if (style.HighlightForeground is not null)
            {
                run.Foreground = style.HighlightForeground;
            }

            para.Inlines.Add(run);
            lastIndex = highlight.Start + highlight.Length;
        }

        if (lastIndex < line.Text.Length || para.Inlines.Count == 0)
        {
            para.Inlines.Add(new Run(line.Text[lastIndex..]));
        }

        if (line.Truncated)
        {
            var marker = new Run(style.TruncatedMarker);
            if (style.Muted is not null)
            {
                marker.Foreground = style.Muted;
            }

            para.Inlines.Add(marker);
        }

        return para;
    }

    private void TrimToMaxLines()
    {
        while (LogDocument.Blocks.Count > MaxDisplayLines)
        {
            LogDocument.Blocks.Remove(LogDocument.Blocks.FirstBlock);
        }
    }

    private void OnApplyFilterClick(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        FilterInput.Text = string.Empty;
        _filterRegex = null;
        ReloadCurrentFile();
    }

    private void OnCaseSensitiveChanged(object sender, RoutedEventArgs e)
    {
        if (_filterRegex is not null)
        {
            ApplyFilter();
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        ClearErrorState();
        var pattern = FilterInput.Text.Trim();
        if (string.IsNullOrEmpty(pattern))
        {
            _filterRegex = null;
        }
        else
        {
            try
            {
                var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (ChkCaseSensitive.IsChecked != true)
                    options |= RegexOptions.IgnoreCase;

                _filterRegex = new Regex(pattern, options, RegexEngine.DefaultTimeout);
            }
            catch (RegexParseException)
            {
                ShowErrorState(L("ToolLogViewErrorInvalidRegex"));
                FilterInput.Focus();
                FilterInput.SelectAll();
                return;
            }
        }

        ReloadCurrentFile();
    }

    /// <summary>
    /// Rescans the open file with the current filter.
    /// </summary>
    private void ReloadCurrentFile()
    {
        if (_filePath is not null)
        {
            _ = LoadAsync(_filePath);
        }
    }

    private void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
    {
        _encoding = EncodingCombo.SelectedIndex switch
        {
            EncodingIndexAscii => Encoding.ASCII,
            EncodingIndexUtf16 => Encoding.Unicode,
            EncodingIndexWindows1252 => Encoding.GetEncoding(Windows1252CodePage),
            _ => Encoding.UTF8
        };

        // Reload with new encoding if a file is open
        if (_filePath is not null)
        {
            _ = LoadAsync(_filePath);
        }
    }

    private void UpdateStats(LogScanResult result)
    {
        var sizeKb = result.FileLength / BytesPerKilobyte;
        var lastWrite = result.LastWriteUtc.ToLocalTime();

        StatsText.Text = string.Format(
            L("ToolLogViewStats"),
            LogDocument.Blocks.Count - (_provisional is null ? 0 : 1),
            result.TotalLines,
            sizeKb.ToString("F1"),
            lastWrite.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        var range = new TextRange(LogDocument.ContentStart, LogDocument.ContentEnd);
        var text = range.Text.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpLOGVIEW").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearErrorState()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateViewerState();
        ErrorText.BringIntoView();
    }

    private void ResetDisplay()
    {
        LogDocument.Blocks.Clear();
        _provisional = null;
        StatsText.Text = string.Empty;
        UpdateViewerState();
    }

    private void UpdateViewerState()
    {
        var hasContent = LogDocument.Blocks.Count > 0;
        ResultsPanel.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        TxtEmptyState.Text = _loading
            ? L("ToolLogViewLoading")
            : _filterRegex is not null && !hasContent && _filePath is not null
                ? L("ToolLogViewNoMatches")
                : L("ToolLogViewEmptyState");
    }

    private string L(string key) => _localizer?[key] ?? key;

    private void OnLocaleChanged(string _)
    {
        ApplyLocalization();
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            TxtHelpContent.Text = L("ToolHelpLOGVIEW").Replace("\\n", "\n");
        }
        UpdateViewerState();
    }

    public void Dispose()
    {
        if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;
        _disposed = true;
        _generation++;
        CancelScan();
        StopTail();
        GC.SuppressFinalize(this);
    }
}
