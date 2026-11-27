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

using System.Globalization;
using System.Text.RegularExpressions;
using Heimdall.Core.Logging;
using Renci.SshNet;

namespace Heimdall.Ssh;

/// <summary>
/// Holds a snapshot of server resource usage collected via SSH commands.
/// </summary>
public sealed record ServerHealthData(
    double CpuPercent,
    long MemTotalMb,
    long MemUsedMb,
    long MemFreeMb,
    string DiskUsed,
    string DiskTotal,
    int DiskPercent,
    bool IsSupported,
    string? Uptime = null,
    string? LoadAverage = null,
    long SwapTotalMb = 0,
    long SwapUsedMb = 0);

internal interface IHealthCommandRunner
{
    Task<string> RunAsync(string command, CancellationToken cancellationToken);
}

internal sealed class SshHealthCommandRunner(SshClient client) : IHealthCommandRunner
{
    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();

        using var sshCommand = client.CreateCommand(command);
        var asyncResult = sshCommand.BeginExecute();
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try
            {
                ((SshCommand)state!).CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Command already disposed during shutdown.
            }
            catch (InvalidOperationException)
            {
                // Command already completed or never fully started.
            }
        }, sshCommand);

        var result = await Task<string>.Factory.FromAsync(
                asyncResult,
                sshCommand.EndExecute)
            .ConfigureAwait(false);

        return sshCommand.ExitStatus == 0 ? result : string.Empty;
    }
}

/// <summary>
/// Polls an SSH server for CPU, RAM, swap, disk usage, uptime, and load average at a fixed
/// 15-second interval using a single multiplexed <c>LC_ALL=C</c> command channel.
/// Supports GNU/Linux as well as BusyBox/Alpine (<c>/proc/meminfo</c>, <c>/proc/uptime</c>,
/// <c>/proc/loadavg</c>, and POSIX <c>df -Ph</c>).
/// </summary>
public sealed class ServerHealthMonitor : IDisposable
{
    private const int PollIntervalSeconds = 15;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    internal const string MarkerCpu = "__H_CPU__";
    internal const string MarkerMem = "__H_MEM__";
    internal const string MarkerDisk = "__H_DISK__";
    internal const string MarkerUptime = "__H_UPTIME__";
    internal const string MarkerLoad = "__H_LOAD__";

    internal const string CombinedHealthCommandText =
        "LC_ALL=C sh -c '"
        + "printf \"" + MarkerCpu + "\\n\"; (top -b -n 1 2>/dev/null | head -5); "
        + "printf \"" + MarkerMem + "\\n\"; (free -m 2>/dev/null | grep -E \"^(Mem|Swap):\" || grep -E \"^(MemTotal|MemFree|MemAvailable|SwapTotal|SwapFree):\" /proc/meminfo 2>/dev/null); "
        + "printf \"" + MarkerDisk + "\\n\"; (df -Ph / 2>/dev/null | tail -1); "
        + "printf \"" + MarkerUptime + "\\n\"; (uptime -p 2>/dev/null || cat /proc/uptime 2>/dev/null); "
        + "printf \"" + MarkerLoad + "\\n\"; (cat /proc/loadavg 2>/dev/null); true'";

    private const string CpuCommandText = "LC_ALL=C top -b -n 1 | head -5";
    private const string MemoryCommandText = "LC_ALL=C free -m | grep Mem";
    private const string DiskCommandText = "LC_ALL=C df -Ph / | tail -1";

