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
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelRemoteClipboardTests
{
    [Fact]
    public void PasteCommand_SameEndpointConnectedAndNonEmpty_ReturnsTrue()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = true
        };

        clipboard.Set(CreateContent("host=server;port=22;user=alice"));
        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.True(viewModel.HasClipboard);
        Assert.True(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void PasteCommand_DifferentEndpointWithoutSourceBrowser_ReturnsFalse()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = true
        };

        clipboard.Set(CreateContent("host=other;port=22;user=alice"));
        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.False(viewModel.HasClipboard);
        Assert.False(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void PasteCommand_DifferentEndpointWithConnectedSourceBrowser_ReturnsTrue()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = true
        };
        FakeRemoteBrowser sourceBrowser = new();

        clipboard.Set(CreateContent("host=other;port=22;user=alice", sourceBrowser));
        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.True(viewModel.HasClipboard);
        Assert.True(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void PasteCommand_DifferentEndpointWithDisconnectedSourceBrowser_ReturnsFalse()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = true
        };
        FakeRemoteBrowser sourceBrowser = new() { IsConnected = false };

        clipboard.Set(CreateContent("host=other;port=22;user=alice", sourceBrowser));
        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.False(viewModel.HasClipboard);
        Assert.False(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void PasteCommand_EmptyClipboard_ReturnsFalse()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = true
        };

        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.False(viewModel.HasClipboard);
        Assert.False(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public void PasteCommand_Disconnected_ReturnsFalse()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher(), clipboard)
        {
            IsConnected = false
        };

        clipboard.Set(CreateContent("host=server;port=22;user=alice"));
        SetEndpointKey(viewModel, "host=server;port=22;user=alice");

        Assert.True(viewModel.HasClipboard);
        Assert.False(viewModel.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task PasteClipboardAsync_SameEndpointCrossPane_CallsCopyOnReceivingPaneBrowser()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel sourcePane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/src",
            IsConnected = true
        };
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SftpFileInfo sourceEntry = CreateEntry("a.txt", "/src/a.txt", isDirectory: false);

        SetEndpointKey(sourcePane, "host=server;port=22;user=alice");
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        sourcePane.SetSelection([sourceEntry], sourceEntry);
        sourcePane.CopySelectedCommand.Execute(null);

        await targetPane.PasteClipboardAsync();

        (string Source, string Destination, bool Recursive) copy = Assert.Single(targetBrowser.CopyCalls);
        Assert.Equal("/src/a.txt", copy.Source);
        Assert.Equal("/dst/a.txt", copy.Destination);
        Assert.False(copy.Recursive);
        Assert.NotNull(clipboard.Current);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpoint_DoesNotCallReceivingPaneBrowser()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateContent("host=other;port=22;user=alice"));

        await targetPane.PasteClipboardAsync();

        Assert.Empty(targetBrowser.CopyCalls);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointCopiesFileViaDownloadTempPublish()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel sourcePane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/src",
            IsConnected = true
        };
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(sourcePane, sourceBrowser);
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(sourcePane, "host=a;port=22;user=alice");
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        sourcePane.SetSelection([CreateEntry("a.txt", "/src/a.txt", isDirectory: false)], null);

        sourcePane.CopySelectedCommand.Execute(null);
        await targetPane.PasteClipboardAsync();

        (string RemotePath, string LocalPath) download = Assert.Single(sourceBrowser.DownloadCalls);
        Assert.Equal("/src/a.txt", download.RemotePath);
        // A publication, not an upload: the upload path replaces its destination and must not be
        // reachable from a cross-endpoint paste any more.
        Assert.Empty(targetBrowser.UploadCalls);
        (string LocalPath, string RemotePath, bool LocalPathExisted) publish =
            Assert.Single(targetBrowser.PublishCalls);
        Assert.Equal("/dst/a.txt", publish.RemotePath);
        Assert.True(publish.LocalPathExisted);
        Assert.False(File.Exists(publish.LocalPath));
        Assert.Empty(targetBrowser.CopyCalls);
        Assert.NotNull(clipboard.Current);
    }

    // Was "tolerates an existing destination directory". Merging is the defect, not a feature: the
    // destination listing shows /dst as empty while /dst/proj already exists, which is precisely a stale
    // listing. Continuing into it would place every file of the pasted tree among entries belonging to
    // somebody else, so the reservation refuses and the paste stops.
    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointDirectory_RefusesToMergeIntoAnExistingDirectory()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        sourceBrowser.Listings["/src/proj"] =
            [CreateEntry("readme.txt", "/src/proj/readme.txt", isDirectory: false)];
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];

        // Present on the server, absent from the listing of its parent: the stale-snapshot case.
        targetBrowser.Listings["/dst/proj"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("proj", "/src/proj", isDirectory: true)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // Reserved exactly once, refused, and nothing written inside it.
        Assert.Equal(["/dst/proj"], targetBrowser.ReserveCalls);
        Assert.Empty(targetBrowser.PublishCalls);

        // The replacing primitives must not be reachable as a fallback.
        Assert.Empty(targetBrowser.UploadCalls);
        Assert.Empty(targetBrowser.CreateDirectoryCalls);

        // The exact final status, not merely the absence of a completed paste.
        AssertLocalized(localizer, "SftpStatusTransferFailed", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);
    }

    // The refresh reloads the directory and reports Ready when the listing succeeds, so a status written
    // before it is silently replaced by the outcome of an unrelated operation. Asserting only that the
    // status is not "paste complete" does not catch that: Ready satisfies it while the refusal has
    // vanished from the user's view.
    [Fact]
    public async Task PasteClipboardAsync_CollisionRefusal_LeavesTheRefusalVisibleAfterTheRefresh()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        targetBrowser.BeforePublish = destination =>
            targetBrowser.AddFileToModel(destination, "written by somebody else");
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // The exact final status, not the absence of two forbidden ones: excluding Ready and
        // PasteComplete would accept any other error status, including one describing a different
        // failure entirely.
        AssertLocalized(localizer, "SftpStatusTransferFailed", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);

        // The refresh still happened: the entry the other party wrote is now visible.
        Assert.Contains(targetPane.UnfilteredEntries, entry => entry.Name == "a.txt");
    }

    [Fact]
    public async Task PasteClipboardAsync_Success_ReportsCompletionAfterTheRefresh()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        AssertLocalized(localizer, "SftpStatusPasteComplete", targetPane.StatusText);
        Assert.False(targetPane.IsErrorStatus);
        Assert.Contains(targetPane.UnfilteredEntries, entry => entry.Name == "a.txt");
    }

    // A cancellation is not proof that nothing landed: the link may have taken effect before the answer
    // was lost. So the destination is reloaded, the source is kept, the clipboard entry is kept, and the
    // outcome stays a cancellation rather than becoming a success or a generic error.
    [Fact]
    public async Task PasteClipboardAsync_CancelledAfterTheDestinationLanded_KeepsTheSourceAndShowsIt()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        targetBrowser.BeforePublish = destination =>
        {
            // The publication really took effect on the server, and a later listing will show it. Only
            // then is the answer lost.
            targetBrowser.AddFileToModel(destination, "landed before the answer was lost");
            throw new OperationCanceledException();
        };
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // Attempted, and the attempt is on record even though the hook threw.
        Assert.Single(targetBrowser.PublishCalls);

        // Refreshed: what actually landed is now visible to the user.
        Assert.Contains(targetPane.UnfilteredEntries, entry => entry.Name == "a.txt");

        // The cut source survives an outcome nobody can confirm.
        Assert.Empty(sourceBrowser.DeleteCalls);
        Assert.NotNull(clipboard.Current);
        Assert.Contains(clipboard.Current!.Entries, entry => entry.FullPath == "/src/a.txt");

        AssertLocalized(localizer, "SftpStatusTransferCancelled", targetPane.StatusText);
    }

    // Two roots, the first genuinely moved, the second interrupted. The clipboard must lose the root
    // whose source is gone and keep the one that is not demonstrably done: otherwise the next paste is
    // asked to read a path that no longer exists.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasteClipboardAsync_CutInterruptedOnTheSecondRoot_KeepsOnlyTheUnmovedRoot(
        bool cancelled)
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        targetBrowser.BeforePublish = destination =>
        {
            if (cancelled)
            {
                if (string.Equals(destination, "/dst/b.txt", StringComparison.Ordinal))
                {
                    throw new OperationCanceledException();
                }

                return;
            }

            // The source pane is torn down once the first root has moved, so the second root's download
            // fails the way a closed session really fails and is classified as a lost source.
            if (string.Equals(destination, "/dst/a.txt", StringComparison.Ordinal))
            {
                sourceBrowser.ThrowObjectDisposedOnDownload = true;
            }
        };
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(new SftpClipboardContent(
            [
                CreateEntry("a.txt", "/src/a.txt", isDirectory: false),
                CreateEntry("b.txt", "/src/b.txt", isDirectory: false),
            ],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // The first root moved for real, so its source is gone and it must leave the clipboard.
        Assert.Contains("/src/a.txt", sourceBrowser.DeleteCalls);

        SftpClipboardContent? remaining = clipboard.Current;
        Assert.NotNull(remaining);
        Assert.Equal(["/src/b.txt"], remaining!.Entries.Select(entry => entry.FullPath).ToList());
    }

    // Defence in depth behind the identity seam. Two unknown identities are not evidence of one server;
    // they are two servers nobody could name. Treating them as equal is what routed a paste between two
    // different FTP hosts through the same-endpoint path, where the no-clobber gate is never consulted
    // and the commit is a replacing rename. An unknown identity must fall through to the cross-endpoint
    // path, where a transport that cannot publish exclusively refuses.
    [Fact]
    public async Task PasteClipboardAsync_BothEndpointKeysUnknown_IsNotTreatedAsTheSameEndpoint()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new() { SupportsNoClobber = false };
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, string.Empty);
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            string.Empty,
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // Routed cross-endpoint, and refused there by the capability gate.
        AssertLocalized(localizer, "SftpErrorPasteNoClobberUnsupported", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);

        // Not one byte moved, by any route: the same-endpoint primitives included.
        Assert.Empty(targetBrowser.CopyCalls);
        Assert.Equal(0, targetBrowser.RenameCallCount);
        Assert.Empty(targetBrowser.UploadCalls);
        Assert.Empty(targetBrowser.PublishCalls);
        Assert.Empty(targetBrowser.CreateDirectoryCalls);
        Assert.Empty(targetBrowser.ReserveCalls);
        Assert.Empty(sourceBrowser.DownloadCalls);
    }

    // The gate. A transport that cannot publish without replacing does not paste at all, and refuses
    // before touching the destination: not after creating a directory, not after uploading a first file.
    [Fact]
    public async Task PasteClipboardAsync_DestinationCannotPublishWithoutReplacing_RefusesBeforeAnyMutation()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new() { SupportsNoClobber = false };
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        AssertLocalized(localizer, "SftpErrorPasteNoClobberUnsupported", targetPane.StatusText);

        // Nothing reached the destination, by any route.
        Assert.Empty(targetBrowser.PublishCalls);
        Assert.Empty(targetBrowser.ReserveCalls);
        Assert.Empty(targetBrowser.UploadCalls);
        Assert.Empty(targetBrowser.CreateDirectoryCalls);
        Assert.Empty(targetBrowser.CopyCalls);

        // And the source was never even read: refusing after a download would still be refusing, but it
        // would have moved the user's data for an operation that was never going to be allowed.
        Assert.Empty(sourceBrowser.DownloadCalls);
    }

    // The race the design exists for. The name is chosen from a live listing, and the destination is
    // created after that listing and before the publication. Only the exclusive publish can catch this:
    // any probe the caller performs is already in the past by the time the bytes are sent.
    [Fact]
    public async Task PasteClipboardAsync_DestinationAppearsAfterTheListing_RefusesAndLeavesItIntact()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        targetBrowser.BeforePublish = destination =>
            targetBrowser.ExistingFiles.TryAdd(destination, "written by somebody else");
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // Attempted once, refused, and the other party's content is still there untouched.
        Assert.Single(targetBrowser.PublishCalls);
        Assert.Equal("written by somebody else", targetBrowser.ExistingFiles["/dst/a.txt"]);

        // The exact final status. Excluding one forbidden value would also accept Ready, which is
        // what a refresh writes when it runs after the status instead of before it.
        AssertLocalized(localizer, "SftpStatusTransferFailed", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);
    }

    // The cached listing must carry no authority at all, in either direction. Here it claims a file is
    // present that the server does not have: if the paste consulted it, the name would be renamed away
    // or the write refused. It publishes under the asked-for name instead.
    [Fact]
    public async Task PasteClipboardAsync_CachedListingDisagreesWithTheServer_FollowsTheServer()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = [CreateEntry("a.txt", "/dst/a.txt", isDirectory: false)]
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        (string LocalPath, string RemotePath, bool LocalPathExisted) publish =
            Assert.Single(targetBrowser.PublishCalls);
        Assert.Equal("/dst/a.txt", publish.RemotePath);
    }

    // An unconfirmed outcome is not a failure to publish: the destination may hold the file, or not.
    // Deleting the cut source on that basis is how the only copy of a file disappears.
    [Fact]
    public async Task PasteClipboardAsync_CutWithUnconfirmedPublication_KeepsTheSourceAndTheClipboard()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new() { UnconfirmedPublishRemotePath = "/dst/a.txt" };
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Empty(sourceBrowser.DeleteCalls);
        Assert.NotNull(clipboard.Current);
        Assert.Contains(clipboard.Current!.Entries, entry => entry.FullPath == "/src/a.txt");

        // The exact final status: an unconfirmed publication is reported as a failed transfer, and the
        // refresh that precedes it must not have replaced that with Ready.
        AssertLocalized(localizer, "SftpStatusTransferFailed", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);
    }

    // The source-lost catch refreshes too, and nothing would notice its removal today: a directory root
    // is reserved successfully, then the source session dies while a child is being downloaded. The
    // reserved-but-incomplete directory is exactly what the user needs to see.
    [Fact]
    public async Task PasteClipboardAsync_SourceLostAfterAReservation_ShowsThePartialTreeAndKeepsTheSource()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        sourceBrowser.Listings["/src/proj"] =
            [CreateEntry("readme.txt", "/src/proj/readme.txt", isDirectory: false)];
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];

        // The root is reserved for real; the source pane dies before its first child can be read.
        targetBrowser.BeforeReserve = _ => sourceBrowser.ThrowObjectDisposedOnDownload = true;
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("proj", "/src/proj", isDirectory: true)],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // Reserved, and nothing published inside it.
        Assert.Equal(["/dst/proj"], targetBrowser.ReserveCalls);
        Assert.Empty(targetBrowser.PublishCalls);

        // The refresh ran: the partial tree is visible rather than being left to surprise the user on
        // the next manual refresh.
        Assert.Contains(targetPane.UnfilteredEntries, entry => entry.Name == "proj");

        // A cut whose move never completed keeps its source and its clipboard entry.
        Assert.Empty(sourceBrowser.DeleteCalls);
        Assert.NotNull(clipboard.Current);
        Assert.Contains(clipboard.Current!.Entries, entry => entry.FullPath == "/src/proj");

        AssertLocalized(localizer, "SftpErrorSourceSessionUnavailable", targetPane.StatusText);
        Assert.True(targetPane.IsErrorStatus);
    }

    // Every node of the tree, root and descendants alike, goes through an exclusive reservation or an
    // exclusive publication. A single node left on a replacing primitive is a hole in the guarantee.
    [Fact]
    public async Task PasteClipboardAsync_NestedTree_ReservesEveryNodeExclusively()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        sourceBrowser.Listings["/src/proj"] =
        [
            CreateEntry("readme.txt", "/src/proj/readme.txt", isDirectory: false),
            CreateEntry("inner", "/src/proj/inner", isDirectory: true),
        ];
        sourceBrowser.Listings["/src/proj/inner"] =
            [CreateEntry("deep.txt", "/src/proj/inner/deep.txt", isDirectory: false)];
        FakeRemoteBrowser targetBrowser = new();
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("proj", "/src/proj", isDirectory: true)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal(["/dst/proj", "/dst/proj/inner"], targetBrowser.ReserveCalls);
        Assert.Equal(
            ["/dst/proj/inner/deep.txt", "/dst/proj/readme.txt"],
            targetBrowser.PublishCalls.Select(call => call.RemotePath).Order(StringComparer.Ordinal).ToList());

        // No node took the replacing route.
        Assert.Empty(targetBrowser.UploadCalls);
        Assert.Empty(targetBrowser.CreateDirectoryCalls);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutDifferentEndpointDeletesSourceAfterSuccessfulTransferAndClearsClipboard()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal(["/src/a.txt"], sourceBrowser.DeleteCalls);
        Assert.Null(clipboard.Current);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutSourceRefusalKeepsSuccessfulTransferAndWarns()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new()
        {
            DeleteException = new RemoteRecursiveDeleteException(
                RemoteRecursiveDeleteFailureReason.ExecUnavailable),
        };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        SftpFileInfo sourceEntry = CreateEntry(
            "a.txt",
            "/src/a.txt",
            isDirectory: false);
        clipboard.Set(new SftpClipboardContent(
            [sourceEntry],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Single(targetBrowser.PublishCalls);
        Assert.Equal(["/src/a.txt"], sourceBrowser.DeleteCalls);
        SftpClipboardContent remaining = Assert.IsType<SftpClipboardContent>(clipboard.Current);
        Assert.Equal("/src/a.txt", Assert.Single(remaining.Entries).FullPath);
        Assert.Equal("SftpCutSourceNotDeleted", targetPane.StatusText);
        Assert.False(targetPane.IsErrorStatus);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutDifferentEndpointFailureKeepsUntransferredSources()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new() { UnconfirmedPublishRemotePath = "/dst/b.txt" };
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        SftpFileInfo first = CreateEntry("a.txt", "/src/a.txt", isDirectory: false);
        SftpFileInfo second = CreateEntry("b.txt", "/src/b.txt", isDirectory: false);
        clipboard.Set(new SftpClipboardContent(
            [first, second],
            "/src",
            SftpClipboardMode.Cut,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal(["/src/a.txt"], sourceBrowser.DeleteCalls);
        SftpClipboardContent remaining = Assert.IsType<SftpClipboardContent>(clipboard.Current);
        SftpFileInfo retained = Assert.Single(remaining.Entries);
        Assert.Equal("/src/b.txt", retained.FullPath);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointUnavailableSourceFailsGracefully()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new() { ThrowObjectDisposedOnIsConnected = true };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        AssertLocalized(localizer, "SftpErrorSourceSessionUnavailable", targetPane.StatusText);
        Assert.Empty(targetBrowser.UploadCalls);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointSourceClosesDuringDownloadFailsGracefully()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new() { ThrowObjectDisposedOnDownload = true };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        AssertLocalized(localizer, "SftpErrorSourceSessionUnavailable", targetPane.StatusText);
        Assert.Empty(targetBrowser.UploadCalls);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointDestinationDisconnectIsTransferFailure()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new() { IsConnected = false };
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // A destination that went away is a transfer failure, not a missing SOURCE session: the two
        // diagnoses must not collapse into one another.
        Assert.Equal("SftpStatusTransferFailed", targetPane.StatusText);
        Assert.NotEqual("SftpErrorSourceSessionUnavailable", targetPane.StatusText);
    }

    [Fact]
    public async Task PasteClipboardAsync_CrossEndpointProgress_ReportsALocalizedTransferLine()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        FakeRemoteBrowser sourceBrowser = new();
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        SetLocalizer(targetPane, localizer);
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        // The progress line is built from the catalog, so it carries the entry name and its rank.
        Assert.Equal(
            localizer.Format("SftpStatusTransferringEntry", "a.txt", "1", "1"),
            targetPane.TransferStatusText);
        Assert.Contains("a.txt", targetPane.TransferStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Transferring", targetPane.TransferStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutModeCrossPane_ClearsSharedClipboard()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel sourcePane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/src",
            IsConnected = true
        };
        EmbeddedSftpViewModel targetPane = new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SftpFileInfo sourceEntry = CreateEntry("a.txt", "/src/a.txt", isDirectory: false);

        SetEndpointKey(sourcePane, "host=server;port=22;user=alice");
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        sourcePane.SetSelection([sourceEntry], sourceEntry);
        sourcePane.CutSelectedCommand.Execute(null);

        await targetPane.PasteClipboardAsync();

        Assert.Equal(1, targetBrowser.RenameCallCount);
        Assert.Equal("/src/a.txt", targetBrowser.LastRenamedOldPath);
        Assert.Equal("/dst/a.txt", targetBrowser.LastRenamedNewPath);
        Assert.Null(clipboard.Current);
        Assert.False(sourcePane.HasClipboard);
        Assert.False(targetPane.HasClipboard);
    }

    // --- Transfer coordinator ------------------------------------------------------------------
    // A remote paste and a duplicate move bytes, so they must hold the same coordinator as an upload
    // or a download: refused while one runs, and never cancelling it to take its place.
    //
    // "Not cancelled" is asserted through the holder's own work - it must still copy both of its
    // entries after the challenger has been refused - rather than through a token captured from the
    // browser: a token that turns out not to be the coordinator's leaves the assertion green under a
    // cancellation mutant, which is exactly how a first draft of these tests passed vacuously.

    [Fact]
    public async Task PasteClipboardAsync_SameEndpointWhileTransferRuns_RefusesAndLeavesRunningTransferAlive()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser targetBrowser = BlockingCopyBrowser(copyStarted, releaseCopy);
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateTwoEntryContent("host=server;port=22;user=alice"));

        try
        {
            Task running = targetPane.PasteClipboardAsync();
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The paste holds the coordinator, it does not merely read it.
            Assert.True(targetPane.IsTransferInProgress);

            await targetPane.PasteClipboardAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(targetBrowser.CopyCalls);
            Assert.Equal("A file transfer is already in progress.", targetPane.StatusText);

            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            // Both of the holder's entries were copied: the refusal did not abort it.
            Assert.Equal(
                ["/src/a.txt", "/src/b.txt"],
                targetBrowser.CopyCalls.Select(call => call.Source));
            Assert.False(targetPane.IsTransferInProgress);
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task PasteClipboardAsync_CrossEndpointWhileTransferRuns_RefusesAndLeavesRunningTransferAlive()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser targetBrowser = BlockingCopyBrowser(copyStarted, releaseCopy);
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateTwoEntryContent("host=server;port=22;user=alice"));

        try
        {
            Task running = targetPane.PasteClipboardAsync();
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Now the challenger comes from another endpoint: the download/temp/upload path, which
            // used to replace the coordinator's token source outside its lock and cancel the copy.
            FakeRemoteBrowser sourceBrowser = new();
            clipboard.Set(CreateContent("host=other;port=22;user=alice", sourceBrowser));

            await targetPane.PasteClipboardAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(sourceBrowser.DownloadCalls);
            Assert.Equal("A file transfer is already in progress.", targetPane.StatusText);

            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                ["/src/a.txt", "/src/b.txt"],
                targetBrowser.CopyCalls.Select(call => call.Source));
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task DuplicateEntriesAsync_WhileTransferRuns_RefusesAndLeavesRunningTransferAlive()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel pane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser browser = BlockingCopyBrowser(copyStarted, releaseCopy);
        SetBrowser(pane, browser);
        SetEndpointKey(pane, "host=server;port=22;user=alice");
        clipboard.Set(CreateTwoEntryContent("host=server;port=22;user=alice"));

        try
        {
            Task running = pane.PasteClipboardAsync();
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await pane.DuplicateEntriesAsync([CreateEntry("c.txt", "/dst/c.txt", isDirectory: false)])
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(browser.CopyCalls);
            Assert.Equal("A file transfer is already in progress.", pane.StatusText);

            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                ["/src/a.txt", "/src/b.txt"],
                browser.CopyCalls.Select(call => call.Source));
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task PasteClipboardAsync_AfterAPasteCompletes_CanPasteAgain()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        FakeRemoteBrowser targetBrowser = new();

        // The destination directory is now read live to choose names, so it must be listable. The
        // cached UnfilteredEntries the paste used to trust is no longer consulted.
        targetBrowser.Listings["/dst"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateContent("host=server;port=22;user=alice"));

        await targetPane.PasteClipboardAsync();
        await targetPane.PasteClipboardAsync();

        // The coordinator was released by the first paste, so the second one ran.
        Assert.Equal(2, targetBrowser.CopyCalls.Count);
        Assert.False(targetPane.IsTransferInProgress);
    }

    [Fact]
    public async Task DuplicateEntriesAsync_AfterADuplicateCompletes_CanDuplicateAgain()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel pane = CreateReceivingPane(clipboard);
        FakeRemoteBrowser browser = new();
        SetBrowser(pane, browser);
        SftpFileInfo entry = CreateEntry("b.txt", "/dst/b.txt", isDirectory: false);

        await pane.DuplicateEntriesAsync([entry]);
        await pane.DuplicateEntriesAsync([entry]);

        Assert.Equal(2, browser.CopyCalls.Count);
        Assert.False(pane.IsTransferInProgress);
    }

    [Fact]
    public async Task PasteClipboardAsync_CancelledMidPaste_SkipsTheRemainingEntries()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser targetBrowser = new()
        {
            CopyHandler = async (_, _, _, _) =>
            {
                copyStarted.TrySetResult();
                await releaseCopy.Task;
            }
        };
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateTwoEntryContent("host=server;port=22;user=alice"));

        try
        {
            Task running = targetPane.PasteClipboardAsync();
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The token the Cancel command signals is the one the paste loop observes.
            targetPane.CancelTransferCommand.Execute(null);
            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(targetBrowser.CopyCalls);
            Assert.Equal("/src/a.txt", targetBrowser.CopyCalls[0].Source);
            Assert.False(targetPane.IsTransferInProgress);
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task DuplicateEntriesAsync_CancelledMidDuplicate_SkipsTheRemainingEntries()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel pane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser browser = BlockingCopyBrowser(copyStarted, releaseCopy);
        SetBrowser(pane, browser);

        try
        {
            Task running = pane.DuplicateEntriesAsync(
            [
                CreateEntry("a.txt", "/dst/a.txt", isDirectory: false),
                CreateEntry("b.txt", "/dst/b.txt", isDirectory: false)
            ]);
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            pane.CancelTransferCommand.Execute(null);
            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            (string Source, string Destination, bool Recursive) copy = Assert.Single(browser.CopyCalls);
            Assert.Equal("/dst/a.txt", copy.Source);
            Assert.False(pane.IsTransferInProgress);
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task PasteClipboardAsync_CancelDuringCopy_CancelsTheTokenGivenToTheBrowser()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel targetPane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken browserToken = default;
        FakeRemoteBrowser targetBrowser = new()
        {
            CopyHandler = async (_, _, _, ct) =>
            {
                browserToken = ct;
                copyStarted.TrySetResult();
                await releaseCopy.Task;
            }
        };
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateContent("host=server;port=22;user=alice"));

        try
        {
            Task running = targetPane.PasteClipboardAsync();
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(browserToken.IsCancellationRequested);

            targetPane.CancelTransferCommand.Execute(null);

            // Without this, cancelling during a server-side copy of a large tree does nothing: the
            // browser was handed CancellationToken.None and the request never reaches the server.
            Assert.True(browserToken.IsCancellationRequested);

            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    [Fact]
    public async Task DuplicateEntriesAsync_CancelDuringCopy_CancelsTheTokenGivenToTheBrowser()
    {
        RemoteClipboardService clipboard = new();
        EmbeddedSftpViewModel pane = CreateReceivingPane(clipboard);
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken browserToken = default;
        FakeRemoteBrowser browser = new()
        {
            CopyHandler = async (_, _, _, ct) =>
            {
                browserToken = ct;
                copyStarted.TrySetResult();
                await releaseCopy.Task;
            }
        };
        SetBrowser(pane, browser);

        try
        {
            Task running = pane.DuplicateEntriesAsync(
                [CreateEntry("a.txt", "/dst/a.txt", isDirectory: false)]);
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(browserToken.IsCancellationRequested);

            pane.CancelTransferCommand.Execute(null);

            Assert.True(browserToken.IsCancellationRequested);

            releaseCopy.SetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCopy.TrySetResult();
        }
    }

    private static EmbeddedSftpViewModel CreateReceivingPane(RemoteClipboardService clipboard)
        => new(new FakeUiDispatcher(), clipboard)
        {
            CurrentPath = "/dst",
            IsConnected = true,
            UnfilteredEntries = []
        };

    /// <summary>
    /// A browser whose first copy blocks until released, so a test can meet a genuinely running
    /// transfer rather than a hand-set flag.
    /// </summary>
    private static FakeRemoteBrowser BlockingCopyBrowser(
        TaskCompletionSource copyStarted,
        TaskCompletionSource releaseCopy)
        => new()
        {
            CopyHandler = async (_, _, _, _) =>
            {
                copyStarted.TrySetResult();
                await releaseCopy.Task;
            }
        };

    private static SftpClipboardContent CreateTwoEntryContent(string endpointKey)
        => new(
            [
                CreateEntry("a.txt", "/src/a.txt", isDirectory: false),
                CreateEntry("b.txt", "/src/b.txt", isDirectory: false)
            ],
            "/src",
            SftpClipboardMode.Copy,
            endpointKey,
            null);

    private static SftpClipboardContent CreateContent(string endpointKey, IRemoteBrowser? sourceBrowser = null)
    {
        return new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            endpointKey,
            sourceBrowser);
    }

    private static SftpFileInfo CreateEntry(string name, string fullPath, bool isDirectory)
    {
        return new SftpFileInfo(
            name,
            fullPath,
            isDirectory ? RemoteEntryKind.Directory : RemoteEntryKind.File,
            1,
            DateTime.UnixEpoch,
            isDirectory ? "rwxr-xr-x" : "rw-r--r--",
            "1000",
            "1000");
    }

    private static void SetBrowser(EmbeddedSftpViewModel viewModel, IRemoteBrowser browser)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_browser",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, browser);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    private static void SetLocalizer(EmbeddedSftpViewModel viewModel, LocalizationManager localizer)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, localizer);
    }

    /// <summary>
    /// Asserts a status came from the catalog under <paramref name="localizer"/>. The guard on the
    /// English text is what makes it discriminating: without it, a status still hardcoded in the
    /// view model would satisfy the comparison whenever the two catalogs happen to agree.
    /// </summary>
    private static void AssertLocalized(
        LocalizationManager localizer,
        string key,
        string actualStatus)
    {
        string expected = localizer[key];
        Assert.NotEqual(key, expected);
        Assert.NotEqual("Source session no longer available.", expected);
        Assert.Equal(expected, actualStatus);
    }

    private static void SetEndpointKey(EmbeddedSftpViewModel viewModel, string endpointKey)
    {
        MethodInfo? method = typeof(EmbeddedSftpViewModel).GetMethod(
            "SetEndpointKey",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [endpointKey]);
    }

    /// <summary>
    /// Destination double with a real existence model, so a refusal comes from the model rather than
    /// from a flag the test set to make the assertion pass.
    /// </summary>
    /// <remarks>
    /// Existence is authoritative here: a directory exists when <see cref="Listings"/> knows it, a file
    /// exists when <see cref="ExistingFiles"/> knows it, and a publication consults that state instead
    /// of trusting the caller. <see cref="BeforePublish"/> and <see cref="BeforeReserve"/> let a test
    /// create the destination in the window between the caller choosing a name and the server evaluating
    /// it, which is the race the whole design exists to close and cannot be reproduced any other way.
    /// </remarks>
    private sealed class FakeRemoteBrowser : IRemoteBrowser, IRemoteNoClobberCapability, IRemoteNoClobberPublisher
    {
        /// <summary>Files present on the destination, path to content.</summary>
        public Dictionary<string, string> ExistingFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>Set to false to model a transport that cannot publish without replacing.</summary>
        public bool SupportsNoClobber { get; set; } = true;

        /// <summary>Runs just before a publication is evaluated, with the destination path.</summary>
        public Action<string>? BeforePublish { get; set; }

        /// <summary>Runs just before a directory reservation is evaluated, with the destination path.</summary>
        public Action<string>? BeforeReserve { get; set; }

        public List<(string LocalPath, string RemotePath, bool LocalPathExisted)> PublishCalls { get; } = [];

        public List<string> ReserveCalls { get; } = [];

        /// <summary>Publication of this path reports an unconfirmed outcome.</summary>
        public string? UnconfirmedPublishRemotePath { get; set; }

        /// <summary>Reservation of this path reports an unconfirmed outcome.</summary>
        public string? UnconfirmedReserveRemotePath { get; set; }

        /// <inheritdoc />
        public IRemoteNoClobberPublisher? NoClobberPublisher => SupportsNoClobber ? this : null;

        /// <inheritdoc />
        public Task PublishFileIfAbsentAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            EnsureConnected();

            // Recorded before the hook runs. A hook that throws still leaves evidence that the call was
            // attempted, which is exactly what a test about an interrupted publication needs to see.
            PublishCalls.Add((localPath, remotePath, File.Exists(localPath)));
            BeforePublish?.Invoke(remotePath);

            if (string.Equals(UnconfirmedPublishRemotePath, remotePath, StringComparison.Ordinal))
            {
                throw new RemoteNoClobberPublishUnavailableException(
                    remotePath,
                    "injected transport failure");
            }

            if (ExistingFiles.ContainsKey(remotePath) || Listings.ContainsKey(remotePath))
            {
                throw new RemoteDestinationExistsException(remotePath);
            }

            AddFileToModel(
                remotePath,
                File.Exists(localPath) ? File.ReadAllText(localPath) : string.Empty);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Places a file in the destination model so a later listing of its parent reveals it.
        /// </summary>
        /// <remarks>
        /// Content and listing are one state, not two. If a published file only landed in
        /// <see cref="ExistingFiles"/>, a refresh would read <see cref="Listings"/> and show nothing,
        /// and an oracle asserting "the destination became visible" would be measuring the fake's
        /// bookkeeping rather than the behaviour under test.
        /// </remarks>
        public void AddFileToModel(string remotePath, string content)
        {
            ExistingFiles[remotePath] = content;

            string parent = ParentOf(remotePath);
            string name = remotePath[(remotePath.LastIndexOf('/') + 1)..];
            List<SftpFileInfo> entries = Listings.TryGetValue(parent, out IReadOnlyList<SftpFileInfo>? existing)
                ? [.. existing]
                : [];

            if (!entries.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal)))
            {
                entries.Add(CreateEntry(name, remotePath, isDirectory: false));
            }

            Listings[parent] = entries;
        }

        private static string ParentOf(string remotePath)
        {
            int lastSlash = remotePath.LastIndexOf('/');
            return lastSlash <= 0 ? "/" : remotePath[..lastSlash];
        }

        /// <inheritdoc />
        public Task CreateDirectoryExclusiveAsync(string remotePath, CancellationToken ct = default)
        {
            EnsureConnected();

            // Recorded before the hook, for the same reason as a publication.
            ReserveCalls.Add(remotePath);
            BeforeReserve?.Invoke(remotePath);

            if (string.Equals(UnconfirmedReserveRemotePath, remotePath, StringComparison.Ordinal))
            {
                throw new RemoteNoClobberPublishUnavailableException(
                    remotePath,
                    "injected transport failure");
            }

            if (Listings.ContainsKey(remotePath) || ExistingFiles.ContainsKey(remotePath))
            {
                throw new RemoteDestinationExistsException(remotePath);
            }

            // Exists as a directory, and visible from its parent: a reservation a later listing could
            // not see would let an oracle about a partial tree pass without the tree being there.
            Listings[remotePath] = [];

            string parent = ParentOf(remotePath);
            string name = remotePath[(remotePath.LastIndexOf('/') + 1)..];
            List<SftpFileInfo> siblings = Listings.TryGetValue(parent, out IReadOnlyList<SftpFileInfo>? existing)
                ? [.. existing]
                : [];
            if (!siblings.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal)))
            {
                siblings.Add(CreateEntry(name, remotePath, isDirectory: true));
                Listings[parent] = siblings;
            }

            return Task.CompletedTask;
        }

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

        private bool _isConnected = true;

        public string CurrentDirectory => "/";

        public bool IsConnected
        {
            get
            {
                if (ThrowObjectDisposedOnIsConnected)
                {
                    throw new ObjectDisposedException(nameof(FakeRemoteBrowser));
                }

                return _isConnected;
            }

            set => _isConnected = value;
        }

        public bool ThrowObjectDisposedOnIsConnected { get; set; }

        public bool ThrowObjectDisposedOnDownload { get; set; }

        public int RenameCallCount { get; private set; }

        public string? LastRenamedOldPath { get; private set; }

        public string? LastRenamedNewPath { get; private set; }

        public List<(string Source, string Destination, bool Recursive)> CopyCalls { get; } = [];

        public Dictionary<string, IReadOnlyList<SftpFileInfo>> Listings { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> CreateDirectoryFailures { get; } = new(StringComparer.Ordinal);

        public List<string> CreateDirectoryCalls { get; } = [];

        public List<(string RemotePath, string LocalPath)> DownloadCalls { get; } = [];

        public List<(string LocalPath, string RemotePath, bool LocalPathExisted)> UploadCalls { get; } = [];

        public List<string> DeleteCalls { get; } = [];

        public string? FailUploadRemotePath { get; set; }

        public Exception? DeleteException { get; set; }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
        {
            EnsureConnected();
            string key = path ?? CurrentDirectory;
            if (Listings.TryGetValue(key, out IReadOnlyList<SftpFileInfo>? entries))
            {
                return Task.FromResult(entries);
            }

            throw new DirectoryNotFoundException(key);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            EnsureConnected();
            if (ThrowObjectDisposedOnDownload)
            {
                throw new ObjectDisposedException(nameof(FakeRemoteBrowser));
            }

            DownloadCalls.Add((remotePath, localPath));
            string? directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(localPath, $"downloaded {remotePath}");
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            EnsureConnected();
            if (string.Equals(FailUploadRemotePath, remotePath, StringComparison.Ordinal))
            {
                throw new IOException("upload failed");
            }

            UploadCalls.Add((localPath, remotePath, File.Exists(localPath)));
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            EnsureConnected();
            CreateDirectoryCalls.Add(path);
            if (CreateDirectoryFailures.Contains(path))
            {
                throw new IOException("mkdir failed");
            }

            Listings.TryAdd(path, []);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            EnsureConnected();
            DeleteCalls.Add(path);
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            EnsureConnected();
            RenameCallCount++;
            LastRenamedOldPath = oldPath;
            LastRenamedNewPath = newPath;
            return Task.CompletedTask;
        }

        /// <summary>
        /// When set, runs after the call is recorded. Lets a test hold a copy in flight so a second
        /// operation meets a genuinely running transfer instead of a hand-set flag.
        /// </summary>
        public Func<string, string, bool, CancellationToken, Task>? CopyHandler { get; set; }

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
        {
            EnsureConnected();
            CopyCalls.Add((sourcePath, destinationPath, recursive));
            return CopyHandler is null
                ? Task.CompletedTask
                : CopyHandler(sourcePath, destinationPath, recursive, ct);
        }

        public void Disconnect()
        {
            IsConnected = false;
        }

        public void Dispose()
        {
            IsConnected = false;
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Browser is not connected.");
            }
        }
    }
}
