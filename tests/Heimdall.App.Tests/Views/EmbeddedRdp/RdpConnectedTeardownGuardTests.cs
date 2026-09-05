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

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the connected handler re-checks disposal after it has pumped messages.
/// </summary>
/// <remarks>
/// <para>The layout flush the handler runs after a connect calls <c>DoEvents</c>, and a tab close
/// dispatched inside that pump tears the view down: <c>Dispose</c> releases the sleep-prevention
/// count and clears the host. The handler then went on to acquire that count again on a view that
/// had already released it, and nothing released it a second time, so the process held the machine
/// awake until it exited - one session at a time. The same shape was closed on the connect path
/// (the guard after the pre-connect flushes); this is the same guard on the connected path.</para>
/// <para>Read as statements of the dispatcher lambda, and the order is read by slicing: the guard
/// must stand as a statement of what follows the flush, and the acquisition as a statement of what
/// follows the guard. A guard folded behind a term that is false by construction changes the
/// statement text and fails here; a guard moved above the flush is no longer found below it.</para>
/// </remarks>
public sealed class RdpConnectedTeardownGuardTests
{
    private const string Member = "private void OnRdpConnected()";
    private const string LambdaOpening = "Dispatcher.Invoke(() =>";
    private const string LambdaClosing = "});";
    private const string Flush = "FlushLayoutPipeline(";
    private const string Guard = "if (_disposed)";
    private const string Acquisition = "AcquireSleepPrevention();";
    private const string Return = "return;";

    [Fact]
    public void TheConnectedHandler_ReChecksDisposalBetweenTheFlushAndTheAcquisition()
    {
        string lambda = DispatcherLambda();

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(lambda, Flush),
            "OnRdpConnected no longer flushes the layout inside its dispatcher lambda; this guard measures nothing.");

        // What follows the flush, read as a body of its own: the guard has to be a step of it.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(StatementsFrom(lambda, Flush), Guard),
            "OnRdpConnected does not re-check _disposed after the layout flush inside its dispatcher lambda.");

        // And what follows the guard: the acquisition has to be a step of it, so a torn-down view
        // never reaches it.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(StatementsFrom(lambda, Guard), Acquisition),
            "Sleep prevention is not acquired below the disposal check, so a torn-down view can still acquire it.");
    }

    [Fact]
    public void TheGuard_LeavesTheHandler()
    {
        string guarded = ViewSource.HandlerBody(DispatcherLambda(), Guard);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(guarded, Return),
            "The disposal check inside OnRdpConnected does not return, so everything below it still runs on a torn-down view.");
    }

    /// <summary>The dispatcher lambda of the handler, from its opening to its closing brace.</summary>
    private static string DispatcherLambda()
    {
        string fromOpening = ViewSource.HandlerBody(ViewSource.HandlerLogic(Member), LambdaOpening);
        int closing = fromOpening.LastIndexOf(LambdaClosing, StringComparison.Ordinal);
        Assert.True(closing > 0, "The dispatcher lambda of OnRdpConnected no longer closes where this guard expects.");
        return fromOpening[..(closing + LambdaClosing.Length)];
    }

    /// <summary>
    /// The statements of the lambda from <paramref name="needle"/> onward, wrapped as one body so
    /// the predicate reads them at depth one.
    /// </summary>
    private static string StatementsFrom(string lambda, string needle)
        => "{" + ViewSource.HandlerBody(lambda, needle);
}
