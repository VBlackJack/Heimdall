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

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Terminal.ConPty;

/// <summary>
/// Terminal session backed by the Windows Pseudo Console (ConPTY) API.
/// Manages the full lifecycle: pipe creation, pseudo console allocation,
/// process launch, async output reading, resize, and cleanup.
/// </summary>
public sealed class ConPtySession : ITerminalSession
{
    private SafePseudoConsoleHandle? _pseudoConsole;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private IntPtr _attrList;
    private int _processId;

    // Pipe endpoints owned by this session:
    // - _pipeInputRead / _pipeOutputWrite are the child-side ends passed to ConPTY,
    //   kept alive for the console's lifetime.
    // - _pipeInputWrite / _pipeOutputRead own parent-side pipe handles until
    //   FileStream construction succeeds and takes ownership.
    // - _outputReader / _inputWriter are the parent-side FileStreams.
    private SafeFileHandle? _pipeInputRead;
    private SafeFileHandle? _pipeInputWrite;
    private SafeFileHandle? _pipeOutputRead;
    private SafeFileHandle? _pipeOutputWrite;
    private FileStream? _outputReader;
    private FileStream? _inputWriter;

    private Task? _readLoop;
    private Task? _exitWatchLoop;
    private CancellationTokenSource? _cts;

    private volatile bool _disposed;
    private readonly object _disposeLock = new();
    private readonly object _processExitedLock = new();
    private Action<int>? _processExited;
    private bool _processExitedRaised;
    private int _processExitCode;

    // Bootstrap-output buffering: a ConPTY child writes its prompt/banner within
    // milliseconds of launch, before the terminal view subscribes to DataReceived.
    // Those bytes are buffered here and replayed to the first subscriber so the
    // initial prompt is never lost. _deliveryLock serializes the bootstrap replay
    // with the read loop's direct deliveries to preserve byte ordering; _dataLock
    // guards the buffer + subscriber state. Lock order is always _deliveryLock then
    // _dataLock. Subscriber callbacks are invoked outside _dataLock.
    private readonly object _dataLock = new();
    private readonly object _deliveryLock = new();
    private Action<ReadOnlyMemory<byte>>? _dataReceived;
    private List<byte[]>? _bootstrapBuffer = new();
    private int _bootstrapBufferedBytes;
    private bool _dataSubscriberAttached;
    private bool _bootstrapCapLogged;

    /// <inheritdoc />
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

                // Replay buffered bootstrap output to the first subscriber in order,
                // outside _dataLock. Holding _deliveryLock here guarantees the read
                // loop cannot interleave a direct delivery ahead of this replay: the
                // read loop only delivers directly once _dataSubscriberAttached is
                // set (above, under _deliveryLock), so it must wait on _deliveryLock.
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            if (_disposed || _processHandle == IntPtr.Zero)
                return false;

