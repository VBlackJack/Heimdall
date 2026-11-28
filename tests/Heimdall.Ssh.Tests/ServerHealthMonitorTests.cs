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

using System.Reflection;
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

    /// <summary>
    /// The shell session disposes its client on disconnect while the monitor
    /// keeps polling it. SSH.NET reports IsConnected on a disposed client by
    /// throwing, and the loop used to log that throw and sleep for another
    /// interval, for ever. A client that is gone ends the loop.
    /// </summary>
    [Fact]
    public async Task PollLoop_ClientDisposed_StopsWithoutPolling()
    {
        using DisposedTestSshClient client = new DisposedTestSshClient();
        BlockingHealthCommandRunner runner = new BlockingHealthCommandRunner();
        using ServerHealthMonitor monitor = new ServerHealthMonitor(_ => runner);

        await monitor.StartAsync(client);
        Task pollLoop = GetPollTask(monitor);

        await pollLoop.WaitAsync(PollLoopExitBackstop);

        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task CollectHealthDataAsync_CombinedSingleExecOutput_ParsesCpuMemSwapDiskUptimeAndLoad()
    {
        int calls = 0;
        var runner = new StaticHealthCommandRunner(_ =>
        {
            calls++;
            return string.Join(
                '\n',
                ServerHealthMonitor.MarkerCpu,
                "%Cpu(s):  5,2 us,  1,8 sy,  0,0 ni, 92,5 id,  0,5 wa",
                ServerHealthMonitor.MarkerMem,
                "Mem:          16384        4096       12288",
                "Swap:          2048         512        1536",
                ServerHealthMonitor.MarkerDisk,
                "/dev/mapper/vg0-root 500G 125G 375G 25% /",
                ServerHealthMonitor.MarkerUptime,
                "up 3 days, 4 hours, 12 minutes",
                ServerHealthMonitor.MarkerLoad,
                "0.42 0.31 0.25 1/234 5678");
        });

        var data = await ServerHealthMonitor.CollectHealthDataAsync(runner, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.NotNull(data);
        Assert.True(data!.IsSupported);
        Assert.Equal(7.5, data.CpuPercent, 1);
        Assert.Equal(16384, data.MemTotalMb);
        Assert.Equal(4096, data.MemUsedMb);
        Assert.Equal(2048, data.SwapTotalMb);
        Assert.Equal(512, data.SwapUsedMb);
        Assert.Equal("125G", data.DiskUsed);
        Assert.Equal("500G", data.DiskTotal);
        Assert.Equal(25, data.DiskPercent);
        Assert.Equal("3 days, 4 hours, 12 minutes", data.Uptime);
        Assert.Equal("0.42, 0.31, 0.25", data.LoadAverage);
    }

    [Fact]
    public async Task CollectHealthDataAsync_BusyBoxAndProcMeminfoOutput_ReturnsSupportedState()
    {
        var runner = new StaticHealthCommandRunner(_ => string.Join(
            '\n',
            ServerHealthMonitor.MarkerCpu,
            "CPU:  12% usr   3% sys   0% nic  85% idle   0% io",
            ServerHealthMonitor.MarkerMem,
            "MemTotal:        4194304 kB",
            "MemFree:         1048576 kB",
            "MemAvailable:    2097152 kB",
            "SwapTotal:       1048576 kB",
            "SwapFree:         524288 kB",
            ServerHealthMonitor.MarkerDisk,
            "/dev/vda1 40.0G 10.0G 30.0G 25% /",
            ServerHealthMonitor.MarkerUptime,
            "90061.42 180000.00",
            ServerHealthMonitor.MarkerLoad,
            "1.05 0.80 0.65 2/99 42"));

        var data = await ServerHealthMonitor.CollectHealthDataAsync(runner, CancellationToken.None);

        Assert.NotNull(data);
        Assert.True(data!.IsSupported);
        Assert.Equal(15.0, data.CpuPercent, 1);
        Assert.Equal(4096, data.MemTotalMb);
        Assert.Equal(2048, data.MemUsedMb);
        Assert.Equal(1024, data.SwapTotalMb);
        Assert.Equal(512, data.SwapUsedMb);
        Assert.Equal("1d 1h 1m", data.Uptime);
        Assert.Equal("1.05, 0.80, 0.65", data.LoadAverage);
    }

    /// <summary>
    /// Bound for a poll loop that has nothing left to poll to exit on its own,
    /// with no cancellation. A loop that instead sleeps for its next interval
    /// is still asleep when this expires, which is the failure.
    /// </summary>
    private static readonly TimeSpan PollLoopExitBackstop = TimeSpan.FromSeconds(5);

    private static Task GetPollTask(ServerHealthMonitor monitor)
    {
        FieldInfo? field = typeof(ServerHealthMonitor).GetField(
            "_pollTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Task? task = Assert.IsAssignableFrom<Task>(field.GetValue(monitor));
        return task;
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

    [Fact]
    public void CombinedHealthCommand_UsesEnvPrefixForNonPosixShellCompatibility()
    {
        Assert.StartsWith("env LC_ALL=C ", ServerHealthMonitor.CombinedHealthCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_StopsPollingAfterUnsupportedHostResponse()
    {
        using var client = new ConnectedTestSshClient();
        var calls = 0;
        var unsupportedReceived = new TaskCompletionSource<ServerHealthData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new StaticHealthCommandRunner(_ =>
        {
            Interlocked.Increment(ref calls);
            return "not-linux-output";
        });
        using var monitor = new ServerHealthMonitor(_ => runner);
        monitor.HealthUpdated += data => unsupportedReceived.TrySetResult(data);

        await monitor.StartAsync(client);
        var received = await unsupportedReceived.Task.WaitAsync(SignalBackstop);

        Assert.False(received.IsSupported);
        await monitor.StopAsync().WaitAsync(SignalBackstop);
        // Combined + 3 fallback calls on the first cycle only (no second cycle).
        Assert.InRange(calls, 1, 4);
    }

    private sealed class ConnectedTestSshClient : SshClient
    {
        public ConnectedTestSshClient()
            : base(new ConnectionInfo("example.com", 22, "tester", new NoneAuthenticationMethod("tester")))
        {
        }

        public override bool IsConnected => true;
    }

    private sealed class DisposedTestSshClient : SshClient
    {
        public DisposedTestSshClient()
            : base(new ConnectionInfo("example.com", 22, "tester", new NoneAuthenticationMethod("tester")))
        {
        }

        public override bool IsConnected => throw new ObjectDisposedException(nameof(SshClient));
    }
}
