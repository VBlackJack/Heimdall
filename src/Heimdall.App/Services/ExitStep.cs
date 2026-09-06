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

namespace Heimdall.App.Services;

/// <summary>
/// One asynchronous step of application exit, bounded and contained.
/// </summary>
/// <remarks>
/// Every step of <c>App.OnExit</c> that releases something is wrapped and bounded
/// except, until this existed, the container disposal: an unbounded await inside an
/// <c>async void</c> override, so a service whose <c>DisposeAsync</c> blocked hung the
/// exit for ever - and on the update path that is the relauncher's wait expiring - and
/// one that threw became an unhandled exception after the dispatcher had begun shutting
/// down. A step that overruns its budget or throws is logged and abandoned; the exit
/// goes on.
/// </remarks>
internal static class ExitStep
{
    /// <summary>
    /// Runs <paramref name="work"/> and returns when it completes or when
    /// <paramref name="budget"/> elapses, whichever comes first. Never throws.
    /// </summary>
    /// <returns>True when the work completed within the budget without throwing.</returns>
    public static async Task<bool> RunBoundedAsync(
        string name,
        Func<Task> work,
        TimeSpan budget,
        Action<string> logWarn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(logWarn);

        try
        {
            await work().WaitAsync(budget);
            return true;
        }
        catch (TimeoutException)
        {
            logWarn($"[App] {name} did not complete within {budget.TotalSeconds:0.#} s at exit; abandoned.");
            return false;
        }
        catch (Exception ex)
        {
            logWarn($"[App] {name} failed at exit: {ex}");
            return false;
        }
    }
}
