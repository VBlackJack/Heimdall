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
using System.Net;
using System.Runtime.CompilerServices;

namespace Heimdall.Terminal.Tests;

internal static class TerminalTestHelpers
{
    /// <summary>
    /// Failure bound for waits gated on a freshly spawned child process, not a synchronisation
    /// point. <see cref="ResolvePowerShellExecutable"/> selects Windows PowerShell 5.1, whose cold
    /// start is paid on a CI runner already running eight test assemblies plus coverage
    /// instrumentation. A measured CI timeout recorded the child still alive with
    /// <c>receivedBytes=0</c> after ten full seconds, so the bound covers the host's startup rather
    /// than anything Heimdall promises. It is paid only on failure: a passing wait completes as
    /// soon as the event arrives.
    /// </summary>
    /// <remarks>
    /// Widening it to sixty seconds stopped the timeouts and stopped the evidence with them. Do
    /// not read a green run as a cure: every wait bounded by this value is routed through
    /// <see cref="TerminalWaitObservation"/>, which reports the ones that still outlive the old ten
    /// second bound even when they end up succeeding. That report, not the absence of failures, is
    /// what says whether the stall is gone.
    /// </remarks>
    internal static readonly TimeSpan ProcessStartupBackstop = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often a polled wait re-tests its condition. Small enough that the elapsed time it
    /// publishes is dominated by the event, not by the polling grain.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Awaits an event from a freshly spawned child process under the shared backstop, publishing
    /// how long it took whenever that outlives the legacy bound.
    /// </summary>
    /// <param name="publish">
    /// Sink override. Present so an oracle can prove this helper really reaches the observation:
    /// routing that is only established by reading the code is routing nothing checks.
    /// </param>
    /// <param name="readElapsed">Elapsed-time override, so that oracle need not block for ten seconds.</param>
    internal static Task<T> AwaitProcessEventAsync<T>(
        Task<T> awaited,
        string awaitedEvent,
        [CallerMemberName] string caller = "",
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        return TerminalWaitObservation.ObserveAsync(
            caller,
            awaitedEvent,
            () => awaited.WaitAsync(ProcessStartupBackstop),
            publish,
            readElapsed);
    }

    /// <summary>
    /// Same wait, for the call sites that carry a <see cref="TerminalTimeoutContext"/> and want the
    /// full snapshot in the failure message.
    /// </summary>
    internal static Task<T> AwaitProcessEventAsync<T>(
        Task<T> awaited,
        TerminalTimeoutContext context,
        [CallerMemberName] string caller = "",
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TerminalTimeoutDiagnostics.WaitAsync(
            awaited,
            ProcessStartupBackstop,
            context,
            caller,
            publish,
            readElapsed);
    }

    /// <summary>
    /// Spins until the condition holds or the backstop expires, publishing how long it took.
    /// </summary>
    internal static bool SpinUntilProcessEvent(
        Func<bool> condition,
        string awaitedEvent,
        [CallerMemberName] string caller = "",
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        return TerminalWaitObservation.ObserveSpin(
            caller,
            awaitedEvent,
            () => SpinWait.SpinUntil(condition, ProcessStartupBackstop),
            publish,
            readElapsed);
    }

    /// <summary>
    /// Polls asynchronously until the condition holds or the backstop expires, publishing how long
    /// it took. Returns whether the condition was ever observed to hold.
    /// </summary>
    internal static Task<bool> PollUntilProcessEventAsync(
        Func<bool> condition,
        string awaitedEvent,
        [CallerMemberName] string caller = "",
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return TerminalWaitObservation.ObservePollAsync(
            caller,
            awaitedEvent,
            async () =>
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow + ProcessStartupBackstop;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (condition())
                    {
                        return true;
                    }

                    await Task.Delay(PollInterval).ConfigureAwait(false);
                }

                // Re-tested once past the deadline: the loop can exit on a scheduling delay that
                // landed after the condition became true, and reporting that as unfinished would
                // manufacture the very signal this instrumentation exists to measure.
                return condition();
            },
            publish,
            readElapsed);
    }

    internal static string ResolvePowerShellExecutable()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemPowerShell = Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(systemPowerShell) ? systemPowerShell : "powershell.exe";
    }

    internal static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static async Task SendBytesOneAtATimeAsync(
        TcpListener listener,
        byte[] bytes,
        TimeSpan? delayBetweenBytes = null)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        client.NoDelay = true;
        await using NetworkStream stream = client.GetStream();
        TimeSpan delay = delayBetweenBytes ?? TimeSpan.FromMilliseconds(25);
        byte[] singleByte = new byte[1];

        foreach (byte value in bytes)
        {
            singleByte[0] = value;
            await stream.WriteAsync(singleByte);
            await stream.FlushAsync();
            await Task.Delay(delay);
        }
    }

    internal static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string? failureMessage = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail(failureMessage ?? "Condition was not satisfied within the allotted time.");
    }

    internal static void AssertProcessHasExited(int processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited || process.WaitForExit(100))
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
        }

        Assert.Fail($"Terminal process {processId} was still running after session disposal.");
    }
}
