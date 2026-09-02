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

using System.Net.Sockets;
using System.Text;

namespace Heimdall.Terminal;

/// <summary>
/// Raw TCP terminal session with minimal Telnet IAC negotiation.
/// Implements <see cref="ITerminalSession"/> so it can be rendered in
/// the same WebView2 + xterm.js infrastructure used by SSH pipe mode.
/// </summary>
public sealed class TelnetSession : ITerminalSession
{
    private const byte IAC = 255;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DO = 253;
    private const byte DONT = 254;
    private const byte SB = 250;
    private const byte SE = 240;
    private const byte NAWS = 31;

    private enum TelnetParserState
    {
        Data,
        Iac,
        IacVerb,
        IacSb,
        IacSbData,
        IacSbIac
    }

    private readonly string _host;
    private readonly int _port;
    private readonly int _connectTimeoutMs;
    private readonly List<byte> _sbBuffer = new List<byte>();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private TelnetParserState _parserState;
    private byte _pendingVerb;
    private byte _sbOption;
    private bool _disposed;
    private bool _isRunning;
    private volatile bool _intentionalClose;
    private bool _nawsNegotiated;
    private int _columns;
    private int _rows;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int>? ProcessExited;

    public bool IsRunning => !_disposed && _isRunning;
    public int? ProcessId => null;
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    public TelnetSession(string host, int port, int connectTimeoutMs = 15000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port > 0 ? port : 23;
        _connectTimeoutMs = connectTimeoutMs;
    }

