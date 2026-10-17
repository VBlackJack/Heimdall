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
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Log file viewer with tail mode, regex filtering, and auto-scroll.
/// Replaces <c>Get-Content -Tail -Wait</c> for environments where
/// PowerShell execution is restricted.
/// </summary>
public partial class LogViewerView : UserControl, IToolView
{
    private const int MaxDisplayLines = 10000;
    private const int TailIntervalMs = 500;
    private const int EncodingIndexUtf8 = 0;
    private const int EncodingIndexAscii = 1;
    private const int EncodingIndexUtf16 = 2;
    private const int EncodingIndexWindows1252 = 3;
    private const int Windows1252CodePage = 1252;

    private LocalizationManager? _localizer;
    private string? _filePath;
    private FileStream? _fileStream;
    private long _lastPosition;
    private DispatcherTimer? _tailTimer;
    private Encoding _encoding = Encoding.UTF8;
    private Regex? _filterRegex;
    private bool _autoScroll = true;
    private int _totalLineCount;
    private int _displayedLineCount;
    private Brush? _highlightBrush;

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
            LoadFile(context.Argument);
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
            LoadFile(dialog.FileName);
        }
    }

    private void OnFilePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var path = FilePathInput.Text.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                LoadFile(path);
            }
        }
    }

    private void LoadFile(string filePath)
    {
        StopTail();
        ClearErrorState();
        DisposeCurrentFile();
        ResetDisplay();

        if (!File.Exists(filePath))
        {
            ShowErrorState(L("ToolLogViewErrorFileNotFound"));
            return;
        }

        _filePath = filePath;

        try
        {
            _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(_fileStream, _encoding, leaveOpen: true);
            var content = reader.ReadToEnd();
            _lastPosition = _fileStream.Position;

            AppendLines(content);
            UpdateStats();

            // Start tail if toggle is checked
            if (BtnTail.IsChecked == true)
            {
                StartTail();
            }
        }
        catch (IOException ex)
        {
            DisposeCurrentFile();
            ResetDisplay();
            ShowErrorState(string.Format(L("ToolLogViewErrorOpen"), ex.Message));
        }
    }

    private void OnTailChecked(object sender, RoutedEventArgs e)
    {
        if (_fileStream is not null && _filePath is not null)
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
        _tailTimer.Tick += (_, _) => ReadNewContent();
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

    private void ReadNewContent()
    {
        if (_fileStream is null || _filePath is null) return;

        try
        {
            var fileInfo = new FileInfo(_filePath);
            var currentLength = fileInfo.Length;

            if (currentLength == _lastPosition) return;

            if (currentLength < _lastPosition)
            {
                // File was truncated; re-read from start
                _lastPosition = 0;
                _totalLineCount = 0;
                _displayedLineCount = 0;
                LogDocument.Blocks.Clear();
                UpdateViewerState();
            }

            _fileStream.Position = _lastPosition;
            using var reader = new StreamReader(_fileStream, _encoding, leaveOpen: true);
            var newContent = reader.ReadToEnd();
            _lastPosition = _fileStream.Position;

            if (!string.IsNullOrEmpty(newContent))
            {
                AppendLines(newContent);
                UpdateStats();
            }
        }
        catch (IOException)
        {
            // File may be temporarily locked; skip this tick
        }
    }

    private void AppendLines(string content)
    {
        var lines = content.Split('\n');
        _highlightBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.Yellow;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            _totalLineCount++;

            if (_filterRegex is not null && !_filterRegex.IsMatch(line))
                continue;

            var para = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };

            if (_filterRegex is not null)
            {
                HighlightMatches(para, line, _filterRegex);
            }
            else
            {
                para.Inlines.Add(new Run(line));
            }

            LogDocument.Blocks.Add(para);
            _displayedLineCount++;
        }

        TrimToMaxLines();
        UpdateViewerState();

        if (_autoScroll)
        {
            LogViewer.ScrollToEnd();
        }
    }

    private void HighlightMatches(Paragraph para, string line, Regex regex)
    {
        var lastIndex = 0;
        var highlightForeground = TryFindResource("TextOnAccentBrush") as Brush;
        foreach (Match match in regex.Matches(line))
        {
            if (match.Index > lastIndex)
            {
                para.Inlines.Add(new Run(line[lastIndex..match.Index]));
            }

            var highlighted = new Run(match.Value)
            {
                Background = _highlightBrush
            };
            if (highlightForeground is not null)
            {
                highlighted.Foreground = highlightForeground;
            }
            para.Inlines.Add(highlighted);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < line.Length)
        {
            para.Inlines.Add(new Run(line[lastIndex..]));
        }
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
        ReloadWithFilter();
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
                var options = RegexOptions.Compiled;
                if (ChkCaseSensitive.IsChecked != true)
                    options |= RegexOptions.IgnoreCase;

                _filterRegex = new Regex(pattern, options, TimeSpan.FromSeconds(1));
            }
            catch (RegexParseException)
            {
                ShowErrorState(L("ToolLogViewErrorInvalidRegex"));
                FilterInput.Focus();
                FilterInput.SelectAll();
                return;
            }
        }

        ReloadWithFilter();
    }

    /// <summary>
    /// Reloads the file content with the current filter applied.
    /// </summary>
    private void ReloadWithFilter()
    {
        if (_filePath is null || _fileStream is null) return;

        ClearErrorState();
        _displayedLineCount = 0;
        _totalLineCount = 0;
        LogDocument.Blocks.Clear();

        try
        {
            _fileStream.Position = 0;
            using var reader = new StreamReader(_fileStream, _encoding, leaveOpen: true);
            var content = reader.ReadToEnd();
            _lastPosition = _fileStream.Position;

            AppendLines(content);
            UpdateStats();
        }
        catch (IOException ex)
        {
            ShowErrorState(string.Format(L("ToolLogViewErrorOpen"), ex.Message));
            UpdateViewerState();
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
            LoadFile(_filePath);
        }
    }

    private void UpdateStats()
    {
        if (_filePath is null) return;

        try
        {
            var fileInfo = new FileInfo(_filePath);
            var sizeKb = fileInfo.Length / 1024.0;
            var lastWrite = fileInfo.LastWriteTime;

            StatsText.Text = string.Format(
                L("ToolLogViewStats"),
                _displayedLineCount,
                _totalLineCount,
                sizeKb.ToString("F1"),
                lastWrite.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (IOException)
        {
            // File info may be temporarily unavailable
        }
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
        _totalLineCount = 0;
        _displayedLineCount = 0;
        _lastPosition = 0;
        LogDocument.Blocks.Clear();
        StatsText.Text = string.Empty;
        UpdateViewerState();
    }

    private void DisposeCurrentFile()
    {
        _fileStream?.Dispose();
        _fileStream = null;
        _filePath = null;
    }

    private void UpdateViewerState()
    {
        var hasContent = LogDocument.Blocks.Count > 0;
        ResultsPanel.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        TxtEmptyState.Text = _filterRegex is not null && !hasContent && _filePath is not null
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
        StopTail();
        _fileStream?.Dispose();
        _fileStream = null;
        GC.SuppressFinalize(this);
    }
}
