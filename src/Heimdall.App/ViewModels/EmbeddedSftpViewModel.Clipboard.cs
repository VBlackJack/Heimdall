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
    /// Pastes the clipboard into the current directory. Copy mode copies each supported entry
    /// (clipboard kept, repeatable); cut mode moves supported entries and retains unsupported entries
    /// in the clipboard. Every destination name is resolved to be collision-free, so nothing is ever
    /// overwritten. A cut entry that would resolve to its own path (same directory, same name) is skipped.
    /// </summary>
    /// <remarks>
    /// A remote paste moves bytes, so it holds the transfer coordinator for its whole duration: it is
    /// refused while another transfer runs instead of racing it, and its cancellation token is the one
    /// the Cancel command signals. The gate sits here rather than in each branch so both the
    /// same-endpoint and the cross-endpoint path are covered by a single acquire/release pair.
    /// </remarks>
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

        TransferStartState startState = TryBeginTransfer(out CancellationTokenSource? transferCts);
        if (startState == TransferStartState.Busy)
        {
            await RunOnUiAsync(RefuseConcurrentTransfer).ConfigureAwait(false);
            return;
        }

        if (startState != TransferStartState.Started || transferCts is null)
        {
            return;
        }

        try
        {
            if (IsClipboardForCurrentEndpoint(clipboard))
            {
                await PasteSameEndpointClipboardAsync(clipboard, transferCts.Token).ConfigureAwait(false);
                return;
            }

            await PasteCrossEndpointClipboardAsync(clipboard, transferCts.Token).ConfigureAwait(false);
        }
        finally
        {
            CompleteTransfer(transferCts);
        }
    }

    /// <summary>
    /// Reports that a concurrent transfer blocks the operation. Refusing is deliberate: silently
    /// cancelling the running transfer would destroy work the user never asked to abandon.
    /// </summary>
    private void RefuseConcurrentTransfer() => UpdateStatus(
        _localizer?["SftpTransferInProgress"] ?? "A file transfer is already in progress.");

    /// <summary>Progress line shown while a clipboard entry is being transferred.</summary>
    private string TransferringEntryStatus(string entryName, int position, int total)
        => _localizer?.Format(
            "SftpStatusTransferringEntry",
            entryName,
            position.ToString(),
            total.ToString())
            ?? $"Transferring {entryName} ({position}/{total})...";

    /// <summary>
    /// Status shown when the pane holding the cut or copied entries is gone.
    /// </summary>
    /// <remarks>
    /// The matching <see cref="SourceSessionUnavailableException"/> keeps an English message: it is
    /// caught and converted to this status before anything reaches the user, so its own text is
    /// developer-facing only, like every other exception message in this assembly.
    /// </remarks>
    private string SourceSessionUnavailableStatus()
        => _localizer?["SftpErrorSourceSessionUnavailable"] ?? "Source session no longer available.";

    private async Task PasteSameEndpointClipboardAsync(SftpClipboardContent clipboard, CancellationToken ct)
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

        // Source paths of cut entries that have been fully processed (moved, or skipped as a self-move).
        // On a mid-loop failure these are dropped from the clipboard so a re-paste cannot target sources
        // that have already moved away; the entries not yet processed (including the one that failed,
        // whose source still exists) are retained.
        var processedCutSources = new HashSet<string>(StringComparer.Ordinal);
        var skippedUnsupportedPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            // Read live, not from what the pane last rendered: a cut publishes by a bare rename,
            // which on FTP (RNFR/RNTO) many servers answer by overwriting, and the only thing
            // between the user and an overwritten file was how old the snapshot was.
            HashSet<string> existingNames = await ReadLiveDestinationNamesAsync(targetDirectory, ct)
                .ConfigureAwait(false);

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

            foreach (SftpFileInfo entry in clipboard.Entries)
            {
                // Entry granularity is all this layer can offer: interrupting the server-side copy of
                // a single entry belongs to the browser, which does not take a token yet.
                ct.ThrowIfCancellationRequested();

                if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
                {
                    skippedUnsupportedPaths.Add(entry.FullPath);
                    continue;
                }

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

                    await _browser.RenameAsync(entry.FullPath, destination, ct);
                    processedCutSources.Add(entry.FullPath);
                }
                else
                {
                    await _browser.CopyAsync(entry.FullPath, destination, entry.IsDirectory, ct);
                }

                existingNames.Add(targetName);
            }

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
                });
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusPasteComplete")));
            await Refresh().ConfigureAwait(false);

            if (skippedUnsupportedPaths.Count > 0)
            {
                foreach (string path in skippedUnsupportedPaths)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsupported remote entry '{path}' during same-endpoint paste.");
                }

                string warning = _localizer?.Format(
                    "WarnRemoteEntriesSkippedUnsupported",
                    skippedUnsupportedPaths.Count)
                    ?? $"Skipped {skippedUnsupportedPaths.Count} entries that are neither files nor directories. See the log for details.";
                await RunOnUiAsync(() => ShowOperationWarning(warning));
            }
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

            await RunOnUiAsync(() => ReportOperationOutcome(ex));
        }
    }

    private async Task PasteCrossEndpointClipboardAsync(SftpClipboardContent clipboard, CancellationToken ct)
    {
        if (_browser is null)
        {
            return;
        }

        IRemoteBrowser? sourceBrowser = clipboard.SourceBrowser;
        if (!IsSourceBrowserAvailable(sourceBrowser))
        {
            await RunOnUiAsync(() => SetErrorStatus(SourceSessionUnavailableStatus()))
                .ConfigureAwait(false);
            return;
        }

        // Before anything is created on the destination, and asked through the capability seam rather
        // than by testing the browser's type: the browser may be wrapped by the operations-log
        // decorator, and a type test would answer about the wrapper. A transport that cannot publish
        // without replacing does not paste at all. Names here are resolved from a listing, which is a
        // snapshot; without an exclusive publish the only thing between the user and an overwritten
        // file is how old that snapshot is.
        IRemoteNoClobberPublisher? publisher =
            (_browser as IRemoteNoClobberCapability)?.NoClobberPublisher;
        if (publisher is null)
        {
            await RunOnUiAsync(() => SetErrorStatus(NoClobberUnsupportedStatus()))
                .ConfigureAwait(false);
            return;
        }

        TransferProgressValue = 0;

        bool cut = clipboard.Mode == SftpClipboardMode.Cut;
        bool cutSourceNotDeleted = false;
        var processedCutSources = new HashSet<string>(StringComparer.Ordinal);
        var skippedUnsupportedPaths = new HashSet<string>(StringComparer.Ordinal);
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

            // Read live from the destination, never from the cached UI listing. The cached listing is
            // whatever the pane last displayed, so a name it does not know about produces a destination
            // path that already exists. This read only chooses a name: no security decision rests on
            // it, because it can go stale the moment it returns. The guarantee is the exclusive
            // reservation of each node below.
            HashSet<string> existingNames = await ReadLiveDestinationNamesAsync(targetDirectory, ct)
                .ConfigureAwait(false);

            int totalEntries = clipboard.Entries.Count;
            for (int index = 0; index < totalEntries; index++)
            {
                ct.ThrowIfCancellationRequested();

                SftpFileInfo entry = clipboard.Entries[index];
                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                SftpFileInfo plannedRoot = entry with { Name = targetName };

                TransferStatusText = TransferringEntryStatus(entry.Name, index + 1, totalEntries);
                RemoteTransferPlan plan = await TransferCrossEndpointRootAsync(
                    sourceBrowser,
                    publisher,
                    plannedRoot,
                    targetDirectory,
                    ct)
                    .ConfigureAwait(false);
                skippedUnsupportedPaths.UnionWith(plan.SkippedUnsupportedPaths);

                // Reached only when every node of this root was published, because a collision and an
                // unconfirmed outcome both throw out of the call above. An unconfirmed publication in
                // particular must never authorise this: the destination may hold the file, or not, and
                // deleting the source on a maybe is how the only copy disappears.
                if (cut && plan.SkippedUnsupportedPaths.Count == 0)
                {
                    bool sourceDeleted = await DeleteCrossEndpointSourceAsync(
                            sourceBrowser,
                            entry.FullPath,
                            ct)
                        .ConfigureAwait(false);
                    if (sourceDeleted)
                    {
                        processedCutSources.Add(entry.FullPath);
                    }
                    else
                    {
                        cutSourceNotDeleted = true;
                    }
                }

                if (plan.Ops.Count > 0)
                {
                    existingNames.Add(targetName);
                }
            }

            await ReconcileCutClipboardAsync(clipboard, cut, processedCutSources).ConfigureAwait(false);

            // Refresh first, status last. A refresh reloads the directory and reports Ready when the
            // listing succeeds, so any status written before it is overwritten by the outcome of an
            // unrelated operation.
            await RefreshAfterPasteAsync().ConfigureAwait(false);
            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusPasteComplete")))
                .ConfigureAwait(false);

            List<string> completionWarnings = [];
            if (skippedUnsupportedPaths.Count > 0)
            {
                foreach (string path in skippedUnsupportedPaths)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsupported remote entry '{path}' during cross-endpoint paste.");
                }

                string warning = _localizer?.Format(
                    "WarnRemoteEntriesSkippedUnsupported",
                    skippedUnsupportedPaths.Count)
                    ?? $"Skipped {skippedUnsupportedPaths.Count} entries that are neither files nor directories. See the log for details.";
                completionWarnings.Add(warning);
            }

            if (cutSourceNotDeleted)
            {
                completionWarnings.Add(L10n("SftpCutSourceNotDeleted"));
            }

            if (completionWarnings.Count > 0)
            {
                string warning = string.Join(Environment.NewLine, completionWarnings);
                await RunOnUiAsync(() => ShowOperationWarning(warning)).ConfigureAwait(false);
            }
        }
        // Every exit below has begun processing, so all three reconcile the cut clipboard, attempt a
        // refresh of the destination, and only then state the outcome. A cancellation is not proof that
        // nothing landed: a link or a directory reservation can have taken effect before the answer was
        // lost, so a reload is attempted and the current source is kept exactly as for an unconfirmed
        // outcome. The reload is best-effort: a disconnected session or a failing listing leaves the view
        // as it was, which never changes the outcome and never authorises deleting a source.
        catch (OperationCanceledException)
        {
            await ReconcileCutClipboardAsync(clipboard, cut, processedCutSources).ConfigureAwait(false);
            await RefreshAfterPasteAsync().ConfigureAwait(false);
            await RunOnUiAsync(() =>
                UpdateStatus(_localizer?["SftpStatusTransferCancelled"] ?? "Transfer cancelled"))
                .ConfigureAwait(false);
        }
        catch (SourceSessionUnavailableException)
        {
            await ReconcileCutClipboardAsync(clipboard, cut, processedCutSources).ConfigureAwait(false);
            await RefreshAfterPasteAsync().ConfigureAwait(false);
            await RunOnUiAsync(() => SetErrorStatus(SourceSessionUnavailableStatus()))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReconcileCutClipboardAsync(clipboard, cut, processedCutSources).ConfigureAwait(false);
            await RefreshAfterPasteAsync().ConfigureAwait(false);
            await RunOnUiAsync(() => SetTransferError(ex)).ConfigureAwait(false);
        }
        finally
        {
            sourceBrowser.TransferProgress -= sourceProgress;
        }
    }

    /// <summary>
    /// Rebuilds the cut clipboard from the sources that were actually removed.
    /// </summary>
    /// <remarks>
    /// Called on every exit that began processing, successful or not. Without it, an abnormal exit after
    /// a first root has been published and its source deleted leaves that root in the clipboard pointing
    /// at a path that no longer exists, and the next paste fails on a source it can no longer read.
    /// <para>
    /// Only roots whose source was genuinely deleted are dropped. The root being processed when the
    /// interruption arrived stays, along with every root not yet reached: an ambiguous publication or
    /// reservation must never remove an entry, because the entry is the user's record that the move did
    /// not demonstrably complete.
    /// </para>
    /// </remarks>
    private Task ReconcileCutClipboardAsync(
        SftpClipboardContent clipboard,
        bool cut,
        HashSet<string> processedCutSources)
    {
        if (!cut)
        {
            return Task.CompletedTask;
        }

        List<SftpFileInfo> remaining = clipboard.Entries
            .Where(entry => !processedCutSources.Contains(entry.FullPath))
            .ToList();

        return RunOnUiAsync(() =>
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

    /// <summary>
    /// Reloads the destination directory after a paste, without letting the reload decide the outcome.
    /// </summary>
    /// <remarks>
    /// The reload is best-effort in two distinct ways, and neither ever changes the paste's verdict.
    /// <see cref="LoadDirectoryCoreAsync"/> already returns without listing when a load is in flight or
    /// the session is disconnected, so "refreshed" can legitimately mean "nothing happened". The catch
    /// below is defence in depth rather than a path reached today: that method handles its own failures
    /// and is not expected to throw, and if it ever did, the transfer's result would still be what the
    /// user needs to see.
    /// </remarks>
    private async Task RefreshAfterPasteAsync()
    {
        try
        {
            await Refresh().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP could not refresh the destination after a cross-endpoint paste: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Status shown when the destination transport cannot publish without replacing an existing entry.
    /// </summary>
    private string NoClobberUnsupportedStatus()
        => _localizer?["SftpErrorPasteNoClobberUnsupported"]
            ?? "This session cannot paste without risking overwriting existing files.";

    private async Task<RemoteTransferPlan> TransferCrossEndpointRootAsync(
        IRemoteBrowser sourceBrowser,
        IRemoteNoClobberPublisher publisher,
        SftpFileInfo root,
        string targetDirectory,
        CancellationToken ct)
    {
        RemoteTransferPlan plan = await RemoteTransferTreePlanner.PlanAsync(
            [root],
            targetDirectory,
            (path, listCt) => ListCrossEndpointSourceDirectoryAsync(sourceBrowser, path, listCt),
            ct).ConfigureAwait(false);

        int totalFiles = plan.Ops.Count(op => op.Kind == RemoteTransferOpKind.TransferFile);
        int transferredFiles = 0;

        foreach (RemoteTransferOp op in plan.Ops)
        {
            ct.ThrowIfCancellationRequested();

            if (op.Kind == RemoteTransferOpKind.MakeDirectory)
            {
                await CreateCrossEndpointDirectoryAsync(publisher, op.DestinationRemotePath, ct)
                    .ConfigureAwait(false);
                continue;
            }

            transferredFiles++;
            string fileName = Path.GetFileName(op.SourceRemotePath);
            TransferStatusText = TransferringEntryStatus(fileName, transferredFiles, totalFiles);
            await TransferCrossEndpointFileAsync(sourceBrowser, publisher, op, ct).ConfigureAwait(false);
        }

        return plan;
    }

    /// <summary>
    /// Reserves one destination directory exclusively, refusing rather than merging into an existing one.
    /// </summary>
    /// <remarks>
    /// The merge tolerance this used to carry is removed on purpose. Continuing into a directory that
    /// already exists is precisely how a paste ends up writing into a subtree it never reserved: every
    /// file below it is then placed among entries that belong to somebody else, and the collision it was
    /// meant to avoid simply moves one level down.
    /// <para>
    /// A tree that fails partway is therefore left incomplete. That is the lesser harm: a recursive
    /// cleanup of a directory this paste created could delete entries another party added inside it in
    /// the meantime.
    /// </para>
    /// </remarks>
    private static Task CreateCrossEndpointDirectoryAsync(
        IRemoteNoClobberPublisher publisher,
        string destinationPath,
        CancellationToken ct)
        => publisher.CreateDirectoryExclusiveAsync(destinationPath, ct);

    /// <summary>
    /// Downloads one source file and publishes it at its destination, which must not already exist.
    /// </summary>
    /// <remarks>
    /// The publication replaces the plain upload this used to perform. An upload carries no existence
    /// contract: it truncates whatever sits at the destination, and the destination name was chosen from
    /// a listing that may already be out of date by the time the bytes arrive.
    /// </remarks>
    private async Task TransferCrossEndpointFileAsync(
        IRemoteBrowser sourceBrowser,
        IRemoteNoClobberPublisher publisher,
        RemoteTransferOp op,
        CancellationToken ct)
    {
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

            await publisher.PublishFileIfAbsentAsync(localTemp, op.DestinationRemotePath, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteCrossEndpointTempPath(localTemp);
        }
    }

    /// <summary>
    /// Lists the destination directory live, for choosing non-colliding names only.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>UnfilteredEntries</c>: that collection is what the pane last rendered, so a
    /// file created since the last refresh is invisible to it and the name built from it collides. This
    /// listing is not a safety check either, and nothing may treat it as one: it is stale the instant it
    /// returns. It only avoids the common case of a name already in use.
    /// </remarks>
    private async Task<HashSet<string>> ReadLiveDestinationNamesAsync(
        string targetDirectory,
        CancellationToken ct)
    {
        if (_browser is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        IReadOnlyList<SftpFileInfo> entries = await _browser
            .ListDirectoryAsync(targetDirectory, ct)
            .ConfigureAwait(false);

        return new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.Ordinal);
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

    private static async Task<bool> DeleteCrossEndpointSourceAsync(
        IRemoteBrowser sourceBrowser,
        string sourcePath,
        CancellationToken ct)
    {
        try
        {
            await sourceBrowser.DeleteAsync(sourcePath, ct).ConfigureAwait(false);
            return true;
        }
        catch (RemoteRecursiveDeleteException ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP cross-endpoint cut left source '{sourcePath}' in place ({ex.Reason}).");
            return false;
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
    /// <remarks>
    /// Duplicating copies bytes on the server, so it holds the transfer coordinator like any other
    /// transfer: refused while one is running, never cancelling it.
    /// </remarks>
    public async Task DuplicateEntriesAsync(IReadOnlyList<SftpFileInfo> entries)
    {
        if (entries.Count == 0 || _disposed || _browser is null)
        {
            return;
        }

        TransferStartState startState = TryBeginTransfer(out CancellationTokenSource? transferCts);
        if (startState == TransferStartState.Busy)
        {
            await RunOnUiAsync(RefuseConcurrentTransfer).ConfigureAwait(false);
            return;
        }

        if (startState != TransferStartState.Started || transferCts is null)
        {
            return;
        }

        CancellationToken ct = transferCts.Token;
        string targetDirectory = CurrentPath;
        var skippedUnsupportedPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            HashSet<string> existingNames = await ReadLiveDestinationNamesAsync(targetDirectory, ct)
                .ConfigureAwait(false);

            foreach (SftpFileInfo entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
                {
                    skippedUnsupportedPaths.Add(entry.FullPath);
                    continue;
                }

                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                string destination = CombineRemotePath(targetDirectory, targetName);

                await _browser.CopyAsync(entry.FullPath, destination, entry.IsDirectory, ct);

                existingNames.Add(targetName);
            }

            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusDuplicateComplete")));
            await Refresh().ConfigureAwait(false);

            if (skippedUnsupportedPaths.Count > 0)
            {
                foreach (string path in skippedUnsupportedPaths)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsupported remote entry '{path}' during duplicate.");
                }

                string warning = _localizer?.Format(
                    "WarnRemoteEntriesSkippedUnsupported",
                    skippedUnsupportedPaths.Count)
                    ?? $"Skipped {skippedUnsupportedPaths.Count} entries that are neither files nor directories. See the log for details.";
                await RunOnUiAsync(() => ShowOperationWarning(warning));
            }
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => ReportOperationOutcome(ex));
        }
        finally
        {
            CompleteTransfer(transferCts);
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
