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

public sealed class OpenPortsViewModelTests
{
    /// <summary>The status words the connection count by its number, French 0 in the singular.</summary>
    [Theory]
    [InlineData("en", 1, "1 connection at ")]
    [InlineData("en", 2, "2 connections at ")]
    [InlineData("fr", 0, "0 connexion \u00e0 ")]
    [InlineData("fr", 2, "2 connexions \u00e0 ")]
    [InlineData("es", 1, "1 conexi\u00f3n a las ")]
    [InlineData("es", 0, "0 conexiones a las ")]
    public async Task RefreshCommand_WordsTheConnectionCountByItsNumber(string locale, int count, string expectedStart)
    {
        var localizer = await CreateLocalizerAsync(locale);
        var service = new FakeOpenPortsService
        {
            Entries = Enumerable.Range(0, count)
                .Select(i => new PortEntry("TCP", "127.0.0.1", 1000 + i, "*", 0, "LISTENING", i, "proc"))
                .ToList(),
        };
        var vm = new OpenPortsViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.StartsWith(expectedStart, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_PopulatesHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task RefreshCommand_LoadsPortsAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Ports.Count);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshCommand_PreservesOrder()
    {
        var service = new FakeOpenPortsService
        {
            Entries =
            [
                new PortEntry("TCP", "b", 1, "r", 2, "ESTABLISHED", 1, "proc"),
                new PortEntry("UDP", "a", 3, "*", 0, "LISTENING", 2, "proc2"),
            ],
        };
        var vm = new OpenPortsViewModel(service);

        vm.RefreshCommand.Execute(null);

        Assert.Equal("b", vm.Ports[0].LocalAddress);
        Assert.Equal("a", vm.Ports[1].LocalAddress);
    }

    [Fact]
    public void RefreshCommand_ReplacesPreviousResults()
    {
        var service = new FakeOpenPortsService();
        var vm = new OpenPortsViewModel(service);

        vm.RefreshCommand.Execute(null);
        service.Entries =
        [
            new PortEntry("TCP", "10.0.0.2", 443, "10.0.0.1", 50000, "ESTABLISHED", 7, "iis"),
        ];

        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Ports);
        Assert.Equal("10.0.0.2", vm.Ports[0].LocalAddress);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
        vm.Initialize(en);
        vm.RefreshCommand.Execute(null);
        var englishStatus = vm.StatusText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsHelpAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
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
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void CopyResultsCommand_RaisesClipboardPayload()
    {
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;
        vm.RefreshCommand.Execute(null);

        vm.CopyResultsCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.StartsWith("Protocol\tLocal Address\tLocal Port\tRemote Address\tRemote Port\tState\tPID\tProcess", payload, StringComparison.Ordinal);
        Assert.Contains("nginx", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyResultsCommand_CannotExecuteBeforeRefresh()
    {
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    [Fact]
    public void Dispose_ThenRefresh_HasNoSideEffects()
    {
        var service = new FakeOpenPortsService();
        var vm = new OpenPortsViewModel(service);

        vm.Dispose();
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Ports);
        Assert.Equal(0, service.LoadCalls);
    }

    [Fact]
    public async Task UpdateLocalizer_SameInstance_NoOp()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new OpenPortsViewModel(new FakeOpenPortsService());
        vm.Initialize(localizer);
        vm.RefreshCommand.Execute(null);
        var status = vm.StatusText;

        vm.UpdateLocalizer(localizer);

        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public async Task RefreshCommand_EmptyPorts_StillUpdatesStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeOpenPortsService { Entries = [] };
        var vm = new OpenPortsViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Ports);
        Assert.Contains("0", vm.StatusText, StringComparison.Ordinal);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeOpenPortsService : IOpenPortsService
    {
        public int LoadCalls { get; private set; }

        public IReadOnlyList<PortEntry> Entries { get; set; } =
        [
            new PortEntry("TCP", "127.0.0.1", 443, "203.0.113.10", 51515, "ESTABLISHED", 1234, "nginx"),
            new PortEntry("UDP6", "::1", 5353, "*", 0, "LISTENING", 2222, "mdnsresponder"),
        ];

        public IReadOnlyList<PortEntry> Load()
        {
            LoadCalls++;
            return Entries;
        }
    }
}
