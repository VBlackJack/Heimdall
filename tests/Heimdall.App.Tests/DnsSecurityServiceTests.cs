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
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public sealed class DnsSecurityServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("bad domain")]
    [InlineData("bad\u0000domain.example")]
    public void CreateNslookupStartInfo_InvalidDomain_ThrowsArgumentException(string domain)
    {
        var ex = Assert.Throws<ArgumentException>(() => DnsSecurityService.CreateNslookupStartInfo(domain, "TXT"));

        Assert.Equal("domain", ex.ParamName);
    }

    [Fact]
    public void CreateNslookupStartInfo_InvalidRecordType_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => DnsSecurityService.CreateNslookupStartInfo("example.com", "AAAA"));

        Assert.Equal("recordType", ex.ParamName);
    }

    [Fact]
    public void CreateNslookupStartInfo_UsesArgumentListForValidInputs()
    {
        var psi = DnsSecurityService.CreateNslookupStartInfo("example.com", "TXT");

        Assert.Equal("nslookup.exe", Path.GetFileName(psi.FileName), ignoreCase: true);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(2, psi.ArgumentList.Count);
        Assert.Equal("-type=TXT", psi.ArgumentList[0]);
        Assert.Equal("example.com", psi.ArgumentList[1]);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void CreateNslookupTunnelCommand_EscapesDomainWithEscapeShellArg()
    {
        var command = DnsSecurityService.CreateNslookupTunnelCommand("TXT", "example.com");

        Assert.Contains($" {InputValidator.EscapeShellArg("example.com")} ", command, StringComparison.Ordinal);
        Assert.Contains($"-type={InputValidator.EscapeShellArg("TXT")}", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAllChecksAsync_WithoutGateway_UsesLocalQueries()
    {
        var localCalls = new List<(string Type, string Domain)>();
        var tunnelCalls = new List<(string Type, string Domain)>();

        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                localCalls.Add((type, domain));
                return Task.FromResult(GetSuccessfulResponse(type, domain));
            },
            tunnelQuery: (gateway, type, domain, ct) =>
            {
                tunnelCalls.Add((type, domain));
                return Task.FromResult(string.Empty);
            });

        var results = await service.RunAllChecksAsync("example.com", CancellationToken.None);

        Assert.Equal(6, results.Count);
        Assert.All(results, result => Assert.Equal(DnsCheckStatus.Pass, result.Status));
        Assert.Empty(tunnelCalls);
        Assert.Contains(("TXT", "example.com"), localCalls);
        Assert.Contains(("TXT", "_dmarc.example.com"), localCalls);
        Assert.Contains(("CAA", "example.com"), localCalls);
        Assert.Contains(("DNSKEY", "example.com"), localCalls);
        Assert.Contains(("MX", "example.com"), localCalls);
        Assert.Contains(localCalls, call => call == ("TXT", "default._domainkey.example.com"));
    }

    [Fact]
    public async Task RunAllChecksAsync_WithGateway_UsesTunnelQueries()
    {
        var localCalls = 0;
        var tunnelCalls = new List<(string Type, string Domain)>();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22 };

        var service = new DnsSecurityService(
            gateway,
            localQuery: (type, domain, ct) =>
            {
                localCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (gw, type, domain, ct) =>
            {
                Assert.Same(gateway, gw);
                tunnelCalls.Add((type, domain));
                return Task.FromResult(GetSuccessfulResponse(type, domain));
            });

        var results = await service.RunAllChecksAsync("example.com", CancellationToken.None);

        Assert.Equal(0, localCalls);
        Assert.Equal(6, results.Count);
        Assert.All(results, result => Assert.Equal(DnsCheckStatus.Pass, result.Status));
        Assert.Contains(("TXT", "example.com"), tunnelCalls);
    }

    [Fact]
    public async Task RunAllChecksAsync_DkimShortCircuitsOnFirstPassingSelector()
    {
        var dkimQueries = new List<string>();

        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                if (domain.Contains("._domainkey.", StringComparison.Ordinal))
                {
                    dkimQueries.Add(domain);
                }

                return Task.FromResult(domain switch
                {
                    "default._domainkey.example.com" => string.Empty,
                    "google._domainkey.example.com" => string.Empty,
                    "selector1._domainkey.example.com" => "\"v=DKIM1; p=abc\"",
                    "selector2._domainkey.example.com" => throw new Xunit.Sdk.XunitException("selector2 should not be queried"),
                    _ => GetSuccessfulResponse(type, domain),
                });
            },
            tunnelQuery: (_, _, _, _) => Task.FromResult(string.Empty));

        var results = await service.RunAllChecksAsync("example.com", CancellationToken.None);

        Assert.Equal(
            ["default._domainkey.example.com", "google._domainkey.example.com", "selector1._domainkey.example.com"],
            dkimQueries);
        Assert.Equal(DnsCheckStatus.Pass, results.Single(result => result.Kind == DnsCheckKind.Dkim).Status);
    }

    [Fact]
    public async Task RunAllChecksAsync_DkimRevokedKeySurfacesWarnWhenNoSelectorPasses()
    {
        List<string> dkimQueries = [];

        DnsSecurityService service = new(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                if (domain.Contains("._domainkey.", StringComparison.Ordinal))
                {
                    dkimQueries.Add(domain);
                }

                return Task.FromResult(domain switch
                {
                    "default._domainkey.example.com" => "\"v=DKIM1; p=\"",
                    "google._domainkey.example.com" => string.Empty,
                    "selector1._domainkey.example.com" => string.Empty,
                    "selector2._domainkey.example.com" => string.Empty,
                    _ => GetSuccessfulResponse(type, domain),
                });
            },
            tunnelQuery: (_, _, _, _) => Task.FromResult(string.Empty));

        IReadOnlyList<DnsCheckResult> results = await service.RunAllChecksAsync("example.com", CancellationToken.None);
        DnsCheckResult dkim = results.Single(result => result.Kind == DnsCheckKind.Dkim);

        Assert.Equal(
            ["default._domainkey.example.com", "google._domainkey.example.com", "selector1._domainkey.example.com", "selector2._domainkey.example.com"],
            dkimQueries);
        Assert.Equal(DnsCheckStatus.Warn, dkim.Status);
        Assert.Equal("ToolDnsSecDkimRevoked", dkim.DetailKey);
    }

    [Fact]
    public async Task RunAllChecksAsync_DkimValidSelectorPreferredOverRevoked()
    {
        List<string> dkimQueries = [];

        DnsSecurityService service = new(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                if (domain.Contains("._domainkey.", StringComparison.Ordinal))
                {
                    dkimQueries.Add(domain);
                }

                return Task.FromResult(domain switch
                {
                    "default._domainkey.example.com" => "\"v=DKIM1; p=\"",
                    "google._domainkey.example.com" => string.Empty,
                    "selector1._domainkey.example.com" => "\"v=DKIM1; p=abc\"",
                    "selector2._domainkey.example.com" => throw new Xunit.Sdk.XunitException("selector2 should not be queried"),
                    _ => GetSuccessfulResponse(type, domain),
                });
            },
            tunnelQuery: (_, _, _, _) => Task.FromResult(string.Empty));

        IReadOnlyList<DnsCheckResult> results = await service.RunAllChecksAsync("example.com", CancellationToken.None);
        DnsCheckResult dkim = results.Single(result => result.Kind == DnsCheckKind.Dkim);

        Assert.Equal(
            ["default._domainkey.example.com", "google._domainkey.example.com", "selector1._domainkey.example.com"],
            dkimQueries);
        Assert.Equal(DnsCheckStatus.Pass, dkim.Status);
    }

    [Fact]
    public async Task RunAllChecksAsync_DnssecFallsBackToRrsigWhenDnskeyMissing()
    {
        var queries = new List<(string Type, string Domain)>();

        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                queries.Add((type, domain));
                return Task.FromResult(type switch
                {
                    "DNSKEY" => string.Empty,
                    "RRSIG" => "example.com. 300 IN RRSIG DNSKEY 13 2 300 20260417000000 20260317000000 12345 example.com. abc",
                    _ => GetSuccessfulResponse(type, domain),
                });
            },
            tunnelQuery: (_, _, _, _) => Task.FromResult(string.Empty));

        var results = await service.RunAllChecksAsync("example.com", CancellationToken.None);
        var dnssec = results.Single(result => result.Kind == DnsCheckKind.Dnssec);

        Assert.Equal(DnsCheckStatus.Pass, dnssec.Status);
        Assert.Contains(("DNSKEY", "example.com"), queries);
        Assert.Contains(("RRSIG", "example.com"), queries);
    }

    [Fact]
    public async Task RunAllChecksAsync_PerCheckExceptionsBecomeErrorResults()
    {
        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                if (type == "CAA")
                {
                    throw new InvalidOperationException("CAA exploded");
                }

                return Task.FromResult(GetSuccessfulResponse(type, domain));
            },
            tunnelQuery: (_, _, _, _) => Task.FromResult(string.Empty));

        var results = await service.RunAllChecksAsync("example.com", CancellationToken.None);
        var caa = results.Single(result => result.Kind == DnsCheckKind.Caa);

        Assert.Equal(DnsCheckStatus.Fail, caa.Status);
        Assert.Equal("CAA exploded", caa.RawRecord);
        Assert.All(results.Where(result => result.Kind != DnsCheckKind.Caa), result => Assert.Equal(DnsCheckStatus.Pass, result.Status));
    }

    [Fact]
    public async Task RunAllChecksAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(string.Empty);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAllChecksAsync("example.com", cts.Token));
    }

    [Fact]
    public async Task RunAllChecksAsync_InvalidDomain_DoesNotInvokeQueryDelegates()
    {
        var localCalls = 0;
        var tunnelCalls = 0;

        var service = new DnsSecurityService(
            gateway: null,
            localQuery: (type, domain, ct) =>
            {
                localCalls++;
                return Task.FromResult(string.Empty);
            },
            tunnelQuery: (_, _, _, _) =>
            {
                tunnelCalls++;
                return Task.FromResult(string.Empty);
            });

        var results = await service.RunAllChecksAsync("bad domain", CancellationToken.None);

        Assert.Equal(0, localCalls);
        Assert.Equal(0, tunnelCalls);
        Assert.Equal(6, results.Count);
        Assert.All(results, result => Assert.Equal(DnsCheckStatus.Fail, result.Status));
        Assert.All(
            results,
            result => Assert.Contains("Invalid DNS lookup domain", result.RawRecord ?? string.Empty, StringComparison.Ordinal));
    }

    private static string GetSuccessfulResponse(string type, string domain) => (type, domain) switch
    {
        ("TXT", "example.com") => "\"v=spf1 include:_spf.example.com -all\"",
        ("TXT", "_dmarc.example.com") => "\"v=DMARC1; p=reject; rua=mailto:dmarc@example.com\"",
        ("TXT", var dkimDomain) when dkimDomain.Contains("._domainkey.", StringComparison.Ordinal) => "\"v=DKIM1; k=rsa; p=abc\"",
        ("CAA", "example.com") => "0 issue \"letsencrypt.org\"",
        ("DNSKEY", "example.com") => "example.com. 300 IN DNSKEY 257 3 13 AAAA",
        ("MX", "example.com") => "10 mail.example.com.",
        _ => string.Empty,
    };
}
