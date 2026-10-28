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

using System.Net;
using System.Net.Sockets;
using System.Text;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The directory an FTP session starts in: the server's PWD when it is an absolute path, else "/".
/// </summary>
public sealed class FtpBrowserInitialDirectoryTests
{
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("/home/heimdall", "/home/heimdall")]
    [InlineData("/home/heimdall/", "/home/heimdall")]
    [InlineData("/", "/")]
    [InlineData("C:\\", "/")]
    [InlineData("C:\\Users\\heimdall", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void ResolveInitialDirectory_KeepsAnAbsolutePathAndFallsBackToTheRoot(string? reported, string expected)
    {
        Assert.Equal(expected, FtpBrowser.ResolveInitialDirectory(reported));
    }

    [Fact]
    public async Task Connect_StartsInTheDirectoryTheServerReports()
    {
        using FakeFtpServer server = new("/home/heimdall");
        using FtpBrowser browser = new();

        await browser.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, "operator", "secret", passiveMode: true, useSsl: false)
            .WaitAsync(Backstop);

        Assert.Equal("/home/heimdall", browser.CurrentDirectory);
    }

    [Fact]
    public async Task Connect_AgainstAServerAnsweringADrivePath_StartsAtTheRoot()
    {
        using FakeFtpServer server = new("C:\\");
        using FtpBrowser browser = new();

        await browser.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, "operator", "secret", passiveMode: true, useSsl: false)
            .WaitAsync(Backstop);

        Assert.Equal("/", browser.CurrentDirectory);
    }

    [Fact]
    public async Task Connect_RaisesDirectoryChangedWithTheInitialDirectory()
    {
        using FakeFtpServer server = new("/srv/ftp");
        using FtpBrowser browser = new();
        string? announced = null;
        browser.DirectoryChanged += directory => announced = directory;

        await browser.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, "operator", "secret", passiveMode: true, useSsl: false)
            .WaitAsync(Backstop);

        Assert.Equal("/srv/ftp", announced);
    }

    /// <summary>
    /// A loopback FTP server that accepts one login and answers PWD with the directory it was given.
    /// Anything it does not know gets 502, which a client treats as "not implemented".
    /// </summary>
    private sealed class FakeFtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly string _workingDirectory;

        public FakeFtpServer(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeAsync(_stop.Token));
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                await using NetworkStream stream = client.GetStream();
                await WriteLineAsync(stream, "220 fake ftp ready", ct);
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                {
                    await WriteLineAsync(stream, ReplyTo(line), ct);
                    if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                // The test ended or the client went away.
            }
        }

        private string ReplyTo(string line)
        {
            string command = line.Split(' ', 2)[0].ToUpperInvariant();
            return command switch
            {
                "USER" => "331 password required",
                "PASS" => "230 logged in",
                "SYST" => "215 UNIX Type: L8",
                "FEAT" => "211 no extensions",
                "PWD" => $"257 \"{_workingDirectory}\" is current directory.",
                "OPTS" or "TYPE" or "NOOP" => "200 ok",
                "QUIT" => "221 bye",
                _ => "502 not implemented"
            };
        }

        private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);
    }
}
