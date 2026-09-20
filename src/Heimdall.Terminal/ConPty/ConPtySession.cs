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
    /// <summary>Buffer size for both the pipe FileStreams and the read loop.</summary>
    private const int PipeBufferBytes = 4096;

    /// <summary>Exit code reported for a child this session terminated itself.</summary>
    private const uint KillExitCode = 1;

    /// <summary>How long the read loop lets a child finalize before reading its exit code.</summary>
    private const uint ExitFinalizeWaitMilliseconds = 1000;

    /// <summary>
    /// Reported when the exit code could not be established. A failed query is not an exit
    /// code, and reporting one would make the terminal announce an end that never happened.
    /// </summary>
    private const int UnknownExitCode = -1;

    /// <summary>Failure bound on joining a lifecycle thread in <see cref="Dispose"/>.</summary>
    private static readonly TimeSpan LoopJoinTimeout = TimeSpan.FromMilliseconds(500);

    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeProcessHandle? _processHandle;
    private IntPtr _attrList;
    private int _processId;

    // Pipe endpoints owned by this session:
    // - _pipeInputRead / _pipeOutputWrite are the child-side ends handed to ConPTY. The
    //   pseudo console duplicates them, so our copies are released as soon as it exists;
    //   these fields only carry them far enough for the failure path to clean up.
    // - _pipeInputWrite / _pipeOutputRead own parent-side pipe handles until
    //   FileStream construction succeeds and takes ownership.
    // - _outputReader / _inputWriter are the parent-side FileStreams.
    private SafeFileHandle? _pipeInputRead;
    private SafeFileHandle? _pipeInputWrite;
    private SafeFileHandle? _pipeOutputRead;
    private SafeFileHandle? _pipeOutputWrite;
    private FileStream? _outputReader;
    private FileStream? _inputWriter;

    // Dedicated threads, not pool work items: the output pipe is synchronous, so its read
    // blocks for the whole session, and the exit watch blocks on an infinite wait. Two pool
    // threads parked per open shell is a starvation source this codebase already pays for.
    private Thread? _readLoop;
    private Thread? _exitWatchLoop;
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
            SafeProcessHandle? handle = _processHandle;
            if (_disposed || handle is null || handle.IsInvalid)
                return false;

            try
            {
                // A failed query is not evidence of an exit. Reporting "not running" for it
                // made a transient failure indistinguishable from the shell ending.
                return !NativeMethods.GetExitCodeProcess(handle, out uint exitCode)
                    || exitCode == NativeMethods.STILL_ACTIVE;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
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

        if (_processHandle is not null)
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

            // Release our copies of the child-side ends now that the pseudo console owns
            // duplicates. Holding the output write end kept a writer on the pipe alive for
            // the session's whole life, so the read loop could never see EOF and could only
            // ever end at Dispose.
            _pipeInputRead = null;
            inputRead.Dispose();
            _pipeOutputWrite = null;
            outputWrite.Dispose();

            SetupProcessAttributeList();

            // Wrap parent-side pipe ends in FileStreams for managed I/O.
            _inputWriter = new FileStream(inputWrite, FileAccess.Write, PipeBufferBytes);
            _pipeInputWrite = null;
            _outputReader = new FileStream(outputRead, FileAccess.Read, PipeBufferBytes);
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
        if (_disposed)
            return;

        TerminateIfRunning();
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

        // Close pseudo console first - this signals the child process to exit
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

        // Close any child-side pipe ends the failure path left behind; on the success
        // path StartAsync already released them to the pseudo console.
        _pipeInputRead?.Dispose();
        _pipeInputRead = null;
        _pipeOutputWrite?.Dispose();
        _pipeOutputWrite = null;

        // Terminate the child process if still running. The handle stays open until the
        // lifecycle threads have been joined below: the exit watch is blocked inside an
        // infinite wait on it, and terminating is what releases that wait.
        TerminateIfRunning();

        // Free the attribute list.
        FreeAttributeList();

        // Wait briefly for the lifecycle threads to finish.
        JoinLoopThread(_readLoop, "read loop");
        JoinLoopThread(_exitWatchLoop, "exit watch");
        _readLoop = null;
        _exitWatchLoop = null;

        // Only now is no thread able to enter an interop call with this handle. The
        // SafeProcessHandle would defer the close for an in-flight call anyway, which is
        // the point of carrying one: a raw handle closed here could be reused by Windows
        // for an unrelated object while another thread still held its numeric value.
        _processHandle?.Dispose();
        _processHandle = null;

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
        // Non-inheritable: CreateProcessW is called with bInheritHandles = false, so marking
        // them inheritable bought nothing and leaked all four ends into every unrelated child
        // the application starts with inheritance on. One such child holding the output write
        // end keeps this session's pipe alive long after its shell has died.
        var sa = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = 0 // FALSE
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
        // A Windows path cannot contain a quote, so one here only ever arrives from a profile
        // trying to close the quoted program token and append its own command.
        if (executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Executable path must not contain a quote character.", nameof(executable));
        }

        string cmdLine = string.IsNullOrEmpty(arguments)
            ? $"\"{executable}\""
            : $"\"{executable}\" {arguments}";

        var si = new NativeMethods.STARTUPINFOEX
        {
            lpAttributeList = _attrList
        };
        si.StartupInfo.cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

        // Declare the standard handles, and declare them empty. Without STARTF_USESTDHANDLES,
        // CreateProcessW propagates the parent's own standard handles into the child, and the
        // pseudo console attribute does not override them. When the parent's handles are pipes
        // rather than console handles - a redirected host, a test runner, a service - the shell
        // takes the pipes, reads end-of-file on its input and exits cleanly. That is the
        // "Local shell started ... [Session ended: Process exited with code 0]" report: the
        // session received exactly the sixteen bytes conhost emits on its own account and
        // nothing the shell wrote, because the shell was never on the other end of it.
        //
        // Measured 2026-09-20 in such a host: without the flag, four starts out of four exited
        // with code 0 after roughly 750 ms having delivered 16 bytes; with it, four out of four
        // stayed alive and delivered the full 121-byte prompt. Leaving the three handles null
        // is what makes conhost hand the child the pseudo console's own.
        si.StartupInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
        si.StartupInfo.hStdInput = IntPtr.Zero;
        si.StartupInfo.hStdOutput = IntPtr.Zero;
        si.StartupInfo.hStdError = IntPtr.Zero;

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

            _processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            _processId = (int)pi.dwProcessId;

            // Nothing resumes or waits on the primary thread, so close it here rather than
            // carry a second raw handle for the session's lifetime.
            if (pi.hThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(pi.hThread);
            }
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
        CancellationToken token = _cts.Token;
        FileStream reader = _outputReader!;

        _readLoop = StartLoopThread("Heimdall ConPTY read", () =>
        {
            byte[] buffer = new byte[PipeBufferBytes];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // A blocking read on purpose: the pipe handle is synchronous, so the
                    // async overload would block a pool thread just the same, only less
                    // visibly. This thread exists to be blocked.
                    int bytesRead = reader.Read(buffer, 0, buffer.Length);
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
                SafeInvokeProcessExitedOnce(ReadExitCode(ExitFinalizeWaitMilliseconds));
            }
        });
    }

    private void StartExitWatchLoop()
    {
        SafeProcessHandle? handle = _processHandle;
        if (handle is null || handle.IsInvalid)
        {
            return;
        }

        // No duplicate is needed any more: the SafeProcessHandle keeps the underlying handle
        // alive for the whole of the interop call below, so a concurrent Dispose defers the
        // close rather than pulling the handle out from under this wait.
        _exitWatchLoop = StartLoopThread("Heimdall ConPTY exit watch", () =>
        {
            try
            {
                uint waitResult = NativeMethods.WaitForSingleObject(handle, NativeMethods.INFINITE);
                if (waitResult == NativeMethods.WAIT_FAILED)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"[ConPtySession] Process wait failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                if (!_disposed)
                {
                    SafeInvokeProcessExitedOnce(ReadExitCode(0));
                }
            }
            catch (ObjectDisposedException) { /* Expected when disposing during the wait */ }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Exit watch: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Starts a background thread for one of the session's lifecycle loops.
    /// </summary>
    private static Thread StartLoopThread(string name, Action body)
    {
        Thread thread = new Thread(() => body())
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
        return thread;
    }

    /// <summary>
    /// Waits, bounded, for a lifecycle thread to finish. The bound is a failure bound: on the
    /// normal path the child is already terminated and both threads return at once.
    /// </summary>
    private static void JoinLoopThread(Thread? thread, string what)
    {
        if (thread is null)
        {
            return;
        }

        try
        {
            if (!thread.Join(LoopJoinTimeout))
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[ConPtySession] Dispose {what} did not finish within {LoopJoinTimeout.TotalMilliseconds} ms.");
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] Dispose {what} join: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the child's exit code, first giving it <paramref name="finalizeWaitMilliseconds"/>
    /// to finalize. Returns <see cref="UnknownExitCode"/> when the query fails or the child is
    /// still running: neither is an exit code, and passing one off as a code made the terminal
    /// announce an end that had not happened.
    /// </summary>
    private int ReadExitCode(uint finalizeWaitMilliseconds)
    {
        SafeProcessHandle? handle = _processHandle;
        if (handle is null || handle.IsInvalid)
        {
            return UnknownExitCode;
        }

        try
        {
            if (finalizeWaitMilliseconds > 0)
            {
                NativeMethods.WaitForSingleObject(handle, finalizeWaitMilliseconds);
            }

            if (!NativeMethods.GetExitCodeProcess(handle, out uint exitCode)
                || exitCode == NativeMethods.STILL_ACTIVE)
            {
                return UnknownExitCode;
            }

            return (int)exitCode;
        }
        catch (ObjectDisposedException)
        {
            return UnknownExitCode;
        }
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

    /// <summary>
    /// Terminates the child if it is still running, leaving the handle open for the callers
    /// that still need it. A failed exit-code query is treated as "still running": skipping
    /// the terminate on a query failure orphaned the child.
    /// </summary>
    private void TerminateIfRunning()
    {
        SafeProcessHandle? handle = _processHandle;
        if (handle is null || handle.IsInvalid)
        {
            return;
        }

        try
        {
            if (!NativeMethods.GetExitCodeProcess(handle, out uint exitCode)
                || exitCode == NativeMethods.STILL_ACTIVE)
            {
                NativeMethods.TerminateProcess(handle, KillExitCode);
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[ConPtySession] TerminateIfRunning: {ex.Message}");
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
