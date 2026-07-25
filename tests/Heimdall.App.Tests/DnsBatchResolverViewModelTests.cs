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

public sealed class DnsBatchResolverViewModelTests
{
    [Fact]
    public async Task ResolveCommand_EmptyInput_ShowsRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());
        vm.Initialize(localizer);
        vm.HostnamesInput = string.Empty;

        vm.ResolveCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public async Task ResolveCommand_WhitespaceInput_ShowsRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());
        vm.Initialize(localizer);
        vm.HostnamesInput = "   ";

        vm.ResolveCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
    }

    [Fact]
    public async Task ResolveCommand_DeduplicatesHostnames_CaseInsensitive()
    {
        var service = new FakeDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "Example.com\r\nexample.com\r\napi.example.com",
        };

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(2, service.Calls.Count);
        Assert.Equal("Example.com", service.Calls[0]);
        Assert.Equal("api.example.com", service.Calls[1]);
        Assert.Equal(2, vm.Results.Count);
    }

    [Fact]
    public async Task ResolveCommand_Success_PopulatesResultsAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service);
        vm.Initialize(localizer);
        vm.HostnamesInput = "example.com\r\napi.example.com";

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(2, vm.Results.Count);
        Assert.Contains("2", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.ShowError);
    }

    [Fact]
    public async Task ResolveCommand_PreservesInputOrder()
    {
        var service = new FakeDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "b.example\r\na.example",
        };

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal("b.example", vm.Results[0].Hostname);
        Assert.Equal("a.example", vm.Results[1].Hostname);
    }

    [Fact]
    public async Task ResolveCommand_ServiceFailedRow_IsStillAdded()
    {
        var service = new FakeDnsBatchResolverService
        {
            Factory = host => host.StartsWith("bad", StringComparison.Ordinal)
                ? DnsBatchResolveResult.Failed(host, 5, "HostNotFound")
                : DnsBatchResolveResult.Ok(host, [], 1),
        };
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "good.example\r\nbad.example",
        };

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(2, vm.Results.Count);
        Assert.False(vm.Results[1].Success);
        Assert.Equal("HostNotFound", vm.Results[1].Status);
    }

    [Fact]
    public async Task ResolveCommand_ServiceThrows_ShowsLookupFailedError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new ThrowingDnsBatchResolverService(new InvalidOperationException("boom"));
        var vm = new DnsBatchResolverViewModel(service);
        vm.Initialize(localizer);
        vm.HostnamesInput = "example.com";

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Contains("boom", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveCommand_Cancel_UserCancellation_LeavesNoError()
    {
        var service = new BlockingDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "example.com\r\napi.example.com",
        };

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.False(vm.ShowError);
        Assert.False(vm.HasResults);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task ResolveCommand_CanExecute_TogglesWhileBusy()
    {
        var service = new BlockingDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "example.com",
        };

        Assert.True(vm.ResolveCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.ResolveCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public void ToggleHelpCommand_FlipsVisibility()
    {
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public async Task Initialize_WithLocalizer_PopulatesPlaceholderAndHelp()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());

        vm.Initialize(localizer);

        Assert.Equal(localizer["ToolDnsBatchInputPlaceholder"], vm.InputPlaceholder);
        Assert.Equal(localizer["ToolDnsBatchInputPlaceholder"], vm.EmptyStateText);
        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsStatus()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service);
        vm.Initialize(en);
        vm.HostnamesInput = "example.com";

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);
        var englishStatus = vm.StatusText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.Contains("1", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsErrorTemplateButPreservesRawMessage()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new ThrowingDnsBatchResolverService(new InvalidOperationException("boom"));
        var vm = new DnsBatchResolverViewModel(service);
        vm.Initialize(en);
        vm.HostnamesInput = "example.com";

        vm.ResolveCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
        var englishError = vm.ErrorText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishError, vm.ErrorText);
        Assert.Contains("boom", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChanged_Event_ReprojectsHelpAndPlaceholder()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());
        vm.Initialize(localizer);
        var englishHelp = vm.HelpText;
        var englishPlaceholder = vm.InputPlaceholder;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishHelp, vm.HelpText);
        Assert.NotEqual(englishPlaceholder, vm.InputPlaceholder);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());
        vm.Initialize(localizer);

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsBatchResolverViewModel(new FakeDnsBatchResolverService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    [Fact]
    public void Dispose_ThenResolve_HasNoSideEffects()
    {
        var service = new FakeDnsBatchResolverService();
        var vm = new DnsBatchResolverViewModel(service)
        {
            HostnamesInput = "example.com",
        };

        vm.Dispose();
        vm.ResolveCommand.Execute(null);

        Assert.Empty(service.Calls);
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

    private sealed class FakeDnsBatchResolverService : IDnsBatchResolverService
    {
        public Func<string, DnsBatchResolveResult> Factory { get; set; } =
            host => DnsBatchResolveResult.Ok(host, [System.Net.IPAddress.Parse("192.0.2.10")], 5);

        public List<string> Calls { get; } = [];

        public Task<DnsBatchResolveResult> ResolveAsync(string hostname, CancellationToken ct)
        {
            Calls.Add(hostname);
            return Task.FromResult(Factory(hostname));
        }
    }

    private sealed class ThrowingDnsBatchResolverService : IDnsBatchResolverService
    {
        private readonly Exception _exception;

        public ThrowingDnsBatchResolverService(Exception exception)
        {
            _exception = exception;
        }

        public Task<DnsBatchResolveResult> ResolveAsync(string hostname, CancellationToken ct)
        {
            return Task.FromException<DnsBatchResolveResult>(_exception);
        }
    }

    private sealed class BlockingDnsBatchResolverService : IDnsBatchResolverService
    {
        public async Task<DnsBatchResolveResult> ResolveAsync(string hostname, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return DnsBatchResolveResult.Ok(hostname, [], 0);
        }
    }
}
