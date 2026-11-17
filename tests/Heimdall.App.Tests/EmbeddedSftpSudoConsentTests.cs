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
using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// Every place the pane retries a refused operation as root asks the one gate first: Never never
/// calls sudo, Ask asks once per user action and declines by default, Automatic asks only for a
/// delete, and an unwired pane behaves as it did before the setting existed.
/// </summary>
/// <remarks>
/// No server is involved: a sudo call that is allowed fails at once on the missing host-key store,
/// which the root activity log records, so "root was tried" is read from that log (or from the
/// scripted sudo channels the pane already exposes to tests).
/// </remarks>
public sealed class EmbeddedSftpSudoConsentTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // --- Rename: the gate stands before the probes --------------------------------------------

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, 0, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, 1, 0)]
    public async Task Rename_RefusedAndNotRunAsRoot_StopsBeforeTheProbes(
        SftpSudoFallbackMode mode,
        int expectedQuestions,
        int expectedProbes)
    {
        Harness harness = await Harness.CreateAsync(mode, answer: false);
        harness.Scripted.RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "new.txt";

        await harness.ViewModel.RenameEntryAsync(File("old.txt", "/srv/old.txt"));

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        Assert.Equal(expectedProbes, harness.SudoRenameCommands.Count);
        Assert.True(harness.ViewModel.IsErrorStatus);
        Assert.Contains(harness.Localizer[ExpectedNoteKey(mode)], harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Ask, 1)]
    [InlineData(SftpSudoFallbackMode.Automatic, 0)]
    [InlineData(null, 0)]
    public async Task Rename_RefusedAndAllowed_RunsTheProbesAndTheMove(SftpSudoFallbackMode? mode, int expectedQuestions)
    {
        Harness harness = await Harness.CreateAsync(mode, answer: true);
        harness.Scripted.RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "new.txt";

        await harness.ViewModel.RenameEntryAsync(File("old.txt", "/srv/old.txt"));

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        Assert.Contains(
            PrivilegedFileCommands.BuildHeldRenameBody("/srv/old.txt", "new.txt", replace: false),
            harness.SudoRenameCommands);
        Assert.Equal(1, harness.ViewModel.RootActivity.Snapshot().OperationCount);
    }

    [Fact]
    public async Task Rename_TheQuestionNamesTheActAndThePathAndDeclinesByDefault()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "new.txt";

        await harness.ViewModel.RenameEntryAsync(File("old.txt", "/srv/old.txt"));

        LabeledQuestion question = Assert.Single(harness.Dialog.Questions);
        Assert.Equal("danger", question.Severity);
        Assert.Equal(harness.Localizer["SftpConfirmSudoRenameTitle"], question.Title);
        Assert.Equal(harness.Localizer.Format("SftpConfirmSudoRename", "/srv/old.txt"), question.Message);
        Assert.Equal(harness.Localizer["SftpConfirmSudoRenameAccept"], question.Accept);
        Assert.Equal(harness.Localizer["SftpConfirmSudoRenameDecline"], question.Decline);
        Assert.NotEqual(question.Accept, question.Decline);
        Assert.NotEqual("SftpConfirmSudoRenameAccept", question.Accept);
    }

    [Fact]
    public async Task TheQuestion_ShowsAForgedPathWithoutALineBreakOrABidiOverride()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "new.txt";
        char rightToLeftOverride = (char)0x202E;
        string forged = "/srv/a\nb" + rightToLeftOverride + "gpj.txt";

        await harness.ViewModel.RenameEntryAsync(File("a", forged));

        string shown = Assert.Single(harness.Dialog.Questions).Message;
        Assert.DoesNotContain('\n', shown);
        Assert.DoesNotContain(rightToLeftOverride, shown);
        Assert.Equal(
            harness.Localizer.Format("SftpConfirmSudoRename", ExecutionTrustDescriber.Visible(forged)),
            shown);
    }

    // --- Create folder ------------------------------------------------------------------------

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, true, 0, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, false, 1, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, true, 1, 1)]
    [InlineData(SftpSudoFallbackMode.Automatic, false, 0, 1)]
    [InlineData(null, false, 0, 1)]
    public async Task CreateFolder_FollowsTheGate(
        SftpSudoFallbackMode? mode,
        bool answer,
        int expectedQuestions,
        int expectedRootTries)
    {
        Harness harness = await Harness.CreateAsync(mode, answer);
        harness.UseRefusingMutations();
        harness.Dialog.InputAnswer = "newdir";

        await harness.ViewModel.CreateFolderAsync();

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        Assert.Equal(expectedRootTries, harness.ViewModel.RootActivity.Snapshot().OperationCount);
        if (expectedRootTries == 0)
        {
            Assert.Contains(
                harness.Localizer[ExpectedNoteKey(mode ?? SftpSudoFallbackMode.Automatic, answer)],
                harness.ViewModel.StatusText,
                StringComparison.Ordinal);
        }
    }

    // --- Chmod: one question per batch ---------------------------------------------------------

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, true, 0, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, false, 1, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, true, 1, 2)]
    [InlineData(SftpSudoFallbackMode.Automatic, false, 0, 2)]
    [InlineData(null, false, 0, 2)]
    public async Task Chmod_FollowsTheGate_AndAsksOncePerBatch(
        SftpSudoFallbackMode? mode,
        bool answer,
        int expectedQuestions,
        int expectedSudoCommands)
    {
        Harness harness = await Harness.CreateAsync(mode, answer);
        harness.UseRefusingMutations();
        harness.Dialog.InputAnswer = "640";

        await harness.ViewModel.ChmodEntriesAsync([File("a", "/srv/a"), File("b", "/srv/b")]);

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        Assert.Equal(expectedSudoCommands, harness.SudoChmodCommands.Count);
        if (expectedQuestions == 1)
        {
            Assert.Equal(harness.Localizer["SftpConfirmSudoChmodTitle"], harness.Dialog.Questions[0].Title);
        }
    }

    // --- Delete: always asks except in Never ---------------------------------------------------

    [Theory]
    [InlineData(SftpSudoFallbackMode.Ask)]
    [InlineData(SftpSudoFallbackMode.Automatic)]
    [InlineData(null)]
    public async Task Delete_AsksOnceForTheWholeBatchWhateverTheSetting(SftpSudoFallbackMode? mode)
    {
        Harness harness = await Harness.CreateAsync(mode, answer: true);
        harness.Scripted.DeleteHandler = (_, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.DeleteEntriesAsync([File("a", "/srv/a"), File("b", "/srv/b")]);

        LabeledQuestion question = Assert.Single(harness.Dialog.Questions);
        Assert.Equal(harness.Localizer["SftpConfirmSudoDeleteTitle"], question.Title);
        Assert.Equal(harness.Localizer["SftpConfirmSudoDeleteAccept"], question.Accept);
        Assert.Equal(harness.Localizer["SftpConfirmSudoDeleteDecline"], question.Decline);
        Assert.Equal(2, harness.SudoDeleteCommands.Count);
    }

    [Fact]
    public async Task Delete_InNever_IsRefusedThroughTheDeclinedPathWithoutAnyQuestion()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Never, answer: true);
        harness.Scripted.DeleteHandler = (_, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.DeleteEntriesAsync([File("a", "/srv/a")]);

        Assert.Empty(harness.Dialog.Questions);
        Assert.Empty(harness.SudoDeleteCommands);
        Assert.Equal(
            harness.Localizer.Format("SftpDeleteRefusedPermissionDenied", "a"),
            harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Delete_ADeclinedBatch_RunsNothingAsRoot()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.DeleteHandler = (_, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.DeleteEntriesAsync([File("a", "/srv/a"), File("b", "/srv/b")]);

        Assert.Single(harness.Dialog.Questions);
        Assert.Empty(harness.SudoDeleteCommands);
    }

    // --- Listing -------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_AYesToTheQuestionArmsThePaneAndLaterListingsDoNotAsk()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.ListingFailure = path => path == "/root" ? new SftpPermissionDeniedException("denied") : null;

        await harness.ViewModel.NavigateToPath("/root");

        LabeledQuestion question = Assert.Single(harness.Dialog.Questions);
        Assert.Equal(harness.Localizer["SftpConfirmSudoListingTitle"], question.Title);
        Assert.True(harness.ViewModel.SudoMode);
        Assert.Equal(1, harness.ViewModel.RootActivity.Snapshot().ListingCount);

        // The pane is armed: the next folder lists as root without a second question and without
        // asking the server first.
        int listingsBefore = harness.Scripted.Listings.Count;
        await harness.ViewModel.NavigateToPath("/etc");

        Assert.Single(harness.Dialog.Questions);
        Assert.Equal(listingsBefore, harness.Scripted.Listings.Count);
        Assert.Equal(2, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Fact]
    public async Task Listing_ANoLeavesThePaneUnarmedAndSaysRootWasNotRun()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");

        string pathBefore = harness.ViewModel.CurrentPath;
        bool canGoBackBefore = harness.ViewModel.CanGoBack;

        await harness.ViewModel.NavigateToPath("/root");

        Assert.Single(harness.Dialog.Questions);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.Equal(pathBefore, harness.ViewModel.CurrentPath);
        Assert.Equal(canGoBackBefore, harness.ViewModel.CanGoBack);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().ListingCount);
        Assert.Contains(harness.Localizer[SftpSudoConsentLocaleKeys.NoteDeclined], harness.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.True(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public async Task Listing_FollowingTheSshDirectory_NeverAsksAndSaysWhy()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.NavigateToUntrustedPath("/root");

        Assert.Empty(harness.Dialog.Questions);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().ListingCount);
        Assert.Contains(harness.Localizer[SftpSudoConsentLocaleKeys.NoteNotAsked], harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_TheFirstFolderOfAPane_NeverAsks()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.NavigateInitialAsync("/root");

        Assert.Empty(harness.Dialog.Questions);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Fact]
    public async Task Listing_ARefreshAfterAnOperation_NeverAsks()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.UseRefusingMutations(allowMkdir: true);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "newdir";

        await harness.ViewModel.CreateFolderAsync();

        Assert.Empty(harness.Dialog.Questions);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Fact]
    public async Task Listing_TheRefreshTheUserAsksFor_Asks()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");

        await harness.ViewModel.Refresh();

        Assert.Single(harness.Dialog.Questions);
    }

    [Fact]
    public async Task Listing_InNever_IgnoresASudoButtonLeftOn()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Never, answer: true);
        harness.ViewModel.SudoMode = true;

        await harness.ViewModel.NavigateToPath("/srv");

        Assert.Contains("/srv", harness.Scripted.Listings);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().ListingCount);
        Assert.False(harness.ViewModel.IsListingAsRoot);
        Assert.Empty(harness.Dialog.Questions);
    }

    [Fact]
    public async Task SudoButton_InNever_IsUnavailableWithATooltipAndCannotBeArmed()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Never, answer: true);

        await harness.ViewModel.ToggleSudoMode();

        Assert.False(harness.ViewModel.IsSudoModeAvailable);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.Equal(harness.Localizer[SftpSudoConsentLocaleKeys.ModeNeverTooltip], harness.ViewModel.SudoModeTooltip);
        Assert.NotEqual(SftpSudoConsentLocaleKeys.ModeNeverTooltip, harness.ViewModel.SudoModeTooltip);
    }

    [Fact]
    public async Task SudoButton_WhenTheSettingMovesToNever_IsSwitchedOffAndStaysOffWhenTheSettingReturns()
    {
        SftpSudoFallbackMode mode = SftpSudoFallbackMode.Ask;
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.ViewModel.SetSudoFallbackModeProvider(() => mode);
        await harness.ViewModel.ToggleSudoMode();
        Assert.True(harness.ViewModel.SudoMode);
        List<string?> raised = [];
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        mode = SftpSudoFallbackMode.Never;
        harness.ViewModel.RefreshSudoModeAvailability();

        Assert.Contains(nameof(EmbeddedSftpViewModel.IsSudoModeAvailable), raised);
        Assert.False(harness.ViewModel.IsSudoModeAvailable);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.False(harness.ViewModel.IsListingAsRoot);
        Assert.Equal(harness.Localizer["SftpSudoModeDisabled"], harness.ViewModel.StatusText);

        mode = SftpSudoFallbackMode.Ask;
        harness.ViewModel.RefreshSudoModeAvailability();

        Assert.True(harness.ViewModel.IsSudoModeAvailable);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.False(harness.ViewModel.IsListingAsRoot);
    }

    [Fact]
    public async Task SudoButton_TurnedOff_ListsAgainWithoutAskingAndStaysOff()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        await harness.ViewModel.ToggleSudoMode();
        Assert.True(harness.ViewModel.SudoMode);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");
        harness.Dialog.Questions.Clear();
        int listingsAsRoot = harness.ViewModel.RootActivity.Snapshot().ListingCount;

        await harness.ViewModel.ToggleSudoMode();

        Assert.Empty(harness.Dialog.Questions);
        Assert.False(harness.ViewModel.SudoMode);
        Assert.Equal(listingsAsRoot, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Ask)]
    [InlineData(SftpSudoFallbackMode.Automatic)]
    public async Task SudoButton_OutsideNever_IsAvailableAndArmsTheListingOnly(SftpSudoFallbackMode mode)
    {
        Harness harness = await Harness.CreateAsync(mode, answer: false);

        await harness.ViewModel.ToggleSudoMode();

        Assert.True(harness.ViewModel.IsSudoModeAvailable);
        Assert.True(harness.ViewModel.SudoMode);
        Assert.Equal(harness.Localizer[SftpSudoConsentLocaleKeys.ModeTooltip], harness.ViewModel.SudoModeTooltip);

        // Armed, a write is still asked about.
        harness.Scripted.RenameHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        harness.Dialog.InputAnswer = "new.txt";
        await harness.ViewModel.RenameEntryAsync(File("old.txt", "/srv/old.txt"));
        Assert.Equal(mode == SftpSudoFallbackMode.Ask ? 1 : 0, harness.Dialog.Questions.Count);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Ask, true)]
    [InlineData(SftpSudoFallbackMode.Automatic, true)]
    [InlineData(SftpSudoFallbackMode.Never, false)]
    public async Task TheRefreshAfterAStoppedBatch_IsSkippedOnlyWhenThePaneReallyListsAsRoot(
        SftpSudoFallbackMode mode,
        bool expectedSkip)
    {
        Harness harness = await Harness.CreateAsync(mode, answer: true);
        harness.ViewModel.SudoMode = true;
        SudoBatchGuard guard = new();
        guard.Record(new SudoAuthenticationException(SudoFailureKind.PasswordRejected, "raw"));

        Assert.Equal(expectedSkip, harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));
    }

    [Fact]
    public async Task TheRefreshAfterAStoppedBatch_FollowsAPaneArmedByAYesToo()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");
        SudoBatchGuard guard = new();
        guard.Record(new SudoAuthenticationException(SudoFailureKind.PasswordRejected, "raw"));
        Assert.False(harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));

        await harness.ViewModel.NavigateToPath("/root");

        Assert.True(harness.ViewModel.SudoMode);
        Assert.True(harness.ViewModel.ShouldSkipRefreshAfterStoppedBatch(guard));
    }

    // --- Queued transfers: one question per job ------------------------------------------------

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, true, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, false, 1)]
    [InlineData(SftpSudoFallbackMode.Ask, true, 1)]
    public async Task UploadJob_AsksOncePerJobAndNamesTheJob(SftpSudoFallbackMode mode, bool answer, int expectedQuestions)
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string first = await WriteAsync(scratch, "one.txt");
        string second = await WriteAsync(scratch, "two.txt");
        Harness harness = await Harness.CreateAsync(mode, answer);
        harness.Scripted.UploadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.UploadEntriesAsync([first, second], "/srv/in").WaitAsync(Patience);

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        if (expectedQuestions == 1)
        {
            LabeledQuestion question = harness.Dialog.Questions[0];
            Assert.Equal(harness.Localizer["SftpConfirmSudoUploadTitle"], question.Title);
            Assert.Equal(harness.Localizer["SftpConfirmSudoUploadAccept"], question.Accept);
            SftpTransferJob job = Assert.Single(harness.ViewModel.TransferJobs);
            Assert.Contains(job.Title, question.Message, StringComparison.Ordinal);
            Assert.Contains("/srv/in/one.txt", question.Message, StringComparison.Ordinal);
        }

        if (!answer || mode == SftpSudoFallbackMode.Never)
        {
            Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().OperationCount);
            Assert.Contains(
                harness.Localizer[ExpectedNoteKey(mode, answer)],
                harness.ViewModel.StatusText,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UploadJob_TwoJobsAskTwice()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = await WriteAsync(scratch, "one.txt");
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        harness.Scripted.UploadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.UploadEntriesAsync([file], "/srv/in").WaitAsync(Patience);
        await harness.ViewModel.UploadEntriesAsync([file], "/srv/in").WaitAsync(Patience);

        Assert.Equal(2, harness.Dialog.Questions.Count);
    }

    [Fact]
    public async Task UploadJob_APaneThatWasNeverWired_DoesNotAsk()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = await WriteAsync(scratch, "one.txt");
        Harness harness = await Harness.CreateAsync(mode: null, answer: false);
        harness.Scripted.UploadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.UploadEntriesAsync([file], "/srv/in").WaitAsync(Patience);

        Assert.Empty(harness.Dialog.Questions);
        Assert.Single(harness.Scripted.Uploads);
    }

    [Fact]
    public async Task DownloadJob_AFolderWalkAndItsFiles_AskOncePerJob()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.AddDirectory("/srv/proj", SftpTestKit.File("/srv/proj/a.txt", 1));
        harness.Scripted.ListingFailure = path => path == "/srv/proj" ? new SftpPermissionDeniedException("denied") : null;

        await harness.ViewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/proj")], scratch.Path).WaitAsync(Patience);

        LabeledQuestion question = Assert.Single(harness.Dialog.Questions);
        Assert.Equal(harness.Localizer["SftpConfirmSudoDownloadTitle"], question.Title);
        Assert.Equal(harness.Localizer["SftpConfirmSudoDownloadAccept"], question.Accept);
        Assert.Equal(1, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Fact]
    public async Task DownloadJob_EveryFolderOfTheWalkSharesTheJobsAnswer()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.ListingFailure = _ => new SftpPermissionDeniedException("denied");
        MethodInfo walk = typeof(EmbeddedSftpViewModel).GetMethod(
            "ListDirectoryWithSudoFallbackAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        SftpSudoConsentScope job = new(SftpRootActKind.Download, SftpSudoScopeKind.Job, "the job");

        foreach (string folder in new[] { "/srv/a", "/srv/b", "/srv/c" })
        {
            // The privileged listing cannot connect here, so each folder ends in a failure of its own;
            // what is measured is how many questions the three of them raised.
            await Assert.ThrowsAnyAsync<Exception>(
                () => (Task<IReadOnlyList<SftpFileInfo>>)walk.Invoke(
                    harness.ViewModel,
                    [harness.Scripted, folder, CancellationToken.None, job])!);
        }

        Assert.Single(harness.Dialog.Questions);
        Assert.Equal(3, harness.ViewModel.RootActivity.Snapshot().ListingCount);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never, true, 0)]
    [InlineData(SftpSudoFallbackMode.Ask, false, 1)]
    public async Task DownloadJob_ARefusedFileIsNotReadAsRootUnlessAllowed(
        SftpSudoFallbackMode mode,
        bool answer,
        int expectedQuestions)
    {
        using SftpTestKit.ScratchFolder scratch = new();
        Harness harness = await Harness.CreateAsync(mode, answer);
        harness.Scripted.DownloadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.DownloadFilesAsync([SftpTestKit.File("/srv/secret.txt", 1)], scratch.Path).WaitAsync(Patience);

        Assert.Equal(expectedQuestions, harness.Dialog.Questions.Count);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().OperationCount);
        Assert.Contains(
            harness.Localizer[ExpectedNoteKey(mode, answer)],
            harness.ViewModel.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadJob_AYesLetsTheFileBeReadAsRoot()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Scripted.DownloadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.DownloadFilesAsync([SftpTestKit.File("/srv/secret.txt", 1)], scratch.Path).WaitAsync(Patience);

        Assert.Single(harness.Dialog.Questions);
        Assert.Equal(1, harness.ViewModel.RootActivity.Snapshot().OperationCount);
    }

    // --- The answer is re-checked against the job and the pane ---------------------------------

    [Fact]
    public async Task AQuestionAnsweredAfterThePaneWasDisposed_EndsTheOperationInsteadOfRunningRoot()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Dialog.WhileOpen = harness.ViewModel.MarkDisposed;
        harness.Scripted.DownloadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");
        SftpSudoConsentScope scope = new(SftpRootActKind.Download, SftpSudoScopeKind.Job, "job");

        Exception ended = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.Download,
            "/srv/a",
            scope,
            new SftpPermissionDeniedException("denied")));

        // The answer was a yes, yet the call neither returned (which would let root run) nor was
        // reported as a refusal: it ended as a cancellation.
        Assert.IsNotType<SftpSudoNotRunException>(ended);
        Assert.True(scope.Answer);
        Assert.Single(harness.Dialog.Questions);
    }

    [Fact]
    public async Task AnUploadJobWhosePaneIsDisposedDuringTheQuestion_CallsNoSudoAndReportsNoError()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string file = await WriteAsync(scratch, "one.txt");
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.Dialog.WhileOpen = harness.ViewModel.MarkDisposed;
        harness.Scripted.UploadHandler = (_, _, _) => throw new SftpPermissionDeniedException("denied");

        await harness.ViewModel.UploadEntriesAsync([file], "/srv/in").WaitAsync(Patience);

        Assert.Single(harness.Dialog.Questions);
        Assert.Equal(0, harness.ViewModel.RootActivity.Snapshot().OperationCount);
        Assert.Empty(harness.SudoRenameCommands);
        Assert.False(harness.ViewModel.IsErrorStatus);
    }

    [Fact]
    public async Task AQuestionAnsweredAfterTheJobWasCancelled_EndsTheOperationInsteadOfRunningRoot()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        using CancellationTokenSource job = new();
        harness.Dialog.WhileOpen = job.Cancel;
        SftpSudoConsentScope scope = new(SftpRootActKind.Upload, SftpSudoScopeKind.Job, "job");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.Upload,
            "/srv/a",
            scope,
            new SftpPermissionDeniedException("denied"),
            job.Token));
    }

    [Fact]
    public async Task AScopeStoppedByAFatalSudoFailure_StopsTheRestOfTheJobWithoutAskingAgain()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        SftpSudoConsentScope scope = new(SftpRootActKind.Upload, SftpSudoScopeKind.Job, "job");
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.Upload, "/srv/a", scope, new SftpPermissionDeniedException("denied"));
        scope.Guard.Record(new SudoAuthenticationException(SudoFailureKind.PasswordRejected, "raw"));

        await Assert.ThrowsAsync<SudoBatchStoppedException>(() => harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.Upload, "/srv/b", scope, new SftpPermissionDeniedException("denied")));

        Assert.Single(harness.Dialog.Questions);
    }

    // --- The editor ----------------------------------------------------------------------------

    [Fact]
    public async Task TheEditorOpen_AsksOnceAndSaysLaterSavesAreWrittenAsRootToo()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        SftpSudoConsentScope session = new(SftpRootActKind.EditOpen, SftpSudoScopeKind.EditSession);

        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", session, new SftpPermissionDeniedException("denied"));
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", session, new SftpPermissionDeniedException("denied"));

        LabeledQuestion question = Assert.Single(harness.Dialog.Questions);
        Assert.Equal(harness.Localizer["SftpConfirmSudoEditOpenTitle"], question.Title);
        Assert.Equal(harness.Localizer.Format("SftpConfirmSudoEditOpen", "/etc/hosts"), question.Message);
        Assert.Equal(harness.Localizer["SftpConfirmSudoEditOpenAccept"], question.Accept);
    }

    [Fact]
    public async Task TheInlineSave_AsksOncePerDocumentAndReusesTheAnswer()
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: false);
        SftpSudoConsentScope document = new(SftpRootActKind.InlineSave, SftpSudoScopeKind.Document);

        SftpSudoNotRunException first = await Assert.ThrowsAsync<SftpSudoNotRunException>(
            () => harness.ViewModel.RequireSudoAsync(
                SftpRootActKind.InlineSave, "/etc/hosts", document, new SftpPermissionDeniedException("denied")));
        SftpSudoNotRunException second = await Assert.ThrowsAsync<SftpSudoNotRunException>(
            () => harness.ViewModel.RequireSudoAsync(
                SftpRootActKind.InlineSave, "/etc/hosts", document, new SftpPermissionDeniedException("denied")));

        Assert.Single(harness.Dialog.Questions);
        Assert.Equal(SftpSudoDecision.Declined, first.Decision);
        Assert.Equal(SftpSudoDecision.Declined, second.Decision);
        Assert.Equal(harness.Localizer["SftpConfirmSudoInlineSaveTitle"], harness.Dialog.Questions[0].Title);
    }

    [Fact]
    public async Task AnEditSessionAnsweredUnderAsk_IsNotAskedAgainWhenTheSettingReturnsToAsk()
    {
        // The scope keeps its answer for the edit session: the background saves of the external
        // editor are not asked about, even if the setting moves while it is open.
        SftpSudoFallbackMode mode = SftpSudoFallbackMode.Ask;
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Ask, answer: true);
        harness.ViewModel.SetSudoFallbackModeProvider(() => mode);
        SftpSudoConsentScope session = new(SftpRootActKind.EditOpen, SftpSudoScopeKind.EditSession);
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", session, new SftpPermissionDeniedException("denied"));
        Assert.Single(harness.Dialog.Questions);

        mode = SftpSudoFallbackMode.Automatic;
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", session, new SftpPermissionDeniedException("denied"));
        mode = SftpSudoFallbackMode.Ask;
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", session, new SftpPermissionDeniedException("denied"));

        Assert.Single(harness.Dialog.Questions);

        // Under Ask a scope that has no answer does ask, so the silence above is the scope's memory.
        SftpSudoConsentScope fresh = new(SftpRootActKind.EditOpen, SftpSudoScopeKind.EditSession);
        await harness.ViewModel.RequireSudoAsync(
            SftpRootActKind.EditOpen, "/etc/hosts", fresh, new SftpPermissionDeniedException("denied"));
        Assert.Equal(2, harness.Dialog.Questions.Count);
    }

    // --- The words -----------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheNotRunSentence_JoinsTheServersRefusalAndTheReason_InEveryLanguage(string locale)
    {
        Harness harness = await Harness.CreateAsync(SftpSudoFallbackMode.Never, answer: true, locale);

        foreach (SftpSudoDecision decision in new[]
        {
            SftpSudoDecision.RefusedBySetting,
            SftpSudoDecision.Declined,
            SftpSudoDecision.NotAsked,
        })
        {
            string text = harness.ViewModel.DescribeTransferError(
                new SftpSudoNotRunException(decision, new SftpPermissionDeniedException("denied")));

            Assert.StartsWith(harness.Localizer["SftpErrorRemotePermissionDenied"], text, StringComparison.Ordinal);
            Assert.EndsWith(harness.Localizer[SftpSudoConsentLocaleKeys.NoteFor(decision)!], text, StringComparison.Ordinal);
            Assert.DoesNotContain("SftpSudo", text, StringComparison.Ordinal);
        }
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static string ExpectedNoteKey(SftpSudoFallbackMode mode, bool answer = false)
        => mode == SftpSudoFallbackMode.Never
            ? SftpSudoConsentLocaleKeys.NoteNever
            : SftpSudoConsentLocaleKeys.NoteDeclined;

    private static SftpFileInfo File(string name, string path)
        => new(name, path, RemoteEntryKind.File, 1, DateTime.UnixEpoch, "rw-r--r--", "1000", "1000");

    private static async Task<string> WriteAsync(SftpTestKit.ScratchFolder scratch, string name)
    {
        string path = Path.Combine(scratch.Path, name);
        await System.IO.File.WriteAllTextAsync(path, "x");
        return path;
    }

    internal sealed record LabeledQuestion(string Title, string Message, string Severity, string Accept, string Decline);

    private sealed class Harness
    {
        private readonly List<string> _sudoRenameCommands = [];
        private readonly List<string> _sudoChmodCommands = [];
        private readonly List<string> _sudoDeleteCommands = [];

        private Harness(
            EmbeddedSftpViewModel viewModel,
            ScriptedRemoteBrowser scripted,
            ConsentDialog dialog,
            LocalizationManager localizer)
        {
            ViewModel = viewModel;
            Scripted = scripted;
            Dialog = dialog;
            Localizer = localizer;
        }

        public EmbeddedSftpViewModel ViewModel { get; }

        public ScriptedRemoteBrowser Scripted { get; }

        public ConsentDialog Dialog { get; }

        public LocalizationManager Localizer { get; }

        public IReadOnlyList<string> SudoRenameCommands => _sudoRenameCommands;

        public IReadOnlyList<string> SudoChmodCommands => _sudoChmodCommands;

        public IReadOnlyList<string> SudoDeleteCommands => _sudoDeleteCommands;

        public static async Task<Harness> CreateAsync(SftpSudoFallbackMode? mode, bool answer, string locale = "en")
        {
            LocalizationManager localizer = await SftpTestKit.LoadLocalizerAsync(locale);
            ScriptedRemoteBrowser scripted = new();
            IDialogService service = ConsentDialog.Create(out ConsentDialog dialog);
            dialog.Answer = answer;
            EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(
                scripted,
                localizer: localizer,
                dialogService: service);
            Harness harness = new(viewModel, scripted, dialog, localizer);

            SetField(viewModel, "_sshParams", new SshConnectionParams { Host = "example.test", Username = "tester" });
            if (mode is { } fixedMode)
            {
                viewModel.SetSudoFallbackModeProvider(() => fixedMode);
            }

            viewModel.SetSudoRenameCommandExecutor((command, _) =>
            {
                harness._sudoRenameCommands.Add(command);
                // An existence probe finds nothing; everything else succeeds.
                int exit = command.StartsWith("test -e", StringComparison.Ordinal) ? 1 : 0;
                return Task.FromResult(new SudoRenameCommandResult(exit, string.Empty, string.Empty));
            });
            viewModel.SetSudoChmodCommandExecutor((command, _) =>
            {
                harness._sudoChmodCommands.Add(command);
                return Task.CompletedTask;
            });
            SetField(
                viewModel,
                "_sudoDeleteCommandExecutor",
                new Func<string, CancellationToken, Task>((command, _) =>
                {
                    harness._sudoDeleteCommands.Add(command);
                    return Task.CompletedTask;
                }));
            return harness;
        }

        /// <summary>Swaps the browser for one whose folder creation and permission change are refused.</summary>
        public void UseRefusingMutations(bool allowMkdir = false)
        {
            SetField(ViewModel, "_browser", new RefusingMutationsBrowser(Scripted, allowMkdir));
        }

        private static void SetField(EmbeddedSftpViewModel viewModel, string name, object value)
        {
            FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }
    }

    /// <summary>Wraps the scripted browser and refuses folder creation and permission changes.</summary>
    private sealed class RefusingMutationsBrowser(ScriptedRemoteBrowser inner, bool allowMkdir) : IRemoteBrowser
    {
        public event Action<string>? DirectoryChanged
        {
            add { }
            remove { }
        }

        public event Action<SftpTransferProgress>? TransferProgress
        {
            add { }
            remove { }
        }

        public event Action<RemoteOperationWarning>? OperationWarningRaised
        {
            add { }
            remove { }
        }

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => inner.CurrentDirectory;

        public bool IsConnected => inner.IsConnected;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => inner.ListDirectoryAsync(path, ct);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => inner.GetCurrentDirectoryAsync(ct);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => inner.ChangeDirectoryAsync(path, ct);

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => inner.DownloadFileAsync(remotePath, localPath, ct);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => inner.UploadFileAsync(localPath, remotePath, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => allowMkdir ? Task.CompletedTask : Task.FromException(new SftpPermissionDeniedException("denied"));

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => inner.DeleteAsync(path, ct);

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => Task.FromException(new SftpPermissionDeniedException("denied"));

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => inner.RenameAsync(oldPath, newPath, ct);

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => inner.CopyAsync(sourcePath, destinationPath, recursive, ct);

        public void Disconnect() => inner.Disconnect();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A dialog service on a proxy: it records every labeled confirmation (the root questions),
    /// answers the plain ones yes, and returns the scripted text for an input prompt.
    /// </summary>
    public class ConsentDialog : DispatchProxy
    {
        public bool Answer { get; set; }

        public string? InputAnswer { get; set; }

        public Action? WhileOpen { get; set; }

        internal List<LabeledQuestion> Questions { get; } = [];

        internal static IDialogService Create(out ConsentDialog dialog)
        {
            IDialogService service = DispatchProxy.Create<IDialogService, ConsentDialog>();
            dialog = (ConsentDialog)(object)service;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];

            if (targetMethod.Name == nameof(IDialogService.ShowConfirmAsync))
            {
                if (args.Length >= 5)
                {
                    Questions.Add(new LabeledQuestion(
                        (string)args[0]!,
                        (string)args[1]!,
                        (string)args[2]!,
                        (string)args[3]!,
                        (string)args[4]!));
                    WhileOpen?.Invoke();
                    return Task.FromResult(Answer);
                }

                // The plain confirmation of a delete: the user did ask for the deletion.
                return Task.FromResult(true);
            }

            if (targetMethod.Name == nameof(IDialogService.ShowInputAsync))
            {
                return Task.FromResult(InputAnswer);
            }

            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GetGenericArguments()[0];
                object? defaultValue = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [defaultValue]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
