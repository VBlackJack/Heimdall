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

using System.Threading;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class ServerHealthMonitorTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The waits it bounds complete on
    /// an event: a runner-started signal or a StopAsync completion. The value only
    /// has to be generous enough that a saturated thread pool cannot exhaust it
    /// before that work is scheduled at all, and it is paid only on failure.
    /// </summary>
    private static readonly TimeSpan SignalBackstop = TimeSpan.FromSeconds(30);

    [Fact]
    public void ParseCpuUsage_ParsesTopIdleOutput()
    {
        const string topOutput = "%Cpu(s):  1.3 us,  0.5 sy,  0.0 ni, 98.2 id,  0.0 wa,  0.0 hi,  0.0 si,  0.0 st";

        var cpuUsage = ServerHealthMonitor.ParseCpuUsage(topOutput);

        Assert.Equal(1.8, cpuUsage, 1);
    }

    [Fact]
    public void ParseMemory_ParsesFreeOutput()
    {
        const string freeOutput = "Mem:          15896        8340        1234         456        6322        6789";

        ServerHealthMonitor.ParseMemory(freeOutput, out var total, out var used, out var free);

        Assert.Equal(15896, total);
        Assert.Equal(8340, used);
        Assert.Equal(1234, free);
    }

    [Fact]
    public void ParseDisk_ParsesDfOutput()
    {
        const string dfOutput = "/dev/sda1       120G   45G   69G  40% /";

        ServerHealthMonitor.ParseDisk(dfOutput, out var used, out var total, out var percent);

        Assert.Equal("45G", used);
        Assert.Equal("120G", total);
        Assert.Equal(40, percent);
    }

    [Fact]
    public async Task CollectHealthDataAsync_NonMatchingOutput_ReturnsUnsupportedState()
    {
        var runner = new StaticHealthCommandRunner("busybox output");

        ServerHealthData? data = await ServerHealthMonitor.CollectHealthDataAsync(
            runner,
            CancellationToken.None);

        Assert.NotNull(data);
        Assert.False(data!.IsSupported);
        Assert.Equal(0, data.CpuPercent);
        Assert.Equal(0, data.MemTotalMb);
        Assert.Equal("?", data.DiskUsed);
        Assert.Equal(0, data.DiskPercent);
    }

    [Fact]
    public async Task CollectHealthDataAsync_LinuxOutput_ReturnsSupportedState()
    {
        var runner = new StaticHealthCommandRunner(command =>
        {
            if (command.Contains("top", StringComparison.Ordinal))
            {
                return "%Cpu(s):  1.3 us,  0.5 sy,  0.0 ni, 98.2 id,  0.0 wa,  0.0 hi,  0.0 si,  0.0 st";
            }

            if (command.Contains("free", StringComparison.Ordinal))
            {
                return "Mem:          15896        8340        1234         456        6322        6789";
            }

            return "/dev/sda1       120G   45G   69G  40% /";
        });

        ServerHealthData? data = await ServerHealthMonitor.CollectHealthDataAsync(
            runner,
            CancellationToken.None);

        Assert.NotNull(data);
        Assert.True(data!.IsSupported);
        Assert.Equal(1.8, data.CpuPercent, 1);
        Assert.Equal(15896, data.MemTotalMb);
        Assert.Equal(40, data.DiskPercent);
    }

    [Fact]
    public async Task StopAsync_AllowsImmediateRestart()
    {
        // Regression test for the Start/Stop race fix: the post-Stop state
        // must allow an immediate Start without throwing "already running".
        using var client = new ConnectedTestSshClient();
        var runner = new BlockingHealthCommandRunner();
        using var monitor = new ServerHealthMonitor(_ => runner);

        await monitor.StartAsync(client);
        await runner.Started.WaitAsync(SignalBackstop);
        await monitor.StopAsync().WaitAsync(SignalBackstop);

        // Second Start must succeed; the lock-protected snapshot in StopAsync
        // guarantees _cts/_pollTask were cleared before StopAsync returned.
        var runner2 = new BlockingHealthCommandRunner();
        using var monitor2 = new ServerHealthMonitor(_ => runner2);
        await monitor2.StartAsync(client);
        await runner2.Started.WaitAsync(SignalBackstop);
        await monitor2.StopAsync().WaitAsync(SignalBackstop);
    }

    [Fact]
    public async Task StopAsync_CalledTwice_IsSafe()
    {
        // Second StopAsync must observe the cleared state and short-circuit
        // without throwing or leaking the disposed CTS.
        using var client = new ConnectedTestSshClient();
        var runner = new BlockingHealthCommandRunner();
        using var monitor = new ServerHealthMonitor(_ => runner);

        await monitor.StartAsync(client);
        await runner.Started.WaitAsync(SignalBackstop);

        await monitor.StopAsync().WaitAsync(SignalBackstop);
        await monitor.StopAsync().WaitAsync(SignalBackstop);
    }

    [Fact]
    public async Task StopAsync_CancelsPendingCommandsPromptly()
    {
        using var client = new ConnectedTestSshClient();
        using var cancellationSource = new CancellationTokenSource();
        var runner = new BlockingHealthCommandRunner();
        var monitor = new ServerHealthMonitor(_ => runner);

        try
        {
            await monitor.StartAsync(client, cancellationSource.Token);
            await runner.Started.WaitAsync(SignalBackstop);

            cancellationSource.Cancel();

            await monitor.StopAsync().WaitAsync(SignalBackstop);

            Assert.True(runner.CallCount > 0);
            Assert.True(runner.CancellationCount > 0);
        }
        finally
        {
            await monitor.StopAsync();
            monitor.Dispose();
        }
    }

    private sealed class BlockingHealthCommandRunner : IHealthCommandRunner
    {
        private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private int _cancellationCount;

        public Task Started => _started.Task;
        public int CallCount => Volatile.Read(ref _callCount);
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult(null);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }
    }

    private sealed class StaticHealthCommandRunner : IHealthCommandRunner
    {
        private readonly Func<string, string> _responseFactory;

        public StaticHealthCommandRunner(string response)
            : this(_ => response)
        {
        }

        public StaticHealthCommandRunner(Func<string, string> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public Task<string> RunAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responseFactory(command));
        }
    }

    private sealed class ConnectedTestSshClient : SshClient
    {
        public ConnectedTestSshClient()
            : base(new ConnectionInfo("example.com", 22, "tester", new NoneAuthenticationMethod("tester")))
        {
        }

        public override bool IsConnected => true;
    }
}
