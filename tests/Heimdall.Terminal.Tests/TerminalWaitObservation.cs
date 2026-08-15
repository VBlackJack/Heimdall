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

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Whether a bounded terminal wait delivered the event it was waiting for.
/// </summary>
internal enum TerminalWaitOutcome
{
    /// <summary>The awaited event arrived before the bound expired.</summary>
    Completed,

    /// <summary>
    /// The wait ended without the event. Deliberately not named after a cause: the bound expiring
    /// and the wait throwing land here alike, and the observation does not claim to tell them apart.
    /// </summary>
    Unfinished,
}

/// <summary>
/// Publishes how long a bounded terminal wait actually took, whether or not it succeeded.
/// </summary>
/// <remarks>
/// The backstop these waits run under was raised from ten seconds to sixty. That silenced the
/// timeouts, and silenced with them the only evidence that anything was slow: a wait that used to
/// fail at ten seconds now completes at forty-five and the run is green with no trace of it. Green
/// runs under the raised bound therefore cannot distinguish "the stall is gone" from "the same
/// stall now finishes inside the wider bound", which is precisely the question that has to be
/// answered before the flake family can be called closed.
/// <para>
/// This closes that gap. Every instrumented wait is measured, and one greppable line is published
/// as soon as it outlives <see cref="LegacyBound"/> - INCLUDING when the wait succeeds, which is
/// the entire point. Console output from a PASSING test reaches the
/// <c>dotnet test --verbosity normal</c> log; that was measured on this repository rather than
/// assumed, so the lines accumulate in CI without a collector or an artifact upload.
/// </para>
/// <para>
/// Nothing here changes a bound or fails a test. It only makes the distribution visible, so the
/// contended resource can be found instead of guessed at.
/// </para>
/// </remarks>
internal static class TerminalWaitObservation
{
    /// <summary>
    /// The bound these waits used to fail at. It is no longer a bound: it is the threshold above
    /// which a wait is worth reporting, held at the old value on purpose so the reports answer
    /// exactly the question that raising the backstop made unanswerable.
    /// </summary>
    internal static readonly TimeSpan LegacyBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Prefix every published line starts with, so a CI log can be grepped for the whole
    /// population in one pass.
    /// </summary>
    internal const string Marker = "TERMINAL_WAIT_OVER_LEGACY_BOUND";

    /// <summary>
    /// Returns the line to publish for a wait that outlived <see cref="LegacyBound"/>, or
    /// <see langword="null"/> for one that did not.
    /// </summary>
    /// <remarks>
    /// Strictly greater than: a wait that took exactly the legacy bound did not outlive it, and
    /// would not have failed under it either.
    /// </remarks>
    internal static string? Describe(
        string caller,
        string awaitedEvent,
        TimeSpan elapsed,
        TerminalWaitOutcome outcome)
    {
        if (elapsed <= LegacyBound)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Marker} caller={caller} awaited={awaitedEvent} elapsedMs={elapsed.TotalMilliseconds:F3} legacyBoundMs={LegacyBound.TotalMilliseconds:F3} outcome={Render(outcome)}");
    }

    /// <summary>
    /// Publishes the line for an over-bound wait, and nothing at all for one that stayed under it.
    /// </summary>
    internal static void Publish(
        string caller,
        string awaitedEvent,
        TimeSpan elapsed,
        TerminalWaitOutcome outcome,
        Action<string>? publish = null)
    {
        if (Describe(caller, awaitedEvent, elapsed, outcome) is not { } line)
        {
            return;
        }

        // Resolved per call, never captured once: Console.Out can be redirected, and a delegate
        // bound at static-initialisation time would keep writing to the stream that was current
        // when the class was first touched.
        (publish ?? WriteLineToConsole)(line);
    }

    /// <summary>
    /// Measures an awaited terminal event and publishes it if it outlived the legacy bound.
    /// </summary>
    /// <param name="readElapsed">
    /// Injection point for the oracles. It exists so the over-bound behaviour can be proven without
    /// a test that genuinely blocks for more than ten seconds.
    /// </param>
    internal static async Task<T> ObserveAsync<T>(
        string caller,
        string awaitedEvent,
        Func<Task<T>> wait,
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        readElapsed ??= () => stopwatch.Elapsed;
        TerminalWaitOutcome outcome = TerminalWaitOutcome.Unfinished;

        try
        {
            T result = await wait().ConfigureAwait(false);
            outcome = TerminalWaitOutcome.Completed;
            return result;
        }
        finally
        {
            // In a finally, and the outcome starts at Unfinished: a wait that ends by throwing is
            // the one most worth seeing, so it must not be the one that goes unreported.
            Publish(caller, awaitedEvent, readElapsed(), outcome, publish);
        }
    }

    /// <summary>
    /// Measures a poll that reports success as a return value rather than by not throwing.
    /// </summary>
    internal static async Task<bool> ObservePollAsync(
        string caller,
        string awaitedEvent,
        Func<Task<bool>> wait,
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        readElapsed ??= () => stopwatch.Elapsed;
        bool completed = false;

        try
        {
            completed = await wait().ConfigureAwait(false);
            return completed;
        }
        finally
        {
            Publish(caller, awaitedEvent, readElapsed(), ToOutcome(completed), publish);
        }
    }

    /// <summary>
    /// Measures a synchronous spin that reports success as a return value.
    /// </summary>
    internal static bool ObserveSpin(
        string caller,
        string awaitedEvent,
        Func<bool> wait,
        Action<string>? publish = null,
        Func<TimeSpan>? readElapsed = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        readElapsed ??= () => stopwatch.Elapsed;
        bool completed = false;

        try
        {
            completed = wait();
            return completed;
        }
        finally
        {
            Publish(caller, awaitedEvent, readElapsed(), ToOutcome(completed), publish);
        }
    }

    private static TerminalWaitOutcome ToOutcome(bool completed)
    {
        return completed ? TerminalWaitOutcome.Completed : TerminalWaitOutcome.Unfinished;
    }

    private static string Render(TerminalWaitOutcome outcome)
    {
        return outcome == TerminalWaitOutcome.Completed ? "completed" : "unfinished";
    }

    private static void WriteLineToConsole(string line)
    {
        Console.Out.WriteLine(line);
    }
}
