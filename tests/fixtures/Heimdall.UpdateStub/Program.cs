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

    private static int Main(string[] args)
    {
        int exitCode = 0;
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
                    Console.Error.WriteLine($"Unrecognized argument: {args[i]}");
                    return UsageExitCode;
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
            File.AppendAllText(markerPath, $"{role}|{exitCode}{Environment.NewLine}");
        }

        return exitCode;
    }
}
