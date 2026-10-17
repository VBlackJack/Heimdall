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

public class PortScanViewModelTests
{
    // Upper bound on each hand-off of the parked-update test; it fails the test, it never paces it.
    private static readonly TimeSpan ParkBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Scan_EmptyHost_SetsError()
    {
        var service = new FakeScanService();
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("   ", [22]);

        Assert.True(vm.ShowError);
        Assert.False(vm.IsScanning);
        Assert.Equal(0, service.ScanCallCount);
    }

    [Fact]
    public async Task Scan_InvalidHost_SetsError()
    {
        var service = new FakeScanService();
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("bad;host", [22]);

        Assert.True(vm.ShowError);
        Assert.Equal(0, service.ScanCallCount);
    }

    [Fact]
    public async Task Scan_ValidInput_DelegatesToService()
    {
        var service = new FakeScanService();
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("10.0.0.1", [22, 80]);

        Assert.Equal(1, service.ScanCallCount);
        Assert.Equal("10.0.0.1", service.LastHost);
        Assert.Equal([22, 80], service.LastPorts);
    }

    [Fact]
    public async Task Scan_CompletesSuccessfully_PopulatesResults()
    {
        var service = new FakeScanService
        {
            ScanResult =
            [
                new PortProbeResult(22, true, "SSH", "5 ms", "SSH-2.0-OpenSSH_9.0"),
                new PortProbeResult(80, true, "HTTP", "10 ms", null),
                new PortProbeResult(443, false, "HTTPS", "—", null),
            ],
        };
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("10.0.0.1", [22, 80, 443]);

        Assert.Equal(3, vm.GetAllResults().Count);
        Assert.Equal(2, vm.OpenCount);
        Assert.Equal(1, vm.ClosedCount);
    }

