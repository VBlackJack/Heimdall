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

using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Network;

namespace Heimdall.App.Tests;

public class FirewallTesterViewModelTests
{
    [Fact]
    public void ParseAndValidateInputs_EmptyHosts_ReturnsErrorKey()
    {
        var vm = new FirewallTesterViewModel();

        var (hosts, ports, errorKey) = vm.ParseAndValidateInputs(string.Empty, "22");

        Assert.Null(hosts);
        Assert.Null(ports);
        Assert.Equal("ToolFwErrorNoHosts", errorKey);
    }

    [Fact]
    public void ParseAndValidateInputs_InvalidHostsOnly_ReturnsErrorKey()
    {
        var vm = new FirewallTesterViewModel();

        var (hosts, ports, errorKey) = vm.ParseAndValidateInputs("bad;host", "22");

        Assert.Null(hosts);
        Assert.Null(ports);
        Assert.Equal("ErrorInvalidHost", errorKey);
    }

    [Fact]
    public void ParseAndValidateInputs_EmptyPorts_ReturnsErrorKey()
    {
        var vm = new FirewallTesterViewModel();

        var (hosts, ports, errorKey) = vm.ParseAndValidateInputs("127.0.0.1", string.Empty);

        Assert.Null(hosts);
        Assert.Null(ports);
        Assert.Equal("ToolFwErrorNoPorts", errorKey);
    }

    [Fact]
    public void ParseAndValidateInputs_ValidInputs_ReturnsCappedLists()
    {
        var vm = new FirewallTesterViewModel();
        var hostsText = string.Join(Environment.NewLine, Enumerable.Range(1, 100).Select(index => $"10.0.0.{index}"));

        var (hosts, ports, errorKey) = vm.ParseAndValidateInputs(hostsText, "1-100");

        Assert.Null(errorKey);
        Assert.NotNull(hosts);
        Assert.NotNull(ports);
        Assert.Equal(50, hosts!.Count);
        Assert.Equal(50, ports!.Count);
    }

