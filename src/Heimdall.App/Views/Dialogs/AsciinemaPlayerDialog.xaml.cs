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
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Redaction;
using Heimdall.Terminal.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Built-in read-only asciinema v2 (<c>.cast</c>) replay window powered by the embedded xterm.js
/// WebView2 surface. Supports play/pause, speed control, idle-time compression, and instant seeking.
/// </summary>
public partial class AsciinemaPlayerDialog : Window, IDisposable
{
    private const string TerminalClearResetSequence = "\x1b[?1049l\x1b[0m\x1bc";
    private const int SeekThrottleMilliseconds = 40;
    private const string ClipboardWritePrefix = "clipboard-write:";

    private readonly LocalizationManager _localizer;
    private readonly AppSettings? _settings;
    private readonly string? _initialFilePath;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _seekThrottleTimer;

    private IDisposable? _lockdown;
    private AsciinemaCastRecording? _recording;
    private bool _allowInitialNavigation;
    private bool _webViewReady;
    private bool _isPlaying;
    private bool _updatingSlider;
    private double _currentSeconds;
    private int _nextEventIndex;
    private double _speed = 1.0;
    private long _lastTickMs;
    private int _loadVersion;
    private double _pendingSeekSeconds;
    private bool _hasPendingSeek;
    private bool _disposed;
    private IReadOnlyList<AsciinemaCommandChapter> _chapters = [];
    private IReadOnlyList<AsciinemaSearchMatch> _searchMatches = [];
    private int _currentSearchMatchIndex = -1;
    private string _lastSearchQuery = string.Empty;
    private bool _updatingChapterCombo;

    public AsciinemaPlayerDialog(
        LocalizationManager localizer,
        AppSettings? settings = null,
        string? initialFilePath = null)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _settings = settings;
        _initialFilePath = initialFilePath;

        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playbackTimer.Tick += OnPlaybackTick;

