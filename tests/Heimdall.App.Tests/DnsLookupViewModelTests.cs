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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public sealed class DnsLookupViewModelTests
{
    [Fact]
    public async Task LookupCommand_EmptyHostname_SetsHostRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService();
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = string.Empty;

        vm.LookupCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.Null(service.LastRequest);
    }

    [Fact]
    public async Task LookupCommand_WhitespaceHostname_SetsHostRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService();
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "   ";

        vm.LookupCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.Null(service.LastRequest);
    }

    [Fact]
    public async Task LookupCommand_InvalidDomain_SetsInvalidHostError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService();
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "invalid hostname with spaces";

        vm.LookupCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationInvalidHost"], vm.ErrorText);
        Assert.Null(service.LastRequest);
    }

    [Fact]
    public async Task LookupCommand_Success_PopulatesResultsHeaderAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Result = DnsLookupResult.Ok("93.184.216.34", 42),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.True(vm.HasResults);
        Assert.Equal("93.184.216.34", vm.Results);
        Assert.Contains("A", vm.ResultHeader, StringComparison.Ordinal);
        Assert.Contains("example.com", vm.ResultHeader, StringComparison.Ordinal);
        Assert.Contains("42", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.ShowError);
    }

    [Fact]
    public async Task LookupCommand_TrimsWhitespaceFromHostname_BeforeService()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("ok", 5) };
        var vm = new DnsLookupViewModel(service) { Hostname = "  example.com  " };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.NotNull(service.LastRequest);
        Assert.Equal("example.com", service.LastRequest!.Hostname);
    }

    [Fact]
    public async Task LookupCommand_SelectedRecordTypeIndex_PassesRecordTypeToService()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("ok", 1) };
        var vm = new DnsLookupViewModel(service)
        {
            Hostname = "example.com",
            // Index 2 maps to MX in the AllRecordTypes array (A, AAAA, MX, ...)
            SelectedRecordTypeIndex = 2,
        };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.NotNull(service.LastRequest);
        Assert.Equal(DnsRecordType.MX, service.LastRequest!.RecordType);
    }

    [Fact]
    public async Task LookupCommand_OutOfRangeRecordTypeIndex_ClampsToLastEntry()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("ok", 1) };
        var vm = new DnsLookupViewModel(service)
        {
            Hostname = "example.com",
            SelectedRecordTypeIndex = 999,
        };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.NotNull(service.LastRequest);
        Assert.Equal(DnsRecordType.ANY, service.LastRequest!.RecordType);
    }

    [Fact]
    public async Task LookupCommand_SelectedDnsServerIndex_PassesServerAddressToService()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("ok", 1) };
        var vm = new DnsLookupViewModel(service)
        {
            Hostname = "example.com",
            // Index 1 maps to Google (8.8.8.8) in NetworkToolPresets.DnsServers.
            SelectedDnsServerIndex = 1,
        };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.NotNull(service.LastRequest);
        Assert.Equal("8.8.8.8", service.LastRequest!.DnsServer);
    }

    [Fact]
    public async Task LookupCommand_DefaultDnsServerIndex_SendsNullServer()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("ok", 1) };
        var vm = new DnsLookupViewModel(service)
        {
            Hostname = "example.com",
            SelectedDnsServerIndex = 0,
        };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.NotNull(service.LastRequest);
        Assert.Null(service.LastRequest!.DnsServer);
    }

    [Fact]
    public async Task LookupCommand_ServiceReturnsError_SetsShowErrorAndFormatsWithArg()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Result = DnsLookupResult.Error("ToolDnsErrorLookupFailed", 10, "no such host"),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Contains("no such host", vm.ErrorText, StringComparison.Ordinal);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public async Task LookupCommand_ServiceReturnsErrorWithoutArg_UsesBareTemplate()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Result = DnsLookupResult.Error("ToolDnsErrorTimeout", 5000),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolDnsErrorTimeout"], vm.ErrorText);
    }

    [Fact]
    public async Task LookupCommand_ServiceThrowsOperationCancelled_SetsTimeoutErrorMessage()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Exception = new OperationCanceledException(),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolDnsErrorTimeout"], vm.ErrorText);
    }

    [Fact]
    public async Task LookupCommand_ServiceThrowsException_SetsLookupFailedErrorWithMessage()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Exception = new InvalidOperationException("boom"),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Contains("boom", vm.ErrorText, StringComparison.Ordinal);
    }

    // CIUnstable: cancellation propagation can exceed the 2 s WaitUntilAsync
    // budget on the GitHub Actions Windows runner. Stable on dev machines.
    [Fact]
    [Trait("Category", "CIUnstable")]
    public async Task CancelCommand_UserCancellation_ClearsStatusWithoutError()
    {
        var service = new BlockingDnsLookupService();
        var vm = new DnsLookupViewModel(service) { Hostname = "example.com" };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.False(vm.ShowError);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task LookupCommand_WhileBusy_CanExecuteIsFalse()
    {
        var service = new BlockingDnsLookupService();
        var vm = new DnsLookupViewModel(service) { Hostname = "example.com" };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.LookupCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public void ToggleHelpCommand_FlipsIsHelpVisible()
    {
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void CanCopyResults_IsFalseWhenResultsEmpty()
    {
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CanCopyResults_BecomesTrueAfterSuccessfulLookup()
    {
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("1.2.3.4", 1) };
        var vm = new DnsLookupViewModel(service) { Hostname = "example.com" };

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Initialize_WithLocalizer_PopulatesWatermarkAndEmptyState()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(localizer);

        Assert.Equal(localizer["ToolWatermarkExampleDomain"], vm.HostnameWatermark);
        Assert.Equal(localizer["ToolEmptyStateDns"], vm.EmptyStateText);
        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task Initialize_PopulatesDnsServerLabelsInPresetOrder()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(localizer);

        Assert.Equal(NetworkToolPresets.DnsServers.Length, vm.DnsServers.Count);
        Assert.Equal(localizer["ToolDnsPresetSystem"], vm.DnsServers[0]);
        Assert.Equal(localizer["ToolDnsPresetGoogle"], vm.DnsServers[1]);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsResultHeaderAndStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeDnsLookupService { Result = DnsLookupResult.Ok("1.2.3.4", 123) };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(en);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);
        var englishHeader = vm.ResultHeader;
        var englishStatus = vm.StatusText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishHeader, vm.ResultHeader);
        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.Contains("example.com", vm.ResultHeader, StringComparison.Ordinal);
        Assert.Contains("123", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsErrorWithArgs()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeDnsLookupService
        {
            Result = DnsLookupResult.Error("ToolDnsErrorLookupFailed", 12, "no such host"),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(en);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
        var englishError = vm.ErrorText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishError, vm.ErrorText);
        Assert.Contains("no such host", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChanged_Event_RebuildsDnsServerLabels()
    {
        var en = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(en);

        var englishLabel = vm.DnsServers[0];
        await en.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishLabel, vm.DnsServers[0]);
        Assert.Equal(NetworkToolPresets.DnsServers.Length, vm.DnsServers.Count);
    }

    [Fact]
    public async Task LocaleChanged_Event_RefreshesHelpTextAndWatermark()
    {
        var en = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(en);
        var englishHelp = vm.HelpText;
        var englishWatermark = vm.HostnameWatermark;

        await en.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishHelp, vm.HelpText);
        Assert.NotEqual(englishWatermark, vm.HostnameWatermark);
    }

    [Fact]
    public void SetGateway_ForwardsToService()
    {
        var service = new FakeDnsLookupService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22 };
        var vm = new DnsLookupViewModel(service);

        vm.SetGateway(gateway);

        Assert.Same(gateway, service.LastGateway);
    }

    [Fact]
    public void SetGateway_Null_ForwardsToService()
    {
        var service = new FakeDnsLookupService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22 };
        var vm = new DnsLookupViewModel(service);

        vm.SetGateway(gateway);
        vm.SetGateway(null);

        Assert.Null(service.LastGateway);
    }

    [Fact]
    public async Task UpdateLocalizer_SameInstance_DoesNotRebuildTwice()
    {
        var en = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(en);
        var labelsBefore = vm.DnsServers.Count;

        vm.UpdateLocalizer(en);

        Assert.Equal(labelsBefore, vm.DnsServers.Count);
    }

    [Fact]
    public async Task ResetTransientState_ClearsPreviousErrorOnNewLookup()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsLookupService
        {
            Result = DnsLookupResult.Error("ToolDnsErrorLookupFailed", 1, "first failure"),
        };
        var vm = new DnsLookupViewModel(service);
        vm.Initialize(localizer);
        vm.Hostname = "example.com";

        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.True(vm.ShowError);

        service.Result = DnsLookupResult.Ok("1.2.3.4", 20);
        vm.LookupCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.False(vm.ShowError);
        Assert.Equal("1.2.3.4", vm.Results);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(localizer);

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var en = await CreateLocalizerAsync("en");
        var vm = new DnsLookupViewModel(new FakeDnsLookupService());
        vm.Initialize(en);
        var labelsBeforeDispose = vm.DnsServers[0];

        vm.Dispose();
        await en.SwitchLocaleAsync("fr");

        // After Dispose, the VM must not react to further locale changes.
        Assert.Equal(labelsBeforeDispose, vm.DnsServers[0]);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    /// <summary>
    /// Failure bound for a polled condition. The poll returns as soon as the
    /// predicate holds, so this is not a synchronisation point: it only has to
    /// outlast a saturated thread pool scheduling the work being observed.
    /// </summary>
    private const int PollBackstopMs = 30000;

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = PollBackstopMs)
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

    private sealed class FakeDnsLookupService : IDnsLookupService
    {
        public DnsLookupResult Result { get; set; } = DnsLookupResult.Ok(string.Empty, 0);
        public Exception? Exception { get; set; }
        public DnsLookupRequest? LastRequest { get; private set; }
        public SshGatewayDto? LastGateway { get; private set; }

        public void SetGateway(SshGatewayDto? gateway) => LastGateway = gateway;

        public Task<DnsLookupResult> LookupAsync(
            DnsLookupRequest request,
            Func<string, string> localize,
            CancellationToken ct)
        {
            LastRequest = request;
            if (Exception is not null)
            {
                return Task.FromException<DnsLookupResult>(Exception);
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class BlockingDnsLookupService : IDnsLookupService
    {
        private readonly TaskCompletionSource<DnsLookupResult> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetGateway(SshGatewayDto? gateway)
        {
        }

        public async Task<DnsLookupResult> LookupAsync(
            DnsLookupRequest request,
            Func<string, string> localize,
            CancellationToken ct)
        {
            using var registration = ct.Register(() => _gate.TrySetCanceled(ct));
            return await _gate.Task.ConfigureAwait(false);
        }
    }
}