    [Fact]
    public async Task Scan_ProgressUpdateParkedAcrossTheFinalTotals_DoesNotOverwriteThem()
    {
        // Progress<T> posts each update to the captured context, here a pool thread. The update
        // for port 22 reads one open port and is parked just before it writes that count; the
        // service then returns all three results. The counters must end on the final totals,
        // not on the parked update's stale count.
        var updateParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new ManualResetEventSlim();
        var staleCountWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ParkingScanService(updateParked.Task)
        {
            Reported = new PortProbeResult(22, true, "SSH", "5 ms", null),
            ScanResult =
            [
                new PortProbeResult(22, true, "SSH", "5 ms", null),
                new PortProbeResult(80, true, "HTTP", "10 ms", null),
                new PortProbeResult(443, false, "HTTPS", "-", null),
            ],
        };
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);
        var testThread = Environment.CurrentManagedThreadId;
        vm.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(PortScanViewModel.OpenCount)
                && Environment.CurrentManagedThreadId != testThread
                && !updateParked.Task.IsCompleted)
            {
                updateParked.TrySetResult();
                releaseUpdate.Wait(ParkBound);
            }
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PortScanViewModel.OpenCount) && vm.OpenCount == 1)
            {
                staleCountWritten.TrySetResult();
            }
        };

        var scan = vm.ScanAsync("10.0.0.1", [22, 80, 443]);
        releaseUpdate.Set();
        await staleCountWritten.Task.WaitAsync(ParkBound);
        await scan.WaitAsync(ParkBound);

        Assert.Equal(2, vm.OpenCount);
        Assert.Equal(1, vm.ClosedCount);
        Assert.Equal(3, vm.Completed);
    }

    [Fact]
    public async Task Scan_ServiceThrows_SetsError()
    {
        var service = new FakeScanService { ExceptionToThrow = new InvalidOperationException("boom") };
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("10.0.0.1", [22]);

        Assert.True(vm.ShowError);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorText));
    }

    [Fact]
    public void SetGateway_DelegatesToService()
    {
        var service = new FakeScanService();
        var gateway = new SshGatewayDto { Name = "gw", Host = "jump", Port = 22, User = "me" };
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        vm.SetGateway(gateway);

        Assert.Same(gateway, service.LastGateway);
    }

    [Fact]
    public async Task Scan_ProgressUpdates_UpdateCounters()
    {
        var service = new FakeScanService
        {
            ScanResult =
            [
                new PortProbeResult(22, true, "SSH", "5 ms", null),
                new PortProbeResult(80, false, "HTTP", "—", null),
            ],
        };
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        await vm.ScanAsync("10.0.0.1", [22, 80]);

        Assert.Equal(2, vm.Completed);
        Assert.Equal(2, vm.Total);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(1, vm.OpenCount);
        Assert.Equal(1, vm.ClosedCount);
    }

    [Fact]
    public async Task Cancel_StopsScanning()
    {
        var service = new BlockingScanService();
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        var scanTask = vm.ScanAsync("10.0.0.1", [22]);
        await Task.Delay(50);
        vm.CancelScan();
        await scanTask;

        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task Cancel_DoesNotShowError()
    {
        var service = new BlockingScanService();
        var vm = new PortScanViewModel(service);
        vm.Initialize(null);

        var scanTask = vm.ScanAsync("10.0.0.1", [22]);
        await Task.Delay(50);
        vm.CancelScan();
        await scanTask;

        Assert.False(vm.ShowError);
        Assert.True(string.IsNullOrWhiteSpace(vm.ErrorText));
    }

    [Fact]
    public void ParseAndValidatePorts_Empty_ReturnsErrorKey()
    {
        var vm = new PortScanViewModel();
        var (ports, errorKey) = vm.ParseAndValidatePorts(string.Empty);

        Assert.Null(ports);
        Assert.Equal("ToolValidationPortRangeRequired", errorKey);
    }

    private sealed class FakeScanService : IPortScanService
    {
        public IReadOnlyList<PortProbeResult>? ScanResult { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public int ScanCallCount { get; private set; }
        public SshGatewayDto? LastGateway { get; private set; }
        public string LastHost { get; private set; } = string.Empty;
        public IReadOnlyList<int> LastPorts { get; private set; } = Array.Empty<int>();

        public Task<IReadOnlyList<PortProbeResult>> ScanAsync(
            string host,
            IReadOnlyList<int> ports,
            Action<PortScanProgress>? onProgress,
            CancellationToken ct)
        {
            ScanCallCount++;
            LastHost = host;
            LastPorts = [.. ports];

            if (ExceptionToThrow is not null)
            {
                return Task.FromException<IReadOnlyList<PortProbeResult>>(ExceptionToThrow);
            }

            var results = ScanResult ?? Array.Empty<PortProbeResult>();
            for (var i = 0; i < results.Count; i++)
            {
                onProgress?.Invoke(new PortScanProgress
                {
                    Completed = i + 1,
                    Total = ports.Count,
                    LatestResult = results[i],
                });
            }

            return Task.FromResult(results);
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
            LastGateway = gateway;
        }
    }

    /// <summary>
    /// Reports one update, then holds its return until the view model's progress callback for
    /// that update is parked, so the final totals are written while the update is in flight.
    /// </summary>
    private sealed class ParkingScanService(Task updateParked) : IPortScanService
    {
        public required PortProbeResult Reported { get; init; }
        public required IReadOnlyList<PortProbeResult> ScanResult { get; init; }

        public Task<IReadOnlyList<PortProbeResult>> ScanAsync(
            string host,
            IReadOnlyList<int> ports,
            Action<PortScanProgress>? onProgress,
            CancellationToken ct)
        {
            onProgress?.Invoke(new PortScanProgress
            {
                Completed = 1,
                Total = ports.Count,
                LatestResult = Reported,
            });
            updateParked.Wait(ParkBound);
            return Task.FromResult(ScanResult);
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
        }
    }

    private sealed class BlockingScanService : IPortScanService
    {
        public async Task<IReadOnlyList<PortProbeResult>> ScanAsync(
            string host,
            IReadOnlyList<int> ports,
            Action<PortScanProgress>? onProgress,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Array.Empty<PortProbeResult>();
        }

        public void SetGateway(SshGatewayDto? gateway)
        {
        }
    }
}
