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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Sftp;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// The transfer side of the browser: a folder downloads with its content, batches queue behind one
/// another and can be cancelled or retried, the bar measures the batch rather than the file, a
/// deletion is visible and cancellable, and a drag can move entries.
/// </summary>
public sealed class EmbeddedSftpTransferQueueTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------
    // Recursive download
    // ------------------------------------------------------------------

    [Fact]
    public async Task Download_AFolder_FetchesTheWholeTreeAndLeavesEmptyFoldersBehind()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory(
            "/srv/proj",
            SftpTestKit.File("/srv/proj/a.txt", 10),
            SftpTestKit.Directory("/srv/proj/sub"),
            SftpTestKit.Directory("/srv/proj/empty"));
        browser.AddDirectory("/srv/proj/sub", SftpTestKit.File("/srv/proj/sub/b.txt", 20));
        browser.AddDirectory("/srv/proj/empty");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/proj")], scratch.Path);

        Assert.Equal(
            [
                ("/srv/proj/a.txt", Path.Combine(scratch.Path, "proj", "a.txt")),
                ("/srv/proj/sub/b.txt", Path.Combine(scratch.Path, "proj", "sub", "b.txt")),
            ],
            browser.Downloads.Select(call => (call.Remote, call.Local)));
        Assert.True(Directory.Exists(Path.Combine(scratch.Path, "proj", "empty")));
        Assert.True(File.Exists(Path.Combine(scratch.Path, "proj", "sub", "b.txt")));
        Assert.False(viewModel.IsTransferInProgress);
        Assert.Empty(viewModel.TransferJobs);
    }

    [Fact]
    public async Task Download_ALinkInsideAFolder_IsReportedAndNeverFollowed()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory(
            "/srv/proj",
            SftpTestKit.Link("/srv/proj/loop"),
            SftpTestKit.File("/srv/proj/ok.txt", 1));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/proj")], scratch.Path);

        Assert.Single(browser.Downloads);
        Assert.DoesNotContain("/srv/proj/loop", browser.Listings);
        Assert.Equal(localizer.Format("WarnRemoteEntriesSkippedUnsupported", 1), viewModel.StatusText);
    }

    [Fact]
    public async Task Download_AnExistingFileInsideTheTree_ShowsOneConflictAndTheChoiceIsHonoured()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        Directory.CreateDirectory(Path.Combine(scratch.Path, "proj"));
        await File.WriteAllTextAsync(Path.Combine(scratch.Path, "proj", "a.txt"), "mine");
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory(
            "/srv/proj",
            SftpTestKit.File("/srv/proj/a.txt", 1),
            SftpTestKit.File("/srv/proj/b.txt", 1));
        ScriptedConflictPresenter presenter = new(dialog => new FileConflictDialogResult(
        [
            new FileConflictDecision(dialog.Rows.Single().ItemIndex, FileConflictResolutionChoice.Skip),
        ]));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/proj")], scratch.Path);

        // The folder itself merges silently; only the colliding file asks, and Skip keeps it.
        Assert.Equal(1, presenter.CallCount);
        Assert.Equal(["/srv/proj/b.txt"], browser.Downloads.Select(call => call.Remote));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(scratch.Path, "proj", "a.txt")));
    }

    [Fact]
    public async Task Download_ANameThatCannotBeSavedSafely_IsSkippedAndCounted()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        await viewModel.DownloadFilesAsync(
            [SftpTestKit.Entry("/srv/a/b", RemoteEntryKind.File, 1, "rw-r--r--") with { Name = "a/b" }],
            scratch.Path);

        Assert.Empty(browser.Downloads);
        Assert.Equal(
            localizer.Format("WarnRemoteNamesSkippedUnsafeOne", 1),
            viewModel.StatusText);
    }

    [Fact]
    public async Task Download_ListingTheFolderIsRefused_FailsTheJobAndKeepsItForRetry()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.ListingFailure = _ => new InvalidOperationException("boom");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/proj")], scratch.Path);

        SftpTransferJob job = Assert.Single(viewModel.TransferJobs);
        Assert.Equal(SftpTransferJobStatus.Failed, job.Status);
        Assert.True(viewModel.IsErrorStatus);
        Assert.True(viewModel.HasFinishedTransferJobs);
        Assert.False(viewModel.IsTransferInProgress);
    }

    // ------------------------------------------------------------------
    // Progress of the batch
    // ------------------------------------------------------------------

    [Fact]
    public async Task Progress_RunsAcrossTheWholeBatchInsteadOfRestartingAtEachFile()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory(
            "/srv/d",
            SftpTestKit.File("/srv/d/a.bin", 1000),
            SftpTestKit.File("/srv/d/b.bin", 1000));
        List<TaskCompletionSource> started = [];
        List<TaskCompletionSource> release = [];
        for (int index = 0; index < 2; index++)
        {
            started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            release.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        int call = 0;
        browser.DownloadHandler = async (_, _, ct) =>
        {
            int mine = Interlocked.Increment(ref call) - 1;
            started[mine].TrySetResult();
            await release[mine].Task.WaitAsync(ct);
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        Task transfer = viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);
        await started[0].Task.WaitAsync(Patience);
        viewModel.UpdateTransferProgress(new SftpTransferProgress("a.bin", 500, 1000, false));
        double midFirst = viewModel.TransferProgressValue;
        Assert.Contains("(1/2)", viewModel.TransferStatusText, StringComparison.Ordinal);

        release[0].SetResult();
        await started[1].Task.WaitAsync(Patience);
        viewModel.UpdateTransferProgress(new SftpTransferProgress("b.bin", 250, 1000, false));
        double midSecond = viewModel.TransferProgressValue;
        Assert.Contains("(2/2)", viewModel.TransferStatusText, StringComparison.Ordinal);

        release[1].SetResult();
        await transfer.WaitAsync(Patience);

        Assert.Equal(25.0, midFirst, precision: 3);
        Assert.Equal(62.5, midSecond, precision: 3);
        Assert.False(viewModel.IsTransferIndeterminate);
    }

    [Fact]
    public async Task Progress_WhilePlanningTheBatch_IsIndeterminateAndSaysItIsPreparing()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        TaskCompletionSource listingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseListing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 10));
        browser.ListingGate = async (_, ct) =>
        {
            listingStarted.TrySetResult();
            await releaseListing.Task.WaitAsync(ct);
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        Task transfer = viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);
        await listingStarted.Task.WaitAsync(Patience);

        Assert.True(viewModel.IsTransferInProgress);
        Assert.True(viewModel.IsTransferIndeterminate);
        Assert.Equal(localizer["SftpStatusTransferPreparing"], viewModel.TransferStatusText);

        releaseListing.SetResult();
        await transfer.WaitAsync(Patience);

        Assert.False(viewModel.IsTransferIndeterminate);
    }

    [Fact]
    public async Task Progress_ShowsASmoothedRateAndTheTimeLeftOnceThereIsSomethingToMeasure()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        FakeTimeProvider clock = new();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 100 * 1024));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.DownloadHandler = async (_, _, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);
        viewModel.ProgressClock = clock;

        Task transfer = viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);
        await started.Task.WaitAsync(Patience);
        viewModel.UpdateTransferProgress(new SftpTransferProgress("a.bin", 0, 100 * 1024, false));
        Assert.DoesNotContain("/s", viewModel.TransferStatusText, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(1));
        viewModel.UpdateTransferProgress(new SftpTransferProgress("a.bin", 10 * 1024, 100 * 1024, false));
        clock.Advance(TimeSpan.FromSeconds(1));
        viewModel.UpdateTransferProgress(new SftpTransferProgress("a.bin", 20 * 1024, 100 * 1024, false));

        Assert.Contains(
            EmbeddedSftpViewModel.FormatSize(10 * 1024) + "/s",
            viewModel.TransferStatusText,
            StringComparison.Ordinal);
        Assert.Contains(viewModel.FormatRemaining(TimeSpan.FromSeconds(8)), viewModel.TransferStatusText, StringComparison.Ordinal);

        release.SetResult();
        await transfer.WaitAsync(Patience);
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(65, "1 min 5 s")]
    [InlineData(3661, "1 h 1 min")]
    public async Task FormatRemaining_WritesAShortLocalizedDuration(int seconds, string expected)
    {
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(
            localizer: await SftpTestKit.LoadLocalizerAsync());

        Assert.Equal(expected, viewModel.FormatRemaining(TimeSpan.FromSeconds(seconds)));
    }

    // ------------------------------------------------------------------
    // Failure messages
    // ------------------------------------------------------------------

    [Fact]
    public async Task Download_FailsAfterOneFile_SaysHowManyWereFetched()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 1), SftpTestKit.File("/srv/d/b.bin", 1));
        browser.DownloadHandler = (remote, _, _) => remote.EndsWith("b.bin", StringComparison.Ordinal)
            ? Task.FromException(new InvalidOperationException("disk"))
            : Task.CompletedTask;
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);

        Assert.True(viewModel.IsErrorStatus);
        Assert.StartsWith(
            localizer.Format("SftpErrorDownloadFailedAfterOne", 1, 2, string.Empty).TrimEnd(),
            viewModel.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_TheConnectionDropsMidBatch_SaysTheConnectionWasLost()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 1), SftpTestKit.File("/srv/d/b.bin", 1));
        browser.DownloadHandler = (remote, _, _) =>
        {
            if (!remote.EndsWith("b.bin", StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            browser.IsConnected = false;
            return Task.FromException(new IOException("reset"));
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);

        Assert.Equal(localizer.Format("SftpErrorConnectionLostDuringTransfer", 1, 2), viewModel.StatusText);
        Assert.Equal(viewModel.StatusText, Assert.Single(viewModel.TransferJobs).Detail);
    }

    [Fact]
    public void DescribeTransferError_NamesTheLocalDiskInsteadOfTransferFailed()
    {
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel();

        Assert.Equal("SftpErrorLocalAccessDenied", viewModel.DescribeTransferError(new UnauthorizedAccessException()));
        Assert.Equal(
            "SftpErrorLocalDiskFull",
            viewModel.DescribeTransferError(new IOException("full") { HResult = unchecked((int)0x80070070) }));
        Assert.Equal("SftpStatusTransferFailed", viewModel.DescribeTransferError(new IOException("other")));
    }

    // ------------------------------------------------------------------
    // The queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task Queue_BatchesRunInOrderAndTheSecondOneIsListed()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = Path.Combine(scratch.Path, "f.txt");
        await File.WriteAllTextAsync(file, "x");
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.UploadHandler = async (_, remote, ct) =>
        {
            if (remote == "/one/f.txt")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        Task first = viewModel.UploadEntriesAsync([file], "/one");
        await firstStarted.Task.WaitAsync(Patience);
        Task second = viewModel.UploadEntriesAsync([file], "/two");

        Assert.Equal(
            [SftpTransferJobStatus.Running, SftpTransferJobStatus.Queued],
            viewModel.TransferJobs.Select(job => job.Status));
        Assert.True(viewModel.ShowTransferPanel);

        releaseFirst.SetResult();
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);

        Assert.Equal(["/one/f.txt", "/two/f.txt"], browser.Uploads.Select(call => call.Remote));
        Assert.Empty(viewModel.TransferJobs);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Fact]
    public async Task Queue_ACancelledQueuedBatchNeverStartsAndCanBeRetried()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = Path.Combine(scratch.Path, "f.txt");
        await File.WriteAllTextAsync(file, "x");
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.UploadHandler = async (_, remote, ct) =>
        {
            if (remote == "/one/f.txt")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        Task first = viewModel.UploadEntriesAsync([file], "/one");
        await firstStarted.Task.WaitAsync(Patience);
        Task second = viewModel.UploadEntriesAsync([file], "/two");
        SftpTransferJob queued = viewModel.TransferJobs[1];

        viewModel.CancelJobCommand.Execute(queued);

        Assert.Equal(SftpTransferJobStatus.Cancelled, queued.Status);
        Assert.True(queued.CanRetry);
        Assert.True(viewModel.HasFinishedTransferJobs);

        releaseFirst.SetResult();
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);
        Assert.Equal(["/one/f.txt"], browser.Uploads.Select(call => call.Remote));

        await viewModel.RetryJobCommand.ExecuteAsync(queued);

        Assert.Equal(["/one/f.txt", "/two/f.txt"], browser.Uploads.Select(call => call.Remote));
        Assert.Empty(viewModel.TransferJobs);
    }

    [Fact]
    public async Task Queue_ACancelledRunningBatchIsMarkedCancelledAndTheNextOneStillRuns()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = Path.Combine(scratch.Path, "f.txt");
        await File.WriteAllTextAsync(file, "x");
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.UploadHandler = async (_, remote, ct) =>
        {
            if (remote == "/one/f.txt")
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        Task first = viewModel.UploadEntriesAsync([file], "/one");
        await firstStarted.Task.WaitAsync(Patience);
        Task second = viewModel.UploadEntriesAsync([file], "/two");
        SftpTransferJob running = viewModel.TransferJobs[0];

        viewModel.CancelJobCommand.Execute(running);
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);

        Assert.Equal(SftpTransferJobStatus.Cancelled, running.Status);
        Assert.Equal(["/one/f.txt", "/two/f.txt"], browser.Uploads.Select(call => call.Remote));
        Assert.Equal([running], viewModel.TransferJobs);
    }

    [Fact]
    public async Task Queue_AFailedBatchStaysListedWithItsReasonAndRunsAgainOnRetry()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 1));
        bool fail = true;
        browser.DownloadHandler = (_, _, _) => fail
            ? Task.FromException(new InvalidOperationException("network"))
            : Task.CompletedTask;
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);
        SftpTransferJob job = Assert.Single(viewModel.TransferJobs);

        Assert.Equal(SftpTransferJobStatus.Failed, job.Status);
        Assert.False(string.IsNullOrEmpty(job.Detail));
        Assert.True(job.CanRetry);
        Assert.False(job.CanCancel);

        fail = false;
        await viewModel.RetryJobCommand.ExecuteAsync(job);

        Assert.Equal(SftpTransferJobStatus.Completed, job.Status);
        Assert.Empty(viewModel.TransferJobs);
        Assert.True(File.Exists(Path.Combine(scratch.Path, "d", "a.bin")));
    }

    [Fact]
    public async Task Queue_ClearFinishedRemovesTheBatchesThatDidNotComplete()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        ScriptedRemoteBrowser browser = new();
        browser.ListingFailure = _ => new InvalidOperationException("boom");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);
        Assert.Single(viewModel.TransferJobs);

        viewModel.ClearFinishedJobsCommand.Execute(null);

        Assert.Empty(viewModel.TransferJobs);
        Assert.False(viewModel.HasTransferJobs);
        Assert.False(viewModel.HasFinishedTransferJobs);
        Assert.False(viewModel.ShowTransferPanel);
    }

    [Fact]
    public async Task Queue_ABatchParkedBehindAnotherOperationRunsWhenThatOperationCompletes()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = Path.Combine(scratch.Path, "f.txt");
        await File.WriteAllTextAsync(file, "x");
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        viewModel.IsTransferInProgress = true;

        await viewModel.UploadEntriesAsync([file], "/srv");
        Assert.Empty(browser.Uploads);

        viewModel.IsTransferInProgress = false;
        viewModel.ResumeTransferQueue();
        await Task.WhenAll(viewModel.TransferJobs.Select(job => job.Completion.WaitAsync(Patience)));

        Assert.Equal(["/srv/f.txt"], browser.Uploads.Select(call => call.Remote));
    }

    [Fact]
    public async Task Queue_DisposingThePaneCancelsWhatWaits()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = Path.Combine(scratch.Path, "f.txt");
        await File.WriteAllTextAsync(file, "x");
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        viewModel.IsTransferInProgress = true;
        await viewModel.UploadEntriesAsync([file], "/srv");
        SftpTransferJob parked = Assert.Single(viewModel.TransferJobs);

        viewModel.MarkDisposed();

        Assert.Equal(SftpTransferJobStatus.Cancelled, parked.Status);
        await parked.Completion.WaitAsync(Patience);
    }

    // ------------------------------------------------------------------
    // Upload progress
    // ------------------------------------------------------------------

    [Fact]
    public async Task Upload_ProgressCountsTheBytesOfTheWholeBatch()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        using SftpTestKit.ScratchFolder scratch = new();
        string small = Path.Combine(scratch.Path, "small.bin");
        string large = Path.Combine(scratch.Path, "large.bin");
        await File.WriteAllBytesAsync(small, new byte[1000]);
        await File.WriteAllBytesAsync(large, new byte[3000]);
        TaskCompletionSource smallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSmall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.UploadHandler = async (_, remote, ct) =>
        {
            if (remote.EndsWith("small.bin", StringComparison.Ordinal))
            {
                smallStarted.TrySetResult();
                await releaseSmall.Task.WaitAsync(ct);
            }
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer);

        Task transfer = viewModel.UploadEntriesAsync([small, large], "/srv");
        await smallStarted.Task.WaitAsync(Patience);
        viewModel.UpdateTransferProgress(new SftpTransferProgress("small.bin", 500, 1000, true));

        // 500 of 4000 bytes, not 50 percent of the current file.
        Assert.Equal(12.5, viewModel.TransferProgressValue, precision: 3);
        Assert.Contains("(1/2)", viewModel.TransferStatusText, StringComparison.Ordinal);

        releaseSmall.SetResult();
        await transfer.WaitAsync(Patience);
    }

    // ------------------------------------------------------------------
    // Deleting
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_ShowsItsProgressHoldsTheTransferSlotAndCanBeCancelled()
    {
        LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync();
        (IDialogService dialog, ScriptedDialogProxy _) = ScriptedDialogProxy.Create();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedRemoteBrowser browser = new();
        browser.DeleteHandler = async (path, ct) =>
        {
            if (path == "/d/big")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, localizer: localizer, dialogService: dialog);

        Task deletion = viewModel.DeleteEntriesAsync(
        [
            SftpTestKit.File("/d/a.txt"),
            SftpTestKit.Directory("/d/big"),
            SftpTestKit.File("/d/c.txt"),
        ]);
        await started.Task.WaitAsync(Patience);

        // Visible, and counted by the close guard, which only reads the transfer slot.
        Assert.True(viewModel.IsTransferInProgress);
        Assert.True(viewModel.SampleTransferState().IsTransferInProgress);
        Assert.Equal(localizer.Format("SftpStatusDeleting", "big", 2, 3), viewModel.TransferStatusText);
        Assert.True(viewModel.IsTransferIndeterminate);

        viewModel.CancelTransferCommand.Execute(null);
        await deletion.WaitAsync(Patience);

        Assert.Equal(["/d/a.txt", "/d/big"], browser.Deletes);
        Assert.Equal(localizer.Format("SftpStatusDeleteCancelled", 1, 3), viewModel.StatusText);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Fact]
    public async Task Delete_WhileATransferRuns_IsRefusedBeforeAnyQuestionIsAsked()
    {
        (IDialogService dialog, ScriptedDialogProxy script) = ScriptedDialogProxy.Create();
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, dialogService: dialog);
        viewModel.IsTransferInProgress = true;

        await viewModel.DeleteEntriesAsync([SftpTestKit.File("/d/a.txt")]);

        Assert.Empty(script.Confirmations);
        Assert.Empty(browser.Deletes);
        Assert.Equal("SftpTransferInProgress", viewModel.StatusText);
    }

    // ------------------------------------------------------------------
    // Drag to move
    // ------------------------------------------------------------------

    [Fact]
    public async Task Move_PutsEntriesInTheFolderAndNeverReplacesAnExistingName()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d", SftpTestKit.File("/d/a.txt"), SftpTestKit.File("/d/b.txt"), SftpTestKit.Directory("/d/sub"));
        browser.AddDirectory("/d/sub", SftpTestKit.File("/d/sub/b.txt"));
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        await viewModel.MoveEntriesAsync(
            [SftpTestKit.File("/d/a.txt"), SftpTestKit.File("/d/b.txt")],
            "/d/sub");

        Assert.Equal(
            [("/d/a.txt", "/d/sub/a.txt"), ("/d/b.txt", "/d/sub/b (copy).txt")],
            browser.Renames);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Fact]
    public async Task Move_AFolderIntoItselfOrAnEntryIntoItsOwnFolder_MovesNothingAndSaysSo()
    {
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/d/sub");
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);

        await viewModel.MoveEntriesAsync(
            [SftpTestKit.Directory("/d/sub"), SftpTestKit.File("/d/sub/a.txt")],
            "/d/sub");

        Assert.Empty(browser.Renames);
        Assert.Equal("SftpStatusMoveNothing", viewModel.StatusText);
    }

    [Fact]
    public async Task Move_WhileATransferRuns_IsRefusedWithoutTouchingTheServer()
    {
        ScriptedRemoteBrowser browser = new();
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser);
        viewModel.IsTransferInProgress = true;

        await viewModel.MoveEntriesAsync([SftpTestKit.File("/d/a.txt")], "/d/sub");

        Assert.Empty(browser.Renames);
        Assert.Empty(browser.Listings);
        Assert.Equal("SftpTransferInProgress", viewModel.StatusText);
    }

    [Theory]
    [InlineData("/d/a.txt", "/d/sub", RemoteEntryKind.File, true)]
    [InlineData("/d/a.txt", "/d", RemoteEntryKind.File, false)]
    [InlineData("/d/a.txt", "/", RemoteEntryKind.File, true)]
    [InlineData("/a.txt", "/", RemoteEntryKind.File, false)]
    [InlineData("/d/sub", "/d/sub", RemoteEntryKind.Directory, false)]
    [InlineData("/d/sub", "/d/sub/deeper", RemoteEntryKind.Directory, false)]
    [InlineData("/d/sub", "/d/subway", RemoteEntryKind.Directory, true)]
    [InlineData("/d/link", "/d/sub", RemoteEntryKind.SymbolicLink, false)]
    public void CanMoveInto_RefusesNoOpsLoopsAndSpecialEntries(
        string source,
        string target,
        RemoteEntryKind kind,
        bool expected)
    {
        SftpFileInfo entry = SftpTestKit.Entry(source, kind, 0, "rwxr-xr-x");

        Assert.Equal(expected, EmbeddedSftpViewModel.CanMoveInto(entry, target));
    }

    // ------------------------------------------------------------------
    // Disconnecting
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfirmDisconnect_AsksOnlyWhileATransferRuns()
    {
        (IDialogService dialog, ScriptedDialogProxy script) = ScriptedDialogProxy.Create();
        script.Confirm = (_, _) => false;
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(dialogService: dialog);

        Assert.True(await viewModel.ConfirmDisconnectAsync());
        Assert.Empty(script.Confirmations);

        viewModel.IsTransferInProgress = true;

        Assert.False(await viewModel.ConfirmDisconnectAsync());
        Assert.Equal(
            ("SftpConfirmDisconnectTransferTitle", "SftpConfirmDisconnectTransferMessage"),
            Assert.Single(script.Confirmations));
    }
}
