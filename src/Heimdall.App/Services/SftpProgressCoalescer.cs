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

using Heimdall.Sftp;

namespace Heimdall.App.Services;

/// <summary>
/// Holds the newest progress event of a transfer until the display takes it, so a flood of events
/// costs one redraw instead of one dispatch each.
/// </summary>
/// <remarks>
/// A browser raises a progress event for every buffer it moves, thousands a second on a fast link,
/// and the view queued a dispatcher call for each one; the queue could fall behind and the window
/// seemed frozen. Events are posted from the transfer thread and taken by a timer on the UI
/// thread: only the last of them is ever shown, which is all a progress bar can use.
/// </remarks>
public sealed class SftpProgressCoalescer
{
    private SftpTransferProgress? _latest;

    /// <summary>Gets whether an event is waiting to be taken.</summary>
    public bool HasPending => Volatile.Read(ref _latest) is not null;

    /// <summary>Posts an event, replacing any that has not been taken yet. Safe from any thread.</summary>
    public void Post(SftpTransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Volatile.Write(ref _latest, progress);
    }

    /// <summary>Takes the newest event, or <see langword="null"/> when none arrived since the last take.</summary>
    public SftpTransferProgress? Take() => Interlocked.Exchange(ref _latest, null);
}
