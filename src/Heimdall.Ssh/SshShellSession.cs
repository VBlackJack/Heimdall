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

/// <summary>
/// Manages an interactive SSH shell session with PTY allocation.
/// Provides event-driven data reception and supports terminal resize.
/// Replaces the legacy plink + ConPTY / WebView2+xterm.js approach.
/// </summary>
public sealed class SshShellSession : IDisposable
{
    private const int ReadBufferSize = 8192;

    /// <summary>
    /// Best-effort wait for the read loop to honour cancellation before
    /// stream/client disposal begins.
    /// </summary>
    private static readonly TimeSpan StopReadLoopGraceful = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Final wait before accepting that a native SSH.NET pipe read may be stuck.
    /// </summary>
    private static readonly TimeSpan StopReadLoopFinal = TimeSpan.FromSeconds(2);

    private SshClient? _client;
    private ShellStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;
    private int _disconnectNotified;
    private bool _disposed;

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
    public bool IsConnected => _client?.IsConnected == true && _stream is not null;

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

        if (_client is not null)
        {
            throw new InvalidOperationException("Session is already connected. Call Disconnect() first.");
        }

        _disconnectNotified = 0;

        var pinnedVerifier = await SshConnectionFactory.ResolveHostKeyAsync(
                connectionParams,
                hostKeyStore,
                hostKeyVerifier,
                cancellationToken)
            .ConfigureAwait(false);

        var connectionInfo = SshConnectionFactory.Create(connectionParams);
        _client = new SshClient(connectionInfo);

        SshConnectionFactory.AttachPinnedHostKeyVerification(
            _client,
            connectionParams,
            pinnedVerifier);

        await using var connectReg = cancellationToken.Register(
            () => { try { _client.Disconnect(); } catch (Exception ex) { Core.Logging.FileLogger.Debug("SSH disconnect cleanup suppressed", ex); } });
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _client.Connect();
        }, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        _stream = _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)terminalColumns,
            rows: (uint)terminalRows,
            width: 0,
            height: 0,
            bufferSize: ReadBufferSize);

        // Link the read-loop CTS to the external cancellation token so
        // a cancel signal from the caller propagates all the way down to
        // the pipe read, instead of being swallowed once Connect completes.
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);
    }

    /// <summary>Writes raw bytes to the shell's standard input.</summary>
    /// <param name="data">Byte data to send.</param>
    public void Write(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream is null)
        {
            throw new InvalidOperationException("Session is not connected.");
        }

        _stream.Write(data, 0, data.Length);
        _stream.Flush();
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

        if (_stream is null)
        {
            throw new InvalidOperationException("Session is not connected.");
        }

        try
        {
            _stream.ChangeWindowSize((uint)columns, (uint)rows, 0u, 0u);
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

    /// <summary>
    /// Gracefully disconnects the SSH session, stopping the read loop and
    /// closing the shell stream and client connection.
    /// </summary>
    public void Disconnect()
    {
        if (_disposed || _client is null)
        {
            return;
        }

        var loopExited = StopReadLoop();
        if (!loopExited && _readLoopTask is { } pending)
        {
            try
            {
                if (!pending.Wait(StopReadLoopFinal))
                {
                    Core.Logging.FileLogger.Error(
                        "SshShellSession: read loop is still running after a "
                        + $"{StopReadLoopFinal.TotalSeconds:F0}-second wait during Disconnect. "
                        + "Underlying SSH.NET pipe may be stuck; task will be leaked.");
                }
            }
            catch (AggregateException)
            {
                // The loop observes and logs non-cancellation failures itself.
            }
        }

        DisposeReadLoopCancellationSource();
        _readLoopTask = null;

        CleanupStream();
        DisconnectClient();

        NotifyDisconnected(SshSessionDisconnectInfo.Clean());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var loopExited = StopReadLoop();
        if (!loopExited && _readLoopTask is { } pending)
        {
            try
            {
                if (!pending.Wait(StopReadLoopFinal))
                {
                    Core.Logging.FileLogger.Error(
                        "SshShellSession: read loop is still running after a "
                        + $"{StopReadLoopFinal.TotalSeconds:F0}-second wait during Dispose. "
                        + "Underlying SSH.NET pipe may be stuck; task will be leaked.");
                }
            }
            catch (AggregateException)
            {
                // The loop observes and logs non-cancellation failures itself.
            }
        }

        DisposeReadLoopCancellationSource();
        _readLoopTask = null;

        CleanupStream();
        DisconnectClient();
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
            while (!cancellationToken.IsCancellationRequested && !_disposed && _stream is not null)
            {
                int bytesRead;

                try
                {
                    bytesRead = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
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
            return SshSessionDisconnectInfo.Clean("Remote shell exited.");
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
        var stream = Interlocked.Exchange(ref _stream, null);
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
