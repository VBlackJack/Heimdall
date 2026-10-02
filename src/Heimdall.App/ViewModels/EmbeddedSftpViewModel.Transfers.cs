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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Sftp;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Transfer queue, recursive download and batch progress of the embedded SFTP/FTP browser.
/// </summary>
/// <remarks>
/// <para>
/// Transfers run one at a time, in order. A batch handed in while another one runs is queued
/// instead of refused, shown in the list with its own cancel and retry actions, and a batch that
/// failed or was cancelled stays in the list so it can be run again: the retry replans from the
/// original request, and the conflict dialog then lets the user skip what already arrived.
/// </para>
/// <para>
/// The whole queue holds the single transfer slot (<see cref="IsTransferInProgress"/>), so the
/// close guard, the paste and the delete all see one continuous transfer rather than a flicker
/// between batches.
/// </para>
/// </remarks>
public sealed partial class EmbeddedSftpViewModel
{
    /// <summary>Entries between two reports of how far the planning walk of a download has got.</summary>
    private const int ScanReportEveryEntries = 25;

    /// <summary>Win32 <c>ERROR_DISK_FULL</c>, in the low word of an <see cref="IOException.HResult"/>.</summary>
    private const int ErrorDiskFull = 112;

    /// <summary>Win32 <c>ERROR_HANDLE_DISK_FULL</c>, in the low word of an <see cref="IOException.HResult"/>.</summary>
    private const int ErrorHandleDiskFull = 39;

    private const int Win32ErrorMask = 0xFFFF;

    private readonly object _jobsGate = new();
    private readonly List<SftpTransferJob> _jobQueue = [];
    private bool _pumpRunning;
    private CancellationTokenSource? _currentJobCts;
    private int _nextJobId;
    private TransferProgressTracker? _progressTracker;
    private bool _progressIsUpload;
    private (int Downloaded, int Total) _downloadBatchProgress;
    private bool _jobsObserverAttached;

    /// <summary>Whether the transfer bar has no measurable fraction to show (planning, or nothing to measure).</summary>
    [ObservableProperty]
    private bool _isTransferIndeterminate;

    /// <summary>Whether the transfer list holds a batch (queued, running, or ended and not yet cleared).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTransferPanel))]
    private bool _hasTransferJobs;

    /// <summary>Whether the list holds a batch that ended without completing, so it can be cleared.</summary>
    [ObservableProperty]
    private bool _hasFinishedTransferJobs;

    /// <summary>Whether the transfer panel is shown: a transfer runs, or the list holds a batch.</summary>
    public bool ShowTransferPanel => IsTransferInProgress || HasTransferJobs;

    /// <summary>The queued, running, failed and cancelled batches.</summary>
    public ObservableCollection<SftpTransferJob> TransferJobs { get; } = [];

    /// <summary>The clock the batch progress reads; settable so a test can drive the rate and the estimate.</summary>
    internal TimeProvider ProgressClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Runs when the queue has been found empty and before the transfer slot is released; settable
    /// so a test can hand in a batch at the exact moment the queue is winding down.
    /// </summary>
    internal Action? QueueDrainedProbe { get; set; }

    private void EnsureJobsObserver()
    {
        if (_jobsObserverAttached)
        {
            return;
        }

        _jobsObserverAttached = true;
        TransferJobs.CollectionChanged += OnTransferJobsChanged;
    }

