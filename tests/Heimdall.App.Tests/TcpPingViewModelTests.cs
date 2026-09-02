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

using System.Diagnostics;
using System.Globalization;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public sealed class TcpPingViewModelTests
{
    private const int DefaultWaitTimeoutMs = 10_000;

    [Fact]
    public async Task StartCommand_EmptyHost_ShowsRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = string.Empty;

        vm.StartCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task StartCommand_WhitespaceHost_ShowsRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "   ";

        vm.StartCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task StartCommand_InvalidHost_ShowsValidationError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "bad host";

        vm.StartCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationInvalidHost"], vm.ErrorText);
        Assert.Empty(service.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99999")]
    [InlineData("abc")]
    public async Task StartCommand_InvalidPort_ShowsRangeError(string port)
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Port = port;

        vm.StartCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationPortRangeRequired"], vm.ErrorText);
        Assert.Empty(service.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    [InlineData("abc")]
    public async Task StartCommand_InvalidCount_ShowsCountError(string count)
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Count = count;

        vm.StartCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolTcpPingErrorCount"], vm.ErrorText);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task StartCommand_SingleSuccess_PopulatesResultsSummaryAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 12.5),
        };
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Port = "443";
        vm.Count = "1";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal("[1/1] example.com:443 - 12.5 ms" + Environment.NewLine, vm.Results);
        Assert.Contains("Lost: 0/1", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("1 ping", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.ShowError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    [Trait("Category", "CIUnstable")]
    public async Task StartCommand_MixedResults_PreservesFailedLineAndSummary()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        service.Scripted.Enqueue(request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0));
        service.Scripted.Enqueue(request => TcpPingProbeResult.Failed(request.Seq, request.Host, request.Port, "Timeout"));
        service.Scripted.Enqueue(request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 30.0));

        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Count = "3";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        var lines = vm.Results.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("[2/3]", lines[1], StringComparison.Ordinal);
        Assert.Contains("FAILED:", lines[1], StringComparison.Ordinal);
        Assert.Contains("Lost: 1/3", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCommand_AllLost_UsesHyphenSummaryPlaceholders()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Failed(request.Seq, request.Host, request.Port, "Timeout"),
        };
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Count = "2";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Contains("Min: -", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Lost: 2/2", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCommand_TrimsHostBeforeProbing()
    {
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0),
        };
        var vm = new TcpPingViewModel(service)
        {
            Host = "  host.example.com  ",
            Count = "1",
        };

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Single(service.Calls);
        Assert.Equal("host.example.com", service.Calls[0].Host);
    }

    [Fact]
    public async Task StartCommand_ParsesPortFromString()
    {
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0),
        };
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
            Port = "8443",
            Count = "1",
        };

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Single(service.Calls);
        Assert.Equal(8443, service.Calls[0].Port);
    }

    [Fact]
    public async Task StartCommand_ServiceThrowsGenericException_EndsGracefullyWithExistingResults()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        service.Scripted.Enqueue(request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0));
        service.Scripted.Enqueue(_ => throw new InvalidOperationException("boom"));

        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Count = "3";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.Contains("[1/3]", vm.Results, StringComparison.Ordinal);
        Assert.DoesNotContain("[2/3]", vm.Results, StringComparison.Ordinal);
        Assert.Contains("Lost: 0/1", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCommand_ServiceThrowsOperationCanceledExceptionWithoutUserCancel_EndsGracefully()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeTcpPingService();
        service.Scripted.Enqueue(_ => throw new OperationCanceledException());

        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(localizer);
        vm.Host = "example.com";
        vm.Count = "2";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.False(vm.ShowError);
        Assert.Equal(string.Empty, vm.Results);
        Assert.Contains("0 ping", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopCommand_CancelsBlockingRun_AndLeavesEmptySummaryWhenNoProbeCompleted()
    {
        var service = new BlockingTcpPingService();
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
            Count = "10",
        };

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);
        vm.StopCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(service.WasCancelled);
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.SummaryText);
    }

    [Fact]
    public void StopCommand_WhenIdle_IsNoOp()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        vm.StopCommand.Execute(null);
    }

    [Fact]
    public async Task StartAndStopCommand_CanExecute_TogglesWithBusyState()
    {
        var service = new BlockingTcpPingService();
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
        };

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void CopyResultsCommand_BeforeRun_IsDisabled()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        Assert.False(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyResultsCommand_AfterSuccess_IsEnabled()
    {
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 1.0),
        };
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
            Count = "1",
        };

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyResultsCommand_DuringRun_IsDisabled()
    {
        var service = new BlockingTcpPingService();
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
        };

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.CopyResultsCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);
    }

    [Fact]
    public void DefaultValues_AreInitialized()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal("443", vm.Port);
        Assert.Equal("10", vm.Count);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasResults);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void UpdateLocalizer_Null_DoesNotThrow_AndFallsBackToKeys()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        vm.UpdateLocalizer(null);

        Assert.Equal("ToolWatermarkHostnameOrIp", vm.HostWatermark);
    }

    [Fact]
    public async Task LocaleChange_ReprojectsSummaryText()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0),
        };
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(en);
        vm.Host = "example.com";
        vm.Count = "1";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);
        var englishSummary = vm.SummaryText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishSummary, vm.SummaryText);
        Assert.Contains("Min :", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChange_ReprojectsStatusText()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeTcpPingService
        {
            Default = request => TcpPingProbeResult.Ok(request.Seq, request.Host, request.Port, 10.0),
        };
        var vm = new TcpPingViewModel(service);
        vm.UpdateLocalizer(en);
        vm.Host = "example.com";
        vm.Count = "1";

        vm.StartCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);
        var englishStatus = vm.StatusText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.Contains("1", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChange_ReprojectsValidationError()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new TcpPingViewModel(new FakeTcpPingService());
        vm.UpdateLocalizer(en);
        vm.Host = "example.com";
        vm.Count = "abc";

        vm.StartCommand.Execute(null);
        var englishError = vm.ErrorText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishError, vm.ErrorText);
        Assert.Equal(fr["ToolTcpPingErrorCount"], vm.ErrorText);
    }

    [Fact]
    public async Task LocaleChange_RefreshesHelpText()
    {
        var en = await CreateLocalizerAsync("en");
        var vm = new TcpPingViewModel(new FakeTcpPingService());
        vm.UpdateLocalizer(en);
        var englishHelp = vm.HelpText;

        await en.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishHelp, vm.HelpText);
    }

    [Fact]
    public void ToggleHelpCommand_FlipsVisibility()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        Assert.False(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new TcpPingViewModel(new FakeTcpPingService());

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public void Dispose_ThenStart_HasNoSideEffects()
    {
        var service = new FakeTcpPingService();
        var vm = new TcpPingViewModel(service)
        {
            Host = "example.com",
        };

        vm.Dispose();
        vm.StartCommand.Execute(null);

        Assert.Empty(service.Calls);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = DefaultWaitTimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not satisfied in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeTcpPingService : ITcpPingService
    {
        public List<TcpPingProbeRequest> Calls { get; } = [];
        public Queue<Func<TcpPingProbeRequest, TcpPingProbeResult>> Scripted { get; } = [];
        public Func<TcpPingProbeRequest, TcpPingProbeResult>? Default { get; set; }

        public Task<TcpPingProbeResult> ProbeAsync(TcpPingProbeRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            ct.ThrowIfCancellationRequested();
            var factory = Scripted.Count > 0 ? Scripted.Dequeue() : Default;
            if (factory is null)
            {
                throw new Xunit.Sdk.XunitException("FakeTcpPingService called without a scripted response.");
            }

            return Task.FromResult(factory(request));
        }
    }

    private sealed class BlockingTcpPingService : ITcpPingService
    {
        private readonly TaskCompletionSource<TcpPingProbeResult> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public Task<TcpPingProbeResult> ProbeAsync(TcpPingProbeRequest request, CancellationToken ct)
        {
            ct.Register(() =>
            {
                WasCancelled = true;
                _gate.TrySetCanceled(ct);
            });

            return _gate.Task;
        }

        public void Release(TcpPingProbeResult result) => _gate.TrySetResult(result);
    }
}
