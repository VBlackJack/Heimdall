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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public sealed class NetworkInterfacesViewModelTests
{
    /// <summary>The status words the interface count by its number, French 0 in the singular.</summary>
    [Theory]
    [InlineData("en", 1, "1 interface at ")]
    [InlineData("en", 2, "2 interfaces at ")]
    [InlineData("fr", 0, "0 interface \u00e0 ")]
    [InlineData("fr", 2, "2 interfaces \u00e0 ")]
    [InlineData("es", 1, "1 interfaz a las ")]
    [InlineData("es", 0, "0 interfaces a las ")]
    public async Task RefreshCommand_WordsTheInterfaceCountByItsNumber(string locale, int count, string expectedStart)
    {
        var localizer = await CreateLocalizerAsync(locale);
        var service = new FakeNetworkInterfacesService
        {
            Snapshots = Enumerable.Range(0, count)
                .Select(i => new NicSnapshot($"nic{i}", "Ethernet", "Up", "1 Gbps", "", "", "", "", "DHCP"))
                .ToList(),
        };
        var vm = new NetworkInterfacesViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.StartsWith(expectedStart, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_PopulatesHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task RefreshCommand_LoadsSnapshotsAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Interfaces.Count);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshCommand_PreservesOrder()
    {
        var service = new FakeNetworkInterfacesService
        {
            Snapshots =
            [
                new NicSnapshot("b", "Ethernet", "Up", "1 Gbps", "", "", "", "", "DHCP"),
                new NicSnapshot("a", "Ethernet", "Up", "1 Gbps", "", "", "", "", "DHCP"),
            ],
        };
        var vm = new NetworkInterfacesViewModel(service);

        vm.RefreshCommand.Execute(null);

        Assert.Equal("b", vm.Interfaces[0].Name);
        Assert.Equal("a", vm.Interfaces[1].Name);
    }

    [Fact]
    public void RefreshCommand_ReplacesPreviousResults()
    {
        var service = new FakeNetworkInterfacesService();
        var vm = new NetworkInterfacesViewModel(service);

        vm.RefreshCommand.Execute(null);
        service.Snapshots =
        [
            new NicSnapshot("eth9", "Ethernet", "Up", "100 Mbps", "", "", "", "", "DHCP"),
        ];

        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Interfaces);
        Assert.Equal("eth9", vm.Interfaces[0].Name);
    }

    [Fact]
    public async Task RefreshCommand_Exception_ShowsRawStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new ThrowingNetworkInterfacesService(new InvalidOperationException("boom"));
        var vm = new NetworkInterfacesViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Equal("boom", vm.StatusText);
        Assert.Empty(vm.Interfaces);
        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        vm.Initialize(en);
        vm.RefreshCommand.Execute(null);
        var englishStatus = vm.StatusText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateLocalizer_PreservesRawErrorStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new NetworkInterfacesViewModel(new ThrowingNetworkInterfacesService(new InvalidOperationException("boom")));
        vm.Initialize(en);
        vm.RefreshCommand.Execute(null);

        vm.UpdateLocalizer(fr);

        Assert.Equal("boom", vm.StatusText);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsHelpAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        vm.Initialize(localizer);
        vm.RefreshCommand.Execute(null);
        var englishHelp = vm.HelpText;
        var englishStatus = vm.StatusText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishHelp, vm.HelpText);
        Assert.NotEqual(englishStatus, vm.StatusText);
    }

    [Fact]
    public void ToggleHelpCommand_FlipsVisibility()
    {
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void CopyResultsCommand_RaisesClipboardPayload()
    {
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;
        vm.RefreshCommand.Execute(null);

        vm.CopyResultsCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.StartsWith("Name\tType\tStatus\tSpeed\tMAC\tIPv4\tSubnet\tGateway\tDHCP", payload, StringComparison.Ordinal);
        Assert.Contains("Ethernet 1", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyResultsCommand_CannotExecuteBeforeRefresh()
    {
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    [Fact]
    public void Dispose_ThenRefresh_HasNoSideEffects()
    {
        var service = new FakeNetworkInterfacesService();
        var vm = new NetworkInterfacesViewModel(service);

        vm.Dispose();
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Interfaces);
        Assert.Equal(0, service.LoadCalls);
    }

    [Fact]
    public async Task UpdateLocalizer_SameInstance_NoOp()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new NetworkInterfacesViewModel(new FakeNetworkInterfacesService());
        vm.Initialize(localizer);
        vm.RefreshCommand.Execute(null);
        var status = vm.StatusText;

        vm.UpdateLocalizer(localizer);

        Assert.Equal(status, vm.StatusText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeNetworkInterfacesService : INetworkInterfacesService
    {
        public int LoadCalls { get; private set; }

        public IReadOnlyList<NicSnapshot> Snapshots { get; set; } =
        [
            new NicSnapshot("Ethernet 1", "Ethernet", "Up", "1 Gbps", "AA:BB:CC:DD:EE:FF", "192.0.2.10", "255.255.255.0", "192.0.2.1", "DHCP"),
            new NicSnapshot("Wi-Fi", "Wireless80211", "Down", "-", "", "", "", "", "Static"),
        ];

        public IReadOnlyList<NicSnapshot> Load()
        {
            LoadCalls++;
            return Snapshots;
        }
    }

    private sealed class ThrowingNetworkInterfacesService : INetworkInterfacesService
    {
        private readonly Exception _exception;

        public ThrowingNetworkInterfacesService(Exception exception)
        {
            _exception = exception;
        }

        public IReadOnlyList<NicSnapshot> Load() => throw _exception;
    }
}