        _seekThrottleTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(SeekThrottleMilliseconds)
        };
        _seekThrottleTimer.Tick += OnSeekThrottleTick;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync().ConfigureAwait(true);

        if (!_disposed && !string.IsNullOrWhiteSpace(_initialFilePath))
        {
            await LoadRecordingFileAsync(_initialFilePath).ConfigureAwait(true);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var env = await WebView2Helper.CreateEnvironmentAsync("SSH").ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            await PlayerWebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            if (_disposed || PlayerWebView.CoreWebView2 is null)
            {
                return;
            }

            _lockdown?.Dispose();
            _lockdown = WebView2Lockdown.Apply(PlayerWebView.CoreWebView2, onNewWindow: null);
            PlayerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PlayerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PlayerWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            PlayerWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            PlayerWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PlayerWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            PlayerWebView.CoreWebView2.Settings.IsPinchZoomEnabled = false;

            PlayerWebView.CoreWebView2.NavigationStarting += (_, navArgs) =>
            {
                if (!_allowInitialNavigation)
                {
                    navArgs.Cancel = true;
                }
            };

            PlayerWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            PlayerWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _allowInitialNavigation = false;
                if (_disposed)
                {
                    return;
                }

                _webViewReady = true;
                try
                {
                    PlayerWebView.CoreWebView2?.PostWebMessageAsString("session-ended:marked");
                }
                catch (Exception ex)
                {
                    FileLogger.Debug($"[AsciinemaPlayerDialog] session-ended marker suppressed: {ex.Message}");
                }

                if (_recording is not null)
                {
                    RenderSeekState(_currentSeconds);
                }
            };

            _allowInitialNavigation = true;
            PlayerWebView.CoreWebView2.NavigateToString(
                EmbeddedSshView.BuildReadOnlyTerminalHtml(_settings, _localizer));
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            FileLogger.Warn($"[AsciinemaPlayerDialog] WebView2 init failed: {ex.Message}");
            TxtMeta.Text = _localizer.Format("CastPlayerLoadFailed", ex.Message);
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = _localizer["CastPlayerFileFilter"],
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            await LoadRecordingFileAsync(dlg.FileName).ConfigureAwait(true);
        }
    }

    private async Task LoadRecordingFileAsync(string filePath)
    {
        StopPlayback();
        _seekThrottleTimer.Stop();
        _hasPendingSeek = false;
        int version = ++_loadVersion;

        BtnPlayPause.IsEnabled = false;
        BtnRestart.IsEnabled = false;
        TimelineSlider.IsEnabled = false;
        TxtMeta.Text = _localizer["CastPlayerLoading"];

        var loadResult = await Task.Run(() =>
        {
            bool ok = AsciinemaCastPlayerEngine.TryLoadFile(
                filePath,
                out var parsed,
                out var errorCode,
                out string? rawError);
            return (Ok: ok, Recording: parsed, ErrorCode: errorCode, RawError: rawError);
        }).ConfigureAwait(true);

        if (_disposed || version != _loadVersion)
        {
            return;
        }

        if (!loadResult.Ok || loadResult.Recording is null)
        {
            string localizedReason = FormatLoadError(loadResult.ErrorCode, loadResult.RawError);
            TxtMeta.Text = _localizer.Format("CastPlayerLoadFailed", localizedReason);
            return;
        }

        var recording = loadResult.Recording;
        _recording = recording;
        _currentSeconds = 0;
        _nextEventIndex = 0;

        string title = string.IsNullOrWhiteSpace(recording.Header.Title)
            ? Path.GetFileName(filePath)
            : recording.Header.Title;
        TxtTitle.Text = title;
        string eventsCount = _localizer.FormatCount(
            recording.Events.Count,
            "CastPlayerEventCountOne",
            "CastPlayerEventCount",
            recording.Events.Count);
        TxtMeta.Text = _localizer.Format(
            "CastPlayerMetaFormat",
            Path.GetFileName(filePath),
            recording.Header.Width,
            recording.Header.Height,
            eventsCount);

        BtnPlayPause.IsEnabled = true;
        BtnRestart.IsEnabled = true;
        TimelineSlider.IsEnabled = true;
        TxtSearch.IsEnabled = true;
        BtnExportTranscript.IsEnabled = true;
        PopulateChaptersAndResetSearch(recording);

        SyncTimelineBounds();
        RenderSeekState(0);
        StartPlayback();
    }

    private void PopulateChaptersAndResetSearch(AsciinemaCastRecording recording)
    {
        _searchMatches = [];
        _currentSearchMatchIndex = -1;
        _lastSearchQuery = string.Empty;
        TxtSearchCount.Text = string.Empty;
        BtnSearchPrev.IsEnabled = false;
        BtnSearchNext.IsEnabled = false;

        _chapters = AsciinemaCastPlayerEngine.ExtractCommandChapters(recording);
        _updatingChapterCombo = true;
        try
        {
            CmbChapters.Items.Clear();
            if (_chapters.Count == 0)
            {
                CmbChapters.Items.Add(new ComboBoxItem { Content = _localizer["CastPlayerChaptersNone"], Tag = null });
                CmbChapters.SelectedIndex = 0;
                CmbChapters.IsEnabled = false;
            }
            else
            {
                CmbChapters.Items.Add(new ComboBoxItem
                {
                    Content = _localizer.Format("CastPlayerChaptersCount", _chapters.Count),
                    Tag = null
                });
                foreach (var chapter in _chapters)
                {
                    string ts = AsciinemaCastPlayerEngine.FormatTimestamp(
                        UseIdleCap() ? chapter.CompressedSeconds : chapter.ElapsedSeconds);
                    CmbChapters.Items.Add(new ComboBoxItem
                    {
                        Content = $"[{ts}] {chapter.Label}",
                        Tag = chapter
                    });
                }

                CmbChapters.SelectedIndex = 0;
                CmbChapters.IsEnabled = true;
            }
        }
        finally
        {
            _updatingChapterCombo = false;
        }
    }

    private void OnChapterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingChapterCombo || _recording is null)
        {
            return;
        }

        if (CmbChapters.SelectedItem is ComboBoxItem { Tag: AsciinemaCommandChapter chapter })
        {
            SeekToEventTimestamps(chapter.ElapsedSeconds, chapter.CompressedSeconds);
        }
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            NavigateSearchMatch(forward: true);
        }
    }

    private void OnSearchPrevClick(object sender, RoutedEventArgs e)
        => NavigateSearchMatch(forward: false);

    private void OnSearchNextClick(object sender, RoutedEventArgs e)
        => NavigateSearchMatch(forward: true);

    private void NavigateSearchMatch(bool forward)
    {
        if (_recording is null)
        {
            return;
        }

        string query = TxtSearch.Text?.Trim() ?? string.Empty;
        if (!string.Equals(query, _lastSearchQuery, StringComparison.Ordinal))
        {
            _lastSearchQuery = query;
            _searchMatches = AsciinemaCastPlayerEngine.Search(_recording, query);
            _currentSearchMatchIndex = -1;
        }

        if (_searchMatches.Count == 0)
        {
            TxtSearchCount.Text = string.IsNullOrEmpty(query) ? string.Empty : "0 / 0";
            BtnSearchPrev.IsEnabled = false;
            BtnSearchNext.IsEnabled = false;
            return;
        }

        BtnSearchPrev.IsEnabled = true;
        BtnSearchNext.IsEnabled = true;

        if (_currentSearchMatchIndex < 0)
        {
            _currentSearchMatchIndex = forward ? 0 : _searchMatches.Count - 1;
        }
        else if (forward)
        {
            _currentSearchMatchIndex = (_currentSearchMatchIndex + 1) % _searchMatches.Count;
        }
        else
        {
            _currentSearchMatchIndex = (_currentSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        }

        AsciinemaSearchMatch match = _searchMatches[_currentSearchMatchIndex];
        TxtSearchCount.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_currentSearchMatchIndex + 1} / {_searchMatches.Count}");
        SeekToEventTimestamps(match.ElapsedSeconds, match.CompressedSeconds);
    }

    private void SeekToEventTimestamps(double elapsedSeconds, double compressedSeconds)
    {
        if (_recording is null)
        {
            return;
        }

        _seekThrottleTimer.Stop();
        _hasPendingSeek = false;
        _currentSeconds = Math.Clamp(
            UseIdleCap() ? compressedSeconds : elapsedSeconds,
            0,
            CurrentDuration());
        RenderSeekState(_currentSeconds);
    }

    private void OnExportTranscriptClick(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        RedactionResult redacted = AsciinemaCastPlayerEngine.ExportMaskedTranscript(_recording);
        if (redacted.Refused)
        {
            string msg = _localizer.Format(
                "CastPlayerExportRefused",
                redacted.RefusedRuleId ?? "redaction-timeout");
            TxtMeta.Text = msg;
            if ((Application.Current as App)?.Services is { } sp)
            {
                sp.GetService<IDialogService>()?
                    .ShowError(_localizer["CastPlayerDialogTitle"], msg);
            }
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"session-transcript-{DateTime.UtcNow:yyyyMMdd-HHmm}.txt",
            DefaultExt = ".txt"
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, redacted.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            int maskedCount = redacted.Counts.Values.Sum();
            TxtMeta.Text = _localizer.Format("CastPlayerExportSuccess", Path.GetFileName(dlg.FileName), maskedCount);
        }
        catch (IOException ex)
        {
            TxtMeta.Text = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            TxtMeta.Text = ex.Message;
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        if (_isPlaying)
        {
            StopPlayback();
        }
        else
        {
            if (_currentSeconds >= CurrentDuration())
            {
                _currentSeconds = 0;
                RenderSeekState(0);
            }

            StartPlayback();
        }
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _currentSeconds = 0;
        RenderSeekState(0);
        StartPlayback();
    }

    private void OnTimelineSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _recording is null)
        {
            return;
        }

        _currentSeconds = Math.Clamp(e.NewValue, 0, CurrentDuration());
        _pendingSeekSeconds = _currentSeconds;
        _hasPendingSeek = true;
        UpdateClockText();
        _seekThrottleTimer.Stop();
        _seekThrottleTimer.Start();
    }

    private void OnSeekThrottleTick(object? sender, EventArgs e)
    {
        _seekThrottleTimer.Stop();
        if (_disposed || !_hasPendingSeek || _recording is null)
        {
            return;
        }

        _hasPendingSeek = false;
        RenderSeekState(_pendingSeekSeconds);
    }

    private void OnSpeedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSpeed.SelectedItem is ComboBoxItem { Tag: string raw }
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed > 0)
        {
            _speed = parsed;
        }
    }

    private void OnIdleCapChanged(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        double oldMax = Math.Max(0.001, TimelineSlider.Maximum);
        double ratio = Math.Clamp(_currentSeconds / oldMax, 0, 1);
        SyncTimelineBounds();
        _currentSeconds = ratio * CurrentDuration();
        RenderSeekState(_currentSeconds);
        UpdateClockText();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || _recording is null)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        double dt = Math.Max(0, (nowMs - _lastTickMs) / 1000.0);
        _lastTickMs = nowMs;

        double duration = CurrentDuration();
        _currentSeconds = Math.Min(duration, _currentSeconds + (dt * _speed));

        bool useIdleCap = UseIdleCap();
        var batch = new StringBuilder();
        while (_nextEventIndex < _recording.Events.Count)
        {
            var evt = _recording.Events[_nextEventIndex];
            double evtTime = useIdleCap ? evt.CompressedSeconds : evt.ElapsedSeconds;
            if (evtTime > _currentSeconds)
            {
                break;
            }

            if (evt.Type == "o" && evt.Data.Length > 0)
            {
                batch.Append(evt.Data);
            }
            else if (evt.Type == "r" && AsciinemaCastPlayerEngine.TryParseResizePayload(evt.Data, out int cols, out int rows))
            {
                if (batch.Length > 0)
                {
                    PostTerminalData(batch.ToString());
                    batch.Clear();
                }

                PostTerminalResize(cols, rows);
            }

            _nextEventIndex++;
        }

        if (batch.Length > 0)
        {
            PostTerminalData(batch.ToString());
        }

        UpdateSliderAndClock();

        if (_currentSeconds >= duration || _nextEventIndex >= _recording.Events.Count)
        {
            StopPlayback();
        }
    }

    private void StartPlayback()
    {
        if (_recording is null)
        {
            return;
        }

        _isPlaying = true;
        _lastTickMs = Environment.TickCount64;
        _playbackTimer.Start();
        string pauseText = _localizer["CastPlayerBtnPause"];
        BtnPlayPause.Content = pauseText;
        AutomationProperties.SetName(BtnPlayPause, pauseText);
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _playbackTimer.Stop();
        string playText = _localizer["CastPlayerBtnPlay"];
        BtnPlayPause.Content = playText;
        AutomationProperties.SetName(BtnPlayPause, playText);
    }

    private void RenderSeekState(double seconds)
    {
        if (_recording is null)
        {
            return;
        }

        var (output, lastIdx, width, height) = AsciinemaCastPlayerEngine.BuildSeekStateAtTime(
            _recording,
            seconds,
            UseIdleCap());
        _nextEventIndex = lastIdx + 1;

        if (_webViewReady)
        {
            PostTerminalResize(width, height);
            PostTerminalData(TerminalClearResetSequence + output);
        }

        UpdateSliderAndClock();
    }

    private void PostTerminalResize(int cols, int rows)
    {
        if (_disposed || !_webViewReady || PlayerWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            PlayerWebView.CoreWebView2.PostWebMessageAsString(
                string.Create(CultureInfo.InvariantCulture, $"resize-cols-rows:{cols},{rows}"));
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[AsciinemaPlayerDialog] PostTerminalResize failed: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        string msg;
        try
        {
            msg = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (string.IsNullOrEmpty(msg) || !msg.StartsWith(ClipboardWritePrefix, StringComparison.Ordinal))
        {
            return;
        }

        string b64 = msg[ClipboardWritePrefix.Length..];
        if (string.IsNullOrEmpty(b64))
        {
            return;
        }

        try
        {
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is FormatException or System.Runtime.InteropServices.ExternalException)
        {
            FileLogger.Debug($"[AsciinemaPlayerDialog] clipboard-write skipped: {ex.Message}");
        }
    }

    private void PostTerminalData(string text)
    {
        if (_disposed || !_webViewReady || PlayerWebView.CoreWebView2 is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            PlayerWebView.CoreWebView2.PostWebMessageAsString("data:" + b64);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[AsciinemaPlayerDialog] PostWebMessage failed: {ex.Message}");
        }
    }

    private void SyncTimelineBounds()
    {
        _updatingSlider = true;
        try
        {
            double duration = CurrentDuration();
            TimelineSlider.Maximum = duration > 0 ? duration : 1.0;
            TimelineSlider.Value = Math.Clamp(_currentSeconds, 0, TimelineSlider.Maximum);
        }
        finally
        {
            _updatingSlider = false;
        }

        UpdateClockText();
    }

    private void UpdateSliderAndClock()
    {
        _updatingSlider = true;
        try
        {
            TimelineSlider.Value = Math.Clamp(_currentSeconds, 0, TimelineSlider.Maximum);
        }
        finally
        {
            _updatingSlider = false;
        }

        UpdateClockText();
    }

    private void UpdateClockText()
    {
        TxtClock.Text = $"{AsciinemaCastPlayerEngine.FormatTimestamp(_currentSeconds)} / {AsciinemaCastPlayerEngine.FormatTimestamp(CurrentDuration())}";
    }

    private bool UseIdleCap() => ChkIdleCap.IsChecked == true;

    private double CurrentDuration()
    {
        if (_recording is null)
        {
            return 0;
        }

        return UseIdleCap() ? _recording.CompressedDurationSeconds : _recording.TotalDurationSeconds;
    }

    private string FormatLoadError(AsciinemaCastLoadErrorCode code, string? rawError)
    {
        return code switch
        {
            AsciinemaCastLoadErrorCode.EmptyPath => _localizer["CastPlayerErrorEmptyPath"],
            AsciinemaCastLoadErrorCode.FileNotFound => _localizer["CastPlayerErrorFileNotFound"],
            AsciinemaCastLoadErrorCode.FileTooLargeOrEmpty => _localizer["CastPlayerErrorFileTooLargeOrEmpty"],
            AsciinemaCastLoadErrorCode.EmptyContent => _localizer["CastPlayerErrorEmptyContent"],
            AsciinemaCastLoadErrorCode.MissingHeader => _localizer["CastPlayerErrorMissingHeader"],
            AsciinemaCastLoadErrorCode.InvalidHeader => _localizer["CastPlayerErrorInvalidHeader"],
            AsciinemaCastLoadErrorCode.UnsupportedVersion => _localizer["CastPlayerErrorUnsupportedVersion"],
            _ => string.IsNullOrWhiteSpace(rawError) ? _localizer["CastPlayerErrorReadFailed"] : rawError
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playbackTimer.Stop();
        _seekThrottleTimer.Stop();
        _lockdown?.Dispose();
        _lockdown = null;
        try
        {
            PlayerWebView.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
