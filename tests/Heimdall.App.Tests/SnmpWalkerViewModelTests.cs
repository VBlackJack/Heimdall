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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public class SnmpWalkerViewModelTests
{
    [Fact]
    public async Task Walk_EmptyHost_SetsError()
    {
        var service = new FakeWalkerService();
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("   ", "public", "1.3.6.1.2.1.1");

        Assert.True(vm.ShowError);
        Assert.False(vm.IsWalking);
        Assert.Equal(0, service.WalkCallCount);
    }

    [Theory]
    [InlineData("-Lf/tmp/x", "1.3.6.1")]
    [InlineData("10.0.0.1", "-Ln")]
    public async Task Walk_OptionLikeHostOrOid_SetsErrorAndSkipsService(string host, string oid)
    {
        var service = new FakeWalkerService();
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);
        vm.SetGateway(new SshGatewayDto { Name = "gw", Host = "jump", Port = 22, User = "me" });

        await vm.WalkAsync(host, "public", oid);

        Assert.True(vm.ShowError);
        Assert.False(vm.IsWalking);
        Assert.Equal(0, service.WalkCallCount);
        Assert.Equal(0, service.TunnelWalkCallCount);
    }

    [Theory]
    [InlineData("SNMPv2-MIB::sysDescr.0")]
    [InlineData("1.3.6.1")]
    public async Task Walk_SymbolicOrNumericOid_IsAccepted(string oid)
    {
        var service = new FakeWalkerService();
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("10.0.0.1", "public", oid);

        Assert.False(vm.ShowError);
        Assert.Equal(1, service.WalkCallCount);
    }

    [Theory]
    [InlineData("-Lf/tmp/x", "1.3.6.1", "host")]
    [InlineData("10.0.0.1", "-Ln", "oid")]
    public async Task WalkViaTunnel_OptionLikeHostOrOid_ThrowsBeforeConnecting(string host, string oid, string param)
    {
        var gateway = new SshGatewayDto { Name = "gw", Host = "unreachable.invalid", Port = 22, User = "me" };
        var service = new SnmpWalkerService(gateway);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.WalkViaTunnelAsync(host, "public", oid, null, CancellationToken.None));

        Assert.Equal(param, ex.ParamName);
    }

    [Fact]
    public async Task Walk_ValidHost_DelegatesToDirectService()
    {
        var service = new FakeWalkerService
        {
            WalkResult =
            [
                new SnmpEntry { Oid = "1.3.6.1.2.1.1.1.0", Name = "sysDescr.0", Type = "STRING", Value = "Test" },
            ],
        };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1.2.1.1");

        Assert.Equal(1, service.WalkCallCount);
        Assert.Equal(0, service.TunnelWalkCallCount);
    }

    [Fact]
    public async Task Walk_DefaultsCommunityAndOid()
    {
        var service = new FakeWalkerService();
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("10.0.0.1", string.Empty, string.Empty);

        Assert.Equal(NetworkToolPresets.SnmpDefaultCommunity, service.LastCommunity);
        Assert.Equal(NetworkToolPresets.SnmpDefaultOid, service.LastOid);
    }

    [Fact]
    public async Task Walk_CompletesSuccessfully_PopulatesEntries()
    {
        var service = new FakeWalkerService
        {
            WalkResult =
            [
                new SnmpEntry { Oid = "1.3.6.1.2.1.1.1.0", Name = "sysDescr.0", Type = "STRING", Value = "Linux" },
                new SnmpEntry { Oid = "1.3.6.1.2.1.1.5.0", Name = "sysName.0", Type = "STRING", Value = "router" },
                new SnmpEntry { Oid = "1.3.6.1.2.1.1.6.0", Name = "sysLocation.0", Type = "STRING", Value = "lab" },
            ],
        };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1.2.1.1");

        Assert.Equal(3, vm.GetEntries().Count);
        Assert.False(vm.IsWalking);
        Assert.True(vm.ShowResults);
    }

    [Fact]
    public async Task Walk_ServiceThrows_SetsError()
    {
        var service = new FakeWalkerService { ThrowOnWalk = new SocketException() };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1.2.1.1");

        Assert.True(vm.ShowError);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorText));
    }

    [Fact]
    public async Task TestCommunities_DelegatesToService()
    {
        var service = new FakeWalkerService
        {
            CommunityTestResult =
            [
                new CommunityResult { Community = "public", Status = "Accepted", SysName = "router" },
            ],
        };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);

        await vm.TestCommunitiesAsync("10.0.0.1");

        Assert.Equal(1, service.CommunityTestCallCount);
        Assert.Single(vm.GetCommunityResults());
    }

    [Fact]
    public async Task SetGateway_DelegatesToService_AndUsesTunnelPath()
    {
        var service = new FakeWalkerService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22, User = "me" };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);
        vm.SetGateway(gateway);

        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1.2.1.1");

        Assert.Same(gateway, service.LastGateway);
        Assert.Equal(1, service.TunnelWalkCallCount);
    }

    [Fact]
    public async Task BuildCsvExport_AfterWalk_ReturnsNonEmptyString()
    {
        var service = new FakeWalkerService
        {
            WalkResult =
            [
                new SnmpEntry { Oid = "1.3.6.1", Name = "sysDescr", Type = "STRING", Value = "Linux" },
            ],
        };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);
        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1");

        var csv = vm.BuildCsvExport();

        Assert.False(string.IsNullOrWhiteSpace(csv));
        Assert.Contains("sysDescr", csv);
    }

    [Fact]
    public async Task BuildClipboardText_AfterWalk_ReturnsFormattedText()
    {
        var service = new FakeWalkerService
        {
            WalkResult =
            [
                new SnmpEntry { Oid = "1.3.6.1", Name = "sysDescr", Type = "STRING", Value = "Linux" },
            ],
        };
        var vm = new SnmpWalkerViewModel(service);
        vm.Initialize(null);
        await vm.WalkAsync("10.0.0.1", "public", "1.3.6.1");

        var text = vm.BuildClipboardText();

        Assert.Contains("ToolSnmpColOid", text);
        Assert.Contains("sysDescr", text);
    }

    private sealed class FakeWalkerService : ISnmpWalkerService
    {
        public IReadOnlyList<SnmpEntry>? WalkResult { get; set; }
        public IReadOnlyList<CommunityResult>? CommunityTestResult { get; set; }
        public Exception? ThrowOnWalk { get; set; }
        public int WalkCallCount { get; private set; }
        public int TunnelWalkCallCount { get; private set; }
        public int CommunityTestCallCount { get; private set; }
        public SshGatewayDto? LastGateway { get; private set; }
        public string LastCommunity { get; private set; } = string.Empty;
        public string LastOid { get; private set; } = string.Empty;

        public Task<IReadOnlyList<SnmpEntry>> WalkDirectAsync(
            string host,
            string community,
            string startOid,
            int timeoutMs,
            Action<SnmpWalkProgress>? onProgress,
            CancellationToken ct)
        {
            WalkCallCount++;
            LastCommunity = community;
            LastOid = startOid;

            if (ThrowOnWalk is not null)
            {
                return Task.FromException<IReadOnlyList<SnmpEntry>>(ThrowOnWalk);
            }

            var result = WalkResult ?? Array.Empty<SnmpEntry>();
            for (var i = 0; i < result.Count; i++)
            {
                onProgress?.Invoke(new SnmpWalkProgress { EntryCount = i + 1, LatestEntry = result[i] });
            }

            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<SnmpEntry>> WalkViaTunnelAsync(
            string host,
            string community,
            string oid,
            Action<SnmpWalkProgress>? onProgress,
            CancellationToken ct)
        {
            TunnelWalkCallCount++;
            LastCommunity = community;
            LastOid = oid;

            if (ThrowOnWalk is not null)
            {
                return Task.FromException<IReadOnlyList<SnmpEntry>>(ThrowOnWalk);
            }

            var result = WalkResult ?? Array.Empty<SnmpEntry>();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<CommunityResult>> TestCommunitiesAsync(
            string host,
            Func<string, string>? localize,
            CancellationToken ct)
        {
            CommunityTestCallCount++;
            return Task.FromResult(CommunityTestResult ?? (IReadOnlyList<CommunityResult>)Array.Empty<CommunityResult>());
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
            LastGateway = gateway;
        }
    }
}
