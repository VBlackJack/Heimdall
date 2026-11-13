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

using System.Diagnostics;
using System.Text;
using Heimdall.Core.Terminal;

namespace Heimdall.Terminal;

/// <summary>
/// Pipe mode terminal session: launches an external process with redirected
/// stdin/stdout (no pseudo-console). VT sequences pass through raw, which is
/// required for plink SSH where ConPTY breaks arrow keys.
/// The -t flag on plink forces remote PTY allocation.
/// </summary>
public sealed class PipeModeSession : ITerminalSession, IFirstOutputSignal
{
    private Process? _process;
    private Task? _readLoop;
    private Task? _stderrLoop;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private int _disposedFlag;
    private readonly object _processExitedLock = new();
    private Action<int>? _processExited;
    private bool _processExitedRaised;
    private int _processExitCode;
    private readonly object _firstOutputLock = new();
    private Action? _firstOutputReceived;
    private bool _firstOutputRaised;
    private readonly OutputReplayGate _output = new(nameof(PipeModeSession));

    public event Action<ReadOnlyMemory<byte>>? DataReceived
    {
        add => _output.AddMemory(value);
        remove => _output.RemoveMemory(value);
    }

    /// <inheritdoc />
    public event Action? FirstOutputReceived
    {
        add
        {
            if (value is null)
            {
                return;
            }

            bool invokeImmediately;
            lock (_firstOutputLock)
            {
                invokeImmediately = _firstOutputRaised;
                if (!invokeImmediately)
                {
                    _firstOutputReceived += value;
                }
            }

            if (invokeImmediately)
            {
                SafeInvokeFirstOutputHandler(value);
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_firstOutputLock)
            {
                _firstOutputReceived -= value;
            }
        }
    }

    public event Action<int>? ProcessExited
    {
        add
        {
            if (value is null)
            {
                return;
            }

            bool invokeImmediately;
            int exitCode;
            lock (_processExitedLock)
            {
                invokeImmediately = _processExitedRaised;
                exitCode = _processExitCode;
                if (!invokeImmediately)
                {
                    _processExited += value;
                }
            }

            if (invokeImmediately)
            {
                SafeInvokeProcessExitedHandler(value, exitCode);
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_processExitedLock)
            {
                _processExited -= value;
            }
        }
    }

    public bool IsRunning => _process is not null && !_process.HasExited;
    public int? ProcessId => _process?.Id;
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// The width the session was launched with, or <see langword="null"/> before it started. The
    /// remote PTY keeps this size for the whole session (see <see cref="Resize"/>), so an owner that
    /// sees the terminal grow or shrink can tell that the session did not follow.
    /// </summary>
    public int? LaunchColumns { get; private set; }

    /// <summary>The height the session was launched with, or <see langword="null"/> before it started.</summary>
    public int? LaunchRows { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="columns"/> and <paramref name="rows"/> are the size the caller knows
    /// before the launch, and the launch is the only moment the size can matter: the pipe
    /// transport has no channel to the remote PTY afterwards (see <see cref="Resize"/>). This
    /// method does not deliver them to the child. Windows Plink builds its PTY request only from
    /// its saved-session configuration (<c>TermWidth</c>/<c>TermHeight</c>), never from a console,
    /// so the SSH handler carries the initial size in a temporary PuTTY saved session passed with
    /// <c>-load</c> in <paramref name="arguments"/>. The parameters are kept so the size the caller
    /// resolved stays visible at the launch boundary.
    /// </remarks>
    public Task StartAsync(
        string executable,
        string arguments,
        int columns = 80,
        int rows = 24,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipeModeSession));
        if (_process is not null) throw new InvalidOperationException("Session already started.");

        cancellationToken.ThrowIfCancellationRequested();

