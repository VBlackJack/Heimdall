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
/// Shared remote file-browser clipboard for SFTP/FTP panes.
/// </summary>
public interface IRemoteClipboardService
{
    /// <summary>Raised whenever <see cref="Current"/> changes.</summary>
    event Action? Changed;

    /// <summary>The current remote clipboard content, or null when empty.</summary>
    SftpClipboardContent? Current { get; }

    /// <summary>Stores a new remote clipboard snapshot.</summary>
    void Set(SftpClipboardContent content);

    /// <summary>Clears the current remote clipboard snapshot.</summary>
    void Clear();
}

/// <summary>Whether a clipboard capture is a move (cut) or a copy.</summary>
public enum SftpClipboardMode
{
    /// <summary>Entries will be moved (renamed) on paste, then the clipboard is cleared.</summary>
    Cut,

    /// <summary>Entries will be copied on paste; the clipboard is kept for repeated pastes.</summary>
    Copy,
}

/// <summary>
/// Immutable snapshot of a cut/copy operation: the captured entries, their source directory,
/// mode, and the endpoint key of the pane that captured them.
/// </summary>
/// <param name="Entries">The captured remote entries.</param>
/// <param name="SourceDirectory">The directory the entries were captured from.</param>
/// <param name="Mode">Whether the capture is a cut or a copy.</param>
/// <param name="SourceEndpointKey">The normalized endpoint key of the source pane.</param>
public sealed record SftpClipboardContent(
    IReadOnlyList<SftpFileInfo> Entries,
    string SourceDirectory,
    SftpClipboardMode Mode,
    string SourceEndpointKey);
