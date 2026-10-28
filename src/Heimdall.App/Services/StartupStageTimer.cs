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

namespace Heimdall.App.Services;

/// <summary>
/// Runs one stage of the launch and logs how long it took, so a slow start names the stage that
/// made it slow instead of leaving one total to guess from.
/// </summary>
/// <remarks>
/// The line is written whether the stage completes or throws, with the same wording: a stage
/// that fails slowly is exactly the one whose duration is wanted.
/// </remarks>
internal static class StartupStageTimer
{
    /// <summary>Formats the log line of a finished stage.</summary>
    internal static string FormatLine(string stage, long elapsedMilliseconds)
        => $"[Startup] stage '{stage}' took {elapsedMilliseconds}ms";

    /// <summary>Runs <paramref name="work"/>, then logs its duration.</summary>
    public static async Task RunAsync(string stage, Func<Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(work);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await work();
        }
        finally
        {
            Core.Logging.FileLogger.Info(FormatLine(stage, stopwatch.ElapsedMilliseconds));
        }
    }

    /// <summary>Runs <paramref name="work"/>, logs its duration and returns its result.</summary>
    public static async Task<T> RunAsync<T>(string stage, Func<Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(work);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await work();
        }
        finally
        {
            Core.Logging.FileLogger.Info(FormatLine(stage, stopwatch.ElapsedMilliseconds));
        }
    }
}
