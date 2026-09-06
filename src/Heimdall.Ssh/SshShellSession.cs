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

using System.Text;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh;

internal interface ISshShellStream : IDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    void Write(byte[] data, int offset, int count);

    void Flush();

    void ChangeWindowSize(uint columns, uint rows, uint width, uint height);

    void Close();
}

/// <summary>
/// Manages an interactive SSH shell session with PTY allocation.
/// Provides event-driven data reception and supports terminal resize.
/// Replaces the legacy plink + ConPTY / WebView2+xterm.js approach.
/// </summary>
public sealed class SshShellSession : IDisposable
{
    private const int ReadBufferSize = 8192;

    /// <summary>Terminal type requested for the PTY; matches the xterm.js front end.</summary>
    internal const string TerminalType = "xterm-256color";

    /// <summary>
    /// Best-effort wait for the read loop to honour cancellation before
    /// stream/client disposal begins.
    /// </summary>
    private static readonly TimeSpan StopReadLoopGraceful = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Final wait before accepting that a native SSH.NET pipe read may be stuck.
    /// </summary>
    private static readonly TimeSpan StopReadLoopFinal = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _timeProvider;
    private readonly Transport _transport;
    private SshClient? _client;
    private ISshShellStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;
    private readonly object _teardownGate = new();
    private readonly object _streamGate = new();
    private int _disconnectNotified;
    private bool _teardownStarted;
    private bool _disposed;

    /// <summary>
    /// Creates a shell session.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock backing the background teardown wait. Defaults to
    /// <see cref="TimeProvider.System"/>, so runtime behaviour is unchanged;
    /// tests substitute a controllable clock to drive teardown without
    /// depending on wall-clock time.
    /// </param>
    public SshShellSession(TimeProvider? timeProvider = null)
        : this(timeProvider, null)
    {
    }

