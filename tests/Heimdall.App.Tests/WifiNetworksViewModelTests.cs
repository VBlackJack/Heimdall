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

public sealed class WifiNetworksViewModelTests
{
    /// <summary>The status words the network count by its number, French 0 in the singular.</summary>
    [Theory]
    [InlineData("en", 1, "1 network at ")]
    [InlineData("en", 2, "2 networks at ")]
    [InlineData("fr", 0, "0 r\u00e9seau \u00e0 ")]
    [InlineData("fr", 2, "2 r\u00e9seaux \u00e0 ")]
    [InlineData("es", 1, "1 red a las ")]
    [InlineData("es", 0, "0 redes a las ")]
    public async Task ScanCommand_WordsTheNetworkCountByItsNumber(string locale, int count, string expectedStart)
    {
        var localizer = await CreateLocalizerAsync(locale);
        var service = new FakeWifiScanService
        {
            Results = Enumerable.Range(0, count)
                .Select(i => new WifiEntry($"Net{i}", $"aa:{i}", "70%", 70, "1", "WPA2", "CCMP", "ax"))
                .ToList(),
        };
        var vm = new WifiNetworksViewModel(service);
        vm.Initialize(localizer);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && service.Calls > 0);

        Assert.StartsWith(expectedStart, vm.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Before any scan the empty panel shows a sentence. It used to show the status template
    /// itself, placeholders and all: "{0} network(s) at {1}".
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task EmptyStateText_IsASentenceWithoutPlaceholders(string locale)
    {
        var localizer = await CreateLocalizerAsync(locale);
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyStateText));
        Assert.DoesNotContain("{", vm.EmptyStateText, StringComparison.Ordinal);
        Assert.Equal(localizer["ToolWifiEmptyState"], vm.EmptyStateText);
    }

    [Fact]
    public async Task Initialize_PopulatesHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task ScanCommand_Success_LoadsNetworksAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);

        Assert.Equal(2, vm.Networks.Count);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ScanCommand_PreservesServiceOrder()
    {
        var service = new FakeWifiScanService
        {
            Results =
            [
                new WifiEntry("First", "aa", "70%", 70, "1", "WPA2", "CCMP", "ax"),
                new WifiEntry("Second", "bb", "40%", 40, "6", "Open", "None", "n"),
            ],
        };
        var vm = new WifiNetworksViewModel(service);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count == 2);

        Assert.Equal("First", vm.Networks[0].Ssid);
        Assert.Equal("Second", vm.Networks[1].Ssid);
    }

    [Fact]
    public async Task ScanCommand_ClearsPreviousResultsBeforeReload()
    {
        var service = new FakeWifiScanService();
        var vm = new WifiNetworksViewModel(service);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count == 2);

        service.Results = [new WifiEntry("OnlyOne", "aa", "90%", 90, "11", "WPA3", "GCMP", "ax")];
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count == 1);

        Assert.Single(vm.Networks);
        Assert.Equal("OnlyOne", vm.Networks[0].Ssid);
    }

    [Fact]
    public async Task ScanCommand_Exception_SetsErrorAndPreservesStatusSnapshot()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeWifiScanService();
        var vm = new WifiNetworksViewModel(service);
        vm.Initialize(localizer);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count == 2);
        var previousStatus = vm.StatusText;

        service.Exception = new InvalidOperationException("boom");
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasError);

        Assert.Equal("boom", vm.ErrorText);
        Assert.Equal(previousStatus, vm.StatusText);
        Assert.Empty(vm.Networks);
    }

    [Fact]
    public async Task ScanCommand_IsBusyWhileRunnerBlocks()
    {
        var service = new BlockingWifiScanService();
        var vm = new WifiNetworksViewModel(service);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.ScanCommand.CanExecute(null));

        service.Release([new WifiEntry("Corp", "aa", "70%", 70, "1", "WPA2", "CCMP", "ax")]);
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public async Task ScanCommand_CanExecute_TogglesWhileBusy()
    {
        var service = new BlockingWifiScanService();
        var vm = new WifiNetworksViewModel(service);

        Assert.True(vm.ScanCommand.CanExecute(null));

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.ScanCommand.CanExecute(null));

        service.Release([]);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void CopyResultsCommand_CannotExecuteBeforeScan()
    {
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyResultsCommand_RaisesLocalizedClipboardPayload()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);

        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;

        vm.CopyResultsCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.StartsWith("SSID\tBSSID\tSignal\tChannel\tAuthentication\tEncryption\tRadio Type", payload, StringComparison.Ordinal);
        Assert.Contains("CorpWifi", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyResultsCommand_CannotExecuteAfterEmptyScan()
    {
        var vm = new WifiNetworksViewModel(new FakeWifiScanService { Results = [] });

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.ErrorText);
    }

    [Fact]
    public async Task CopyResultsCommand_UsesCurrentLocaleHeaders()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);

        await localizer.SwitchLocaleAsync("fr");

        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;
        vm.CopyResultsCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.StartsWith("SSID\tBSSID\tSignal\tCanal\tAuthentification\tChiffrement\tType radio", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanCommand_Error_DisablesCopy()
    {
        var vm = new WifiNetworksViewModel(new FakeWifiScanService { Exception = new InvalidOperationException("boom") });

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasError);

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsStatusAndHelp()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(en);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);
        var englishStatus = vm.StatusText;
        var englishHelp = vm.HelpText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.NotEqual(englishHelp, vm.HelpText);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateLocalizer_PreservesRawError()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService { Exception = new InvalidOperationException("boom") });
        vm.Initialize(en);

        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasError);

        vm.UpdateLocalizer(fr);

        Assert.Equal("boom", vm.ErrorText);
    }

    [Fact]
    public async Task UpdateLocalizer_SameInstance_NoOp()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);
        var status = vm.StatusText;

        vm.UpdateLocalizer(localizer);

        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public async Task LocaleChanged_Event_ReprojectsHelpAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.Networks.Count > 0);
        var englishHelp = vm.HelpText;
        var englishStatus = vm.StatusText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishHelp, vm.HelpText);
        Assert.NotEqual(englishStatus, vm.StatusText);
    }

    [Fact]
    public async Task LocaleChanged_Event_PreservesRawErrorText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService { Exception = new InvalidOperationException("boom") });
        vm.Initialize(localizer);
        vm.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasError);

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal("boom", vm.ErrorText);
    }

    [Fact]
    public void ToggleHelpCommand_FlipsVisibility()
    {
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new WifiNetworksViewModel(new FakeWifiScanService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    [Fact]
    public void Dispose_ThenScan_HasNoSideEffects()
    {
        var service = new FakeWifiScanService();
        var vm = new WifiNetworksViewModel(service);

        vm.Dispose();
        vm.ScanCommand.Execute(null);

        Assert.Equal(0, service.Calls);
        Assert.Empty(vm.Networks);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var timeoutAt = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > timeoutAt)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeWifiScanService : IWifiScanService
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }

        public IReadOnlyList<WifiEntry> Results { get; set; } =
        [
            new WifiEntry("CorpWifi", "aa:bb:cc:dd:ee:ff", "82%", 82, "36", "WPA2-Enterprise", "CCMP", "802.11ax"),
            new WifiEntry("GuestWifi", "11:22:33:44:55:66", "47%", 47, "11", "Open", "None", "802.11n"),
        ];

        public Task<IReadOnlyList<WifiEntry>> ScanAsync()
        {
            Calls++;
            if (Exception is not null)
            {
                return Task.FromException<IReadOnlyList<WifiEntry>>(Exception);
            }

            return Task.FromResult(Results);
        }
    }

    private sealed class BlockingWifiScanService : IWifiScanService
    {
        private readonly TaskCompletionSource<IReadOnlyList<WifiEntry>> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WifiEntry>> ScanAsync() => _gate.Task;

        public void Release(IReadOnlyList<WifiEntry> entries) => _gate.TrySetResult(entries);
    }
}
