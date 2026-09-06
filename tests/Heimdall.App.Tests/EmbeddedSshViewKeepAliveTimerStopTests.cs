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
/// construct; the assertion is on the executable statement, not on a comment.</para>
/// </remarks>
public sealed class EmbeddedSshViewKeepAliveTimerStopTests
{
    private const string StopSignature = "private void StopKeepAliveTimer()";

    [Fact]
    public void TheStopTakesTheTimerOutOfTheFieldAtomically()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            StopSignature);
        string[] lines = EmbeddedSshViewSourceReader.ExecutableLines(body);

        Assert.Contains(
            lines,
            line => line == "System.Threading.Timer? stoppedTimer = Interlocked.Exchange(ref _keepAliveTimer, null);");
        Assert.DoesNotContain(
            lines,
            line => line.StartsWith("_keepAliveTimer.Dispose()", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line == "_keepAliveTimer = null;");
    }

    /// <summary>
    /// The exchanged timer, not the field, is what gets disposed.
    /// </summary>
    [Fact]
    public void TheExchangedTimerIsTheOneDisposed()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            StopSignature);
        string[] lines = EmbeddedSshViewSourceReader.ExecutableLines(body);

        Assert.Contains(lines, line => line == "stoppedTimer.Dispose();");
    }

    /// <summary>
    /// Guards the guard: the source being read carries the method and the field.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesTheKeepAliveTimer()
    {
        string source = EmbeddedSshViewSourceReader.ReadViewSource();

        Assert.Contains(StopSignature, source, StringComparison.Ordinal);
        Assert.Contains("private System.Threading.Timer? _keepAliveTimer;", source, StringComparison.Ordinal);
    }
}
