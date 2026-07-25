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
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public sealed class DnsSecurityCheckerViewModelTests
{
    [Fact]
    public async Task CheckCommand_EmptyInput_SetsHostRequiredError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsSecurityService();
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(localizer);
        vm.Input = string.Empty;

        vm.CheckCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolValidationHostRequired"], vm.ErrorText);
        Assert.Null(service.LastDomain);
    }

    [Fact]
    public async Task CheckCommand_NormalizesUrlAndCallsServiceWithDomain()
    {
        var service = new FakeDnsSecurityService { Results = CreateResults(allPass: true) };
        var vm = new DnsSecurityCheckerViewModel(service)
        {
            Input = "https://Example.com:8443/path?q=1",
        };

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal("example.com", service.LastDomain);
    }

    [Fact]
    public async Task CheckCommand_EmailInput_StripsLocalPart()
    {
        var service = new FakeDnsSecurityService { Results = CreateResults(allPass: true) };
        var vm = new DnsSecurityCheckerViewModel(service)
        {
            Input = "admin@example.com",
        };

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal("example.com", service.LastDomain);
    }

    [Fact]
    public async Task CheckCommand_InvalidDomain_SetsValidationError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsSecurityService();
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(localizer);
        vm.Input = "not a domain";

        vm.CheckCommand.Execute(null);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ErrorInvalidDomain"], vm.ErrorText);
        Assert.Null(service.LastDomain);
    }

    [Fact]
    public async Task CheckCommand_AllPass_PopulatesResultsAndSummary()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsSecurityService { Results = CreateResults(allPass: true) };
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(localizer);
        vm.Input = "example.com";

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(DnsSummaryStatus.AllPass, vm.SummaryStatus);
        Assert.Equal(6, vm.CheckResults.Count);
        Assert.False(vm.ShowError);
        Assert.Contains("example.com", vm.ReportText, StringComparison.Ordinal);
        Assert.Contains("6", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCommand_FourPasses_SetsGoodSummary()
    {
        var service = new FakeDnsSecurityService
        {
            Results =
            [
                Pass(DnsCheckKind.Spf),
                Pass(DnsCheckKind.Dkim),
                Pass(DnsCheckKind.Dmarc),
                Pass(DnsCheckKind.Caa),
                Fail(DnsCheckKind.Dnssec),
                Fail(DnsCheckKind.Mx),
            ],
        };
        var vm = new DnsSecurityCheckerViewModel(service) { Input = "example.com" };

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(DnsSummaryStatus.Good, vm.SummaryStatus);
    }

    [Fact]
    public async Task CheckCommand_AllFail_SetsBadSummary()
    {
        var service = new FakeDnsSecurityService
        {
            Results =
            [
                Fail(DnsCheckKind.Spf),
                Fail(DnsCheckKind.Dkim),
                Fail(DnsCheckKind.Dmarc),
                Fail(DnsCheckKind.Caa),
                Fail(DnsCheckKind.Dnssec),
                Fail(DnsCheckKind.Mx),
            ],
        };
        var vm = new DnsSecurityCheckerViewModel(service) { Input = "example.com" };

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);

        Assert.Equal(DnsSummaryStatus.Bad, vm.SummaryStatus);
    }

    [Fact]
    public async Task CheckCommand_ServiceThrows_SetsLookupError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsSecurityService { ExceptionToThrow = new InvalidOperationException("boom") };
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(localizer);
        vm.Input = "example.com";

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Contains("boom", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelCommand_UserCancellation_StopsWithoutError()
    {
        var service = new BlockingDnsSecurityService();
        var vm = new DnsSecurityCheckerViewModel(service) { Input = "example.com" };

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsBusy);
        vm.CancelCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.False(vm.ShowError);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public async Task CheckCommand_OperationCanceledWithoutUserCancel_ShowsTimeoutError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var service = new FakeDnsSecurityService { ExceptionToThrow = new OperationCanceledException() };
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(localizer);
        vm.Input = "example.com";

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy);

        Assert.True(vm.ShowError);
        Assert.Equal(localizer["ToolDnsErrorTimeout"], vm.ErrorText);
    }

    [Fact]
    public async Task UpdateLocalizer_ReprojectsResultsAndReport()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var service = new FakeDnsSecurityService { Results = CreateResults(allPass: true) };
        var vm = new DnsSecurityCheckerViewModel(service);
        vm.Initialize(en);
        vm.Input = "example.com";

        vm.CheckCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasResults);
        var englishDetail = vm.CheckResults[0].Detail;
        var englishSummary = vm.SummaryText;
        var englishReport = vm.ReportText;

        vm.UpdateLocalizer(fr);

        Assert.NotEqual(englishDetail, vm.CheckResults[0].Detail);
        Assert.NotEqual(englishSummary, vm.SummaryText);
        Assert.NotEqual(englishReport, vm.ReportText);
    }

    [Fact]
    public void SetGateway_ForwardsToService()
    {
        var service = new FakeDnsSecurityService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22 };
        var vm = new DnsSecurityCheckerViewModel(service);

        vm.SetGateway(gateway);

        Assert.Same(gateway, service.LastGateway);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new DnsSecurityCheckerViewModel(new FakeDnsSecurityService());
        vm.Initialize(localizer);

        vm.Dispose();
        vm.Dispose();
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

    private static IReadOnlyList<DnsCheckResult> CreateResults(bool allPass)
        => allPass
            ?
            [
                Pass(DnsCheckKind.Spf),
                Pass(DnsCheckKind.Dkim),
                Pass(DnsCheckKind.Dmarc),
                Pass(DnsCheckKind.Caa),
                Pass(DnsCheckKind.Dnssec),
                Pass(DnsCheckKind.Mx),
            ]
            :
            [
                Pass(DnsCheckKind.Spf),
                Fail(DnsCheckKind.Dkim),
                Pass(DnsCheckKind.Dmarc),
                Fail(DnsCheckKind.Caa),
                Pass(DnsCheckKind.Dnssec),
                Fail(DnsCheckKind.Mx),
            ];

    private static DnsCheckResult Pass(DnsCheckKind kind)
        => new(kind, DnsCheckStatus.Pass, "record", "ToolDnsSecPass", []);

    private static DnsCheckResult Fail(DnsCheckKind kind)
        => new(kind, DnsCheckStatus.Fail, string.Empty, "ToolDnsSecFail", []);

    private sealed class FakeDnsSecurityService : IDnsSecurityService
    {
        public IReadOnlyList<DnsCheckResult> Results { get; set; } = CreateResults(allPass: true);
        public Exception? ExceptionToThrow { get; set; }
        public string? LastDomain { get; private set; }
        public SshGatewayDto? LastGateway { get; private set; }

        public Task<IReadOnlyList<DnsCheckResult>> RunAllChecksAsync(string domain, CancellationToken ct)
        {
            LastDomain = domain;

            if (ExceptionToThrow is not null)
            {
                return Task.FromException<IReadOnlyList<DnsCheckResult>>(ExceptionToThrow);
            }

            return Task.FromResult(Results);
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
            LastGateway = gateway;
        }
    }

    private sealed class BlockingDnsSecurityService : IDnsSecurityService
    {
        private readonly TaskCompletionSource<IReadOnlyList<DnsCheckResult>> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<DnsCheckResult>> RunAllChecksAsync(string domain, CancellationToken ct)
        {
            using var registration = ct.Register(() => _gate.TrySetCanceled(ct));
            return await _gate.Task;
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
        }
    }
}
