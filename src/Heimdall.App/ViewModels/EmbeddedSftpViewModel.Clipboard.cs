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

using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Sftp;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Clipboard half of the embedded SFTP/FTP browser: the Cut / Copy / Paste / Duplicate operations and
/// the non-destructive name-collision resolver. All transport goes through the same (operations-logged)
/// browser the other view-model operations use, so a paste/duplicate is journaled as a Copy record and a
/// cut-paste as a Rename record without any extra wiring here.
/// </summary>
public sealed partial class EmbeddedSftpViewModel
{
    /// <summary>
    /// The current shared remote clipboard content, or null when empty.
    /// </summary>
    public SftpClipboardContent? Clipboard => _remoteClipboard.Current;

    /// <summary>Whether this pane can see clipboard entries from the same remote endpoint.</summary>
    public bool HasClipboard => Clipboard is { Entries.Count: > 0 } clipboard
        && IsClipboardForCurrentEndpoint(clipboard);

    [RelayCommand]
    private void CutSelected() => SetClipboard(SftpClipboardMode.Cut);

    [RelayCommand]
    private void CopySelected() => SetClipboard(SftpClipboardMode.Copy);

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private Task Paste() => PasteClipboardAsync();

    [RelayCommand]
    private Task DuplicateSelected() => DuplicateEntriesAsync(SelectedFiles);

    private bool CanPaste() => HasClipboard && IsConnected;

    // Captures the current multi-selection plus the source directory and the cut/copy mode. No transport.
    private void SetClipboard(SftpClipboardMode mode)
    {
        IReadOnlyList<SftpFileInfo> selection = SelectedFiles;
        if (selection.Count == 0)
        {
            return;
        }

        _remoteClipboard.Set(new SftpClipboardContent([.. selection], CurrentPath, mode, EndpointKey));

        string statusKey = mode == SftpClipboardMode.Cut ? "SftpStatusCut" : "SftpStatusCopied";
        UpdateStatus(_localizer?.Format(statusKey, selection.Count.ToString())
            ?? $"{selection.Count} item(s)");
    }

    /// <summary>
    /// Pastes the clipboard into the current directory. Copy mode copies each entry (clipboard kept,
    /// repeatable); cut mode moves each entry and then clears the clipboard (consumed once). Every
    /// destination name is resolved to be collision-free, so nothing is ever overwritten. A cut entry
    /// that would resolve to its own path (same directory, same name) is skipped.
    /// </summary>
    public async Task PasteClipboardAsync()
    {
        if (_disposed || _browser is null)
        {
            return;
        }

        SftpClipboardContent? clipboard = Clipboard;
        if (clipboard is null || clipboard.Entries.Count == 0 || !IsClipboardForCurrentEndpoint(clipboard))
        {
            return;
        }

        string targetDirectory = CurrentPath;
        bool cut = clipboard.Mode == SftpClipboardMode.Cut;
        bool sameDirectory = string.Equals(
            clipboard.SourceDirectory.TrimEnd('/'),
            targetDirectory.TrimEnd('/'),
            StringComparison.Ordinal);

        var existingNames = new HashSet<string>(
            UnfilteredEntries.Select(entry => entry.Name),
            StringComparer.Ordinal);

        // A cut back into the same directory must leave the original names "free" so the self-move guard
        // recognizes an unchanged target; a copy into the same directory keeps them as collisions so a
        // second copy is created instead of being skipped.
        if (cut && sameDirectory)
        {
            foreach (SftpFileInfo entry in clipboard.Entries)
            {
                existingNames.Remove(entry.Name);
            }
        }

        // Source paths of cut entries that have been fully processed (moved, or skipped as a self-move).
        // On a mid-loop failure these are dropped from the clipboard so a re-paste cannot target sources
        // that have already moved away; the entries not yet processed (including the one that failed,
        // whose source still exists) are retained.
        var processedCutSources = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (SftpFileInfo entry in clipboard.Entries)
            {
                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                string destination = CombineRemotePath(targetDirectory, targetName);

                if (cut)
                {
                    if (string.Equals(destination, entry.FullPath, StringComparison.Ordinal))
                    {
                        // Self-move (same directory, same name): nothing to do, but it is consumed.
                        processedCutSources.Add(entry.FullPath);
                        continue;
                    }

                    await _browser.RenameAsync(entry.FullPath, destination);
                    processedCutSources.Add(entry.FullPath);
                }
                else
                {
                    await _browser.CopyAsync(entry.FullPath, destination, entry.IsDirectory);
                }

                existingNames.Add(targetName);
            }

            if (cut)
            {
                _remoteClipboard.Clear();
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusPasteComplete")));
            await Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (cut)
            {
                // Retain only the entries not yet moved, so the clipboard stays consistent for a re-paste.
                List<SftpFileInfo> remaining = clipboard.Entries
                    .Where(entry => !processedCutSources.Contains(entry.FullPath))
                    .ToList();

                await RunOnUiAsync(() =>
                {
                    if (remaining.Count > 0)
                    {
                        _remoteClipboard.Set(clipboard with { Entries = remaining });
                    }
                    else
                    {
                        _remoteClipboard.Clear();
                    }
                });
            }

            await RunOnUiAsync(() => SetTransferError(ex));
        }
    }

    /// <summary>
    /// Duplicates each entry into the current directory under a collision-free "(copy)" name. Never
    /// overwrites; the clipboard is not involved.
    /// </summary>
    public async Task DuplicateEntriesAsync(IReadOnlyList<SftpFileInfo> entries)
    {
        if (entries.Count == 0 || _disposed || _browser is null)
        {
            return;
        }

        string targetDirectory = CurrentPath;
        var existingNames = new HashSet<string>(
            UnfilteredEntries.Select(entry => entry.Name),
            StringComparer.Ordinal);

        try
        {
            foreach (SftpFileInfo entry in entries)
            {
                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                string destination = CombineRemotePath(targetDirectory, targetName);

                await _browser.CopyAsync(entry.FullPath, destination, entry.IsDirectory);

                existingNames.Add(targetName);
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusDuplicateComplete")));
            await Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => SetTransferError(ex));
        }
    }

    /// <summary>
    /// Resolves a non-colliding child name: returns <paramref name="desiredName"/> when it is free,
    /// otherwise inserts " (copy)" before the extension, then " (copy 2)", " (copy 3)", and so on.
    /// Dotfiles (for example ".bashrc") are treated as having no extension.
    /// </summary>
    public static string BuildNonCollidingName(IReadOnlyCollection<string> existingNames, string desiredName)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredName);

        var taken = new HashSet<string>(existingNames, StringComparer.Ordinal);
        if (!taken.Contains(desiredName))
        {
            return desiredName;
        }

        (string stem, string extension) = SplitNameAndExtension(desiredName);

        string firstCandidate = $"{stem} (copy){extension}";
        if (!taken.Contains(firstCandidate))
        {
            return firstCandidate;
        }

        for (int counter = 2; ; counter++)
        {
            string candidate = $"{stem} (copy {counter}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // Splits a leaf name into (stem, extension-including-dot). A leading dot (dotfile) or a trailing dot
    // is not treated as an extension boundary, so ".bashrc" -> (".bashrc", "") and "a.txt" -> ("a", ".txt").
    private static (string Stem, string Extension) SplitNameAndExtension(string name)
    {
        int lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == name.Length - 1)
        {
            return (name, string.Empty);
        }

        return (name[..lastDot], name[lastDot..]);
    }
}
