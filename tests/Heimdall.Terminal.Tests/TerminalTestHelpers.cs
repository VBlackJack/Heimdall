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
    /// Four of the waits it bounds are now gated on a command processor child rather than a
    /// PowerShell host, so for those the startup argument above no longer applies. The bound stays
    /// where it is deliberately: it is shared, and lowering it for some waits would trade the
    /// evidence for a narrower failure.
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

    /// <summary>
    /// Child for a test whose subject is the exit code itself rather than a shell.
    /// </summary>
    /// <remarks>
    /// <para>The command processor starts in a fraction of the time a PowerShell host takes, so a
    /// test that only needs a process to end with a given code stops paying for a host it never
    /// exercises. Measured on an idle machine, the four tests using it went from 768-859 ms to
    /// 208-215 ms. Tests that do exercise a shell - terminal output, stdin, encoded commands,
    /// sleeping, cancellation, termination - keep <see cref="ResolvePowerShellExecutable"/>.</para>
    /// <para>It is not a cure for the stalls the CI logs show, and must not be read as one. In run
    /// 32294052268 the <see cref="TerminalWaitObservation"/> population was six waits between 54 and
    /// 60 seconds: four of them are waits of the tests swapped here, and the remaining two belong to
    /// tests that keep a PowerShell child because they exercise stdin or interleaved output. Those
    /// two are why the swap cannot be read as the cure - the stall also reaches waits whose child is
    /// unchanged, and its cause is not established.</para>
    /// </remarks>
    /// <returns>Absolute path to the command processor, or its bare name if it is not where
    /// expected, matching the fallback <see cref="ResolvePowerShellExecutable"/> already uses.</returns>
    internal static string ResolveExitCodeChildExecutable()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string commandProcessor = Path.Combine(systemDirectory, "cmd.exe");
        return File.Exists(commandProcessor) ? commandProcessor : "cmd.exe";
    }

    /// <summary>
    /// Arguments making that child exit with <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">
    /// Code the child exits with. Must not be 259: that is STILL_ACTIVE, which a session reads as
    /// "the process is still running", so a child exiting with it would never be seen to stop. Zero
    /// is accepted but makes an exit-code assertion unable to distinguish a real exit from an
    /// uninitialised value, so prefer a distinctive code.
    /// </param>
    /// <returns>The command processor arguments.</returns>
    /// <remarks>
    /// The child itself writes nothing for <c>/c exit N</c>, on stdout and on stderr alike. That is
    /// not the same as the session receiving nothing: under a pseudo console the host still emits
    /// its own bytes, which no test here asserts against. <c>/d</c> skips any AutoRun command
    /// registered for the command processor, so a machine carrying one cannot add output of its own.
    /// </remarks>
    internal static string BuildExitCodeChildArguments(int exitCode)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(exitCode, StillActiveExitCode);

        return string.Create(CultureInfo.InvariantCulture, $"/d /c exit {exitCode}");
    }

    /// <summary>
    /// Arguments for a child that reads one line from standard input and writes it back behind
    /// <paramref name="prefix"/>, then exits with zero.
    /// </summary>
    /// <param name="prefix">
    /// Text placed in front of the echoed line. Restricted to letters, digits, dash, colon and
    /// underscore: everything else is either a command-processor metacharacter or a delayed
    /// expansion trigger, and quoting it correctly inside the nested command is not worth the risk
    /// for a value only tests choose.
    /// </param>
    /// <remarks>
    /// <para>Delayed expansion is what makes this possible in one command: <c>%line%</c> is
    /// substituted when the line is parsed, which is before <c>set /p</c> has run, so it would
    /// always be empty. <c>!line!</c> under <c>/v:on</c> is substituted when the echo executes.
    /// </para>
    /// <para>A previous pass kept a PowerShell host for the stdin test on the grounds that it
    /// exercises standard input. It does, and so does this - the command processor reads a line
    /// from a redirected stdin exactly as well, without the host's cold start.</para>
    /// </remarks>
    internal static string BuildStdinEchoChildArguments(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        RejectUnsafeChildLiteral(prefix, nameof(prefix));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"/d /v:on /c \"set /p line= & echo {prefix}!line!\"");
    }

    /// <summary>
    /// Arguments for a child that writes <paramref name="first"/>, waits for a line on standard
    /// input, then writes <paramref name="second"/> and exits with zero.
    /// </summary>
    /// <remarks>
    /// <para>The two lines are guaranteed to arrive as two separate reads because the second one
    /// cannot be written until the caller has answered the first - causality rather than a sleep.
    /// A pause would work too and was tried; it makes the separation depend on timing, which is
    /// the property these tests are being moved away from, and it made the test slower than the
    /// PowerShell child it replaced.</para>
    /// <para>Two consecutive echoes with nothing between them would not do: the reader is free to
    /// coalesce them into one read, and a test counting notifications would then be asserting
    /// buffer behaviour rather than the read loop.</para>
    /// </remarks>
    internal static string BuildTwoStageOutputChildArguments(string first, string second)
    {
        ArgumentException.ThrowIfNullOrEmpty(first);
        ArgumentException.ThrowIfNullOrEmpty(second);
        RejectUnsafeChildLiteral(first, nameof(first));
        RejectUnsafeChildLiteral(second, nameof(second));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"/d /c \"echo {first}& set /p ignored= & echo {second}\"");
    }

    /// <summary>
    /// Arguments for a child that writes the contents of <paramref name="payloadFileName"/>, then
    /// records that it has done so by creating <paramref name="signalFileName"/>, then waits for a
    /// line on standard input and echoes it back behind <paramref name="livePrefix"/>.
    /// </summary>
    /// <param name="payloadFileName">Bare file name of the payload, in the child's working directory.</param>
    /// <param name="signalFileName">Bare file name the child creates once the payload is written.</param>
    /// <param name="livePrefix">Prefix the child puts in front of the line it reads back.</param>
    /// <returns>The command processor arguments.</returns>
    /// <remarks>
    /// <para>Both names are relative and resolve against the child's working directory, which the
    /// caller owns and passes to the session. That is not a convenience. An absolute path quoted
    /// inside a nested command-processor command is broken by a space anywhere along it - measured,
    /// not assumed - and the temporary directory's root is outside this repository's control. A bare
    /// file name carries no such hazard whatever the directory is called.</para>
    /// <para>The payload comes from a file because the command processor has no cheap way to emit a
    /// six-figure run of characters, and having one is what allows this child to be the command
    /// processor at all. The PowerShell child it replaces was measured on a saturated runner with
    /// its process created and running and not one byte received after a minute.</para>
    /// <para>The signal file is the causal alternative to sleeping: the caller can wait for it
    /// rather than guess how long the payload takes to appear.</para>
    /// </remarks>
    internal static string BuildBootstrapReplayChildArguments(
        string payloadFileName,
        string signalFileName,
        string livePrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadFileName);
        ArgumentException.ThrowIfNullOrEmpty(signalFileName);
        ArgumentException.ThrowIfNullOrEmpty(livePrefix);
        RejectUnsafeChildFileName(payloadFileName, nameof(payloadFileName));
        RejectUnsafeChildFileName(signalFileName, nameof(signalFileName));
        RejectUnsafeChildLiteral(livePrefix, nameof(livePrefix));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"/d /v:on /c \"type {payloadFileName} & echo ready>{signalFileName} "
                + $"& set /p line= & echo {livePrefix}!line!\"");
    }

    /// <summary>
    /// Refuses any file name that is not a bare name safe to leave unquoted.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="RejectUnsafeChildLiteral"/> on one point and looser on another: a
    /// dot is allowed because file names have extensions, and a colon is refused because it would
    /// make the name a drive-qualified or stream-qualified path, which is exactly the absolute form
    /// this helper exists to keep out of the command line.
    /// </remarks>
    private static void RejectUnsafeChildFileName(string value, string parameterName)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or '.'))
            {
                throw new ArgumentException(
                    $"'{value}' contains '{character}', so it is not a bare file name safe to "
                        + "leave unquoted in a command-processor command.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Refuses any literal that would need quoting inside a nested command-processor command.
    /// </summary>
    private static void RejectUnsafeChildLiteral(string value, string parameterName)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or ':' or '_'))
            {
                throw new ArgumentException(
                    $"'{value}' contains '{character}', which is not safe inside a command-processor command.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// The value a session reads as "still running" rather than as an exit code.
    /// </summary>
    private const int StillActiveExitCode = 259;

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
