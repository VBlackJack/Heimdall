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

namespace Heimdall.App.Tests;

/// <summary>
/// Stopping the keepalive timer takes the field once, whichever thread gets there first.
/// </summary>
/// <remarks>
/// <para>The stop is reached from the timer's own pool thread (a tick that finds the session gone)
/// and from the UI thread (dispose, disconnect). It read the field twice: a null check and then a
/// dispose through the field again. Two callers racing past the check left one of them
/// dereferencing null, and a null reference on a pool thread is not caught by anything: it takes
/// the process down.</para>
/// <para>The auto-reconnect timer next to it already exchanges the field atomically. This pins the
/// keepalive stop to the same shape. Read from source because the view needs a desktop to
/// construct; each anchor is carried through the statement predicate, so a statement folded
/// behind a false term does not keep this green.</para>
/// </remarks>
public sealed class EmbeddedSshViewKeepAliveTimerStopTests
{
    private const string StopSignature = "private void StopKeepAliveTimer()";

    [Fact]
    public void TheStopTakesTheTimerOutOfTheFieldAtomically()
    {
        string logic = SourceStatements.Method(SourceStatements.ViewLogic(), StopSignature);

        SourceStatements.AssertStatementChain(
            logic,
            "System.Threading.Timer? stoppedTimer = Interlocked.Exchange(ref _keepAliveTimer, null);");
        Assert.DoesNotContain("_keepAliveTimer.Dispose()", logic, StringComparison.Ordinal);
        Assert.DoesNotContain("_keepAliveTimer = null", logic, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exchanged timer, not the field, is what gets disposed.
    /// </summary>
    [Fact]
    public void TheExchangedTimerIsTheOneDisposed()
    {
        string logic = SourceStatements.Method(SourceStatements.ViewLogic(), StopSignature);

        SourceStatements.AssertStatementChain(logic, "stoppedTimer.Dispose();");
    }

    /// <summary>
    /// Guards the guard: the field the statement exchanges is the view's timer field, and the
    /// method being read exists (the reader asserts that itself).
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesTheKeepAliveTimer()
    {
        _ = SourceStatements.Method(SourceStatements.ViewLogic(), StopSignature);

        System.Reflection.FieldInfo? field = typeof(Heimdall.App.Views.EmbeddedSshView).GetField(
            "_keepAliveTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(System.Threading.Timer), field.FieldType);
    }
}
