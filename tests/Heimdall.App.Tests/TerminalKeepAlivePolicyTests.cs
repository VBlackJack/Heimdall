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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The TMOUT-reset carriage return goes only into an idle SSH shell, and is never a macro entry.
/// </summary>
/// <remarks>
/// <para>The reset used to be written unconditionally, every interval, into any session the view
/// hosted. A bare CR is a keystroke: written while the user types it submits a half-typed line, an
/// empty password to <c>sudo</c>, Enter inside vim or less. Written into the local shell or a WinRM
/// session it is a stray Enter with no <c>TMOUT</c> to reset. And while a macro was being recorded
/// it was captured as an entry, so the replay carried a phantom Enter.</para>
/// <para>The decision lives in <see cref="TerminalKeepAlivePolicy"/> and is pinned directly. The
/// view needs a desktop to construct, so its use of the policy is read from source, on executable
/// lines only.</para>
/// </remarks>
public sealed class TerminalKeepAlivePolicyTests
{
    private const int Interval = 240;
    private const long Second = 1000;

    [Theory]
    [InlineData("SSH")]
    [InlineData("ssh")]
    public void AnSshSessionHasATmoutToReset(string connectionType)
    {
        Assert.True(TerminalKeepAlivePolicy.AppliesTo(connectionType));
        Assert.Equal(Interval, TerminalKeepAlivePolicy.ResolveIntervalSeconds(connectionType, Interval));
    }

    [Theory]
    [InlineData("LOCAL")]
    [InlineData("WINRM")]
    [InlineData("TELNET")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherSessionGetsNoTimer(string? connectionType)
    {
        Assert.False(TerminalKeepAlivePolicy.AppliesTo(connectionType));
        Assert.Equal(0, TerminalKeepAlivePolicy.ResolveIntervalSeconds(connectionType, Interval));
    }

    [Fact]
    public void AShellThatNeverSawInputGetsTheReset()
    {
        Assert.True(TerminalKeepAlivePolicy.ShouldSendTick(
            nowMilliseconds: 10 * Second,
            lastInputMilliseconds: TerminalKeepAlivePolicy.NoInputRecorded,
            intervalSeconds: Interval));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(Interval - 1)]
    public void InputDuringTheLastIntervalSkipsTheTick(int secondsSinceInput)
    {
        long lastInput = 1000 * Second;
        long now = lastInput + (secondsSinceInput * Second);

        Assert.False(TerminalKeepAlivePolicy.ShouldSendTick(now, lastInput, Interval));
    }

    [Theory]
    [InlineData(Interval)]
    [InlineData(Interval + 1)]
    [InlineData(Interval * 3)]
    public void AnIdleShellGetsTheReset(int secondsSinceInput)
    {
        long lastInput = 1000 * Second;
        long now = lastInput + (secondsSinceInput * Second);

        Assert.True(TerminalKeepAlivePolicy.ShouldSendTick(now, lastInput, Interval));
    }

    /// <summary>
    /// The tick consults the policy, or the policy defends nothing.
    /// </summary>
    [Fact]
    public void TheTickConsultsThePolicy()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            "private void SendKeepAlive()");

        Assert.Contains(
            "TerminalKeepAlivePolicy.ShouldSendTick(",
            EmbeddedSshViewSourceReader.ExecutableText(body),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every write that is real input stamps the clock the policy reads.
    /// </summary>
    [Theory]
    [InlineData("private void WriteToSession(byte[] data, bool marksTerminalInput = true)")]
    [InlineData("private void WriteToSession(string text, bool marksTerminalInput = true)")]
    public void RealInputStampsTheClockTheTickReads(string signature)
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            signature);
        string[] lines = EmbeddedSshViewSourceReader.ExecutableLines(body);

        Assert.Contains(lines, line => line == "if (marksTerminalInput)");
        Assert.Contains(lines, line => line == "NoteTerminalInput();");
    }

    /// <summary>
    /// A macro records what the user typed, not the reset the timer wrote.
    /// </summary>
    [Fact]
    public void TheResetIsNeverRecordedIntoAMacro()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            "private void WriteToSession(byte[] data, bool marksTerminalInput = true)");
        string[] lines = EmbeddedSshViewSourceReader.ExecutableLines(body);

        Assert.Contains(lines, line => line == "if (_isRecording && marksTerminalInput)");
        Assert.DoesNotContain(lines, line => line == "if (_isRecording)");
    }

    /// <summary>
    /// A process-backed session starts the timer only when the policy says the session is SSH.
    /// </summary>
    [Fact]
    public void AProcessBackedSessionAsksThePolicyBeforeStartingTheTimer()
    {
        string body = EmbeddedSshViewSourceReader.ExtractMethodBody(
            EmbeddedSshViewSourceReader.ReadViewSource(),
            "public void AttachTerminalSession(");

        Assert.Contains(
            "StartKeepAliveTimer(TerminalKeepAlivePolicy.ResolveIntervalSeconds(_sessionTab?.ConnectionType, keepAliveIntervalSeconds));",
            EmbeddedSshViewSourceReader.ExecutableText(body),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guards above: the source being read carries the members they extract.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadCarriesTheKeepAliveMembers()
    {
        string source = EmbeddedSshViewSourceReader.ReadViewSource();

        Assert.Contains("private void SendKeepAlive()", source, StringComparison.Ordinal);
        Assert.Contains("public void AttachTerminalSession(", source, StringComparison.Ordinal);
        Assert.Contains("private void WriteToSession(byte[] data", source, StringComparison.Ordinal);
        Assert.Contains("private void WriteToSession(string text", source, StringComparison.Ordinal);
    }
}
