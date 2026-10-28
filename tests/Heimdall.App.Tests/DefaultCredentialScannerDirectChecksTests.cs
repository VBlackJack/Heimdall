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
using Heimdall.App.Services;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// A direct check of MySQL or MSSQL has no login behind it. It must never come back as default
/// credentials because a server answered, and the user is told it was not tested.
/// </summary>
public class DefaultCredentialScannerDirectChecksTests
{
    /// <summary>The first bytes of a MySQL protocol-10 greeting: length, sequence, then the version byte.</summary>
    private static readonly byte[] MySqlGreetingStart = [0x4a, 0x00, 0x00, 0x00, 0x0a];

    [Fact]
    public async Task TestCredentialAsync_DirectMySqlThatGreetsLikeMySql_IsNotReportedAsDefault()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource();
        Task server = GreetAndRejectAsync(listener, serverCts.Token);
        var scanner = new DefaultCredentialScanner();

        CredTestResultDto result = await scanner.TestCredentialAsync(
            IPAddress.Loopback.ToString(), port, "MySQL", "root", string.Empty, CancellationToken.None);

        Assert.Equal(CredTestStatus.NotSupported, result.Status);
        serverCts.Cancel();
        await server;
    }

    [Fact]
    public async Task TestCredentialAsync_DirectMsSql_IsReportedAsNotSupportedWithoutConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = new DefaultCredentialScanner();

        CredTestResultDto result = await scanner.TestCredentialAsync(
            IPAddress.Loopback.ToString(), port, "MSSQL", "sa", string.Empty, CancellationToken.None);

        Assert.Equal(CredTestStatus.NotSupported, result.Status);
        Assert.False(listener.Pending(), "a check that tests nothing should send nothing");
    }

    [Fact]
    public async Task TestCredentialAsync_VncThroughAGateway_IsNotTestedAndSendsNothing()
    {
        // The gateway is never contacted: the result is decided before a tunnel is opened. Were the
        // check to go on, opening the tunnel would fail and the status would be Error.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var scanner = new DefaultCredentialScanner(new Heimdall.Core.Configuration.SshGatewayDto
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
        });

        CredTestResultDto result = await scanner.TestCredentialAsync(
            "10.0.0.5", 5900, "VNC", string.Empty, "password", CancellationToken.None);

        Assert.Equal(CredTestStatus.NotSupported, result.Status);
        Assert.False(listener.Pending(), "a check that tests nothing should send nothing");
    }

    [Theory]
    [InlineData("MySQL", true)]
    [InlineData("PostgreSQL", true)]
    [InlineData("MSSQL", true)]
    [InlineData("SSH", false)]
    [InlineData("Redis", false)]
    public void BuildGatewaySqlCommand_IsBuiltForTheSqlServicesOnly(string service, bool built)
    {
        string? command = DefaultCredentialScanner.BuildGatewaySqlCommand(service, "db.example.test", 1433, "sa", "p'w");

        Assert.Equal(built, command is not null);
    }

    [Fact]
    public void BuildGatewaySqlCommand_AQuoteInThePassword_StaysInsideItsArgument()
    {
        string command = DefaultCredentialScanner.BuildGatewaySqlCommand("MySQL", "h", 3306, "root", "a'; touch /tmp/x; '")!;

        Assert.DoesNotContain("a'; touch", command, StringComparison.Ordinal);
    }

    private static async Task GreetAndRejectAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct);
            await client.GetStream().WriteAsync(MySqlGreetingStart, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // The test is over.
        }
    }
}
