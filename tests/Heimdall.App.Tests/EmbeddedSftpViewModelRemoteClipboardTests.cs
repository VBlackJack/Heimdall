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
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=server;port=22;user=alice");
        clipboard.Set(CreateContent("host=other;port=22;user=alice"));

        await targetPane.PasteClipboardAsync();

        Assert.Empty(targetBrowser.CopyCalls);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointCopiesFileViaDownloadTempUpload()
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
        SetBrowser(sourcePane, sourceBrowser);
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(sourcePane, "host=a;port=22;user=alice");
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        sourcePane.SetSelection([CreateEntry("a.txt", "/src/a.txt", isDirectory: false)], null);

        sourcePane.CopySelectedCommand.Execute(null);
        await targetPane.PasteClipboardAsync();

        (string RemotePath, string LocalPath) download = Assert.Single(sourceBrowser.DownloadCalls);
        Assert.Equal("/src/a.txt", download.RemotePath);
        var upload = Assert.Single(targetBrowser.UploadCalls);
        Assert.Equal("/dst/a.txt", upload.RemotePath);
        Assert.True(upload.LocalPathExisted);
        Assert.False(File.Exists(upload.LocalPath));
        Assert.Empty(targetBrowser.CopyCalls);
        Assert.NotNull(clipboard.Current);
    }

    [Fact]
    public async Task PasteClipboardAsync_DifferentEndpointDirectoryToleratesExistingDestinationDirectory()
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
        targetBrowser.CreateDirectoryFailures.Add("/dst/proj");
        targetBrowser.Listings["/dst/proj"] = [];
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(new SftpClipboardContent(
            [CreateEntry("proj", "/src/proj", isDirectory: true)],
            "/src",
            SftpClipboardMode.Copy,
            "host=a;port=22;user=alice",
            sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal(["/dst/proj"], targetBrowser.CreateDirectoryCalls);
        Assert.Contains(targetBrowser.UploadCalls, call => call.RemotePath == "/dst/proj/readme.txt");
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

        Assert.Single(targetBrowser.UploadCalls);
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
        FakeRemoteBrowser targetBrowser = new() { FailUploadRemotePath = "/dst/b.txt" };
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
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal("Source session no longer available.", targetPane.StatusText);
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
        SetBrowser(targetPane, targetBrowser);
        SetEndpointKey(targetPane, "host=b;port=22;user=bob");
        clipboard.Set(CreateContent("host=a;port=22;user=alice", sourceBrowser));

        await targetPane.PasteClipboardAsync();

        Assert.Equal("Source session no longer available.", targetPane.StatusText);
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

        Assert.Equal("SftpStatusTransferFailed", targetPane.StatusText);
        Assert.NotEqual("Source session no longer available.", targetPane.StatusText);
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
    // entries after the challenger has been refused. Watching the token the browser receives would
    // measure nothing: the callers still pass CancellationToken.None down to CopyAsync, so a
    // captured token is always CancellationToken.None and a cancellation mutant survives it.

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

    private static void SetEndpointKey(EmbeddedSftpViewModel viewModel, string endpointKey)
    {
        MethodInfo? method = typeof(EmbeddedSftpViewModel).GetMethod(
            "SetEndpointKey",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [endpointKey]);
    }

    private sealed class FakeRemoteBrowser : IRemoteBrowser
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
