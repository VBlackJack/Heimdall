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

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using FluentFTP.Exceptions;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;
using Heimdall.TestSupport;

namespace Heimdall.App.Tests;

/// <summary>
/// Every FTP connect failure other than a certificate rejection used to read "FTP connection failed.",
/// and a user cancel was caught by the same handler and shown as an error. The failure is now
/// classified into messages the user can act on, and a cancel is rethrown like SshHandler does.
/// </summary>
/// <remarks>
/// The class of each failure is proved on the exception types FluentFTP and the framework raise;
/// authentication, refusal and cancel are also driven end to end against a loopback server. Mutants
/// proved red, each green once restored: removing the cancel catch, classifying SocketException as
/// generic, classifying FtpAuthenticationException as generic, and mapping Timeout to the generic key.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class FtpConnectFailureTests : IDisposable
{
    /// <summary>The handler decrypts the profile password, which reaches CredentialProtector.</summary>
    private readonly CredentialProtectorStateScope _protectorState = new();

    /// <inheritdoc />
    public void Dispose() => _protectorState.Dispose();

    private const string GenericKey = "ErrorFtpConnectionFailed";

    public static TheoryData<string, Func<Exception>, FtpConnectFailureKind> Failures => new()
    {
        { "Authentication-01", () => new FtpAuthenticationException("530", "Login incorrect."), FtpConnectFailureKind.Authentication },
        { "Authentication-02", () => new FtpCommandException("530", "Not logged in."), FtpConnectFailureKind.Authentication },
        { "Authentication-03", () => new IOException("wrapped", new FtpAuthenticationException("530", "Login incorrect.")), FtpConnectFailureKind.Authentication },
        { "HostUnreachable-04", () => new SocketException((int)SocketError.ConnectionRefused), FtpConnectFailureKind.HostUnreachable },
        { "HostUnreachable-05", () => new SocketException((int)SocketError.HostNotFound), FtpConnectFailureKind.HostUnreachable },
        { "HostUnreachable-06", () => new SocketException((int)SocketError.NetworkUnreachable), FtpConnectFailureKind.HostUnreachable },
        { "HostUnreachable-07", () => new FtpException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused)), FtpConnectFailureKind.HostUnreachable },
        { "HostUnreachable-08", () => new AggregateException(new SocketException((int)SocketError.ConnectionRefused)), FtpConnectFailureKind.HostUnreachable },
        { "Timeout-09", () => new TimeoutException("Timed out"), FtpConnectFailureKind.Timeout },
        { "Timeout-10", () => new SocketException((int)SocketError.TimedOut), FtpConnectFailureKind.Timeout },
        { "Timeout-11", () => new TaskCanceledException("connect timeout"), FtpConnectFailureKind.Timeout },
        { "Tls-12", () => new AuthenticationException("The remote certificate is invalid."), FtpConnectFailureKind.Tls },
        { "Tls-13", () => new FtpException("TLS", new AuthenticationException("handshake")), FtpConnectFailureKind.Tls },
        { "Tls-14", () => new FtpInvalidCertificateException("bad certificate"), FtpConnectFailureKind.Tls },
        { "Tls-15", () => new FtpSecurityNotAvailableException(), FtpConnectFailureKind.Tls },
        { "Generic-16", () => new FtpCommandException("550", "No such directory."), FtpConnectFailureKind.Generic },
        { "Generic-17", () => new InvalidOperationException("unexpected"), FtpConnectFailureKind.Generic },
        { "Generic-18", () => new FtpException("something else"), FtpConnectFailureKind.Generic }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailure_IsClassified(string caseName, Func<Exception> failure, FtpConnectFailureKind expected)
    {
        // caseName only gives the row a stable display name: the exception text is localized.
        Assert.NotEmpty(caseName);
        Assert.Equal(expected, FtpConnectFailureClassifier.Classify(failure()));
    }

    [Fact]
    public void EveryClass_HasItsOwnKey_AndOnlyTheFallbackKeepsTheGenericOne()
    {
        FtpConnectFailureKind[] kinds = Enum.GetValues<FtpConnectFailureKind>();
        string[] keys = [.. kinds.Select(FtpConnectFailureClassifier.MessageKey)];

        Assert.Equal(kinds.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(GenericKey, FtpConnectFailureClassifier.MessageKey(FtpConnectFailureKind.Generic));
        Assert.All(
            kinds.Where(k => k != FtpConnectFailureKind.Generic),
            kind => Assert.NotEqual(GenericKey, FtpConnectFailureClassifier.MessageKey(kind)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void EveryClass_HasALocalizedMessage(string language)
    {
        using JsonDocument locale = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindLocalesPath(), language + ".json")));
        string generic = locale.RootElement.GetProperty(GenericKey).GetString()!;

        foreach (FtpConnectFailureKind kind in Enum.GetValues<FtpConnectFailureKind>().Where(k => k != FtpConnectFailureKind.Generic))
        {
            string key = FtpConnectFailureClassifier.MessageKey(kind);
            string message = locale.RootElement.GetProperty(key).GetString()!;

            Assert.False(string.IsNullOrWhiteSpace(message), $"{language}:{key} is empty");
            Assert.NotEqual(generic, message);
        }
    }

    [Fact]
    public async Task Connect_ToAClosedPort_ReportsAnUnreachableHost()
    {
        FtpHandler handler = await CreateHandlerAsync();
        using RefusedLoopbackPort refusedPort = RefusedLoopbackPort.Reserve();

        ConnectionResult result = await handler.ConnectAsync(CreateServer(refusedPort.Port), new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(await LocalizedAsync("ErrorFtpHostUnreachable"), result.ErrorMessage);
    }

    [Fact]
    public async Task Connect_WithRefusedCredentials_ReportsAnAuthenticationFailure()
    {
        FtpHandler handler = await CreateHandlerAsync();
        using FakeFtpServer server = FakeFtpServer.RefusingLogin();

        ConnectionResult result = await handler.ConnectAsync(CreateServer(server.Port), new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(await LocalizedAsync("ErrorFtpAuthenticationFailed"), result.ErrorMessage);
    }

    [Fact]
    public async Task Connect_CancelledByTheUser_IsRethrownAndNotShownAsAnError()
    {
        FtpHandler handler = await CreateHandlerAsync();
        using FakeFtpServer server = FakeFtpServer.Silent();
        using CancellationTokenSource cancel = new();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.ConnectAsync(CreateServer(server.Port), new AppSettings(), cancel.Token));
    }

    private static async Task<FtpHandler> CreateHandlerAsync()
        => new(
            new ConnectionStateMachine(),
            await CreateLocalizerAsync(),
            new FtpsCertificateStore(),
            RejectingFtpsCertificateVerifier.Instance);

    private static async Task<string> LocalizedAsync(string key) => (await CreateLocalizerAsync())[key];

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(FindLocalesPath(), "en").ConfigureAwait(false);
        return localizer;
    }

    private static ServerProfileDto CreateServer(int port)
        => new()
        {
            Id = "ftp-failure-test",
            DisplayName = "FTP Failure Test",
            RemoteServer = IPAddress.Loopback.ToString(),
            ConnectionType = "FTP",
            FtpPort = port,
            FtpUsername = "operator",
            UseDirectConnection = true
        };

    private static string FindLocalesPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "locales");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Cannot find locales directory.");
    }

    /// <summary>A loopback FTP server that either refuses the login or never answers.</summary>
    private sealed class FakeFtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _refuseLogin;

        private FakeFtpServer(bool refuseLogin)
        {
            _refuseLogin = refuseLogin;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeAsync(_stop.Token));
        }

        public int Port { get; }

        public static FakeFtpServer RefusingLogin() => new(refuseLogin: true);

        public static FakeFtpServer Silent() => new(refuseLogin: false);

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
                if (!_refuseLogin)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return;
                }

                await WriteLineAsync(stream, "220 fake ftp ready", ct);
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                {
                    string reply = line.StartsWith("USER", StringComparison.OrdinalIgnoreCase) ? "331 password required"
                        : line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "530 Login incorrect."
                        : "500 unsupported";
                    await WriteLineAsync(stream, reply, ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                // The test ended or the client went away.
            }
        }

        private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);
    }
}
