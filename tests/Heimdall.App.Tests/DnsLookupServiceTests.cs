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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public sealed class DnsLookupServiceTests
{
    private static string Localize(string key) => key;

    [Fact]
    public async Task LookupAsync_WithoutGatewayAndAOrAAAA_UsesHostEntryPath()
    {
        var hostEntryCalls = new List<(string Host, string Type)>();
        var nslookupCalls = 0;
        var tunnelCalls = 0;

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (host, type, _, _) =>
            {
                hostEntryCalls.Add((host, type));
                return Task.FromResult("93.184.216.34");
            },
            nslookupQuery: (_, _, _, _) =>
            {
                nslookupCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, _, _) =>
            {
                tunnelCalls++;
                return Task.FromResult(string.Empty);
            });

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("93.184.216.34", result.Output);
        Assert.Single(hostEntryCalls);
        Assert.Equal(("example.com", "A"), hostEntryCalls[0]);
        Assert.Equal(0, nslookupCalls);
        Assert.Equal(0, tunnelCalls);
    }

    [Fact]
    public async Task LookupAsync_WithoutGatewayAndCustomServer_FallsBackToNslookup()
    {
        var hostEntryCalls = 0;
        var nslookupCalls = new List<(string Host, string Type, string? Server)>();

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) =>
            {
                hostEntryCalls++;
                return Task.FromResult(string.Empty);
            },
            nslookupQuery: (host, type, server, _) =>
            {
                nslookupCalls.Add((host, type, server));
                return Task.FromResult("Name:    example.com\nAddress: 93.184.216.34");
            },
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, "8.8.8.8");
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, hostEntryCalls);
        Assert.Single(nslookupCalls);
        Assert.Equal(("example.com", "A", "8.8.8.8"), nslookupCalls[0]);
    }

    [Fact]
    public async Task LookupAsync_WithoutGatewayAndMxRecord_UsesNslookupEvenWithoutCustomServer()
    {
        // MX falls through to nslookup because Dns.GetHostEntry only resolves
        // A/AAAA records; MX/TXT/etc need the CLI.
        var nslookupCalls = 0;

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("hostEntryQuery should not be invoked for MX"),
            nslookupQuery: (_, type, _, _) =>
            {
                nslookupCalls++;
                Assert.Equal("MX", type);
                return Task.FromResult("10 mail.example.com.");
            },
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.MX, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, nslookupCalls);
    }

    [Fact]
    public async Task LookupAsync_WithGateway_UsesTunnelPathWithWireFormatToken()
    {
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump.example.com", Port = 22 };
        var tunnelCalls = new List<(string Host, string Type, string? Server)>();

        var service = BuildService(
            gateway,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("host-entry path must not fire on tunnel route"),
            nslookupQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("local nslookup must not fire on tunnel route"),
            tunnelQuery: (gw, host, type, server, _) =>
            {
                Assert.Same(gateway, gw);
                tunnelCalls.Add((host, type, server));
                return Task.FromResult("example.com. 300 IN TXT \"v=spf1 -all\"");
            });

        var request = new DnsLookupRequest("example.com", DnsRecordType.TXT, "1.1.1.1");
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(tunnelCalls);
        Assert.Equal(("example.com", "TXT", "1.1.1.1"), tunnelCalls[0]);
    }

    [Fact]
    public async Task LookupAsync_SetGateway_SwitchesPathOnSubsequentCall()
    {
        var hostEntryCalls = 0;
        var tunnelCalls = 0;
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22 };

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) =>
            {
                hostEntryCalls++;
                return Task.FromResult("1.2.3.4");
            },
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) =>
            {
                tunnelCalls++;
                return Task.FromResult("1.2.3.4");
            });

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        await service.LookupAsync(request, Localize, CancellationToken.None);
        service.SetGateway(gateway);
        await service.LookupAsync(request, Localize, CancellationToken.None);
        service.SetGateway(null);
        await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.Equal(2, hostEntryCalls);
        Assert.Equal(1, tunnelCalls);
    }

    [Fact]
    public async Task LookupAsync_HostEntryThrowsSocketException_ReturnsErrorResult()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) =>
                throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("invalid.example", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ToolDnsErrorLookupFailed", result.ErrorKey);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorArg));
    }

    [Fact]
    public async Task LookupAsync_GenericException_ReturnsErrorResultWithMessage()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new InvalidOperationException("boom"),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ToolDnsErrorLookupFailed", result.ErrorKey);
        Assert.Equal("boom", result.ErrorArg);
    }

    [Fact]
    public async Task LookupAsync_OperationCancelledDuringQuery_ReturnsTimeoutErrorResult()
    {
        using var cts = new CancellationTokenSource();

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(string.Empty);
            },
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, cts.Token);

        Assert.False(result.Success);
        Assert.Equal("ToolDnsErrorTimeout", result.ErrorKey);
        Assert.Null(result.ErrorArg);
    }

    [Fact]
    public async Task LookupAsync_PreCancelledToken_ThrowsSynchronously()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LookupAsync(request, Localize, cts.Token));
    }

    [Fact]
    public async Task LookupAsync_NullRequest_ThrowsArgumentNullException()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.LookupAsync(null!, Localize, CancellationToken.None));
    }

    [Fact]
    public async Task LookupAsync_NullLocalize_ThrowsArgumentNullException()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.LookupAsync(request, null!, CancellationToken.None));
    }

    [Fact]
    public async Task LookupAsync_RecordTypeIsPassedInWireFormat()
    {
        string capturedToken = "";

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("A/AAAA without custom server must use host-entry - use nslookup fixture for a different branch"),
            nslookupQuery: (_, type, _, _) =>
            {
                capturedToken = type;
                return Task.FromResult("result");
            },
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.CNAME, null);
        await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.Equal("CNAME", capturedToken);
    }

    [Fact]
    public async Task LookupAsync_HostEntryPath_ReceivesLocalizeDelegate()
    {
        var localizerCalls = new List<string>();

        Func<string, string> localize = key =>
        {
            localizerCalls.Add(key);
            return $"[{key}]";
        };

        var service = BuildService(
            gateway: null,
            hostEntryQuery: (host, type, loc, _) =>
            {
                // Simulate the real host-entry path invoking the localizer for the
                // "no addresses" path.
                return Task.FromResult(loc("ToolDnsNoIpv4"));
            },
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("[ToolDnsNoIpv4]", result.Output);
        Assert.Contains("ToolDnsNoIpv4", localizerCalls);
    }

    [Fact]
    public async Task LookupAsync_NullOutputFromQuery_IsNormalizedToEmptyString()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => Task.FromResult<string>(null!),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task LookupAsync_ElapsedMsIsNonNegative()
    {
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => Task.FromResult("1.2.3.4"),
            nslookupQuery: (_, _, _, _) => Task.FromResult(string.Empty),
            tunnelQuery: (_, _, _, _, _) => Task.FromResult(string.Empty));

        var request = new DnsLookupRequest("example.com", DnsRecordType.A, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task LookupAsync_RejectsHostnameWithCommandInjection()
    {
        var nslookupCalls = 0;
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("host-entry path must not fire for invalid hostname"),
            nslookupQuery: (_, _, _, _) =>
            {
                nslookupCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, _, _) => throw new Xunit.Sdk.XunitException("tunnel path must not fire for invalid hostname"));

        var request = new DnsLookupRequest("example.com && calc.exe", DnsRecordType.MX, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, nslookupCalls);
        Assert.Contains("Invalid DNS hostname", result.ErrorArg);
    }

    [Fact]
    public async Task LookupAsync_RejectsHostnameWithNullByte()
    {
        var nslookupCalls = 0;
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("host-entry path must not fire for invalid hostname"),
            nslookupQuery: (_, _, _, _) =>
            {
                nslookupCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, _, _) => throw new Xunit.Sdk.XunitException("tunnel path must not fire for invalid hostname"));

        var request = new DnsLookupRequest("example.com\0oops", DnsRecordType.MX, null);
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, nslookupCalls);
        Assert.Contains("Invalid DNS hostname", result.ErrorArg);
    }

    [Fact]
    public async Task LookupAsync_RejectsDnsServerWithGarbage()
    {
        var nslookupCalls = 0;
        var service = BuildService(
            gateway: null,
            hostEntryQuery: (_, _, _, _) => throw new Xunit.Sdk.XunitException("host-entry path must not fire for invalid DNS server"),
            nslookupQuery: (_, _, _, _) =>
            {
                nslookupCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, _, _) => throw new Xunit.Sdk.XunitException("tunnel path must not fire for invalid DNS server"));

        var request = new DnsLookupRequest("example.com", DnsRecordType.MX, "dns server && calc");
        var result = await service.LookupAsync(request, Localize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, nslookupCalls);
        Assert.Contains("Invalid DNS server", result.ErrorArg);
    }

    [Fact]
    public void CreateLocalNslookupStartInfo_UsesArgumentListForValidInputs()
    {
        var psi = DnsLookupService.CreateLocalNslookupStartInfo("example.com", "MX", "8.8.8.8");

        Assert.Equal("nslookup.exe", Path.GetFileName(psi.FileName), ignoreCase: true);
        Assert.Equal(3, psi.ArgumentList.Count);
        Assert.Equal("-type=MX", psi.ArgumentList[0]);
        Assert.Equal("example.com", psi.ArgumentList[1]);
        Assert.Equal("8.8.8.8", psi.ArgumentList[2]);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    private static DnsLookupService BuildService(
        SshGatewayDto? gateway,
        Func<string, string, Func<string, string>, CancellationToken, Task<string>> hostEntryQuery,
        Func<string, string, string?, CancellationToken, Task<string>> nslookupQuery,
        Func<SshGatewayDto, string, string, string?, CancellationToken, Task<string>> tunnelQuery)
        => new(gateway, hostEntryQuery, nslookupQuery, tunnelQuery);
}
