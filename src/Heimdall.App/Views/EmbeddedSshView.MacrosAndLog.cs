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

using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Macros;
using Heimdall.Core.Models;
using Heimdall.Terminal.Logging;
using Heimdall.Terminal.Macros;

namespace Heimdall.App.Views;

public partial class EmbeddedSshView
{
    /// <summary>Whether a transcript recording is currently active for this session.</summary>
    public bool IsTranscriptActive =>
        SessionLogService is not null
        && _sessionLogKey is not null
        && SessionLogService.IsSessionActive(_sessionLogKey);

    /// <summary>
    /// Starts (or restarts) transcript recording for this session through the shared
    /// <see cref="ISessionLogService"/>, bypassing the auto-start gate. Used by the manual
    /// Start Transcript menu action.
    /// </summary>
    /// <returns>The log file path, or <c>null</c> if recording could not start.</returns>
    public string? StartTranscript() => StartSessionLog();

    /// <summary>Stops the active transcript recording for this session, if any.</summary>
    public void StopTranscript()
    {
        if (SessionLogService is not null && _sessionLogKey is not null)
        {
            SessionLogService.StopSession(_sessionLogKey);
        }
    }

    /// <summary>
    /// Auto-starts transcript recording when the gate policy allows it for this session's
    /// protocol and the global logging toggle is on. No-op otherwise.
    /// </summary>
    private void TryAutoStartSessionLog()
    {
        bool globalEnabled = TerminalSettings?.SessionLoggingEnabled ?? false;
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(SessionLoggingOverride, globalEnabled);
        if (SessionLogGatePolicy.ShouldAutoStart(enabled, _sessionTab?.ConnectionType))
        {
            StartSessionLog();
        }
    }

    /// <summary>
    /// Builds the session-log context and starts recording through the service. The session key
    /// is generated once and reused for the tap, manual start/stop, and teardown.
    /// </summary>
    private string? StartSessionLog()
    {
        if (SessionLogService is null || _sessionTab is null)
        {
            return null;
        }

        _sessionLogKey ??= Guid.NewGuid().ToString("N");

        string protocol = string.IsNullOrWhiteSpace(_sessionTab.ConnectionType)
            ? "SESSION"
            : _sessionTab.ConnectionType;
        string host = string.IsNullOrWhiteSpace(_sessionLogEndpoint)
            ? _sessionLogDisplayName
            : _sessionLogEndpoint;

        SessionLogContext context = new SessionLogContext(
            _sessionLogKey,
            protocol,
            host,
            _sessionLogDisplayName,
            DateTime.UtcNow);

        return SessionLogService.StartSession(context);
    }

    /// <summary>Whether a macro recording is currently in progress.</summary>
    public bool IsRecordingMacro => _isRecording;

    /// <summary>Clears any previous recording state and begins capturing terminal input.</summary>
    public void StartRecording()
    {
        _macroEntries.Clear();
        _macroStopwatch.Restart();
        _isRecording = true;
        Core.Logging.FileLogger.Info("Macro recording started");
    }

    /// <summary>
    /// Stops recording and returns the captured entries.
    /// Returns an empty list if recording was not active.
    /// </summary>
    public List<MacroEntry> StopRecording()
    {
        _isRecording = false;
        _macroStopwatch.Stop();
        Core.Logging.FileLogger.Info($"Macro recording stopped ({_macroEntries.Count} entries)");
        return new List<MacroEntry>(_macroEntries);
    }

