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

public class TracerouteViewModelTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The waits it bounds complete on
    /// an event: a signal the fake traceroute handler raises once it is entered or
    /// blocked. The value only has to be generous enough that a saturated thread
    /// pool cannot exhaust it before that handler runs at all, and it is paid only
    /// on failure. Raised from 30 seconds after Stop_CancelsInFlight exhausted that
    /// value on run 30157869234 attempt 2, where the continuation that observes the
    /// cancellation was still queued on the bounded concurrency context xUnit posts
    /// test continuations to.
    /// </summary>
    private static readonly TimeSpan SignalBackstop = TimeSpan.FromSeconds(60);

    [Fact]
    public void ValidateInputs_DelegatesToEngine()
    {
        var vm = new TracerouteViewModel(new FakeTracerouteService());

        var (inputs, errorKey) = vm.ValidateInputs(string.Empty, "30");

        Assert.Null(inputs);
        Assert.Equal("ToolValidationHostRequired", errorKey);
    }

    [Fact]
    public async Task TraceAsync_PopulatesHops_WhenServiceReportsHops()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = async (inputs, onHop, onProgress, onHostname, ct) =>
            {
                onHop?.Report(new TraceHopResult(1, "10.0.0.1", string.Empty, "1 ms", HopStatus.Reply));
                onHop?.Report(new TraceHopResult(2, "10.0.0.2", string.Empty, "2 ms", HopStatus.Reply));
                onHop?.Report(new TraceHopResult(3, "8.8.8.8", string.Empty, "3 ms", HopStatus.Destination));
                await Task.CompletedTask;
                return true;
            },
        };

        var vm = new TracerouteViewModel(service);
        await TraceAndDrainProgressAsync(vm, new TraceInputs("1.1.1.1", 5));

        Assert.Equal(3, vm.Hops.Count);
        Assert.True(vm.SessionCompleted);
    }

    [Fact]
    public async Task TraceAsync_ClearsHopsAtStart()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) =>
            {
                onHop?.Report(new TraceHopResult(1, "10.0.0.3", string.Empty, "1 ms", HopStatus.Reply));
                return Task.FromResult(true);
            },
        };

        var vm = new TracerouteViewModel(service);
        vm.Hops.Add(new TraceHopResult(1, "old", string.Empty, "*", HopStatus.Timeout));
        vm.Hops.Add(new TraceHopResult(2, "old2", string.Empty, "*", HopStatus.Timeout));

        await TraceAndDrainProgressAsync(vm, new TraceInputs("1.1.1.1", 5));

        Assert.Single(vm.Hops);
        Assert.Equal("10.0.0.3", vm.Hops[0].Address);
    }

    [Fact]
    public async Task TraceAsync_UpdatesProgress()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) =>
            {
                onProgress?.Report((5, 30));
                return Task.FromResult(true);
            },
        };

        var vm = new TracerouteViewModel(service);
        await TraceAndDrainProgressAsync(vm, new TraceInputs("1.1.1.1", 30));

        Assert.Equal(5, vm.CurrentHop);
        Assert.Equal(30, vm.MaxHops);
    }

    [Fact]
    public async Task TraceAsync_HostnameUpdate_ReplacesRowWhenAddressMatches()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) =>
            {
                onHop?.Report(new TraceHopResult(1, "10.0.0.1", string.Empty, "1 ms", HopStatus.Reply));
                onHostname?.Report(new HopHostnameUpdate(0, "10.0.0.1", "gw.local"));
                return Task.FromResult(true);
            },
        };

        var vm = new TracerouteViewModel(service);
        await TraceAndDrainProgressAsync(vm, new TraceInputs("1.1.1.1", 5));

        Assert.Equal("gw.local", vm.Hops[0].Hostname);
    }

    [Fact]
    public async Task TraceAsync_HostnameUpdate_Ignored_WhenAddressMismatch()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) =>
            {
                onHop?.Report(new TraceHopResult(1, "10.0.0.1", string.Empty, "1 ms", HopStatus.Reply));
                onHostname?.Report(new HopHostnameUpdate(0, "10.0.0.99", "wrong.local"));
                return Task.FromResult(true);
            },
        };

        var vm = new TracerouteViewModel(service);
        await TraceAndDrainProgressAsync(vm, new TraceInputs("1.1.1.1", 5));

        Assert.Equal(string.Empty, vm.Hops[0].Hostname);
    }

    [Fact]
    public async Task TraceAsync_ServiceReturnsFalse_SetsShowError()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) => Task.FromResult(false),
        };

        var vm = new TracerouteViewModel(service);
        await vm.TraceAsync(new TraceInputs("1.1.1.1", 5));

        Assert.True(vm.ShowError);
        Assert.False(vm.SessionCompleted);
    }

    [Fact]
    public async Task TraceAsync_ServiceThrows_SetsShowError()
    {
        var service = new FakeTracerouteService
        {
            TraceHandler = (inputs, onHop, onProgress, onHostname, ct) =>
                throw new InvalidOperationException("boom"),
        };

        var vm = new TracerouteViewModel(service);
        await vm.TraceAsync(new TraceInputs("1.1.1.1", 5));

        Assert.True(vm.ShowError);
        Assert.Contains("boom", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_CancelsInFlight()
    {
        var blocker = new TaskCompletionSource();
        var service = new FakeTracerouteService
        {
            TraceHandler = async (inputs, onHop, onProgress, onHostname, ct) =>
            {
                try
                {
                    // Scheduling only: resume the cancellation-observing continuation
                    // on the thread pool instead of the bounded concurrency context
                    // xUnit posts to, which removes one of the two queue hops in the
                    // signal path. It reduces exposure to a saturated queue, it does
                    // not replace SignalBackstop. Safe because nothing below this
                    // await touches thread-affine state.
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    blocker.TrySetResult();
                    throw;
                }

                return true;
            },
        };

        var vm = new TracerouteViewModel(service);
        var runTask = vm.TraceAsync(new TraceInputs("1.1.1.1", 5));
        vm.Stop();
        await blocker.Task.WaitAsync(SignalBackstop);
        await runTask.WaitAsync(SignalBackstop);

        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void SetGateway_DelegatesToService()
    {
        var service = new FakeTracerouteService();
        var vm = new TracerouteViewModel(service);
        var gateway = new SshGatewayDto { Name = "gw", Host = "1.2.3.4", Port = 22 };

        vm.SetGateway(gateway);

        Assert.Same(gateway, service.LastGateway);
    }

    [Fact]
    public async Task TraceAsync_IsTracing_ToggledCorrectly()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var service = new FakeTracerouteService
        {
            TraceHandler = async (inputs, onHop, onProgress, onHostname, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return true;
            },
        };

        var vm = new TracerouteViewModel(service);
        var runTask = vm.TraceAsync(new TraceInputs("1.1.1.1", 5));
        await entered.Task.WaitAsync(SignalBackstop);

        Assert.True(vm.IsTracing);

        release.TrySetResult();
        await runTask;

        Assert.False(vm.IsTracing);
    }

    [Fact]
    public async Task TraceAsync_TwiceInRow_SecondCallIgnoredIfRunning()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var service = new FakeTracerouteService
        {
            TraceHandler = async (inputs, onHop, onProgress, onHostname, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return true;
            },
        };

        var vm = new TracerouteViewModel(service);
        var first = vm.TraceAsync(new TraceInputs("1.1.1.1", 5));
        await entered.Task.WaitAsync(SignalBackstop);
        await vm.TraceAsync(new TraceInputs("1.1.1.1", 5));
        release.TrySetResult();
        await first;

        Assert.Equal(1, service.CallCount);
    }

    private static async Task TraceAndDrainProgressAsync(
        TracerouteViewModel viewModel,
        TraceInputs inputs)
    {
        SynchronizationContext? originalContext = SynchronizationContext.Current;
        QueuedSynchronizationContext progressContext = new();
        try
        {
            SynchronizationContext.SetSynchronizationContext(progressContext);
            Task traceTask = viewModel.TraceAsync(inputs);
            Assert.True(
                traceTask.IsCompleted,
                "The focused fake traceroute handler must complete synchronously.");
            await traceTask.ConfigureAwait(false);
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

    private sealed class FakeTracerouteService : ITracerouteService
    {
        public Func<TraceInputs, IProgress<TraceHopResult>?, IProgress<(int Current, int Total)>?, IProgress<HopHostnameUpdate>?, CancellationToken, Task<bool>>? TraceHandler { get; set; }

        public SshGatewayDto? LastGateway { get; private set; }

        public int CallCount { get; private set; }

        public void SetGateway(SshGatewayDto? gateway)
        {
            LastGateway = gateway;
        }

        public Task<bool> TraceAsync(
            TraceInputs inputs,
            IProgress<TraceHopResult>? onHop,
            IProgress<(int Current, int Total)>? onProgress,
            IProgress<HopHostnameUpdate>? onHostname,
            CancellationToken ct)
        {
            CallCount++;
            if (TraceHandler is not null)
            {
                return TraceHandler(inputs, onHop, onProgress, onHostname, ct);
            }

            return Task.FromResult(true);
        }
    }
}
