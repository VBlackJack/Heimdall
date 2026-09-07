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

namespace Heimdall.UpdateStub;

/// <summary>
/// A stand-in for the two executables the update relauncher script drives: the
/// installer it launches and waits on, and the application it relaunches afterwards.
/// </summary>
/// <remarks>
/// It records that it ran and returns a chosen exit code, which is everything the
/// harness needs to tell the script's success path from its failure path. Recording
/// by appending rather than overwriting matters: the same marker file can then prove
/// both that the installer ran and that the relaunch happened, in order.
/// </remarks>
internal static class Program
{
    private const string ExitCodeOption = "--exit-code";

    private const string MarkerOption = "--marker";

    private const string RoleOption = "--role";

    /// <summary>Returned when the arguments cannot be understood at all.</summary>
    /// <remarks>
    /// Distinct from any Inno Setup code the harness drives on purpose, so a
    /// misconfigured fixture cannot be mistaken for the installer failure under test.
    /// </remarks>
    private const int UsageExitCode = 64;

    /// <summary>Returned when the marker could not be recorded within the budget.</summary>
    /// <remarks>
    /// Distinct from <see cref="UsageExitCode"/> and from every Inno Setup code the harness
    /// drives, so "the stand-in ran but could not write" is never mistaken for the installer
    /// failure under test - or, as happened for a fortnight, for silence.
    /// </remarks>
    private const int MarkerExitCode = 65;

    /// <summary>How long a marker write may keep retrying against a competing handle.</summary>
    /// <remarks>
    /// Generous next to the harness's 25 ms poll and far below its 20 second deadline: the
    /// contended window is one read of a file of a few dozen bytes.
    /// </remarks>
    private static readonly TimeSpan MarkerWriteBudget = TimeSpan.FromSeconds(5);

    private const int MarkerWriteRetryDelayMilliseconds = 20;

    /// <summary>
    /// Printed to stderr once, the first time a marker write has to be retried.
    /// </summary>
    /// <remarks>
    /// The one observable that says the contention under test actually occurred. A test
    /// that waits for this line, rather than for a wall-clock delay, cannot pass on a
    /// collision that never happened - and cannot fail a stand-in that merely started
    /// slowly. Kept as a constant so the harness matches on the same text.
    /// </remarks>
    internal const string MarkerBusyNotice = "heimdall-stub: marker busy, retrying";

    /// <summary>
    /// Marker path used when no <c>--marker</c> is given.
    /// </summary>
    /// <remarks>
    /// The relauncher script starts the target executable with no arguments at all, so
    /// the relaunch role cannot be told anything on a command line. The environment is
    /// the one channel that reaches it: the harness sets this variable on the
    /// PowerShell host it starts, and the script's child processes inherit it.
    /// </remarks>
    private const string MarkerEnvironmentVariable = "HEIMDALL_UPDATE_STUB_MARKER";

    /// <summary>Exit code source when no <c>--exit-code</c> survives the command line.</summary>
    private const string ExitCodeEnvironmentVariable = "HEIMDALL_UPDATE_STUB_EXIT_CODE";

    private static int Main(string[] args)
    {
        int exitCode = int.TryParse(
            Environment.GetEnvironmentVariable(ExitCodeEnvironmentVariable),
            out int fromEnvironment)
            ? fromEnvironment
            : 0;
        string? markerPath = Environment.GetEnvironmentVariable(MarkerEnvironmentVariable);

        // Defaults to the executable's own name, so a copy placed as the relaunch
        // target identifies itself without being told.
        string role = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case ExitCodeOption when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out exitCode))
                    {
                        Console.Error.WriteLine($"{ExitCodeOption} expects an integer.");
                        return UsageExitCode;
                    }

                    break;

                case MarkerOption when i + 1 < args.Length:
                    markerPath = args[++i];
                    break;

                case RoleOption when i + 1 < args.Length:
                    role = args[++i];
                    break;

                default:

                    // Ignored rather than fatal. Windows PowerShell 5.1 and pwsh 7 do
                    // not agree on how a single -ArgumentList string is split into a
                    // child's arguments, and a real silent installer ignores switches
                    // it does not know. Everything that matters arrives through the
                    // environment, which both hosts pass down identically.
                    Console.Error.WriteLine($"Ignoring unrecognized argument: {args[i]}");
                    break;
            }
        }

        if (markerPath is not null)
        {
            string? directory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Appended, so one marker file can carry the whole sequence in order.
            if (!TryAppendMarker(markerPath, $"{role}|{exitCode}{Environment.NewLine}"))
            {
                Console.Error.WriteLine($"could not record the '{role}' marker at {markerPath}.");
                return MarkerExitCode;
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Appends one marker line, retrying while the file is held by somebody else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer's half of BL-0067. The harness polls the marker file every 25
    /// milliseconds, and its poll used <c>File.ReadAllText</c>, which asks for
    /// <see cref="FileShare.Read"/> and therefore DENIES writers. A plain
    /// <c>File.AppendAllText</c> here threw the moment it landed inside one of those
    /// windows, nothing caught it, and this process died without recording anything -
    /// leaving a test waiting for a marker that would never come, with the host having
    /// exited 0 and no error anywhere. The reader was fixed in the same change, which is
    /// what removes the collision; this side survives one that happens anyway.
    /// </para>
    /// <para>
    /// The share mode is deliberately narrow. Readers are admitted, a second WRITER is
    /// not, and that refusal is load bearing: two stand-ins appending at once would each
    /// have captured the end offset at open time and could overwrite each other's line,
    /// which is exactly the double-relaunch defect <c>CountRole</c> exists to catch. A
    /// competing writer is made to wait by the retry below rather than let through.
    /// </para>
    /// <para>
    /// Bounded and reported. Returning a distinct exit code rather than throwing keeps the
    /// installer role - the one the script waits on and whose code is read - able to say
    /// that it ran but could not write. The relaunch role is started without <c>-Wait</c>
    /// and with no redirection, so nothing observes its code or its stderr; for that role
    /// this is a bound and a clean death, not a diagnostic.
    /// </para>
    /// </remarks>
    private static bool TryAppendMarker(string markerPath, string line)
    {
        DateTime deadline = DateTime.UtcNow + MarkerWriteBudget;
        bool announced = false;

        while (true)
        {
            try
            {
                using FileStream stream = new(
                    markerPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read | FileShare.Delete);
                using StreamWriter writer = new(stream);
                writer.Write(line);
                return true;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException
                    && DateTime.UtcNow < deadline)
            {
                // Said once, on the first retry only. This is what lets a test wait for
                // the collision to have happened rather than assume it did after a fixed
                // delay: without it, a stand-in that started slowly would write after the
                // competing handle was gone and the test would pass having proved nothing.
                if (!announced)
                {
                    announced = true;
                    Console.Error.WriteLine(MarkerBusyNotice);
                }

                Thread.Sleep(MarkerWriteRetryDelayMilliseconds);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
