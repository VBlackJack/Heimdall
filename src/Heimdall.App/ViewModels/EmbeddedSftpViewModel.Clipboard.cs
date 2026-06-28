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

using System.IO;
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

    /// <summary>Whether this pane can paste the current shared remote clipboard content.</summary>
    public bool HasClipboard => Clipboard is { Entries.Count: > 0 } clipboard
        && CanPasteClipboard(clipboard);

    /// <summary>True when an external-clipboard (Explorer) paste should be offered: the Windows
    /// clipboard carries file drops AND this pane is connected.</summary>
    public static bool CanPasteFromExternalClipboard(bool clipboardHasFileDrop, bool isConnected)
        => clipboardHasFileDrop && isConnected;

    [RelayCommand]
    private void CutSelected() => SetClipboard(SftpClipboardMode.Cut);

    [RelayCommand]
    private void CopySelected() => SetClipboard(SftpClipboardMode.Copy);

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private Task Paste() => PasteClipboardAsync();

    [RelayCommand]
    private Task DuplicateSelected() => DuplicateEntriesAsync(SelectedFiles);

    private bool CanPaste() => Clipboard is { Entries.Count: > 0 } clipboard
        && IsConnected
        && CanPasteClipboard(clipboard);

    // Captures the current multi-selection plus the source directory and the cut/copy mode. No transport.
    private void SetClipboard(SftpClipboardMode mode)
    {
        IReadOnlyList<SftpFileInfo> selection = SelectedFiles;
        if (selection.Count == 0)
        {
            return;
        }

        _remoteClipboard.Set(new SftpClipboardContent(
            [.. selection],
            CurrentPath,
            mode,
            EndpointKey,
            _browser));

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
        if (clipboard is null || clipboard.Entries.Count == 0)
        {
            return;
        }

        if (IsClipboardForCurrentEndpoint(clipboard))
        {
            await PasteSameEndpointClipboardAsync(clipboard).ConfigureAwait(false);
            return;
        }

        await PasteCrossEndpointClipboardAsync(clipboard).ConfigureAwait(false);
    }

    private async Task PasteSameEndpointClipboardAsync(SftpClipboardContent clipboard)
    {
        if (_browser is null)
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

    private async Task PasteCrossEndpointClipboardAsync(SftpClipboardContent clipboard)
    {
        if (_browser is null)
        {
            return;
        }

        IRemoteBrowser? sourceBrowser = clipboard.SourceBrowser;
        if (!IsSourceBrowserAvailable(sourceBrowser))
        {
            await RunOnUiAsync(() => SetErrorStatus("Source session no longer available."))
                .ConfigureAwait(false);
            return;
        }

        _transferCts?.Cancel();
        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        CancellationToken ct = _transferCts.Token;

        TransferProgressValue = 0;
        IsTransferInProgress = true;

        bool cut = clipboard.Mode == SftpClipboardMode.Cut;
        var processedCutSources = new HashSet<string>(StringComparer.Ordinal);
        Action<SftpTransferProgress> sourceProgress = progress =>
        {
            _ = _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    UpdateTransferProgress(progress);
                }
            });
        };

        sourceBrowser!.TransferProgress += sourceProgress;

        try
        {
            string targetDirectory = CurrentPath;
            var existingNames = new HashSet<string>(
                UnfilteredEntries.Select(entry => entry.Name),
                StringComparer.Ordinal);

            int totalEntries = clipboard.Entries.Count;
            for (int index = 0; index < totalEntries; index++)
            {
                ct.ThrowIfCancellationRequested();

                SftpFileInfo entry = clipboard.Entries[index];
                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                SftpFileInfo plannedRoot = entry with { Name = targetName };

                TransferStatusText = $"Transferring {entry.Name} ({index + 1}/{totalEntries})...";
                await TransferCrossEndpointRootAsync(sourceBrowser, plannedRoot, targetDirectory, ct)
                    .ConfigureAwait(false);

                if (cut)
                {
                    await DeleteCrossEndpointSourceAsync(sourceBrowser, entry.FullPath, ct)
                        .ConfigureAwait(false);
                    processedCutSources.Add(entry.FullPath);
                }

                existingNames.Add(targetName);
            }

            if (cut)
            {
                _remoteClipboard.Clear();
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusPasteComplete")))
                .ConfigureAwait(false);
            await Refresh().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
                UpdateStatus(_localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled"))
                .ConfigureAwait(false);
        }
        catch (SourceSessionUnavailableException)
        {
            await RunOnUiAsync(() => SetErrorStatus("Source session no longer available."))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (cut)
            {
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
                }).ConfigureAwait(false);
            }

            await RunOnUiAsync(() => SetTransferError(ex)).ConfigureAwait(false);
        }
        finally
        {
            sourceBrowser.TransferProgress -= sourceProgress;
            IsTransferInProgress = false;
            TransferProgressValue = 0;
        }
    }

    private async Task TransferCrossEndpointRootAsync(
        IRemoteBrowser sourceBrowser,
        SftpFileInfo root,
        string targetDirectory,
        CancellationToken ct)
    {
        IReadOnlyList<RemoteTransferOp> ops = await RemoteTransferTreePlanner.PlanAsync(
            [root],
            targetDirectory,
            (path, listCt) => ListCrossEndpointSourceDirectoryAsync(sourceBrowser, path, listCt),
            ct).ConfigureAwait(false);

        int totalFiles = ops.Count(op => op.Kind == RemoteTransferOpKind.TransferFile);
        int transferredFiles = 0;

        foreach (RemoteTransferOp op in ops)
        {
            ct.ThrowIfCancellationRequested();

            if (op.Kind == RemoteTransferOpKind.MakeDirectory)
            {
                await CreateCrossEndpointDirectoryAsync(op.DestinationRemotePath, ct).ConfigureAwait(false);
                continue;
            }

            transferredFiles++;
            string fileName = Path.GetFileName(op.SourceRemotePath);
            TransferStatusText = $"Transferring {fileName} ({transferredFiles}/{totalFiles})...";
            await TransferCrossEndpointFileAsync(sourceBrowser, op, ct).ConfigureAwait(false);
        }
    }

    private async Task CreateCrossEndpointDirectoryAsync(string destinationPath, CancellationToken ct)
    {
        if (_browser is null)
        {
            return;
        }

        try
        {
            await _browser.CreateDirectoryAsync(destinationPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!await RemoteDirectoryExistsAsync(_browser, destinationPath, ct).ConfigureAwait(false))
            {
                throw;
            }

            Core.Logging.FileLogger.Info(
                $"EmbeddedSFTP cross-server paste merge: remote directory already exists, continuing: {destinationPath}");
        }
    }

    private async Task TransferCrossEndpointFileAsync(
        IRemoteBrowser sourceBrowser,
        RemoteTransferOp op,
        CancellationToken ct)
    {
        if (_browser is null)
        {
            return;
        }

        string localTemp = CreateCrossEndpointTempPath();
        try
        {
            try
            {
                await sourceBrowser.DownloadFileAsync(op.SourceRemotePath, localTemp, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSourceUnavailableException(ex))
            {
                throw new SourceSessionUnavailableException(ex);
            }

            await _browser.UploadFileAsync(localTemp, op.DestinationRemotePath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteCrossEndpointTempPath(localTemp);
        }
    }

    private static async Task<IReadOnlyList<SftpFileInfo>> ListCrossEndpointSourceDirectoryAsync(
        IRemoteBrowser sourceBrowser,
        string path,
        CancellationToken ct)
    {
        try
        {
            return await sourceBrowser.ListDirectoryAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSourceUnavailableException(ex))
        {
            throw new SourceSessionUnavailableException(ex);
        }
    }

    private static async Task DeleteCrossEndpointSourceAsync(
        IRemoteBrowser sourceBrowser,
        string sourcePath,
        CancellationToken ct)
    {
        try
        {
            await sourceBrowser.DeleteAsync(sourcePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSourceUnavailableException(ex))
        {
            throw new SourceSessionUnavailableException(ex);
        }
    }

    private bool CanPasteClipboard(SftpClipboardContent clipboard)
    {
        if (IsClipboardForCurrentEndpoint(clipboard))
        {
            return true;
        }

        return IsSourceBrowserAvailable(clipboard.SourceBrowser);
    }

    private static bool IsSourceBrowserAvailable(IRemoteBrowser? sourceBrowser)
    {
        if (sourceBrowser is null)
        {
            return false;
        }

        try
        {
            return sourceBrowser.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsSourceUnavailableException(Exception ex)
    {
        return ex is ObjectDisposedException
            || (ex is InvalidOperationException
                && ex.Message.Contains("connected", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SourceSessionUnavailableException : Exception
    {
        public SourceSessionUnavailableException(Exception innerException)
            : base("Source session no longer available.", innerException)
        {
        }
    }

    private static string CreateCrossEndpointTempPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Heimdall", "cross-server-paste");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N"));
    }

    private static void TryDeleteCrossEndpointTempPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[CrossServerPaste] failed to delete staging file '{path}': {ex.Message}");
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
