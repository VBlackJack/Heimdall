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

public sealed class RouteTableViewModelTests
{
    /// <summary>The status words the route count by its number, French 0 in the singular.</summary>
    [Theory]
    [InlineData("en", 1, "1 route at ")]
    [InlineData("en", 2, "2 routes at ")]
    [InlineData("fr", 0, "0 route \u00e0 ")]
    [InlineData("fr", 2, "2 routes \u00e0 ")]
    [InlineData("es", 1, "1 ruta a las ")]
    [InlineData("es", 0, "0 rutas a las ")]
    public async Task RefreshCommand_WordsTheRouteCountByItsNumber(string locale, int count, string expectedStart)
    {
        var localizer = await CreateLocalizerAsync(locale);
        var service = new FakeRouteTableService
        {
            Entries = Enumerable.Range(0, count)
                .Select(i => new RouteEntry($"10.0.{i}.0", "255.255.255.0", "On-link", "10.0.0.1", "1"))
                .ToList(),
        };
        var vm = new RouteTableViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.StartsWith(expectedStart, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_PopulatesHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new RouteTableViewModel(new FakeRouteTableService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task RefreshCommand_LoadsRoutesAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new RouteTableViewModel(new FakeRouteTableService());
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Routes.Count);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshCommand_PreservesOrder()
    {
        var service = new FakeRouteTableService
        {
            Entries =
            [
                new RouteEntry("b", "mask", "gw", "iface", "1"),
                new RouteEntry("a", "mask", "gw", "iface", "2"),
            ],
        };
        var vm = new RouteTableViewModel(service);

        vm.RefreshCommand.Execute(null);

        Assert.Equal("b", vm.Routes[0].Destination);
        Assert.Equal("a", vm.Routes[1].Destination);
    }

    [Fact]
    public void RefreshCommand_ReplacesPreviousResults()
    {
        var service = new FakeRouteTableService();
        var vm = new RouteTableViewModel(service);

        vm.RefreshCommand.Execute(null);
        service.Entries =
        [
            new RouteEntry("10.0.0.0", "255.0.0.0", "10.0.0.1", "10.0.0.2", "5"),
        ];

        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Routes);
        Assert.Equal("10.0.0.0", vm.Routes[0].Destination);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new RouteTableViewModel(new FakeRouteTableService());
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
        var vm = new RouteTableViewModel(new FakeRouteTableService());
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
        var vm = new RouteTableViewModel(new FakeRouteTableService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void CopyResultsCommand_RaisesClipboardPayload()
    {
        var vm = new RouteTableViewModel(new FakeRouteTableService());
        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;
        vm.RefreshCommand.Execute(null);

        vm.CopyResultsCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.StartsWith("Destination\tMask\tGateway\tInterface\tMetric", payload, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyResultsCommand_CannotExecuteBeforeRefresh()
    {
        var vm = new RouteTableViewModel(new FakeRouteTableService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new RouteTableViewModel(new FakeRouteTableService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new RouteTableViewModel(new FakeRouteTableService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    [Fact]
    public void Dispose_ThenRefresh_HasNoSideEffects()
    {
        var service = new FakeRouteTableService();
        var vm = new RouteTableViewModel(service);

        vm.Dispose();
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Routes);
        Assert.Equal(0, service.LoadCalls);
    }

    [Fact]
    public async Task UpdateLocalizer_SameInstance_NoOp()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new RouteTableViewModel(new FakeRouteTableService());
        vm.Initialize(localizer);
        vm.RefreshCommand.Execute(null);
        var status = vm.StatusText;

        vm.UpdateLocalizer(localizer);

        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public async Task RefreshCommand_EmptyRoutes_StillUpdatesStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeRouteTableService { Entries = [] };
        var vm = new RouteTableViewModel(service);
        vm.Initialize(localizer);

        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Routes);
        Assert.Contains("0", vm.StatusText, StringComparison.Ordinal);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeRouteTableService : IRouteTableService
    {
        public int LoadCalls { get; private set; }

        public IReadOnlyList<RouteEntry> Entries { get; set; } =
        [
            new RouteEntry("0.0.0.0", "0.0.0.0", "192.0.2.1", "192.0.2.10", "25"),
            new RouteEntry("192.0.2.0", "255.255.255.0", "On-link", "192.0.2.10", "281"),
        ];

        public IReadOnlyList<RouteEntry> Load()
        {
            LoadCalls++;
            return Entries;
        }
    }
}
