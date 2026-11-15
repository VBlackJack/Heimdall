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

using Heimdall.Core.Utilities;

namespace Heimdall.App.ViewModels;

/// <summary>The kind of act the SFTP pane ran as root.</summary>
internal enum SftpRootActKind
{
    /// <summary>A file sent to the server through the privileged channel.</summary>
    Upload,

    /// <summary>A file sent to the server after the operator chose to replace the existing one.</summary>
    Overwrite,

    /// <summary>A save from the integrated editor that had to go through the privileged channel.</summary>
    InlineSave,

    /// <summary>A save from the external editor of a file opened as root.</summary>
    ExternalEditorSave,

    /// <summary>A file opened for editing as root (it is read as root, then edited outside).</summary>
    EditOpen,

    /// <summary>A folder created as root.</summary>
    NewFolder,

    /// <summary>An entry renamed as root.</summary>
    Rename,

    /// <summary>A permission change made as root.</summary>
    PermissionChange,

    /// <summary>A file read as root and saved on this computer.</summary>
    Download,

    /// <summary>An entry deleted as root.</summary>
    Delete,

    /// <summary>A directory read as root. Counted apart from the operations.</summary>
    Listing,
}

/// <summary>How a root act ended.</summary>
internal enum SftpRootActOutcome
{
    /// <summary>The act completed.</summary>
    Succeeded,

    /// <summary>The act failed.</summary>
    Failed,

    /// <summary>The act was cancelled before it completed.</summary>
    Cancelled,
}

/// <summary>One act the pane ran as root.</summary>
/// <param name="Kind">What was done.</param>
/// <param name="Path">The remote path, already written out visibly (no control or bidi character).</param>
/// <param name="TargetPath">The second path of a rename, written out visibly, or null.</param>
/// <param name="Outcome">How the act ended.</param>
/// <param name="Timestamp">When the act ended, in local time.</param>
internal sealed record SftpRootAct(
    SftpRootActKind Kind,
    string Path,
    string? TargetPath,
    SftpRootActOutcome Outcome,
    DateTimeOffset Timestamp);

/// <summary>A copy of what the log held at one moment.</summary>
/// <param name="RecentOperations">The latest operations (never a listing), newest first.</param>
/// <param name="OperationCount">Every operation attempted, whatever its outcome.</param>
/// <param name="FailedOperationCount">The operations that failed.</param>
/// <param name="ListingCount">Every directory listing attempted as root.</param>
/// <param name="FailedListingCount">The listings that failed.</param>
/// <param name="LastListing">The latest listing, or null when there was none.</param>
internal sealed record SftpRootActivitySnapshot(
    IReadOnlyList<SftpRootAct> RecentOperations,
    int OperationCount,
    int FailedOperationCount,
    int ListingCount,
    int FailedListingCount,
    SftpRootAct? LastListing);

/// <summary>
/// What a pane did as root, kept for the life of the pane so the operator can see afterwards that
/// root was used. It records and displays; it never decides whether anything runs.
/// </summary>
/// <remarks>
/// The log is written from whatever thread runs the operation and read from the UI thread, so
/// every access takes the lock. <see cref="Changed"/> is raised outside the lock and a failing
/// subscriber is swallowed: an indicator must never break the operation it describes.
/// </remarks>
internal sealed class SftpRootActivityLog
{
    /// <summary>How many operations the log remembers; older ones still count in the totals.</summary>
    internal const int MaxRecentOperations = 20;

    /// <summary>The longest path kept, before the truncation marker.</summary>
    internal const int MaxShownPathLength = 1024;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<SftpRootAct> _recent = [];
    private int _operationCount;
    private int _failedOperationCount;
    private int _listingCount;
    private int _failedListingCount;
    private SftpRootAct? _lastListing;

    /// <summary>Creates a log that stamps acts with the system clock.</summary>
    public SftpRootActivityLog()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates a log that stamps acts with <paramref name="clock"/>.</summary>
    public SftpRootActivityLog(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Raised after an act was recorded, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>Records one act. Never throws.</summary>
    /// <param name="kind">What was done.</param>
    /// <param name="path">The remote path as the server named it; it is written out visibly here.</param>
    /// <param name="targetPath">The second path of a rename, or null.</param>
    /// <param name="outcome">How the act ended.</param>
    public void Record(
        SftpRootActKind kind,
        string? path,
        string? targetPath,
        SftpRootActOutcome outcome)
    {
        SftpRootAct act = new(
            kind,
            UntrustedText.MakeVisible(path, MaxShownPathLength),
            targetPath is null ? null : UntrustedText.MakeVisible(targetPath, MaxShownPathLength),
            outcome,
            _clock.GetLocalNow());

        lock (_gate)
        {
            if (kind == SftpRootActKind.Listing)
            {
                _listingCount++;
                if (outcome == SftpRootActOutcome.Failed)
                {
                    _failedListingCount++;
                }

                _lastListing = act;
            }
            else
            {
                _operationCount++;
                if (outcome == SftpRootActOutcome.Failed)
                {
                    _failedOperationCount++;
                }

                _recent.Insert(0, act);
                if (_recent.Count > MaxRecentOperations)
                {
                    _recent.RemoveRange(MaxRecentOperations, _recent.Count - MaxRecentOperations);
                }
            }
        }

        RaiseChanged();
    }

    /// <summary>Copies what the log holds now.</summary>
    public SftpRootActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SftpRootActivitySnapshot(
                _recent.ToArray(),
                _operationCount,
                _failedOperationCount,
                _listingCount,
                _failedListingCount,
                _lastListing);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"SftpRootActivityLog: a subscriber failed on a recorded act: {ex.Message}");
        }
    }
}

/// <summary>One line of the list the root badge opens.</summary>
/// <param name="Text">What the line says.</param>
/// <param name="TimeText">When it happened, for the time column of the menu.</param>
/// <param name="AutomationName">What a screen reader says for the line.</param>
/// <param name="IsSummary">Whether the line stands for every listing rather than for one operation.</param>
public sealed record SftpRootActivityEntry(
    string Text,
    string TimeText,
    string AutomationName,
    bool IsSummary);