    /// <summary>
    /// Creates a shell session whose network-facing steps are replaceable.
    /// </summary>
    /// <param name="timeProvider">Clock backing the background teardown wait.</param>
    /// <param name="transport">
    /// The steps of <see cref="ConnectAsync"/> that reach the network or SSH.NET.
    /// Defaults to <see cref="Transport.Default"/>, which is the production wiring.
    /// </param>
    internal SshShellSession(TimeProvider? timeProvider, Transport? transport)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transport = transport ?? Transport.Default;
    }

    /// <summary>
    /// The four steps of <see cref="ConnectAsync"/> that reach the network or
    /// SSH.NET, so a test can stand in for any one of them and drive the session
    /// past a step it cannot perform against a real server.
    /// </summary>
    /// <param name="ResolveHostKeyAsync">Probes and pins the server host key.</param>
    /// <param name="CreateClient">Builds the SSH.NET client for the connection parameters.</param>
    /// <param name="ConnectAsync">Runs the SSH handshake on a built client.</param>
    /// <param name="CreateShellStream">Opens the PTY shell stream on a connected client.</param>
    internal sealed record Transport(
        Func<SshConnectionParams, HostKeyStore, IHostKeyVerifier, CancellationToken, Task<PinnedFingerprintVerifier>> ResolveHostKeyAsync,
        Func<SshConnectionParams, SshClient> CreateClient,
        Func<SshClient, CancellationToken, Task> ConnectAsync,
        Func<SshClient, int, int, ISshShellStream> CreateShellStream)
    {
        /// <summary>The production wiring.</summary>
        public static Transport Default { get; } = new(
            SshConnectionFactory.ResolveHostKeyAsync,
            SshConnectionFactory.CreateSshClient,
            SshConnectionFactory.ConnectWithCancellationAsync,
            CreateDefaultShellStream);
    }

    private static ISshShellStream CreateDefaultShellStream(SshClient client, int terminalColumns, int terminalRows)
    {
        ShellStream shellStream = client.CreateShellStream(
            terminalName: TerminalType,
            columns: (uint)terminalColumns,
            rows: (uint)terminalRows,
            width: 0,
            height: 0,
            bufferSize: ReadBufferSize);
        return new SshNetShellStream(shellStream);
    }

    /// <summary>Raised when data is received from the remote shell.</summary>
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Raised when the session is disconnected. The argument contains the
    /// classified SSH failure when one is available.
    /// </summary>
    public event Action<SshSessionDisconnectInfo>? Disconnected;

    /// <summary>
    /// Raised when a security-relevant failure occurs. Fired in addition to <see cref="Disconnected"/>.
    /// </summary>
    public event Action<SshSessionSecurityEvent>? SecurityEventOccurred;

    /// <summary>Whether the underlying SSH connection is active.</summary>
    public bool IsConnected => _client?.IsConnected == true && Volatile.Read(ref _stream) is not null;

    /// <summary>Exposes the underlying SSH client for multiplexed operations (e.g. health monitoring).</summary>
    public SshClient? Client => _client;

    /// <summary>
    /// Connects to the SSH server, allocates a PTY, and starts the interactive shell.
    /// A background read loop begins immediately after connection, raising
    /// <see cref="DataReceived"/> for each chunk of output.
    /// </summary>
    /// <param name="connectionParams">SSH connection parameters.</param>
    /// <param name="hostKeyStore">TOFU host key store for server verification.</param>
    /// <param name="hostKeyVerifier">Verifier used when a host key is unknown or changed.</param>
    /// <param name="terminalColumns">Initial terminal width in columns.</param>
    /// <param name="terminalRows">Initial terminal height in rows.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    public async Task ConnectAsync(
        SshConnectionParams connectionParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        int terminalColumns = 80,
        int terminalRows = 24,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connectionParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        // Fail fast on a pre-cancelled token: without this, the linked CTS
        // and read-loop Task.Run further down would silently swallow the
        // cancellation, leaving the caller unaware that nothing happened.
        cancellationToken.ThrowIfCancellationRequested();

        lock (_teardownGate)
        {
            if (_teardownStarted)
            {
                throw new InvalidOperationException("Session teardown is in progress.");
            }

            if (_client is not null)
            {
                throw new InvalidOperationException("Session is already connected. Call Disconnect() first.");
            }

            _disconnectNotified = 0;
        }

        PinnedFingerprintVerifier pinnedVerifier = await _transport.ResolveHostKeyAsync(
                connectionParams,
                hostKeyStore,
                hostKeyVerifier,
                cancellationToken)
            .ConfigureAwait(false);

        SshClient client = _transport.CreateClient(connectionParams);
        _client = client;

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            client,
            connectionParams,
            pinnedVerifier);

        await _transport.ConnectAsync(client, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        ISshShellStream shellStream = _transport.CreateShellStream(client, terminalColumns, terminalRows);
        lock (_streamGate)
        {
            _stream = shellStream;
        }

        // The connect token governs the connect, and nothing after it. Once the
        // shell is up the session is owned through Disconnect/Dispose, which
        // every owner in the application calls; a read loop that also listened
        // to the connect token exited silently when that token was cancelled
        // after a successful connect, with no cleanup and no Disconnected
        // event, leaving a connected client and its key file alive with
        // nothing watching them.
        CancellationTokenSource readCts = new CancellationTokenSource();
        _readCts = readCts;
        _readLoopTask = Task.Run(() => ReadLoopAsync(readCts.Token), readCts.Token);
    }

    /// <summary>Writes raw bytes to the shell's standard input.</summary>
    /// <param name="data">Byte data to send.</param>
    public void Write(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_streamGate)
        {
            ISshShellStream? stream = _stream;
            if (stream is null)
            {
                throw new InvalidOperationException("Session is not connected.");
            }

            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
    }

    /// <summary>Writes a UTF-8 encoded string to the shell's standard input.</summary>
    /// <param name="text">Text to send.</param>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Sends a terminal window-change request to the server,
    /// notifying it of the new terminal dimensions.
    /// </summary>
    /// <param name="columns">New terminal width in columns.</param>
    /// <param name="rows">New terminal height in rows.</param>
    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_streamGate)
        {
            ISshShellStream? stream = _stream;
            if (stream is null)
            {
                throw new InvalidOperationException("Session is not connected.");
            }

            try
            {
                stream.ChangeWindowSize((uint)columns, (uint)rows, 0u, 0u);
            }
            catch (ObjectDisposedException ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"SSH window-change request skipped because the shell stream is disposed: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"SSH window-change request failed for {columns}x{rows}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gracefully disconnects the SSH session, stopping the read loop and
    /// closing the shell stream and client connection.
    /// </summary>
    public void Disconnect()
    {
        BeginTeardown(notifyDisconnected: true, markDisposed: false, operationName: nameof(Disconnect));
    }

    public void Dispose()
    {
        BeginTeardown(notifyDisconnected: false, markDisposed: true, operationName: nameof(Dispose));
    }

    /// <summary>
    /// Background loop that reads data from the shell stream and dispatches it
    /// via the <see cref="DataReceived"/> event. Runs until cancellation or disconnect.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        SshSessionDisconnectInfo? disconnectInfo = null;
        Exception? disconnectException = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                ISshShellStream? stream = Volatile.Read(ref _stream);
                if (stream is null)
                {
                    break;
                }

                int bytesRead;

                try
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    if (!_disposed && !cancellationToken.IsCancellationRequested)
                    {
                        disconnectInfo = CreateShellEofDisconnectInfo(_client?.IsConnected == true);
                    }

                    break;
                }

                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    disconnectInfo = CreateShellEofDisconnectInfo(_client?.IsConnected == true);
                    break;
                }

                var chunk = new byte[bytesRead];
                Array.Copy(buffer, chunk, bytesRead);
                DataReceived?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during Disconnect/Dispose
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                disconnectException = ex;
            }
        }

        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (disconnectException is not null || disconnectInfo is not null)
        {
            CleanupAfterRemoteDisconnect();
        }

        if (disconnectException is not null)
        {
            SshSessionFailureDispatcher.Dispatch(
                disconnectException,
                SecurityEventOccurred,
                NotifyDisconnected);
            return;
        }

        if (disconnectInfo is not null)
        {
            NotifyDisconnected(disconnectInfo);
        }
    }

    internal static SshSessionDisconnectInfo CreateShellEofDisconnectInfo(bool transportConnected)
    {
        if (transportConnected)
        {
            return SshSessionDisconnectInfo.Clean()
                .WithMessageKey(SshDisconnectMessageKeys.MessageKeyRemoteShellExited);
        }

        var failure = new SshFailureInfo(
            SshFailureCode.SessionDisconnected,
            "SSH session disconnected.",
            IsFatal: false);
        return SshSessionDisconnectInfo.FromFailure(failure);
    }

    private void NotifyDisconnected(SshSessionDisconnectInfo disconnectInfo)
    {
        if (Interlocked.Exchange(ref _disconnectNotified, 1) == 0)
        {
            Disconnected?.Invoke(disconnectInfo);
        }
    }

    private void CleanupAfterRemoteDisconnect()
    {
        DisposeReadLoopCancellationSource();
        CleanupStream();
        DisconnectClient();
    }

    private void BeginTeardown(
        bool notifyDisconnected,
        bool markDisposed,
        string operationName)
    {
        Task? pending;
        lock (_teardownGate)
        {
            if (markDisposed)
            {
                _disposed = true;
            }

            if (_teardownStarted)
            {
                return;
            }

            if (!markDisposed && (_disposed || _client is null))
            {
                return;
            }

            if (markDisposed
                && _client is null
                && Volatile.Read(ref _stream) is null
                && _readLoopTask is null
                && _readCts is null)
            {
                return;
            }

            _teardownStarted = true;
            pending = _readLoopTask;
        }

        bool loopExited = StopReadLoop();
        pending = _readLoopTask ?? pending;
        if (loopExited || pending is null || pending.IsCompleted)
        {
            ObserveCompletedReadLoop(pending);
            FinishTeardown(notifyDisconnected);
            return;
        }

        QueueBackgroundTeardown(pending, notifyDisconnected, operationName);
    }

    private void QueueBackgroundTeardown(
        Task pending,
        bool notifyDisconnected,
        string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Task timeout = Task.Delay(StopReadLoopFinal, _timeProvider);
                Task completed = await Task.WhenAny(pending, timeout).ConfigureAwait(false);
                if (completed == pending)
                {
                    await ObserveReadLoopAsync(pending).ConfigureAwait(false);
                }
                else
                {
                    Core.Logging.FileLogger.Error(
                        "SshShellSession: read loop is still running after a "
                        + $"{StopReadLoopFinal.TotalSeconds:F0}-second background wait during {operationName}. "
                        + "Underlying SSH.NET pipe may be stuck; task will be leaked.");
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Debug("SSH shell background teardown suppressed", ex);
            }
            finally
            {
                FinishTeardown(notifyDisconnected);
            }
        });
    }

    private static async Task ObserveReadLoopAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SshShellSession read loop teardown observed: {ex.Message}");
        }
    }

    private static void ObserveCompletedReadLoop(Task? pending)
    {
        if (pending is null || !pending.IsCompleted)
        {
            return;
        }

        try
        {
            pending.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SshShellSession read loop teardown observed: {ex.Message}");
        }
    }

    private void FinishTeardown(bool notifyDisconnected)
    {
        DisposeReadLoopCancellationSource();
        Interlocked.Exchange(ref _readLoopTask, null);

        CleanupStream();
        DisconnectClient();

        if (notifyDisconnected)
        {
            NotifyDisconnected(SshSessionDisconnectInfo.Clean());
        }

        lock (_teardownGate)
        {
            _teardownStarted = false;
        }
    }

    /// <summary>
    /// Signals the read loop to stop and waits briefly for it to complete.
    /// The cancellation source is disposed by the caller after the final wait.
    /// </summary>
    private bool StopReadLoop()
    {
        var cts = _readCts;
        if (cts is null)
        {
            return true;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return _readLoopTask is null || _readLoopTask.IsCompleted;
        }

        var task = _readLoopTask;
        if (task is null)
        {
            return true;
        }

        try
        {
            if (!task.Wait(StopReadLoopGraceful))
            {
                Core.Logging.FileLogger.Warn(
                    "SshShellSession: read loop did not honour cancellation within "
                    + $"{StopReadLoopGraceful.TotalMilliseconds:F0} ms; will retry during final teardown.");
                return false;
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException or ObjectDisposedException))
        {
            // Expected from task cancellation during teardown
        }
        catch (AggregateException ex)
        {
            Core.Logging.FileLogger.Warn($"SshShellSession read loop stop: {ex.InnerException?.Message ?? ex.Message}");
        }

        return true;
    }

    private void DisposeReadLoopCancellationSource()
    {
        var cts = Interlocked.Exchange(ref _readCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Closes and disposes the shell stream.</summary>
    private void CleanupStream()
    {
        lock (_streamGate)
        {
            ISshShellStream? stream = _stream;
            _stream = null;
            if (stream is not null)
            {
                try
                {
                    stream.Close();
                    stream.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Expected when disposing already-closed resources.
                }
                catch (Exception ex)
                {
                    FileLogger.Debug("SSH shell stream cleanup suppressed", ex);
                }
            }
        }
    }

    private sealed class SshNetShellStream(ShellStream inner) : ISshShellStream
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public void Write(byte[] data, int offset, int count)
        {
            inner.Write(data, offset, count);
        }

        public void Flush()
        {
            inner.Flush();
        }

        public void ChangeWindowSize(uint columns, uint rows, uint width, uint height)
        {
            inner.ChangeWindowSize(columns, rows, width, height);
        }

        public void Close()
        {
            inner.Close();
        }

        public void Dispose()
        {
            inner.Dispose();
        }
    }

    /// <summary>Disconnects and disposes the SSH client.</summary>
    private void DisconnectClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                client.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Expected when disposing already-closed resources.
            }
            catch (Exception ex)
            {
                FileLogger.Debug("SSH shell client cleanup suppressed", ex);
            }
        }
    }
}