    /// <summary>
    /// Replays a previously recorded macro by sending each entry to the terminal
    /// with the recorded inter-input delays.
    /// </summary>
    public async Task PlayMacro(TerminalMacro macro, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(macro);

        Core.Logging.FileLogger.Info($"Macro playback started: {macro.Name} ({macro.Entries.Count} entries)");

        var result = await MacroPlaybackExecutor.RunAsync(
            macro.Entries,
            WaitForMacroExpectAsync,
            static (delayMs, token) => Task.Delay(delayMs, token),
            bytes => WriteToSession(bytes),
            OnMacroExpectTimedOut,
            ct);

        if (result.WasStoppedByExpectTimeout)
        {
            Core.Logging.FileLogger.Warn(
                $"Macro playback stopped after expect timeout ({result.ExpectTimeoutMs ?? 0} ms): {macro.Name}");
            return;
        }

        Core.Logging.FileLogger.Info($"Macro playback completed: {macro.Name}");
    }

    private async Task<MacroExpectWaitResult> WaitForMacroExpectAsync(MacroEntry entry, CancellationToken ct)
    {
        if (entry.ExpectPattern is null)
        {
            return MacroExpectWaitResult.NotRequired;
        }

        var timeoutMs = entry.GetEffectiveExpectTimeoutMs();
        var observer = new MacroExpectObserver(entry.ExpectPattern, entry.ExpectIsRegex);
        SetActiveMacroExpectObserver(observer);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
            var completedTask = await Task.WhenAny(observer.Completion, timeoutTask);
            if (completedTask == observer.Completion)
            {
                timeoutCts.Cancel();
                return await observer.Completion;
            }

            ct.ThrowIfCancellationRequested();
            return MacroExpectWaitResult.TimedOut(timeoutMs);
        }
        finally
        {
            ClearActiveMacroExpectObserver(observer);
        }
    }

    private void OnMacroExpectTimedOut(MacroEntry entry, MacroExpectWaitResult result)
    {
        Core.Logging.FileLogger.Warn(
            $"Macro expect timed out after {result.TimeoutMs} ms; action={entry.ExpectOnTimeout}.");

        if (entry.ExpectOnTimeout != ExpectTimeoutAction.Abort)
        {
            return;
        }

        BeginInvokeIfAvailable(() =>
        {
            if (_disposed)
            {
                return;
            }

            UpdateStatus(
                "MacroExpectTimeout",
                displayTextOverride: _localizer?.Format(SshLocalizationKeys.StatusMacroExpectTimedOut, result.TimeoutMs)
                    ?? SshLocalizationKeys.StatusMacroExpectTimedOut,
                forceErrorBrush: true);
        });
    }

    private void ForwardToActiveMacroExpect(ReadOnlySpan<byte> data)
    {
        MacroExpectObserver? observer;
        lock (_macroExpectSync)
        {
            observer = _activeMacroExpectObserver;
        }

        observer?.Observe(data);
    }

    private void SetActiveMacroExpectObserver(MacroExpectObserver observer)
    {
        lock (_macroExpectSync)
        {
            if (_activeMacroExpectObserver is not null)
            {
                throw new InvalidOperationException("A macro expect wait is already active.");
            }

            _activeMacroExpectObserver = observer;
        }
    }

    private void ClearActiveMacroExpectObserver(MacroExpectObserver observer)
    {
        lock (_macroExpectSync)
        {
            if (ReferenceEquals(_activeMacroExpectObserver, observer))
            {
                _activeMacroExpectObserver = null;
            }
        }
    }

    private sealed class MacroExpectObserver
    {
        private readonly object _sync = new();
        private readonly ExpectMatcher _matcher = new();
        private readonly string _pattern;
        private readonly bool _isRegex;
        private readonly TaskCompletionSource<MacroExpectWaitResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public MacroExpectObserver(string pattern, bool isRegex)
        {
            _pattern = pattern;
            _isRegex = isRegex;
        }

        public Task<MacroExpectWaitResult> Completion => _completion.Task;

        public void Observe(ReadOnlySpan<byte> data)
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            lock (_sync)
            {
                if (_completion.Task.IsCompleted)
                {
                    return;
                }

                _matcher.Feed(data);
                if (_matcher.TryMatch(_pattern, _isRegex))
                {
                    _completion.TrySetResult(MacroExpectWaitResult.Matched);
                }
            }
        }
    }
}
