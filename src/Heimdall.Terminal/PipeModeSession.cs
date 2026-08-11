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

namespace Heimdall.Terminal;

/// <summary>
/// Pipe mode terminal session: launches an external process with redirected
/// stdin/stdout (no pseudo-console). VT sequences pass through raw, which is
/// required for plink SSH where ConPTY breaks arrow keys.
/// The -t flag on plink forces remote PTY allocation.
/// </summary>
public sealed class PipeModeSession : ITerminalSession
{
    private Process? _process;
    private Task? _readLoop;
    private Task? _stderrLoop;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private readonly object _processExitedLock = new();
    private Action<int>? _processExited;
    private bool _processExitedRaised;
    private int _processExitCode;
    private readonly object _dataLock = new();
    private readonly object _deliveryLock = new();
    private Action<ReadOnlyMemory<byte>>? _dataReceived;
    private List<byte[]>? _bootstrapBuffer = new();
    private int _bootstrapBufferedBytes;
    private bool _dataSubscriberAttached;
    private bool _bootstrapCapLogged;

    public event Action<ReadOnlyMemory<byte>>? DataReceived
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_deliveryLock)
            {
                bool firstSubscriber;
                byte[][] replay;
                Action<ReadOnlyMemory<byte>>? target;
                lock (_dataLock)
                {
                    _dataReceived += value;
                    target = _dataReceived;
                    firstSubscriber = !_dataSubscriberAttached;
                    if (firstSubscriber)
                    {
                        _dataSubscriberAttached = true;
                        replay = _bootstrapBuffer is { Count: > 0 }
                            ? _bootstrapBuffer.ToArray()
                            : [];
                        _bootstrapBuffer = null;
                    }
                    else
                    {
                        replay = [];
                    }
                }

                if (firstSubscriber)
                {
                    foreach (byte[] chunk in replay)
                    {
                        SafeInvokeDataReceived(target, chunk.AsMemory());
                    }
                }
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_dataLock)
            {
                _dataReceived -= value;
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
            StandardOutputEncoding = null,  // Binary mode — no internal StreamReader
            StandardErrorEncoding = null,  // Binary mode — allows direct BaseStream read
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

    public void Resize(int columns, int rows)
    {
        // Pipe mode cannot resize — the remote PTY size is fixed at connection time.
        // This is a known limitation vs ConPTY.
    }

    public void Kill()
    {
        if (_disposed || _process is null) return;
        KillProcess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        KillProcess();

        lock (_dataLock)
        {
            _bootstrapBuffer = null;
        }

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
                DeliverOrBuffer(copy);
            }
        }
        catch (OperationCanceledException) { /* Expected when session is disposed or cancelled */ }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] ReadStreamLoop: {ex.Message}"); }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = 0;
        try { exitCode = _process?.ExitCode ?? -1; } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] OnProcessExited: {ex.Message}"); }
        SafeInvokeProcessExitedOnce(exitCode);
    }

    private void DeliverOrBuffer(byte[] chunk)
    {
        lock (_deliveryLock)
        {
            Action<ReadOnlyMemory<byte>>? target;
            lock (_dataLock)
            {
                if (!_dataSubscriberAttached)
                {
                    BufferBootstrapChunk(chunk);
                    return;
                }

                target = _dataReceived;
            }

            SafeInvokeDataReceived(target, chunk.AsMemory());
        }
    }

    private void BufferBootstrapChunk(byte[] chunk)
    {
        if (_bootstrapBuffer is null)
        {
            return;
        }

        if (_bootstrapBufferedBytes + chunk.Length > Heimdall.Core.Configuration.AppConstants.MaxConPtyBootstrapBufferBytes)
        {
            if (!_bootstrapCapLogged)
            {
                _bootstrapCapLogged = true;
                Heimdall.Core.Logging.FileLogger.Warn(
                    "[PipeModeSession] Bootstrap output exceeded " +
                    $"{Heimdall.Core.Configuration.AppConstants.MaxConPtyBootstrapBufferBytes} bytes " +
                    "before a subscriber attached; dropping further bootstrap bytes.");
            }

            return;
        }

        _bootstrapBuffer.Add(chunk);
        _bootstrapBufferedBytes += chunk.Length;
    }

    private static void SafeInvokeDataReceived(
        Action<ReadOnlyMemory<byte>>? handler,
        ReadOnlyMemory<byte> data)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(data);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PipeModeSession] DataReceived subscriber: {ex.Message}");
        }
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