    [Fact]
    public async Task TestAsync_CompletesSuccessfully_PopulatesResults()
    {
        var service = new FakeFirewallTesterService
        {
            Result =
            [
                new FwProbeResult("h1", 22, ProbeStatus.Open, 10),
                new FwProbeResult("h1", 80, ProbeStatus.Closed, 20),
                new FwProbeResult("h2", 22, ProbeStatus.Timeout, 30),
                new FwProbeResult("h2", 80, ProbeStatus.Open, 40),
            ],
        };
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        await vm.TestAsync(["h1", "h2"], [22, 80]);

        Assert.Equal(4, vm.GetAllResults().Count);
        Assert.Equal(2, vm.OpenCount);
        Assert.Equal(1, vm.ClosedCount);
        Assert.Equal(1, vm.TimeoutCount);
        Assert.Contains("2", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestAsync_ServiceThrows_SetsError()
    {
        var service = new FakeFirewallTesterService { ExceptionToThrow = new InvalidOperationException("gateway down") };
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        await vm.TestAsync(["h1"], [22]);

        Assert.True(vm.ShowError);
        Assert.Contains("gateway down", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void SetGateway_DelegatesToService()
    {
        var service = new FakeFirewallTesterService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22, User = "me" };
        var vm = new FirewallTesterViewModel(service);

        vm.SetGateway(gateway);

        Assert.Same(gateway, service.LastGateway);
    }

    [Fact]
    public async Task TestAsync_ProgressUpdates_UpdateCounters()
    {
        var service = new FakeFirewallTesterService
        {
            Result =
            [
                new FwProbeResult("h1", 22, ProbeStatus.Open, 10),
                new FwProbeResult("h1", 80, ProbeStatus.Timeout, 20),
            ],
        };
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        await TestAndDrainProgressAsync(vm, ["h1"], [22, 80]);

        Assert.Equal(2, vm.Completed);
        Assert.Equal(2, vm.Total);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(1, vm.OpenCount);
        Assert.Equal(1, vm.TimeoutCount);
    }

    /// <summary>
    /// Producer: <c>FirewallTesterViewModel.TestAsync</c>, the <c>Progress&lt;FwProbeProgress&gt;</c>
    /// callback. <c>Progress&lt;T&gt;</c> posts each report to the synchronization context, so a
    /// report can run after the run has written its final counters. Here the fake reports once
    /// (1 of 2) and returns two results; the posted report is run only after the final state is in
    /// place, and must not overwrite it.
    /// </summary>
    [Fact]
    public async Task TestAsync_LateProgressReport_DoesNotOverwriteFinalCounters()
    {
        var service = new FakeFirewallTesterService
        {
            Result =
            [
                new FwProbeResult("h1", 22, ProbeStatus.Open, 10),
                new FwProbeResult("h1", 80, ProbeStatus.Timeout, 20),
            ],
            ProgressReportCount = 1,
        };
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        await TestAndDrainProgressAsync(vm, ["h1"], [22, 80]);

        Assert.Equal(2, vm.Completed);
        Assert.Equal(2, vm.Total);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(1, vm.OpenCount);
        Assert.Equal(1, vm.TimeoutCount);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task Cancel_StopsTesting()
    {
        var service = new BlockingFirewallTesterService();
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        var task = vm.TestAsync(["h1"], [22]);
        await Task.Delay(50);
        vm.CancelTest();
        service.Release();
        await task;

        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task GetLastHosts_ReturnsInputsFromLastTest()
    {
        var service = new FakeFirewallTesterService
        {
            Result = [new FwProbeResult("h1", 22, ProbeStatus.Open, 10)],
        };
        var vm = new FirewallTesterViewModel(service);
        vm.Initialize(null);

        await vm.TestAsync(["h1", "h2"], [22]);

        Assert.Equal(["h1", "h2"], vm.GetLastHosts());
        Assert.Equal([22], vm.GetLastPorts());
    }

    /// <summary>
    /// Runs a test whose fake service completes synchronously, with the progress reports queued
    /// on a synchronization context instead of the thread pool, then runs the queued reports once
    /// the final counters are written. That is the order a late report takes in the application.
    /// </summary>
    private static async Task TestAndDrainProgressAsync(
        FirewallTesterViewModel viewModel,
        IReadOnlyList<string> hosts,
        IReadOnlyList<int> ports)
    {
        SynchronizationContext? originalContext = SynchronizationContext.Current;
        QueuedSynchronizationContext progressContext = new();
        try
        {
            SynchronizationContext.SetSynchronizationContext(progressContext);
            Task testTask = viewModel.TestAsync(hosts, ports);
            Assert.True(
                testTask.IsCompleted,
                "The fake firewall tester service must complete synchronously.");
            await testTask.ConfigureAwait(false);
            progressContext.RunPostedCallbacks();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
        }

        public void RunPostedCallbacks()
        {
            while (_callbacks.TryDequeue(
                out (SendOrPostCallback Callback, object? State) callback))
            {
                callback.Callback(callback.State);
            }
        }
    }

    private sealed class FakeFirewallTesterService : IFirewallTesterService
    {
        public IReadOnlyList<FwProbeResult> Result { get; set; } = Array.Empty<FwProbeResult>();
        public Exception? ExceptionToThrow { get; set; }
        public SshGatewayDto? LastGateway { get; private set; }

        /// <summary>How many of the results are reported as progress before the task completes.</summary>
        public int ProgressReportCount { get; set; } = int.MaxValue;

        public Task<IReadOnlyList<FwProbeResult>> TestMatrixAsync(
            IReadOnlyList<string> hosts,
            IReadOnlyList<int> ports,
            Action<FwProbeProgress>? onProgress,
            CancellationToken ct)
        {
            if (ExceptionToThrow is not null)
            {
                return Task.FromException<IReadOnlyList<FwProbeResult>>(ExceptionToThrow);
            }

            int reported = Math.Min(Result.Count, ProgressReportCount);
            for (var index = 0; index < reported; index++)
            {
                onProgress?.Invoke(new FwProbeProgress
                {
                    Completed = index + 1,
                    Total = hosts.Count * ports.Count,
                    LatestResult = Result[index],
                });
            }

            return Task.FromResult(Result);
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
            LastGateway = gateway;
        }
    }

    private sealed class BlockingFirewallTesterService : IFirewallTesterService
    {
        private readonly TaskCompletionSource<object?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<FwProbeResult>> TestMatrixAsync(
            IReadOnlyList<string> hosts,
            IReadOnlyList<int> ports,
            Action<FwProbeProgress>? onProgress,
            CancellationToken ct)
        {
            using var registration = ct.Register(() => _gate.TrySetCanceled(ct));
            await _gate.Task;
            return Array.Empty<FwProbeResult>();
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
        }

        public void Release()
        {
            _gate.TrySetResult(null);
        }
    }
}
