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

using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Ssh;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// When a session that cannot resize tells the user that the width of its terminal moved away from
/// the one it was launched with. The tracker keeps no display state: the caller says which notice
/// width the header shows, so these tests drive it the way the view does.
/// </summary>
public sealed class PipeSessionSizeNoticeTests
{
    private const int LaunchColumns = 80;
    private const int LaunchRows = 24;
    private const int ResizedColumns = 125;
    private const int ResizedRows = 40;
    private const int ResizedAgainColumns = 130;

    private static PipeSessionSizeNotice NewPipeNotice() => new(new TerminalSize(LaunchColumns, LaunchRows));

    [Fact]
    public void PipeSession_ResizeToDifferentSize_RaisesSizeMismatchNoticeOnce()
    {
        PipeSessionSizeNotice notice = NewPipeNotice();

        SizeNoticeChange first = notice.Observe(new TerminalSize(ResizedColumns, ResizedRows), shownColumns: null);
        SizeNoticeChange repeated = notice.Observe(new TerminalSize(ResizedColumns, ResizedRows), ResizedColumns);

        first.Should().Be(SizeNoticeChange.Raise);
        repeated.Should().Be(SizeNoticeChange.None);
    }

    [Fact]
    public void PipeSession_ResizeBackToLaunchSize_ClearsTheNotice()
    {
        PipeSessionSizeNotice notice = NewPipeNotice();

        SizeNoticeChange back = notice.Observe(new TerminalSize(LaunchColumns, ResizedRows), ResizedColumns);

        back.Should().Be(SizeNoticeChange.Clear);
    }

    [Fact]
    public void PipeSession_AtLaunchSizeWithNoNotice_DoesNothing()
    {
        NewPipeNotice().Observe(new TerminalSize(LaunchColumns, LaunchRows), shownColumns: null)
            .Should().Be(SizeNoticeChange.None);
    }

    [Fact]
    public void Tracker_RowsOnlyChange_RaisesNothing()
    {
        NewPipeNotice().Observe(new TerminalSize(LaunchColumns, ResizedRows), shownColumns: null)
            .Should().Be(SizeNoticeChange.None);
    }

    [Fact]
    public void Tracker_ColumnsChangeAgain_RefreshesTheNumbers()
    {
        PipeSessionSizeNotice notice = NewPipeNotice();

        SizeNoticeChange change = notice.Observe(new TerminalSize(ResizedAgainColumns, ResizedRows), ResizedColumns);
        SizeNoticeChange sameWidthAgain = notice.Observe(new TerminalSize(ResizedAgainColumns, LaunchRows), ResizedAgainColumns);

        change.Should().Be(SizeNoticeChange.Refresh);
        sameWidthAgain.Should().Be(SizeNoticeChange.None);
    }

    /// <summary>
    /// The disconnect path has set its own status: the notice neither clears (which would put a
    /// stale line back and announce it) nor raises.
    /// </summary>
    [Fact]
    public void Clear_AfterDisconnect_ChangesNothing()
    {
        NewPipeNotice().Observe(new TerminalSize(LaunchColumns, ResizedRows), ResizedColumns, plainStatusShown: false, sessionRunning: false)
            .Should().Be(SizeNoticeChange.None);
        NewPipeNotice().Observe(new TerminalSize(ResizedColumns, ResizedRows), shownColumns: null, plainStatusShown: true, sessionRunning: false)
            .Should().Be(SizeNoticeChange.None);
    }

    /// <summary>
    /// Another status is on screen (a macro timeout in the error brush): it keeps the line.
    /// </summary>
    [Fact]
    public void Notice_IsNotRaisedOverAnotherStatus()
    {
        NewPipeNotice().Observe(new TerminalSize(ResizedColumns, ResizedRows), shownColumns: null, plainStatusShown: false)
            .Should().Be(SizeNoticeChange.None);
    }

    [Fact]
    public void Notice_IsRaisedOnceThePlainStatusIsBack()
    {
        PipeSessionSizeNotice notice = NewPipeNotice();
        TerminalSize resized = new(ResizedColumns, ResizedRows);
        notice.Observe(resized, shownColumns: null, plainStatusShown: false).Should().Be(SizeNoticeChange.None);

        SizeNoticeChange afterPlainStatusIsBack = notice.Observe(resized, shownColumns: null, plainStatusShown: true);

        afterPlainStatusIsBack.Should().Be(SizeNoticeChange.Raise);
    }

    [Fact]
    public void SshNetSession_Resize_RaisesNoNotice()
    {
        using SshShellSession session = new();

        PipeSessionSizeNotice.ForSession(session).Should().BeNull();
    }

    [Fact]
    public void APipeSessionThatNeverStarted_HasNoLaunchSizeToCompareWith()
    {
        using PipeModeSession session = new();

        PipeSessionSizeNotice.ForSession(session).Should().BeNull();
    }
}