    /// <summary>
    /// Opens a TCP connection to the remote Telnet host and starts the read loop.
    /// The <paramref name="executable"/> and <paramref name="arguments"/> parameters
    /// are ignored - host and port are provided via the constructor.
    /// </summary>
    public async Task StartAsync(
        string executable, string arguments,
        int columns = 80, int rows = 24,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TelnetSession));
        if (_client is not null) throw new InvalidOperationException("Session already started.");

        cancellationToken.ThrowIfCancellationRequested();

        _columns = columns;
        _rows = rows;
        _intentionalClose = false;
        ResetParser();
        _cts = new CancellationTokenSource();

        _client = new TcpClient();
        try
        {
            using CancellationTokenSource connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(_connectTimeoutMs));
            await _client.ConnectAsync(_host, _port, connectTimeout.Token).ConfigureAwait(false);
            _stream = _client.GetStream();

            _isRunning = true;
            _readLoop = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
        }
        catch
        {
            CleanupFailedStart();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _stream is null) return;
        try
        {
            _stream.Write(data);
            _stream.Flush();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] Write: {ex.Message}");
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

    /// <summary>
    /// Sends a Telnet NAWS subnegotiation if the remote side accepted NAWS.
    /// Otherwise this is a no-op - raw TCP has no resize signaling.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;

        if (!_nawsNegotiated || _stream is null || _disposed) return;

        SendNawsSubnegotiation(columns, rows);
    }

    public void Kill()
    {
        if (_disposed) return;
        _intentionalClose = true;
        CloseConnection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalClose = true;

        _cts?.Cancel();
        CloseConnection();

        try { _stream?.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] Dispose stream: {ex.Message}"); }
        try { _client?.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] Dispose client: {ex.Message}"); }
        _stream = null;
        _client = null;
        ResetParser();
        _cts?.Dispose();
        _cts = null;
    }

    private void CleanupFailedStart()
    {
        _cts?.Cancel();
        CloseConnection();

        try { _stream?.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] CleanupFailedStart stream: {ex.Message}"); }
        try { _client?.Dispose(); } catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] CleanupFailedStart client: {ex.Message}"); }
        _stream = null;
        _client = null;
        _isRunning = false;
        ResetParser();
        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
    }

    private void CloseConnection()
    {
        try
        {
            _isRunning = false;
            _stream?.Close();
            _client?.Close();
        }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] CloseConnection: {ex.Message}"); }
    }

    /// <summary>
    /// Background read loop that receives data from the TCP stream,
    /// strips Telnet IAC sequences, and raises <see cref="DataReceived"/>
    /// for application data.
    /// </summary>
    private async Task ReadLoop(CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int bytesRead = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                ProcessIncoming(buffer.AsSpan(0, bytesRead));
            }
        }
        catch (OperationCanceledException) { /* Expected on session dispose */ }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] ReadLoop: {ex.Message}"); }

        _isRunning = false;
        int exitCode = _intentionalClose || ct.IsCancellationRequested ? 0 : 1;
        SafeInvokeProcessExited(exitCode);
    }

    /// <summary>
    /// Parses incoming data, handling Telnet IAC commands inline and
    /// forwarding application data via <see cref="DataReceived"/>.
    /// </summary>
    private void ProcessIncoming(ReadOnlySpan<byte> data)
    {
        List<byte> appData = new List<byte>(data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];

            switch (_parserState)
            {
                case TelnetParserState.Data:
                    ProcessDataByte(value, appData);
                    break;

                case TelnetParserState.Iac:
                    ProcessIacByte(value, appData);
                    break;

                case TelnetParserState.IacVerb:
                    ProcessIacVerbOption(value);
                    break;

                case TelnetParserState.IacSb:
                    _sbOption = value;
                    _sbBuffer.Clear();
                    _parserState = TelnetParserState.IacSbData;
                    break;

                case TelnetParserState.IacSbData:
                    ProcessSubnegotiationDataByte(value);
                    break;

                case TelnetParserState.IacSbIac:
                    ProcessSubnegotiationIacByte(value);
                    break;
            }
        }

        EmitBufferedData(appData);
    }

    private void ProcessDataByte(byte value, List<byte> appData)
    {
        if (value == IAC)
        {
            EmitBufferedData(appData);
            _parserState = TelnetParserState.Iac;
            return;
        }

        appData.Add(value);
    }

    private void ProcessIacByte(byte value, List<byte> appData)
    {
        switch (value)
        {
            case IAC:
                appData.Add(IAC);
                _parserState = TelnetParserState.Data;
                break;

            case DO:
            case DONT:
            case WILL:
            case WONT:
                _pendingVerb = value;
                _parserState = TelnetParserState.IacVerb;
                break;

            case SB:
                _parserState = TelnetParserState.IacSb;
                break;

            default:
                if (IsTelnetCommand(value))
                {
                    _parserState = TelnetParserState.Data;
                    return;
                }

                appData.Add(value);
                _parserState = TelnetParserState.Data;
                break;
        }
    }

    private void ProcessIacVerbOption(byte option)
    {
        switch (_pendingVerb)
        {
            case DO:
                HandleDo(option);
                break;

            case WILL:
                HandleWill(option);
                break;

            case WONT:
            case DONT:
                break;
        }

        _pendingVerb = 0;
        _parserState = TelnetParserState.Data;
    }

    private void ProcessSubnegotiationDataByte(byte value)
    {
        if (value == IAC)
        {
            _parserState = TelnetParserState.IacSbIac;
            return;
        }

        _sbBuffer.Add(value);
    }

    private void ProcessSubnegotiationIacByte(byte value)
    {
        if (value == SE)
        {
            CompleteSubnegotiation();
            _parserState = TelnetParserState.Data;
            return;
        }

        if (value == IAC)
        {
            _sbBuffer.Add(IAC);
        }

        _parserState = TelnetParserState.IacSbData;
    }

    private static bool IsTelnetCommand(byte value)
    {
        return value is >= SE and <= DONT;
    }

    private void CompleteSubnegotiation()
    {
        _sbOption = 0;
        _sbBuffer.Clear();
    }

    private void ResetParser()
    {
        _parserState = TelnetParserState.Data;
        _pendingVerb = 0;
        _sbOption = 0;
        _sbBuffer.Clear();
    }

    private void EmitBufferedData(List<byte> data)
    {
        if (data.Count == 0) return;

        EmitData(data.ToArray());
        data.Clear();
    }

    private void HandleDo(byte option)
    {
        if (option == NAWS)
        {
            // Accept NAWS - we can report window size
            SendCommand(WILL, NAWS);
            _nawsNegotiated = true;
            SendNawsSubnegotiation(_columns, _rows);
        }
        else
        {
            // Refuse everything else
            SendCommand(WONT, option);
        }
    }

    private void HandleWill(byte option)
    {
        // Refuse all server-side options
        SendCommand(DONT, option);
    }

    private void SendCommand(byte command, byte option)
    {
        if (_stream is null || _disposed) return;
        try
        {
            Span<byte> buf = stackalloc byte[3];
            buf[0] = IAC;
            buf[1] = command;
            buf[2] = option;
            _stream.Write(buf);
        }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] SendCommand: {ex.Message}"); }
    }

    private void SendNawsSubnegotiation(int columns, int rows)
    {
        if (_stream is null || _disposed) return;
        try
        {
            // IAC SB NAWS <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
            Span<byte> buf = stackalloc byte[9];
            buf[0] = IAC;
            buf[1] = SB;
            buf[2] = NAWS;
            buf[3] = (byte)(columns >> 8);
            buf[4] = (byte)(columns & 0xFF);
            buf[5] = (byte)(rows >> 8);
            buf[6] = (byte)(rows & 0xFF);
            buf[7] = IAC;
            buf[8] = SE;
            _stream.Write(buf);
        }
        catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] SendNawsSubnegotiation: {ex.Message}"); }
    }

    private void EmitData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        byte[] copy = data.ToArray();
        SafeInvokeDataReceived(copy);
    }

    private void SafeInvokeDataReceived(ReadOnlyMemory<byte> data)
    {
        try
        {
            DataReceived?.Invoke(data);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] DataReceived subscriber: {ex.Message}");
        }
    }

    private void SafeInvokeProcessExited(int exitCode)
    {
        try
        {
            ProcessExited?.Invoke(exitCode);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[TelnetSession] ProcessExited subscriber: {ex.Message}");
        }
    }
}