            NativeMethods.GetExitCodeProcess(_processHandle, out uint exitCode);
            return exitCode == NativeMethods.STILL_ACTIVE;
        }
    }

    /// <inheritdoc />
    public int? ProcessId => _disposed ? null : _processId == 0 ? null : _processId;

    /// <inheritdoc />
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Returns true if the ConPTY API is available on this Windows version
    /// (Windows 10 1809+ / Windows Server 2019+).
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                IntPtr module = NativeMethods.GetModuleHandleW("kernel32.dll");
                if (module == IntPtr.Zero)
                    return false;
                return NativeMethods.GetProcAddress(module, "CreatePseudoConsole") != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] IsAvailable check: {ex.Message}");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(
        string executable,
        string arguments,
        int columns = 80,
        int rows = 24,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        if (_processHandle != IntPtr.Zero)
            throw new InvalidOperationException("Session already started. Dispose and create a new instance.");

        cancellationToken.ThrowIfCancellationRequested();

        CreatePipes(out SafeFileHandle inputRead, out SafeFileHandle inputWrite,
                    out SafeFileHandle outputRead, out SafeFileHandle outputWrite);

        // Keep all handles owned from this point; parent-side ownership is
        // transferred to FileStream after pseudo console setup succeeds.
        _pipeInputRead = inputRead;
        _pipeInputWrite = inputWrite;
        _pipeOutputRead = outputRead;
        _pipeOutputWrite = outputWrite;

        try
        {
            CreatePseudoConsole(columns, rows, inputRead, outputWrite);
            SetupProcessAttributeList();

            // Wrap parent-side pipe ends in FileStreams for managed I/O.
            const int bufferSize = 4096;
            _inputWriter = new FileStream(inputWrite, FileAccess.Write, bufferSize);
            _pipeInputWrite = null;
            _outputReader = new FileStream(outputRead, FileAccess.Read, bufferSize);
            _pipeOutputRead = null;

            LaunchProcess(executable, arguments, workingDirectory);
            StartExitWatchLoop();
            StartReadLoop();
        }
        catch
        {
            Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _inputWriter is null)
            return;

        try
        {
            _inputWriter.Write(data);
            _inputWriter.Flush();
        }
        catch (ObjectDisposedException) { /* Expected when session is being torn down */ }
        catch (IOException) { /* Pipe may be broken if process exited */ }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Encodes <paramref name="text"/> as UTF-8. Prefer the byte overload for
    /// escape sequences and binary payloads.
    /// </remarks>
    public void Write(string text)
    {
        if (_disposed || _inputWriter is null || string.IsNullOrEmpty(text))
            return;

        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> buffer = maxBytes <= 1024
            ? stackalloc byte[maxBytes]
            : new byte[maxBytes];

        int written = Encoding.UTF8.GetBytes(text, buffer);
        Write(buffer[..written]);
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        if (_disposed || _pseudoConsole is null || _pseudoConsole.IsInvalid)
            return;

        NativeMethods.COORD size = new NativeMethods.COORD(
            (short)ClampDimension(columns),
            (short)ClampDimension(rows));
        int hr = NativeMethods.ResizePseudoConsole(_pseudoConsole.DangerousGetHandle(), size);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    /// <inheritdoc />
    public void Kill()
    {
        if (_disposed || _processHandle == IntPtr.Zero)
            return;

        NativeMethods.GetExitCodeProcess(_processHandle, out uint exitCode);
        if (exitCode == NativeMethods.STILL_ACTIVE)
        {
            NativeMethods.TerminateProcess(_processHandle, 1);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // Signal the read loop to stop.
        _cts?.Cancel();

        // Release any unreplayed bootstrap output; no subscriber will consume it now.
        lock (_dataLock)
        {
            _bootstrapBuffer = null;
        }

        // Close pseudo console first — this signals the child process to exit
        // and unblocks any pending read on the output pipe.
        _pseudoConsole?.Dispose();
        _pseudoConsole = null;

        // Close managed streams (parent-side pipe ends).
        DisposeStream(ref _inputWriter);
        DisposeStream(ref _outputReader);

        // Close parent-side pipe handles if startup failed before FileStream
        // construction transferred ownership.
        _pipeInputWrite?.Dispose();
        _pipeInputWrite = null;
        _pipeOutputRead?.Dispose();
        _pipeOutputRead = null;

        // Close child-side pipe ends kept alive for the console.
        _pipeInputRead?.Dispose();
        _pipeInputRead = null;
        _pipeOutputWrite?.Dispose();
        _pipeOutputWrite = null;

        // Terminate the child process if still running.
        TerminateAndCloseProcess();

        // Free the attribute list.
        FreeAttributeList();

        // Wait briefly for lifecycle tasks to complete.
        if (_readLoop is not null)
        {
            try { _readLoop.Wait(TimeSpan.FromMilliseconds(500)); }
            catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Dispose read loop wait: {ex.Message}"); }
        }

        if (_exitWatchLoop is not null)
        {
            try { _exitWatchLoop.Wait(TimeSpan.FromMilliseconds(500)); }
            catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Dispose exit watch wait: {ex.Message}"); }
        }

        _cts?.Dispose();
        _cts = null;
    }

    // ========================================================================
    // Private helpers
    // ========================================================================

    private static void CreatePipes(
        out SafeFileHandle inputRead, out SafeFileHandle inputWrite,
        out SafeFileHandle outputRead, out SafeFileHandle outputWrite)
    {
        var sa = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1 // TRUE
        };

        if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe.");

        if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, ref sa, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe.");
        }
    }

    private void CreatePseudoConsole(
        int columns, int rows, SafeFileHandle inputRead, SafeFileHandle outputWrite)
    {
        NativeMethods.COORD size = new NativeMethods.COORD(
            (short)ClampDimension(columns),
            (short)ClampDimension(rows));
        int hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out IntPtr hPC);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        _pseudoConsole = new SafePseudoConsoleHandle(hPC);
    }

    private static int ClampDimension(int value)
        => Math.Clamp(value, 1, short.MaxValue);

    private void SetupProcessAttributeList()
    {
        nint attrSize = 0;
        // First call: query required buffer size (expected to fail).
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);

        _attrList = Marshal.AllocHGlobal((int)attrSize);
        if (!NativeMethods.InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

        if (!NativeMethods.UpdateProcThreadAttribute(
            _attrList,
            0,
            NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            _pseudoConsole!.DangerousGetHandle(),
            (nuint)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }
    }

    private void LaunchProcess(string executable, string arguments, string? workingDirectory = null)
    {
        string cmdLine = string.IsNullOrEmpty(arguments)
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments}";

        var si = new NativeMethods.STARTUPINFOEX
        {
            lpAttributeList = _attrList
        };
        si.StartupInfo.cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

        uint flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT
                   | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

        string? cwd = !string.IsNullOrWhiteSpace(workingDirectory) ? workingDirectory : null;

        // Build environment block with injected variables (merged with current process environment)
        IntPtr envBlock = IntPtr.Zero;
        if (EnvironmentVariables is { Count: > 0 })
        {
            envBlock = BuildEnvironmentBlock(EnvironmentVariables);
        }

        try
        {
            if (!NativeMethods.CreateProcessW(
                null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                false, flags, envBlock, cwd,
                ref si, out NativeMethods.PROCESS_INFORMATION pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
            }

            _processHandle = pi.hProcess;
            _threadHandle = pi.hThread;
            _processId = (int)pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(envBlock);
        }
    }

    /// <summary>
    /// Builds a Unicode environment block for CreateProcessW.
    /// Merges the current process environment with the additional variables.
    /// Format: VAR1=val1\0VAR2=val2\0...\0\0
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(Dictionary<string, string> additional)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Start with current process environment
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string val)
            {
                env[key] = val;
            }
        }

        // Merge additional variables (overwrite if conflict)
        foreach (var kvp in additional)
        {
            env[kvp.Key] = kvp.Value;
        }

        // Build the null-terminated Unicode block.
        // Format: KEY1=val1\0KEY2=val2\0...\0\0
        // Sort by Ordinal (strict Win32 requirement for environment blocks).
        var sb = new System.Text.StringBuilder();
        foreach (var kvp in env.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
        }
        sb.Append('\0'); // Double null terminator

        // Marshal.StringToHGlobalUni stops at the first \0.
        // Must manually copy the full UTF-16 byte array including embedded nulls.
        byte[] envBytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
        IntPtr block = Marshal.AllocHGlobal(envBytes.Length);
        Marshal.Copy(envBytes, 0, block, envBytes.Length);
        return block;
    }

    private void StartReadLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var reader = _outputReader!;

        _readLoop = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await reader.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (bytesRead <= 0)
                        break;

                    // Deliver a copy to subscribers so the buffer can be reused.
                    byte[] copy = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, copy, 0, bytesRead);
                    DeliverOrBuffer(copy);
                }
            }
            catch (OperationCanceledException) { /* Expected when session is disposed */ }
            catch (ObjectDisposedException) { /* Expected when disposing during read */ }
            catch (IOException) { /* Pipe broken after process exit */ }

            // Raise ProcessExited with the child's exit code.
            if (!_disposed)
            {
                int exitCode = -1;
                if (_processHandle != IntPtr.Zero)
                {
                    // Give the process a moment to finalize.
                    NativeMethods.WaitForSingleObject(_processHandle, 1000);
                    if (NativeMethods.GetExitCodeProcess(_processHandle, out uint ec))
                        exitCode = (int)ec;
                }
                SafeInvokeProcessExitedOnce(exitCode);
            }
        }, token);
    }

    private void StartExitWatchLoop()
    {
        IntPtr processHandle = _processHandle;
        if (processHandle == IntPtr.Zero)
        {
            return;
        }

        IntPtr currentProcess = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateHandle(
            currentProcess,
            processHandle,
            currentProcess,
            out IntPtr watchHandle,
            0,
            false,
            NativeMethods.DUPLICATE_SAME_ACCESS))
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[ConPtySession] Duplicate process handle failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        _exitWatchLoop = Task.Run(() =>
        {
            try
            {
                uint waitResult = NativeMethods.WaitForSingleObject(watchHandle, NativeMethods.INFINITE);
                if (waitResult == NativeMethods.WAIT_FAILED)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"[ConPtySession] Process wait failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                int exitCode = -1;
                if (NativeMethods.GetExitCodeProcess(watchHandle, out uint ec))
                {
                    exitCode = (int)ec;
                }

                if (!_disposed)
                {
                    SafeInvokeProcessExitedOnce(exitCode);
                }
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Exit watch: {ex.Message}");
            }
            finally
            {
                try { NativeMethods.CloseHandle(watchHandle); }
                catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Close watch handle: {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Delivers a read-loop chunk to the current subscriber, or buffers it as
    /// bootstrap output when no subscriber has attached yet. Serialized with the
    /// first-subscriber replay via _deliveryLock to preserve byte ordering.
    /// </summary>
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

    /// <summary>
    /// Appends a chunk to the bootstrap buffer. The caller must hold <see cref="_dataLock"/>.
    /// Buffering stops (and is logged once) when the configured cap is exceeded.
    /// </summary>
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
                    "[ConPtySession] Bootstrap output exceeded " +
                    $"{Heimdall.Core.Configuration.AppConstants.MaxConPtyBootstrapBufferBytes} bytes " +
                    "before a subscriber attached; dropping further bootstrap bytes.");
            }

            return;
        }

        _bootstrapBuffer.Add(chunk);
        _bootstrapBufferedBytes += chunk.Length;
    }

    private static void SafeInvokeDataReceived(Action<ReadOnlyMemory<byte>>? handler, ReadOnlyMemory<byte> data)
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
            Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] DataReceived subscriber: {ex.Message}");
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

        SafeInvokeProcessExitedHandlers(handlers, exitCode);
    }

    private static void SafeInvokeProcessExitedHandlers(Action<int>? handlers, int exitCode)
    {
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
            Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] ProcessExited subscriber: {ex.Message}");
        }
    }

    private void TerminateAndCloseProcess()
    {
        if (_processHandle != IntPtr.Zero)
        {
            try
            {
                NativeMethods.GetExitCodeProcess(_processHandle, out uint exitCode);
                if (exitCode == NativeMethods.STILL_ACTIVE)
                    NativeMethods.TerminateProcess(_processHandle, 1);
            }
            catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] TerminateAndCloseProcess: {ex.Message}"); }

            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        if (_threadHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }
    }

    private void FreeAttributeList()
    {
        if (_attrList != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
    }

    private static void DisposeStream<T>(ref T? stream) where T : Stream
    {
        if (stream is null)
            return;

        try { stream.Dispose(); }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] DisposeStream: {ex.Message}"); }
        stream = null;
    }
}
