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

using Heimdall.Terminal;

namespace Heimdall.App.Services;

/// <summary>What the owner of a session has to do about its size notice after a size was observed.</summary>
internal enum SizeNoticeChange
{
    /// <summary>Nothing: the header already shows what the size calls for.</summary>
    None,

    /// <summary>Show the notice: the terminal is no longer as wide as the session was launched.</summary>
    Raise,

    /// <summary>Show the notice again with the new width: the terminal changed width once more.</summary>
    Refresh,

    /// <summary>Remove the notice and put back what it covered: the widths agree again.</summary>
    Clear,
}

/// <summary>
/// Decides when a session that cannot resize has to tell the user that its terminal and its remote
/// PTY no longer agree on the width.
/// </summary>
/// <remarks>
/// <para>A pipe-mode (Plink) session fixes the remote PTY size at launch. When the user then
/// changes the terminal width, the remote keeps wrapping at the old one and the command line looks
/// corrupted with no hint why. Only the width matters: a row-only difference (the tab strip gaining
/// a row, the search bar opening) does not break line editing.</para>
/// <para>The tracker keeps no display state. The caller says which notice width the header shows
/// right now (read from what the header actually holds), so a notice overwritten by another status
/// is raised again instead of staying marked as shown.</para>
/// <para>A transport that resizes after it attaches has no tracker at all: <see cref="ForSession"/>
/// returns <see langword="null"/> for it, so it can never raise the notice.</para>
/// </remarks>
internal sealed class PipeSessionSizeNotice
{
    /// <summary>Creates the tracker for a session launched at the given size.</summary>
    public PipeSessionSizeNotice(TerminalSize launchedAt)
    {
        ArgumentNullException.ThrowIfNull(launchedAt);
        LaunchedAt = launchedAt;
    }

    /// <summary>The size the remote PTY was created with, and keeps.</summary>
    public TerminalSize LaunchedAt { get; }

    /// <summary>
    /// The tracker for a session, or <see langword="null"/> when the session can follow a resize
    /// (everything but a started pipe-mode session).
    /// </summary>
    public static PipeSessionSizeNotice? ForSession(object? session) =>
        session is PipeModeSession { LaunchColumns: > 0, LaunchRows: > 0 } pipe
            ? new PipeSessionSizeNotice(new TerminalSize(pipe.LaunchColumns.Value, pipe.LaunchRows.Value))
            : null;

    /// <summary>What the notice has to do now that the terminal has the given size.</summary>
    /// <param name="current">The size the terminal now has.</param>
    /// <param name="shownColumns">The terminal width the notice in the header states, or
    /// <see langword="null"/> when the header does not show the notice.</param>
    /// <param name="plainStatusShown">Whether the header shows the plain connected status. The
    /// notice is raised over that and over nothing else: another status on screen (a macro timeout,
    /// in the error brush) is more urgent and is left alone until it goes away.</param>
    /// <param name="sessionRunning">Whether the session is still connected. Once it is not, the
    /// disconnect path has set its own status and the notice changes nothing.</param>
    public SizeNoticeChange Observe(
        TerminalSize current,
        int? shownColumns,
        bool plainStatusShown = true,
        bool sessionRunning = true)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!sessionRunning)
        {
            return SizeNoticeChange.None;
        }

        if (current.Columns == LaunchedAt.Columns)
        {
            return shownColumns is null ? SizeNoticeChange.None : SizeNoticeChange.Clear;
        }

        if (shownColumns is null)
        {
            return plainStatusShown ? SizeNoticeChange.Raise : SizeNoticeChange.None;
        }

        return shownColumns == current.Columns ? SizeNoticeChange.None : SizeNoticeChange.Refresh;
    }
}