        LaunchColumns = columns;
        LaunchRows = rows;
        _cts = new CancellationTokenSource();
        bool processStarted = false;

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = null,  // Binary mode - no internal StreamReader
            StandardErrorEncoding = null,  // Binary mode - allows direct BaseStream read
            WorkingDirectory = workingDirectory ?? string.Empty
        };

        // Inject additional environment variables (contextual shells)
        if (EnvironmentVariables is { Count: > 0 })
        {
            foreach (var kvp in EnvironmentVariables)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {executable}");
            }

            processStarted = true;

            // Background read loops for stdout + stderr (both → DataReceived events)
            // Stderr must be merged so Plink host key prompts and error messages
            // are visible in the terminal.
            _readLoop = Task.Run(() => ReadStreamLoop(_process.StandardOutput.BaseStream, _cts.Token), _cts.Token);
            _stderrLoop = Task.Run(() => ReadStreamLoop(_process.StandardError.BaseStream, _cts.Token), _cts.Token);
        }
        catch
        {
            CleanupFailedStart(processStarted);
            throw;
        }

        return Task.CompletedTask;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _process?.HasExited != false) return;
        try
        {
            _process.StandardInput.BaseStream.Write(data);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] Write: {ex.Message}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Encodes <paramref name="text"/> as UTF-8. Prefer the byte overload for
    /// escape sequences and binary payloads.
    /// </remarks>
    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Write(Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A no-op by construction, not by omission. The child talks through anonymous pipes, and a
    /// pipe carries bytes only: there is no console handle to resize and no out-of-band message
    /// Plink would read for a window change, and Plink never sends one on its own. The remote PTY
    /// keeps the size it was created with, which the initial temporary PuTTY session sets;
    /// ConPTY-backed sessions resize, this transport cannot.
    /// </remarks>
    public void Resize(int columns, int rows)
    {
    }

    public void Kill()
    {
        if (_disposed || _process is null) return;
        KillProcess();
    }

    /// <summary>
    /// Raised once when the session is disposed, after the process was killed. Disposal detaches
    /// the exit notification, so an owner holding a temporary file for the process cannot rely on
    /// <see cref="ProcessExited"/> to learn that the session ended when its tab is closed.
    /// </summary>
    public event Action? Disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        _disposed = true;

        _cts?.Cancel();
        KillProcess();

        // Each subscriber in its own try: one that throws must not skip the next.
        Action? subscribers = Disposed;
        if (subscribers is not null)
        {
            foreach (Action subscriber in subscribers.GetInvocationList().Cast<Action>())
            {
                try
                {
                    subscriber();
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] Disposed subscriber: {ex.Message}");
                }
            }
        }

        _output.Release();

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
        }
        try { _process?.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] Dispose process: {ex.Message}"); }
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void CleanupFailedStart(bool processStarted)
    {
        _cts?.Cancel();

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            if (processStarted)
            {
                KillProcess();
            }

            try { _process.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] CleanupFailedStart process: {ex.Message}"); }
            _process = null;
        }

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
        _stderrLoop = null;
    }

    private void KillProcess()
    {
        try
        {
            if (_process?.HasExited == false)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] Kill: {ex.Message}"); }
    }

    private async Task ReadStreamLoop(System.IO.Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                byte[] copy = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, copy, 0, bytesRead);
                _output.Deliver(copy);

                // After the delivery: "raised" implies the chunk was delivered or buffered, so a
                // reaction to the signal that subscribes to DataReceived still gets it in the replay.
                RaiseFirstOutputOnce();
            }
        }
        catch (OperationCanceledException) { /* Expected when session is disposed or cancelled */ }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] ReadStreamLoop: {ex.Message}"); }
    }

    private void RaiseFirstOutputOnce()
    {
        Action? handlers;
        lock (_firstOutputLock)
        {
            if (_firstOutputRaised)
            {
                return;
            }

            _firstOutputRaised = true;
            handlers = _firstOutputReceived;
            _firstOutputReceived = null;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            SafeInvokeFirstOutputHandler(handler);
        }
    }

    private static void SafeInvokeFirstOutputHandler(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] FirstOutputReceived subscriber: {ex.Message}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = 0;
        try { exitCode = _process?.ExitCode ?? -1; } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] OnProcessExited: {ex.Message}"); }
        SafeInvokeProcessExitedOnce(exitCode);
    }

    private void SafeInvokeProcessExitedOnce(int exitCode)
    {
        Action<int>? handlers;
        lock (_processExitedLock)
        {
            if (_processExitedRaised)
            {
                return;
            }

            _processExitedRaised = true;
            _processExitCode = exitCode;
            handlers = _processExited;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<int> handler in handlers.GetInvocationList())
        {
            SafeInvokeProcessExitedHandler(handler, exitCode);
        }
    }

    private static void SafeInvokeProcessExitedHandler(Action<int> handler, int exitCode)
    {
        try
        {
            handler(exitCode);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] ProcessExited subscriber: {ex.Message}");
        }
    }
}
