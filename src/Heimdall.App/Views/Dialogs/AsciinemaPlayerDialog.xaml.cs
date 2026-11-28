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
using Heimdall.Terminal.Logging;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Built-in read-only asciinema v2 (<c>.cast</c>) replay window powered by the embedded xterm.js
/// WebView2 surface. Supports play/pause, speed control, idle-time compression, and instant seeking.
/// </summary>
public partial class AsciinemaPlayerDialog : Window, IDisposable
{
    private const string TerminalClearResetSequence = "\x1b[2J\x1b[H\x1b[3J";

    private readonly LocalizationManager _localizer;
    private readonly AppSettings? _settings;
    private readonly string? _initialFilePath;
    private readonly DispatcherTimer _playbackTimer;

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
    private bool _disposed;

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

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync().ConfigureAwait(true);

        if (!_disposed && !string.IsNullOrWhiteSpace(_initialFilePath))
        {
            LoadRecordingFile(_initialFilePath);
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

            PlayerWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _allowInitialNavigation = false;
                if (_disposed)
                {
                    return;
                }

                _webViewReady = true;
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

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = _localizer["CastPlayerFileFilter"],
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            LoadRecordingFile(dlg.FileName);
        }
    }

    private void LoadRecordingFile(string filePath)
    {
        StopPlayback();

        if (!AsciinemaCastPlayerEngine.TryLoadFile(filePath, out var recording, out var errorCode, out string? rawError) || recording is null)
        {
            string localizedReason = FormatLoadError(errorCode, rawError);
            TxtMeta.Text = _localizer.Format("CastPlayerLoadFailed", localizedReason);
            BtnPlayPause.IsEnabled = false;
            BtnRestart.IsEnabled = false;
            TimelineSlider.IsEnabled = false;
            return;
        }

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

        SyncTimelineBounds();
        RenderSeekState(0);
        StartPlayback();
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
        RenderSeekState(_currentSeconds);
        UpdateClockText();
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

        var (output, lastIdx) = AsciinemaCastPlayerEngine.BuildStateAtTime(_recording, seconds, UseIdleCap());
        _nextEventIndex = lastIdx + 1;

        if (_webViewReady)
        {
            PostTerminalData(TerminalClearResetSequence + output);
        }

        UpdateSliderAndClock();
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