    private void OnTransferJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SftpTransferJob job in e.OldItems.OfType<SftpTransferJob>())
            {
                job.PropertyChanged -= OnTransferJobPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SftpTransferJob job in e.NewItems.OfType<SftpTransferJob>())
            {
                job.PropertyChanged += OnTransferJobPropertyChanged;
            }
        }

        HasTransferJobs = TransferJobs.Count > 0;
        HasFinishedTransferJobs = TransferJobs.Any(job => job.CanRetry);
    }

    private void OnTransferJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SftpTransferJob.Status), StringComparison.Ordinal))
        {
            // Read on the UI thread: the list is only ever changed there.
            _ = RunOnUiAsync(() => HasFinishedTransferJobs = TransferJobs.Any(job => job.CanRetry));
        }
    }

    // ------------------------------------------------------------------
    // Entry points
    // ------------------------------------------------------------------

    /// <summary>
    /// Uploads dropped local entries (files and/or directories) into <paramref name="targetRemoteDir"/>,
    /// recursing into directories. Directories are created before their contents and an existing remote
    /// directory is tolerated so re-dropping a tree merges rather than aborting.
    /// </summary>
    /// <remarks>
    /// Queued behind a running transfer rather than refused. The task completes when the batch has
    /// ended, or at once when it could only be parked behind an operation the queue does not own.
    /// Must be invoked on the UI thread.
    /// </remarks>
    public Task UploadEntriesAsync(IReadOnlyList<string> localPaths, string targetRemoteDir)
    {
        ArgumentNullException.ThrowIfNull(localPaths);

        if (_disposed || _browser is null)
        {
            return Task.CompletedTask;
        }

        List<string> names = localPaths
            .Select(path => Path.GetFileName(path.TrimEnd('\\', '/')))
            .ToList();
        SftpTransferJob job = new(
            NextJobId(),
            LF("SftpTransferJobUpload", DescribeBatchLabel(names), targetRemoteDir),
            [.. localPaths],
            targetRemoteDir);
        return SubmitTransferJobAsync(job, addToList: true);
    }

    /// <summary>
    /// Downloads the selected remote entries into the target folder. A folder is downloaded with
    /// everything it contains; symbolic links and other special entries are reported and left out.
    /// </summary>
    /// <remarks>See <see cref="UploadEntriesAsync"/> for the queueing contract. Must be invoked on the UI thread.</remarks>
    public Task DownloadFilesAsync(IReadOnlyList<SftpFileInfo> files, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (_disposed || _browser is null)
        {
            return Task.CompletedTask;
        }

        SftpTransferJob job = new(
            NextJobId(),
            LF(
                "SftpTransferJobDownload",
                DescribeBatchLabel(files.Select(file => file.Name).ToList()),
                targetFolder),
            [.. files],
            targetFolder);
        return SubmitTransferJobAsync(job, addToList: true);
    }

    /// <summary>
    /// Whether one remote entry can be downloaded. The answer belongs to the download planner,
    /// which decides what to transfer with it; the context menu asks it here, so the offer and
    /// the outcome cannot drift apart.
    /// </summary>
    public static bool IsDownloadable(SftpFileInfo entry)
    {
        return RemoteDownloadTreePlanner.IsDownloadable(entry);
    }

    /// <summary>Whether a selection holds anything the download can actually transfer.</summary>
    public static bool CanDownloadSelection(IEnumerable<SftpFileInfo> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Any(IsDownloadable);
    }

    private int NextJobId() => Interlocked.Increment(ref _nextJobId);

    private string DescribeBatchLabel(IReadOnlyList<string> names)
        => names.Count == 1
            ? names[0]
            : LFC(names.Count, "SftpTransferJobItemsOne", "SftpTransferJobItems", names.Count);

    // ------------------------------------------------------------------
    // Queue
    // ------------------------------------------------------------------

    private async Task SubmitTransferJobAsync(SftpTransferJob job, bool addToList)
    {
        EnsureJobsObserver();
        SetJobState(job, SftpTransferJobStatus.Queued, L10n("SftpTransferJobQueued"));
        if (addToList)
        {
            await RunOnUiAsync(() => TransferJobs.Add(job));
        }

        bool startPump = false;
        lock (_jobsGate)
        {
            _jobQueue.Add(job);
            if (!_pumpRunning)
            {
                _pumpRunning = true;
                startPump = true;
            }
        }

        if (startPump)
        {
            _ = PumpTransferQueueAsync();
        }

        bool parked;
        lock (_jobsGate)
        {
            // Still queued with no pump behind it: another operation holds the transfer slot, and
            // the queue resumes by itself when that operation completes.
            parked = !_pumpRunning && job.Status == SftpTransferJobStatus.Queued;
        }

        if (parked)
        {
            UpdateStatus(LF("SftpStatusTransferQueued", job.Title));
            return;
        }

        await job.Completion;
    }

    /// <summary>Starts the queue again when jobs wait and nothing is running them.</summary>
    internal void ResumeTransferQueue()
    {
        bool start = false;
        lock (_jobsGate)
        {
            if (!_disposed && !_pumpRunning && _jobQueue.Count > 0)
            {
                _pumpRunning = true;
                start = true;
            }
        }

        if (start)
        {
            _ = PumpTransferQueueAsync();
        }
    }

    private async Task PumpTransferQueueAsync()
    {
        TransferStartState state = TryBeginTransfer(out CancellationTokenSource? transferCts);
        if (state != TransferStartState.Started || transferCts is null)
        {
            lock (_jobsGate)
            {
                _pumpRunning = false;
            }

            if (state == TransferStartState.Unavailable)
            {
                CancelQueuedJobs();
            }

            return;
        }

        List<SftpTransferJob> finished = [];
        try
        {
            while (true)
            {
                SftpTransferJob? job;
                lock (_jobsGate)
                {
                    // The pump stays the queue's owner until the slot is released below: a batch
                    // handed in meanwhile must find it running, not take it for a foreign
                    // operation holding the slot.
                    if (_jobQueue.Count == 0)
                    {
                        break;
                    }

                    job = _jobQueue[0];
                    _jobQueue.RemoveAt(0);
                }

                try
                {
                    await RunTransferJobAsync(job, transferCts.Token);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP transfer job failed unexpectedly [{ex.GetType().Name}]: {ex.Message}");
                    SetJobState(job, SftpTransferJobStatus.Failed, DescribeTransferError(ex));
                }

                finished.Add(job);
            }

            QueueDrainedProbe?.Invoke();
        }
        finally
        {
            // Released while the pump still owns the queue, so the resume CompleteTransfer asks
            // for starts nothing; then one decision under the gate: a batch handed in during the
            // release runs on, anything else ends the pump.
            CompleteTransfer(transferCts);
            bool runOn;
            lock (_jobsGate)
            {
                runOn = !_disposed && _jobQueue.Count > 0;
                if (!runOn)
                {
                    _pumpRunning = false;
                }
            }

            if (runOn)
            {
                _ = PumpTransferQueueAsync();
            }

            await RunOnUiAsync(() => RemoveCompletedJobs(finished));
            foreach (SftpTransferJob job in finished)
            {
                job.SignalCompleted();
            }
        }
    }

    private async Task RunTransferJobAsync(SftpTransferJob job, CancellationToken pumpToken)
    {
        using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(pumpToken);
        job.Cancellation = jobCts;
        lock (_jobsGate)
        {
            _currentJobCts = jobCts;
        }

        SetJobState(job, SftpTransferJobStatus.Running, L10n("SftpTransferJobRunning"));
        TransferProgressValue = 0;

        try
        {
            (SftpTransferJobStatus status, string detail) = job.Kind == SftpTransferJobKind.Upload
                ? await ExecuteUploadJobAsync(job, jobCts.Token)
                : await ExecuteDownloadJobAsync(job, jobCts.Token);
            SetJobState(job, status, detail);
        }
        finally
        {
            lock (_jobsGate)
            {
                _currentJobCts = null;
            }

            job.Cancellation = null;
            _progressTracker = null;
            IsTransferIndeterminate = false;
        }
    }

    private void SetJobState(SftpTransferJob job, SftpTransferJobStatus status, string detail)
    {
        job.Status = status;
        job.Detail = detail;
    }

    private void RemoveCompletedJobs(IReadOnlyList<SftpTransferJob> finished)
    {
        foreach (SftpTransferJob job in finished.Where(job => job.Status == SftpTransferJobStatus.Completed))
        {
            TransferJobs.Remove(job);
        }
    }

    private void CancelQueuedJobs()
    {
        List<SftpTransferJob> queued;
        lock (_jobsGate)
        {
            queued = [.. _jobQueue];
            _jobQueue.Clear();
        }

        foreach (SftpTransferJob job in queued)
        {
            SetJobState(job, SftpTransferJobStatus.Cancelled, L10n("SftpTransferJobCancelled"));
            job.SignalCompleted();
        }
    }

    /// <summary>Stops a batch: a queued one never starts, a running one is cancelled where it stands.</summary>
    [RelayCommand]
    private void CancelJob(SftpTransferJob? job)
    {
        if (job is null)
        {
            return;
        }

        bool wasQueued;
        lock (_jobsGate)
        {
            wasQueued = _jobQueue.Remove(job);
        }

        if (wasQueued)
        {
            SetJobState(job, SftpTransferJobStatus.Cancelled, L10n("SftpTransferJobCancelled"));
            job.SignalCompleted();
            return;
        }

        try
        {
            job.Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The batch ended between the check and the cancel: nothing left to stop.
        }
    }

    /// <summary>Runs a failed or cancelled batch again from its original request.</summary>
    [RelayCommand]
    private async Task RetryJob(SftpTransferJob? job)
    {
        if (job is null || !job.CanRetry || _disposed || _browser is null)
        {
            return;
        }

        job.ResetForRetry();
        await SubmitTransferJobAsync(job, addToList: false);
    }

    /// <summary>Removes the failed and cancelled batches from the list.</summary>
    [RelayCommand]
    private void ClearFinishedJobs()
    {
        foreach (SftpTransferJob job in TransferJobs.Where(job => job.CanRetry).ToList())
        {
            TransferJobs.Remove(job);
        }
    }

    // ------------------------------------------------------------------
    // Progress
    // ------------------------------------------------------------------

    private void BeginPreparing()
    {
        _progressTracker = null;
        IsTransferIndeterminate = true;
        TransferProgressValue = 0;
        TransferStatusText = L10n("SftpStatusTransferPreparing");
    }

    private TransferProgressTracker BeginBatchProgress(bool isUpload, long totalBytes, int totalFiles)
    {
        TransferProgressTracker tracker = new(ProgressClock);
        tracker.Begin(totalBytes, totalFiles);
        _progressIsUpload = isUpload;
        _progressTracker = tracker;
        IsTransferIndeterminate = tracker.Fraction is null;
        return tracker;
    }

    private void RenderBatchProgress(string fileName)
    {
        TransferProgressTracker? tracker = _progressTracker;
        if (tracker is null)
        {
            return;
        }

        double? fraction = tracker.Fraction;
        IsTransferIndeterminate = fraction is null;
        double percent = (fraction ?? 0) * 100;
        TransferProgressValue = percent;

        // One key per direction rather than a glyph placeholder: a translator sees a whole line and
        // can reorder it, and the arrow stays with the wording it belongs to.
        string key = _progressIsUpload
            ? "SftpStatusTransferProgressUpload"
            : "SftpStatusTransferProgressDownload";
        string shownName = string.IsNullOrEmpty(tracker.CurrentFileName) ? fileName : tracker.CurrentFileName;
        string line = LF(
            key,
            shownName,
            tracker.CurrentFilePosition,
            tracker.TotalFiles,
            FormatSize(tracker.DoneBytes),
            FormatSize(tracker.TotalBytes),
            percent.ToString("F0"));

        TimeSpan? remaining = tracker.Remaining;
        TransferStatusText = tracker.BytesPerSecond > 0 && remaining is { } eta
            ? LF("SftpStatusTransferRate", line, FormatSize((long)tracker.BytesPerSecond), FormatRemaining(eta))
            : line;
    }

    /// <summary>
    /// Updates transfer progress display state from one browser progress event.
    /// </summary>
    public void UpdateTransferProgress(SftpTransferProgress progress)
    {
        TransferProgressTracker? tracker = _progressTracker;
        if (tracker is not null)
        {
            tracker.Report(progress.BytesTransferred);
            RenderBatchProgress(progress.FileName);
            return;
        }

        // No batch is running through the queue (the inline editor, a paste): the event describes
        // one file, and is shown as the only one of its batch.
        double percent = progress.TotalBytes > 0
            ? (double)progress.BytesTransferred / progress.TotalBytes * 100
            : 0;
        TransferProgressValue = percent;
        IsTransferIndeterminate = progress.TotalBytes <= 0;
        string key = progress.IsUpload
            ? "SftpStatusTransferProgressUpload"
            : "SftpStatusTransferProgressDownload";
        TransferStatusText = LF(
            key,
            progress.FileName,
            1,
            1,
            FormatSize(progress.BytesTransferred),
            FormatSize(progress.TotalBytes),
            percent.ToString("F0"));
    }

    /// <summary>Formats a remaining time as a short localized duration.</summary>
    internal string FormatRemaining(TimeSpan remaining)
    {
        int totalSeconds = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int seconds = totalSeconds % 60;

        if (hours > 0)
        {
            return LF("SftpEtaHoursMinutes", hours, minutes);
        }

        return minutes > 0
            ? LF("SftpEtaMinutesSeconds", minutes, seconds)
            : LF("SftpEtaSeconds", seconds);
    }

    /// <summary>Stops the running batch, or the running paste or delete when no batch is running.</summary>
    [RelayCommand]
    private void CancelTransfer()
    {
        CancellationTokenSource? job;
        lock (_jobsGate)
        {
            job = _currentJobCts;
        }

        try
        {
            if (job is not null)
            {
                job.Cancel();
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // The batch ended between the check and the cancel: nothing left to stop.
        }

        lock (_transferCtsGate)
        {
            _transferCts?.Cancel();
        }
    }

    // ------------------------------------------------------------------
    // Upload job
    // ------------------------------------------------------------------

    private async Task<(SftpTransferJobStatus Status, string Detail)> ExecuteUploadJobAsync(
        SftpTransferJob job,
        CancellationToken ct)
    {
        bool refreshAfterTransfer = true;
        List<string> pendingOperationWarnings = [];
        _uploadBatchProgress = default;
        Action? finalReport = null;
        SftpTransferJobStatus status = SftpTransferJobStatus.Completed;
        string detail = L10n("SftpTransferJobCompleted");
        BeginPreparing();

        try
        {
            UploadPlanOutcome outcome = await UploadPlannedEntriesAsync(job.LocalPaths, job.RemoteDirectory, ct);
            refreshAfterTransfer = outcome.Completed;
            status = outcome.Completed ? SftpTransferJobStatus.Completed : SftpTransferJobStatus.Cancelled;
            if (!outcome.Completed)
            {
                detail = L10n("SftpTransferJobCancelled");
            }

            UpdateStatus(L10n(outcome.Completed ? "SftpStatusTransferComplete" : "SftpStatusTransferCancelled"));

            if (outcome.Completed && outcome.SkippedUnsupportedTargets.Count > 0)
            {
                foreach (string path in outcome.SkippedUnsupportedTargets)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped upload to unsupported remote destination '{path}'.");
                }

                pendingOperationWarnings.Add(LFC(
                    outcome.SkippedUnsupportedTargets.Count,
                    "WarnUploadTargetsSkippedUnsupportedOne",
                    "WarnUploadTargetsSkippedUnsupported",
                    outcome.SkippedUnsupportedTargets.Count));
            }

            if (outcome.Completed && outcome.SkippedLocalReparsePoints.Count > 0)
            {
                pendingOperationWarnings.Add(LFC(
                    outcome.SkippedLocalReparsePoints.Count,
                    "WarnUploadSourcesSkippedReparsePointsOne",
                    "WarnUploadSourcesSkippedReparsePoints",
                    outcome.SkippedLocalReparsePoints.Count));
            }
        }
        catch (OperationCanceledException)
        {
            status = SftpTransferJobStatus.Cancelled;
            detail = L10n("SftpTransferJobCancelled");
            finalReport = () => UpdateStatus(L10n("SftpStatusTransferCancelled"));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP upload failed [{ex.GetType().Name}]: {ex.Message} (sshParams={(_sshParams is not null ? "present" : "null")})");
            status = SftpTransferJobStatus.Failed;
            string failure = DescribeBatchFailure(
                ex,
                _uploadBatchProgress,
                "SftpErrorUploadFailedAfterOne",
                "SftpErrorUploadFailedAfter");
            detail = failure;
            finalReport = () => SetErrorStatus(failure);
        }
        finally
        {
            _progressTracker = null;
            IsTransferIndeterminate = false;
            if (finalReport is not null)
            {
                // The refresh ends with "Ready" and used to run after the failure had been
                // written, unawaited, so the message the user needed was wiped by the listing
                // of a directory that now held part of their batch. The listing first, then
                // the verdict, as the last message written.
                await Refresh();
                finalReport();
            }
            else if (refreshAfterTransfer)
            {
                await Refresh();
                if (pendingOperationWarnings.Count == 0)
                {
                    UpdateStatus(L10n("SftpStatusTransferComplete"));
                }
            }
        }

        if (pendingOperationWarnings.Count > 0)
        {
            ShowOperationWarning(string.Join(Environment.NewLine, pendingOperationWarnings));
        }

        return (status, detail);
    }

    /// <summary>
    /// The message for a batch that ended on an exception, with how many files had landed: "Transfer
    /// failed" alone left the user to work out which of their files were already there. A lost
    /// connection is named as such.
    /// </summary>
    private string DescribeBatchFailure(
        Exception ex,
        (int Done, int Total) progress,
        string failedAfterOneKey,
        string failedAfterKey)
    {
        if (_browser is { IsConnected: false })
        {
            return LF("SftpErrorConnectionLostDuringTransfer", progress.Done, progress.Total);
        }

        string reason = DescribeTransferError(ex);
        return progress.Done == 0
            ? reason
            : LFC(progress.Done, failedAfterOneKey, failedAfterKey, progress.Done, progress.Total, reason);
    }

    // ------------------------------------------------------------------
    // Download job
    // ------------------------------------------------------------------

    private async Task<(SftpTransferJobStatus Status, string Detail)> ExecuteDownloadJobAsync(
        SftpTransferJob job,
        CancellationToken ct)
    {
        IRemoteBrowser? browser = _browser;
        if (browser is null)
        {
            return (SftpTransferJobStatus.Cancelled, L10n("SftpTransferJobCancelled"));
        }

        _downloadBatchProgress = default;
        BeginPreparing();

        try
        {
            RemoteDownloadPlan plan = await RemoteDownloadTreePlanner
                .PlanAsync(
                    job.RemoteEntries,
                    job.LocalDirectory,
                    (directory, token) => ListDirectoryWithSudoFallbackAsync(browser, directory, token),
                    ResolveContainedLocalChild,
                    ReportDownloadScan,
                    ct)
                .ConfigureAwait(true);

            IReadOnlyList<RemoteDownloadOp> ops = plan.Ops;
            IReadOnlyList<FileConflictAnalysisItem> conflictAnalysis = FileConflictPlanner.Analyze(
                ops.Select(op => new FileConflictPlanItem(
                        op.RemotePath,
                        op.LocalPath,
                        op.Kind == RemoteDownloadOpKind.MakeDirectory
                            ? FileConflictItemKind.Directory
                            : FileConflictItemKind.File))
                    .ToList(),
                LocalTargetKind,
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<FileConflictAnalysisItem> conflicts = conflictAnalysis
                .Where(item => item.HasConflict)
                .ToList();

            IReadOnlyList<FileConflictDecision> decisions = [];
            if (conflicts.Count > 0)
            {
                FileConflictDialogViewModel dialogViewModel = new(
                    conflicts,
                    _localizer,
                    item => DescribeDownloadCollision(ops[item.Index]));
                FileConflictDialogResult? dialogResult = await _fileConflictDialogPresenter
                    .ShowAsync(dialogViewModel);
                if (dialogResult is null)
                {
                    UpdateStatus(L10n("SftpStatusTransferCancelled"));
                    return (SftpTransferJobStatus.Cancelled, L10n("SftpTransferJobCancelled"));
                }

                decisions = dialogResult.Decisions;
            }

            IReadOnlyList<FileConflictResolvedItem> resolved = FileConflictPlanner.Resolve(
                conflictAnalysis,
                decisions,
                LocalTargetExists,
                StringComparer.OrdinalIgnoreCase);

            List<FileConflictResolvedItem> fileSteps = resolved
                .Where(item => item.Action != FileConflictEffectiveAction.Skip
                    && ops[item.Index].Kind == RemoteDownloadOpKind.DownloadFile)
                .ToList();
            long totalBytes = fileSteps.Sum(item => ops[item.Index].Size);
            TransferProgressTracker tracker = BeginBatchProgress(isUpload: false, totalBytes, fileSteps.Count);
            _downloadBatchProgress = (0, fileSteps.Count);
            int downloaded = 0;

            foreach (FileConflictResolvedItem step in resolved)
            {
                ct.ThrowIfCancellationRequested();
                if (step.Action == FileConflictEffectiveAction.Skip)
                {
                    continue;
                }

                RemoteDownloadOp op = ops[step.Index];
                if (op.Kind == RemoteDownloadOpKind.MakeDirectory)
                {
                    Directory.CreateDirectory(step.EffectiveTargetPath);
                    continue;
                }

                string fileName = op.RemotePath[(op.RemotePath.LastIndexOf('/') + 1)..];
                tracker.BeginFile(fileName, op.Size);
                RenderBatchProgress(fileName);

                try
                {
                    await browser.DownloadFileAsync(op.RemotePath, step.EffectiveTargetPath, step.Overwrite, ct);
                }
                catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
                {
                    Core.Logging.FileLogger.Info(
                        $"EmbeddedSFTP download permission denied, falling back to sudo for {fileName}");
                    await DownloadViaSudoAsync(op.RemotePath, step.EffectiveTargetPath, ct, step.Overwrite);
                }

                tracker.CompleteFile();
                downloaded++;
                _downloadBatchProgress = (downloaded, fileSteps.Count);
                RenderBatchProgress(fileName);
            }

            UpdateStatus(L10n("SftpStatusTransferComplete"));
            List<string> warnings = [];
            if (plan.SkippedUnsupportedPaths.Count > 0)
            {
                foreach (string path in plan.SkippedUnsupportedPaths)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped unsupported remote entry '{path}' during download.");
                }

                warnings.Add(LF("WarnRemoteEntriesSkippedUnsupported", plan.SkippedUnsupportedPaths.Count));
            }

            if (plan.SkippedUnsafeNames.Count > 0)
            {
                foreach (string path in plan.SkippedUnsafeNames)
                {
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSFTP skipped remote entry with a name that cannot be saved safely: '{path}'.");
                }

                warnings.Add(LFC(
                    plan.SkippedUnsafeNames.Count,
                    "WarnRemoteNamesSkippedUnsafeOne",
                    "WarnRemoteNamesSkippedUnsafe",
                    plan.SkippedUnsafeNames.Count));
            }

            if (warnings.Count > 0)
            {
                ShowOperationWarning(string.Join(Environment.NewLine, warnings));
            }

            return (SftpTransferJobStatus.Completed, L10n("SftpTransferJobCompleted"));
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(L10n("SftpStatusTransferCancelled"));
            return (SftpTransferJobStatus.Cancelled, L10n("SftpTransferJobCancelled"));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP download failed [{ex.GetType().Name}]: {ex.Message} (sshParams={(_sshParams is not null ? "present" : "null")})");
            string failure = DescribeBatchFailure(
                ex,
                _downloadBatchProgress,
                "SftpErrorDownloadFailedAfterOne",
                "SftpErrorDownloadFailedAfter");
            SetErrorStatus(failure);
            return (SftpTransferJobStatus.Failed, failure);
        }
    }

    /// <summary>The size and date of a remote file against the local file it would replace.</summary>
    private static FileConflictComparison? DescribeDownloadCollision(RemoteDownloadOp op)
    {
        if (op.Kind != RemoteDownloadOpKind.DownloadFile)
        {
            return null;
        }

        try
        {
            FileInfo existing = new(op.LocalPath);
            return existing.Exists
                ? new FileConflictComparison(
                    new FileConflictSideInfo(op.Size, ToUtc(op.LastModified)),
                    new FileConflictSideInfo(existing.Length, existing.LastWriteTimeUtc))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A server time as UTC: the listings report UTC, so an unspecified kind is taken as UTC.</summary>
    internal static DateTime ToUtc(DateTime serverTime)
        => serverTime.Kind == DateTimeKind.Local
            ? serverTime.ToUniversalTime()
            : DateTime.SpecifyKind(serverTime, DateTimeKind.Utc);

    private void ReportDownloadScan(int plannedEntries)
    {
        if (plannedEntries % ScanReportEveryEntries == 0)
        {
            TransferStatusText = LF("SftpStatusTransferScanning", plannedEntries);
        }
    }

    private static string? ResolveContainedLocalChild(string parentLocalDirectory, string childName)
        => LocalDownloadPath.TryResolveContained(parentLocalDirectory, childName, out string localPath)
            ? localPath
            : null;

    /// <summary>
    /// Lists a remote folder for the download walk, falling back to a privileged listing when the
    /// account is refused, exactly as browsing into that folder would.
    /// </summary>
    private async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryWithSudoFallbackAsync(
        IRemoteBrowser browser,
        string path,
        CancellationToken ct)
    {
        try
        {
            return await browser.ListDirectoryAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_sshParams is not null && IsPermissionDenied(ex))
        {
            Core.Logging.FileLogger.Info(
                $"EmbeddedSFTP download listing permission denied, falling back to sudo for {path}");
            return await ListDirectoryViaSudoAsync(path, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Disconnect while transferring
    // ------------------------------------------------------------------

    /// <summary>
    /// Asks before a manual disconnect that would cut a running transfer. Answers yes at once when
    /// nothing is running, or when no dialog can be shown.
    /// </summary>
    public async Task<bool> ConfirmDisconnectAsync()
    {
        if (!IsTransferInProgress || _dialogService is null)
        {
            return true;
        }

        return await _dialogService.ShowConfirmAsync(
            L10n("SftpConfirmDisconnectTransferTitle"),
            L10n("SftpConfirmDisconnectTransferMessage"),
            "warning");
    }

    // ------------------------------------------------------------------
    // Move by drag
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves entries into a folder of this server, as a drag onto a folder row does.
    /// </summary>
    /// <remarks>
    /// The destination names are chosen exactly as a paste chooses them: a name already in use gets
    /// a "(copy)" suffix, so a drag never replaces an existing entry. An entry that is already in
    /// the target folder, or a folder dropped into itself or into its own subtree, is left where it
    /// is. Takes the transfer slot like any other operation that changes the server.
    /// </remarks>
    public async Task MoveEntriesAsync(IReadOnlyList<SftpFileInfo> entries, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (_disposed || _browser is null || entries.Count == 0)
        {
            return;
        }

        TransferStartState startState = TryBeginTransfer(out CancellationTokenSource? moveCts);
        if (startState == TransferStartState.Busy)
        {
            UpdateStatus(L10n("SftpTransferInProgress"));
            return;
        }

        if (startState != TransferStartState.Started || moveCts is null)
        {
            return;
        }

        CancellationToken ct = moveCts.Token;
        int moved = 0;
        try
        {
            IsTransferIndeterminate = true;
            HashSet<string> existingNames = await ReadLiveDestinationNamesAsync(targetDirectory, ct)
                .ConfigureAwait(false);

            int position = 0;
            foreach (SftpFileInfo entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                position++;
                if (!CanMoveInto(entry, targetDirectory))
                {
                    continue;
                }

                TransferStatusText = LF("SftpStatusMoving", entry.Name, position, entries.Count);
                string targetName = BuildNonCollidingName(existingNames, entry.Name);
                await _browser.RenameAsync(entry.FullPath, CombineRemotePath(targetDirectory, targetName), ct)
                    .ConfigureAwait(false);
                existingNames.Add(targetName);
                moved++;
            }

            await Refresh().ConfigureAwait(false);
            await RunOnUiAsync(() => UpdateStatus(moved == 0
                ? L10n("SftpStatusMoveNothing")
                : LFC(moved, "SftpStatusMoveCompleteOne", "SftpStatusMoveComplete", moved))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Refresh().ConfigureAwait(false);
            await RunOnUiAsync(() => UpdateStatus(L10n("SftpStatusTransferCancelled"))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP drag move failed [{ex.GetType().Name}]: {ex.Message}");
            await Refresh().ConfigureAwait(false);
            await RunOnUiAsync(() => SetTransferError(ex)).ConfigureAwait(false);
        }
        finally
        {
            CompleteTransfer(moveCts);
        }
    }

    /// <summary>
    /// Whether an entry can be moved into a folder: it is a file or a folder, it is not already
    /// there, and a folder is not asked to move into itself or into its own subtree.
    /// </summary>
    public static bool CanMoveInto(SftpFileInfo entry, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (entry.Kind is not (RemoteEntryKind.File or RemoteEntryKind.Directory))
        {
            return false;
        }

        string target = targetDirectory.TrimEnd('/');
        string source = entry.FullPath.TrimEnd('/');
        if (string.Equals(GetParentPath(source), target.Length == 0 ? "/" : target, StringComparison.Ordinal))
        {
            return false;
        }

        return !entry.IsDirectory
            || !(string.Equals(target, source, StringComparison.Ordinal)
                || target.StartsWith(source + "/", StringComparison.Ordinal));
    }

    /// <summary>Whether the failure is the local disk refusing or running out of room.</summary>
    internal static bool IsLocalDiskFull(Exception ex)
        => ex is IOException io && (io.HResult & Win32ErrorMask) is ErrorDiskFull or ErrorHandleDiskFull;
}