    private static readonly Regex CpuIdleRegex = new(
        @"%?Cpu.*?:\s.*?(\d+[\.,]\d+)\s*id",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex CpuUsageRegex = new(
        @"%?Cpu.*?:\s*(\d+[\.,]\d+)\s*us",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex BusyBoxCpuIdleRegex = new(
        @"CPU:.*?\s(\d+)%\s*idle",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex MemRegex = new(
        @"Mem:\s+(\d+)\s+(\d+)\s+(\d+)",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SwapRegex = new(
        @"Swap:\s+(\d+)\s+(\d+)",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex ProcMemFieldRegex = new(
        @"^(MemTotal|MemFree|MemAvailable|SwapTotal|SwapFree):\s+(\d+)\s+kB",
        RegexOptions.Compiled | RegexOptions.Multiline,
        RegexTimeout);

    private static readonly Regex DiskRegex = new(
        @"(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)%",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex LoadAvgRegex = new(
        @"(\d+\.\d+)\s+(\d+\.\d+)\s+(\d+\.\d+)",
        RegexOptions.Compiled,
        RegexTimeout);

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly Func<SshClient, IHealthCommandRunner> _commandRunnerFactory;
    private readonly Lock _stateLock = new();
    private bool _disposed;

    /// <summary>Raised on the thread pool when new health data is available.</summary>
    public event Action<ServerHealthData>? HealthUpdated;

    public ServerHealthMonitor()
        : this(static client => new SshHealthCommandRunner(client))
    {
    }

    internal ServerHealthMonitor(Func<SshClient, IHealthCommandRunner> commandRunnerFactory)
    {
        _commandRunnerFactory = commandRunnerFactory
            ?? throw new ArgumentNullException(nameof(commandRunnerFactory));
    }

    /// <summary>
    /// Starts polling the remote server for health metrics.
    /// </summary>
    public Task StartAsync(SshClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException("Health monitor is already running.");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollTask = PollLoopAsync(client, _commandRunnerFactory(client), _cts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the polling loop asynchronously.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? pollTask;
        lock (_stateLock)
        {
            cts = _cts;
            pollTask = _pollTask;
            _cts = null;
            _pollTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (pollTask is not null)
            {
                await pollTask.WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected from task cancellation
        }
        catch (TimeoutException)
        {
            // Expected from WaitAsync timeout
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Stops the polling loop without blocking the calling thread (safe to call from WPF UI thread).
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? pollTask;
        lock (_stateLock)
        {
            cts = _cts;
            pollTask = _pollTask;
            _cts = null;
            _pollTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (pollTask is null || pollTask.IsCompleted)
        {
            cts.Dispose();
            return;
        }

        _ = pollTask.ContinueWith(
            static (_, state) =>
            {
                try { ((CancellationTokenSource)state!).Dispose(); }
                catch (ObjectDisposedException) { }
            },
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private async Task PollLoopAsync(
        SshClient client,
        IHealthCommandRunner commandRunner,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    FileLogger.Warn("ServerHealthMonitor: SSH client disconnected, stopping poll loop");
                    break;
                }

                var data = await CollectHealthDataAsync(commandRunner, ct).ConfigureAwait(false);
                if (data is not null)
                {
                    HealthUpdated?.Invoke(data);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                FileLogger.Warn("ServerHealthMonitor: SSH client disposed, stopping poll loop");
                break;
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"ServerHealthMonitor poll error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static async Task<ServerHealthData?> CollectHealthDataAsync(
        IHealthCommandRunner commandRunner,
        CancellationToken cancellationToken)
    {
        var combined = await RunHealthCommandAsync(
                commandRunner,
                CombinedHealthCommandText,
                "combined",
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(combined)
            && combined.Contains(MarkerCpu, StringComparison.Ordinal))
        {
            return ParseCombinedOutput(combined);
        }

        // Fallback for non-POSIX shells or stub runners that answer individual commands.
        var cpuTask = RunHealthCommandAsync(commandRunner, CpuCommandText, "CPU", cancellationToken);
        var memoryTask = RunHealthCommandAsync(commandRunner, MemoryCommandText, "RAM", cancellationToken);
        var diskTask = RunHealthCommandAsync(commandRunner, DiskCommandText, "disk", cancellationToken);

        var results = await Task.WhenAll(cpuTask, memoryTask, diskTask).ConfigureAwait(false);
        return BuildHealthData(results[0], results[1], results[2], uptimeOutput: null, loadOutput: null);
    }

    internal static ServerHealthData ParseCombinedOutput(string combinedOutput)
    {
        var cpuSection = ExtractSection(combinedOutput, MarkerCpu, MarkerMem);
        var memSection = ExtractSection(combinedOutput, MarkerMem, MarkerDisk);
        var diskSection = ExtractSection(combinedOutput, MarkerDisk, MarkerUptime);
        var uptimeSection = ExtractSection(combinedOutput, MarkerUptime, MarkerLoad);
        var loadSection = ExtractSection(combinedOutput, MarkerLoad, nextMarker: null);

        return BuildHealthData(cpuSection, memSection, diskSection, uptimeSection, loadSection);
    }

    private static ServerHealthData BuildHealthData(
        string cpuResult,
        string memResult,
        string diskResult,
        string? uptimeOutput,
        string? loadOutput)
    {
        double cpuPercent = 0;
        long memTotal = 0, memUsed = 0, memFree = 0;
        long swapTotal = 0, swapUsed = 0;
        string diskUsed = "?", diskTotal = "?";
        int diskPercent = 0;

        bool cpuHasOutput = !string.IsNullOrWhiteSpace(cpuResult);
        bool memoryHasOutput = !string.IsNullOrWhiteSpace(memResult);
        bool diskHasOutput = !string.IsNullOrWhiteSpace(diskResult);
        bool cpuParsed = cpuHasOutput && TryParseCpuUsage(cpuResult, out cpuPercent);
        bool memoryParsed = memoryHasOutput && TryParseMemory(memResult, out memTotal, out memUsed, out memFree);
        bool diskParsed = diskHasOutput && TryParseDisk(diskResult, out diskUsed, out diskTotal, out diskPercent);

        if (memoryHasOutput)
        {
            _ = TryParseSwap(memResult, out swapTotal, out swapUsed);
        }

        string? uptime = TryParseUptime(uptimeOutput);
        string? loadAverage = TryParseLoadAverage(loadOutput);

        bool hasAnyOutput = cpuHasOutput || memoryHasOutput || diskHasOutput;
        bool hasParseFailure =
            (cpuHasOutput && !cpuParsed)
            || (memoryHasOutput && !memoryParsed)
            || (diskHasOutput && !diskParsed);
        bool isSupported = hasAnyOutput
            && !hasParseFailure
            && (cpuParsed || memoryParsed || diskParsed);

        return new ServerHealthData(
            cpuPercent,
            memTotal,
            memUsed,
            memFree,
            diskUsed,
            diskTotal,
            diskPercent,
            isSupported,
            uptime,
            loadAverage,
            swapTotal,
            swapUsed);
    }

    private static string ExtractSection(string content, string startMarker, string? nextMarker)
    {
        var startIdx = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return string.Empty;
        }

        startIdx += startMarker.Length;
        if (nextMarker is null)
        {
            return content[startIdx..].Trim();
        }

        var endIdx = content.IndexOf(nextMarker, startIdx, StringComparison.Ordinal);
        return endIdx < 0
            ? content[startIdx..].Trim()
            : content[startIdx..endIdx].Trim();
    }

    private static async Task<string> RunHealthCommandAsync(
        IHealthCommandRunner commandRunner,
        string commandText,
        string metricName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await commandRunner.RunAsync(commandText, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"ServerHealthMonitor {metricName} command failed: {ex.Message}");
            return string.Empty;
        }
    }

    internal static double ParseCpuUsage(string topOutput)
    {
        return TryParseCpuUsage(topOutput, out double cpuUsage)
            ? cpuUsage
            : 0;
    }

    internal static bool TryParseCpuUsage(string topOutput, out double cpuUsage)
    {
        cpuUsage = 0;
        if (string.IsNullOrWhiteSpace(topOutput))
        {
            return false;
        }

        try
        {
            var idleMatch = CpuIdleRegex.Match(topOutput);
            if (idleMatch.Success)
            {
                var idleStr = idleMatch.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(idleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle))
                {
                    cpuUsage = Math.Round(Math.Max(0, 100.0 - idle), 1);
                    return true;
                }
            }

            var busyBoxIdle = BusyBoxCpuIdleRegex.Match(topOutput);
            if (busyBoxIdle.Success
                && double.TryParse(busyBoxIdle.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bbIdle))
            {
                cpuUsage = Math.Round(Math.Max(0, 100.0 - bbIdle), 1);
                return true;
            }

            var usMatch = CpuUsageRegex.Match(topOutput);
            if (usMatch.Success)
            {
                var usStr = usMatch.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(usStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var us))
                {
                    cpuUsage = Math.Round(us, 1);
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    internal static void ParseMemory(string freeOutput, out long total, out long used, out long free)
    {
        _ = TryParseMemory(freeOutput, out total, out used, out free);
    }

    internal static bool TryParseMemory(string freeOutput, out long total, out long used, out long free)
    {
        total = 0;
        used = 0;
        free = 0;
        if (string.IsNullOrWhiteSpace(freeOutput))
        {
            return false;
        }

        try
        {
            var match = MemRegex.Match(freeOutput);
            if (match.Success
                && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out total)
                && long.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out used)
                && long.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out free))
            {
                return true;
            }

            // BusyBox / Alpine fallback: /proc/meminfo values in kB.
            var fields = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Match m in ProcMemFieldRegex.Matches(freeOutput))
            {
                if (long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    fields[m.Groups[1].Value] = kb;
                }
            }

            if (fields.TryGetValue("MemTotal", out var totalKb) && totalKb > 0)
            {
                var availOrFreeKb = fields.TryGetValue("MemAvailable", out var availKb)
                    ? availKb
                    : fields.GetValueOrDefault("MemFree", 0);
                total = totalKb / 1024;
                free = availOrFreeKb / 1024;
                used = Math.Max(0, total - free);
                return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    internal static bool TryParseSwap(string memOutput, out long totalMb, out long usedMb)
    {
        totalMb = 0;
        usedMb = 0;
        if (string.IsNullOrWhiteSpace(memOutput))
        {
            return false;
        }

        try
        {
            var match = SwapRegex.Match(memOutput);
            if (match.Success
                && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out totalMb)
                && long.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out usedMb))
            {
                return true;
            }

            long swapTotalKb = -1;
            long swapFreeKb = -1;
            foreach (Match m in ProcMemFieldRegex.Matches(memOutput))
            {
                if (m.Groups[1].Value == "SwapTotal"
                    && long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                {
                    swapTotalKb = t;
                }
                else if (m.Groups[1].Value == "SwapFree"
                    && long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                {
                    swapFreeKb = f;
                }
            }

            if (swapTotalKb >= 0 && swapFreeKb >= 0)
            {
                totalMb = swapTotalKb / 1024;
                usedMb = Math.Max(0, (swapTotalKb - swapFreeKb) / 1024);
                return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    internal static void ParseDisk(string dfOutput, out string used, out string total, out int percent)
    {
        _ = TryParseDisk(dfOutput, out used, out total, out percent);
    }

    internal static bool TryParseDisk(string dfOutput, out string used, out string total, out int percent)
    {
        used = "?";
        total = "?";
        percent = 0;
        if (string.IsNullOrWhiteSpace(dfOutput))
        {
            return false;
        }

        try
        {
            var match = DiskRegex.Match(dfOutput);
            if (!match.Success)
            {
                return false;
            }

            total = match.Groups[2].Value;
            used = match.Groups[3].Value;
            return int.TryParse(
                match.Groups[5].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out percent);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    internal static string? TryParseUptime(string? uptimeOutput)
    {
        if (string.IsNullOrWhiteSpace(uptimeOutput))
        {
            return null;
        }

        var firstLine = uptimeOutput.Split('\n')[0].Trim();
        if (firstLine.StartsWith("up ", StringComparison.OrdinalIgnoreCase))
        {
            return firstLine[3..].Trim();
        }

        // /proc/uptime format: "<seconds>.<centis> <idle>"
        var firstToken = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstToken is not null
            && double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalSeconds)
            && totalSeconds >= 0)
        {
            var span = TimeSpan.FromSeconds(totalSeconds);
            if (span.Days > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}d {1}h {2}m", span.Days, span.Hours, span.Minutes);
            }

            if (span.Hours > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", span.Hours, span.Minutes);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}m", span.Minutes);
        }

        return null;
    }

    internal static string? TryParseLoadAverage(string? loadOutput)
    {
        if (string.IsNullOrWhiteSpace(loadOutput))
        {
            return null;
        }

        try
        {
            var match = LoadAvgRegex.Match(loadOutput);
            return match.Success
                ? $"{match.Groups[1].Value}, {match.Groups[2].Value}, {match.Groups[3].Value}"
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
