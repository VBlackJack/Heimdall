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
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelTests
{
    [Fact]
    public void Constructor_RequiresUiDispatcher_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new EmbeddedSftpViewModel(null!));
    }

    [Fact]
    public void CurrentPath_Set_UpdatesPathBarText()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        viewModel.CurrentPath = "/var/log";

        Assert.Equal("/var/log", viewModel.PathBarText);
    }

    [Fact]
    public void IsToolbarEnabled_RequiresConnectedAndNotLoading()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            IsConnected = true
        };

        Assert.True(viewModel.IsToolbarEnabled);

        viewModel.IsLoading = true;

        Assert.False(viewModel.IsToolbarEnabled);

        viewModel.IsLoading = false;
        viewModel.IsConnected = false;

        Assert.False(viewModel.IsToolbarEnabled);
    }

    [Fact]
    public void CanNavigateBack_RequiresToolbarStateAndBackHistory()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            IsConnected = true
        };

        Assert.False(viewModel.CanNavigateBack);

        viewModel.CanGoBack = true;

        Assert.True(viewModel.CanNavigateBack);

        viewModel.IsLoading = true;

        Assert.False(viewModel.CanNavigateBack);

        viewModel.IsLoading = false;
        viewModel.IsConnected = false;

        Assert.False(viewModel.CanNavigateBack);
    }

    [Fact]
    public void IsDisconnected_IsInverseOfIsConnected()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        Assert.True(viewModel.IsDisconnected);

        viewModel.IsConnected = true;

        Assert.False(viewModel.IsDisconnected);
    }

    [Fact]
    public void SetSelection_UpdatesSelectedFileSelectedFilesAndSelectionInfoText()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SftpFileInfo file = new(
            "app.log",
            "/var/log/app.log",
            false,
            1,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");
        SftpFileInfo directory = new(
            "archive",
            "/var/log/archive",
            true,
            0,
            DateTime.UnixEpoch,
            "rwxr-xr-x",
            "1000",
            "1000");
        IReadOnlyList<SftpFileInfo> selectedFiles = [file, directory];

        viewModel.SetSelection(selectedFiles, file);

        Assert.Same(file, viewModel.SelectedFile);
        Assert.Same(selectedFiles, viewModel.SelectedFiles);
        Assert.Equal("2 selected (1 B)", viewModel.SelectionInfoText);
    }

    [Fact]
    public void OpenSelectedInTerminalCommand_RaisesDirectoryPathOrCurrentPath()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/home/admin"
        };
        SftpFileInfo directory = new(
            "logs",
            "/home/admin/logs",
            true,
            0,
            DateTime.UnixEpoch,
            "rwxr-xr-x",
            "1000",
            "1000");
        SftpFileInfo file = new(
            "app.log",
            "/home/admin/app.log",
            false,
            10,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");
        string? requestedPath = null;
        viewModel.OpenInTerminalRequested += path => requestedPath = path;

        viewModel.SetSelection([directory], directory);
        viewModel.OpenSelectedInTerminalCommand.Execute(null);

        Assert.Equal("/home/admin/logs", requestedPath);

        requestedPath = null;
        viewModel.SetSelection([file], file);
        viewModel.OpenSelectedInTerminalCommand.Execute(null);

        Assert.Equal("/home/admin", requestedPath);
    }

    [Fact]
    public void SetErrorStatus_SetsIsErrorHighlighted()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        try
        {
            viewModel.SetErrorStatus("Connection failed");

            Assert.True(viewModel.IsErrorHighlighted);
        }
        finally
        {
            viewModel.MarkDisposed();
        }
    }

    [Fact]
    public void UpdateStatus_AfterErrorStatus_ClearsIsErrorHighlighted()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        viewModel.SetErrorStatus("Connection failed");
        viewModel.UpdateStatus("Ready");

        Assert.False(viewModel.IsErrorHighlighted);
    }

    [Fact]
    public void SecurityNotice_DefaultsHidden()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        Assert.False(viewModel.IsSecurityNoticeVisible);
        Assert.Equal(string.Empty, viewModel.SecurityNoticeText);
    }

    [Fact]
    public void ShowSecurityNotice_SetsTextAndVisibility()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        viewModel.ShowSecurityNotice("FTPS data-channel identity is not verified");

        Assert.True(viewModel.IsSecurityNoticeVisible);
        Assert.Equal("FTPS data-channel identity is not verified", viewModel.SecurityNoticeText);
    }

    [Fact]
    public void SecurityNotice_RemainsVisibleWhenStatusChanges()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        viewModel.ShowSecurityNotice("FTPS data-channel identity is not verified");
        viewModel.UpdateStatus("Ready");

        Assert.True(viewModel.IsSecurityNoticeVisible);
        Assert.Equal("FTPS data-channel identity is not verified", viewModel.SecurityNoticeText);
        Assert.Equal("Ready", viewModel.StatusText);
    }

    [Fact]
    public async Task LoadDirectoryAsync_MarkDisposedDuringList_CancelsWithoutErrorAndClearsLoading()
    {
        FakeUiDispatcher dispatcher = new();
        TaskCompletionSource<CancellationToken> capturedToken = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser browser = new()
        {
            ListDirectoryHandler = async (_, ct) =>
            {
                capturedToken.TrySetResult(ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            }
        };
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetBrowser(viewModel, browser);

        Task loadTask = viewModel.LoadDirectoryAsync("/slow");
        CancellationToken listingToken = await capturedToken.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsLoading);
        Assert.False(listingToken.IsCancellationRequested);

        viewModel.MarkDisposed();
        await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(listingToken.IsCancellationRequested);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.IsErrorStatus);
        Assert.Equal("Ready", viewModel.StatusText);
    }

    [Fact]
    public async Task NavigateInitialAsync_PreferredPath_LoadsPreferredPathWithoutHistory()
    {
        FakeUiDispatcher dispatcher = new();
        List<string?> requestedPaths = [];
        FakeRemoteBrowser browser = new()
        {
            ListDirectoryHandler = (path, _) =>
            {
                requestedPaths.Add(path);
                return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
            }
        };
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetBrowser(viewModel, browser);

        await viewModel.NavigateInitialAsync("/var/log");

        Assert.Collection(requestedPaths, path => Assert.Equal("/var/log", path));
        Assert.Equal("/var/log", viewModel.CurrentPath);
        Assert.False(viewModel.CanGoBack);
        Assert.False(viewModel.IsErrorStatus);
    }

    [Fact]
    public async Task NavigateInitialAsync_MissingPreferredPath_FallsBackToHomeWithoutError()
    {
        FakeUiDispatcher dispatcher = new();
        List<string?> requestedPaths = [];
        FakeRemoteBrowser browser = new()
        {
            ListDirectoryHandler = (path, _) =>
            {
                requestedPaths.Add(path);
                if (string.Equals(path, "/missing", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("gone");
                }

                return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
            }
        };
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetBrowser(viewModel, browser);

        await viewModel.NavigateInitialAsync("/missing");

        Assert.Collection(
            requestedPaths,
            path => Assert.Equal("/missing", path),
            path => Assert.Equal("/", path));
        Assert.Equal("/", viewModel.CurrentPath);
        Assert.False(viewModel.CanGoBack);
        Assert.False(viewModel.IsErrorStatus);
        Assert.Equal("Ready", viewModel.StatusText);
    }

    [Fact]
    public async Task NavigateInitialAsync_MissingPreferredPath_DoesNotSetErrorDuringFallback()
    {
        FakeUiDispatcher dispatcher = new();
        bool errorWasSetBeforeHomeListing = false;
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new()
        {
            ListDirectoryHandler = (path, _) =>
            {
                if (string.Equals(path, "/missing", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("gone");
                }

                errorWasSetBeforeHomeListing |= viewModel.IsErrorStatus;
                return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
            }
        };
        SetBrowser(viewModel, browser);

        await viewModel.NavigateInitialAsync("/missing");

        Assert.False(errorWasSetBeforeHomeListing);
        Assert.False(viewModel.IsErrorStatus);
        Assert.Equal("/", viewModel.CurrentPath);
    }

    [Fact]
    public async Task NavigateInitialAsync_BlankPreferredPath_LoadsHome()
    {
        FakeUiDispatcher dispatcher = new();
        List<string?> requestedPaths = [];
        FakeRemoteBrowser browser = new()
        {
            ListDirectoryHandler = (path, _) =>
            {
                requestedPaths.Add(path);
                return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
            }
        };
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetBrowser(viewModel, browser);

        await viewModel.NavigateInitialAsync("   ");

        Assert.Collection(requestedPaths, path => Assert.Equal("/", path));
        Assert.Equal("/", viewModel.CurrentPath);
        Assert.False(viewModel.IsErrorStatus);
    }

    [Theory]
    [InlineData(1, 0, (int)EmbeddedSftpViewModel.SftpDownloadOutcome.Completed)]
    [InlineData(2, 1, (int)EmbeddedSftpViewModel.SftpDownloadOutcome.CompletedWithSkippedDirectories)]
    [InlineData(0, 3, (int)EmbeddedSftpViewModel.SftpDownloadOutcome.OnlyDirectoriesSkipped)]
    [InlineData(0, 0, (int)EmbeddedSftpViewModel.SftpDownloadOutcome.Empty)]
    public void ClassifyDownloadOutcome_ReturnsExpectedOutcome(
        int downloadedFiles,
        int skippedDirectories,
        int expected)
    {
        var actual = EmbeddedSftpViewModel.ClassifyDownloadOutcome(
            downloadedFiles,
            skippedDirectories);

        Assert.Equal((EmbeddedSftpViewModel.SftpDownloadOutcome)expected, actual);
    }

    [Fact]
    public async Task DownloadFilesAsync_DirectoryOnlySelection_DoesNotDownloadAndReportsSkippedFolders()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);

        await viewModel.DownloadFilesAsync(
            [CreateRemoteEntry("logs", "/var/log", isDirectory: true)],
            Path.GetTempPath());

        Assert.Equal(0, browser.DownloadCallCount);
        Assert.Equal("No files downloaded \u2014 folders aren't supported.", viewModel.StatusText);
        Assert.False(viewModel.IsErrorStatus);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData(@"..\secret.txt")]
    [InlineData("nested/report.txt")]
    [InlineData(@"nested\report.txt")]
    public async Task DownloadFilesAsync_UnsafeRemoteFileName_DoesNotDownload(string fileName)
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);

        await viewModel.DownloadFilesAsync(
            [CreateRemoteEntry(fileName, $"/var/reports/{fileName}", isDirectory: false)],
            Path.GetTempPath());

        Assert.Equal(0, browser.DownloadCallCount);
        Assert.False(viewModel.IsErrorStatus);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Fact]
    public void LocalDownloadPath_ValidFileName_ResolvesInsideTargetFolder()
    {
        string targetFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        bool resolved = LocalDownloadPath.TryResolveContained(
            targetFolder,
            "report.txt",
            out string localPath);

        Assert.True(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(targetFolder, "report.txt")),
            localPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData(@"..\secret.txt")]
    [InlineData("nested/report.txt")]
    [InlineData(@"nested\report.txt")]
    public void LocalDownloadPath_UnsafeFileName_ReturnsFalse(string fileName)
    {
        string targetFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        bool resolved = LocalDownloadPath.TryResolveContained(
            targetFolder,
            fileName,
            out string localPath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, localPath);
    }

    [Fact]
    public async Task UploadFilesAsync_WhenTransferAlreadyInProgress_DoesNotUpload()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            IsTransferInProgress = true
        };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);

        await viewModel.UploadFilesAsync(["C:\\temp\\app.log"]);

        Assert.Equal(0, browser.UploadCallCount);
        Assert.True(viewModel.IsTransferInProgress);
        Assert.Equal("A file transfer is already in progress.", viewModel.StatusText);
    }

    [Fact]
    public async Task UploadEntriesAsync_ConcurrentStarts_RunExactlyOneTransfer()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/srv"
        };
        TaskCompletionSource uploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRemoteBrowser browser = new()
        {
            UploadFileHandler = async (_, _, ct) =>
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(ct);
            }
        };
        SetBrowser(viewModel, browser);

        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string filePath = Path.Combine(root, "app.log");
        await File.WriteAllTextAsync(filePath, "payload");

        try
        {
            Task first = viewModel.UploadEntriesAsync([filePath], "/srv");
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task second = viewModel.UploadEntriesAsync([filePath], "/srv");
            await second.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, browser.UploadCallCount);
            Assert.True(viewModel.IsTransferInProgress);

            releaseUpload.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, browser.UploadCallCount);
            Assert.False(viewModel.IsTransferInProgress);
        }
        finally
        {
            releaseUpload.TrySetResult();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadEntriesAsync_RemoteDirectoryAlreadyExists_MergesAndUploadsFiles()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/srv"
        };
        FakeRemoteBrowser browser = new()
        {
            // SSH.NET reports an existing directory with the generic message "Failure" (not
            // "already exists"); tolerance must not depend on the error text.
            CreateDirectoryException = new IOException("Failure"),
            // The existence probe lists the directory; a successful listing means it exists -> merge.
            ListDirectoryHandler = (_, _) => Task.FromResult<IReadOnlyList<SftpFileInfo>>([])
        };
        SetBrowser(viewModel, browser);

        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string projectDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(projectDir);
        string filePath = Path.Combine(projectDir, "a.txt");
        await File.WriteAllTextAsync(filePath, "payload");

        try
        {
            await viewModel.UploadEntriesAsync([projectDir], "/srv");

            // mkdir failed, the probe confirmed the directory exists, so the upload proceeded.
            Assert.Equal(1, browser.CreateDirectoryCallCount);
            Assert.Equal(1, browser.UploadCallCount);
            Assert.Equal("/srv/proj/a.txt", browser.LastUploadedRemotePath);
            Assert.False(viewModel.IsTransferInProgress);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadEntriesAsync_MkdirFailsAndDirectoryAbsent_AbortsWithoutUploading()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/srv"
        };
        FakeRemoteBrowser browser = new()
        {
            // A genuine mkdir failure (e.g. permission), and the probe finds no such directory.
            CreateDirectoryException = new IOException("Failure"),
            ListDirectoryHandler = (_, _) =>
                Task.FromException<IReadOnlyList<SftpFileInfo>>(new IOException("No such file")),
        };
        SetBrowser(viewModel, browser);

        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string projectDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "a.txt"), "payload");

        try
        {
            await viewModel.UploadEntriesAsync([projectDir], "/srv");

            // The directory does not exist, so the failure is genuine: nothing is uploaded.
            Assert.Equal(1, browser.CreateDirectoryCallCount);
            Assert.Equal(0, browser.UploadCallCount);
            Assert.False(viewModel.IsTransferInProgress);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadViaSudoAsync_DoesNotStageThroughBrowserWhenSshSetupFails()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.UploadViaSudoAsync(
                Path.Combine(Path.GetTempPath(), "app.conf"),
                "/etc/app.conf",
                CancellationToken.None));

        Assert.Equal(0, browser.UploadCallCount);
        Assert.Equal(0, browser.ChmodCallCount);
        Assert.Equal(0, browser.DeleteCallCount);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("nested/folder")]
    [InlineData(@"nested\folder")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task CreateFolderAsync_InvalidChildName_DoesNotCreateRemoteDirectory(string folderName)
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/var/www"
        };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(new ConfirmingDialogService(folderName));

        await viewModel.CreateFolderAsync();

        Assert.Equal(0, browser.CreateDirectoryCallCount);
        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer["ErrorInvalidFileName"], viewModel.StatusText);
    }

    [Fact]
    public async Task CreateFolderAsync_ValidChildName_TrimsAndCreatesUnderCurrentPath()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/var/www"
        };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        viewModel.SetDialogService(new ConfirmingDialogService(" reports "));

        await viewModel.CreateFolderAsync();

        Assert.Equal(1, browser.CreateDirectoryCallCount);
        Assert.Equal("/var/www/reports", browser.LastCreatedDirectoryPath);
        Assert.False(viewModel.IsErrorStatus);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("nested/file")]
    [InlineData(@"nested\file")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task RenameEntryAsync_InvalidChildName_DoesNotRenameRemoteEntry(string newName)
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/var/www"
        };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(new ConfirmingDialogService(newName));
        SftpFileInfo entry = CreateRemoteEntry("app.log", "/var/www/app.log", isDirectory: false);

        await viewModel.RenameEntryAsync(entry);

        Assert.Equal(0, browser.RenameCallCount);
        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer["ErrorInvalidFileName"], viewModel.StatusText);
    }

    [Fact]
    public async Task RenameEntryAsync_ValidChildName_TrimsAndRenamesUnderCurrentPath()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher)
        {
            CurrentPath = "/var/www"
        };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        viewModel.SetDialogService(new ConfirmingDialogService(" app.new "));
        SftpFileInfo entry = CreateRemoteEntry("app.log", "/var/www/app.log", isDirectory: false);

        await viewModel.RenameEntryAsync(entry);

        Assert.Equal(1, browser.RenameCallCount);
        Assert.Equal("/var/www/app.log", browser.LastRenamedOldPath);
        Assert.Equal("/var/www/app.new", browser.LastRenamedNewPath);
        Assert.False(viewModel.IsErrorStatus);
    }

    [Fact]
    public async Task ChmodEntriesAsync_FtpThroughLoggingDecorator_ReportsOneUnsupportedMessageWithoutSuccess()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        using FtpBrowser ftpBrowser = new();
        CapturingOperationLog operationLog = new();
        using LoggingRemoteBrowser loggingBrowser = new(
            ftpBrowser,
            operationLog,
            static () => true,
            "FTP",
            "ftp.example");
        ConfirmingDialogService dialogService = new("755");
        SetBrowser(viewModel, loggingBrowser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(dialogService);
        SftpFileInfo[] entries = Enumerable.Range(1, 5)
            .Select(index => CreateRemoteEntry(
                $"file-{index}.txt",
                $"/srv/file-{index}.txt",
                isDirectory: false))
            .ToArray();
        List<string> statusUpdates = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EmbeddedSftpViewModel.StatusText))
            {
                statusUpdates.Add(viewModel.StatusText);
            }
        };

        await viewModel.ChmodEntriesAsync(entries);

        Assert.DoesNotContain(localizer["SftpChmodSuccess"], statusUpdates);
        Assert.Equal(1, dialogService.InputCallCount);
        Assert.Equal(localizer["SftpChmodNotSupported"], viewModel.StatusText);
        Assert.NotEqual(localizer["SftpChmodSuccess"], viewModel.StatusText);
        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal([localizer["SftpChmodNotSupported"]], statusUpdates);
        Assert.Empty(operationLog.Records);
    }

    [Fact]
    public async Task ChmodEntriesAsync_SupportedBrowser_MutatesEveryEntryThenRefreshes()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(new ConfirmingDialogService("640"));
        SftpFileInfo[] entries =
        [
            CreateRemoteEntry("one.txt", "/srv/one.txt", isDirectory: false),
            CreateRemoteEntry("two.txt", "/srv/two.txt", isDirectory: false),
        ];
        List<string> statusUpdates = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EmbeddedSftpViewModel.StatusText))
            {
                statusUpdates.Add(viewModel.StatusText);
            }
        };

        await viewModel.ChmodEntriesAsync(entries);

        Assert.Equal(2, browser.ChmodCallCount);
        Assert.Equal(1, browser.ListDirectoryCallCount);
        Assert.Contains(localizer["SftpChmodSuccess"], statusUpdates);
        Assert.False(viewModel.IsErrorStatus);
    }

    [Fact]
    public async Task ChmodEntriesAsync_BrowserFailure_ReportsFailureWithoutRefreshing()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new()
        {
            ChmodException = new IOException("chmod failed"),
        };
        SetBrowser(viewModel, browser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(new ConfirmingDialogService("755"));
        SftpFileInfo entry = CreateRemoteEntry("one.txt", "/srv/one.txt", isDirectory: false);

        await viewModel.ChmodEntriesAsync([entry]);

        Assert.Equal(1, browser.ChmodCallCount);
        Assert.Equal(0, browser.ListDirectoryCallCount);
        Assert.Equal(localizer["SftpStatusTransferFailed"], viewModel.StatusText);
        Assert.True(viewModel.IsErrorStatus);
    }

    [Fact]
    public async Task DeleteEntriesAsync_ProtectedRoot_DoesNotCallBrowserDelete()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SetLocalizer(viewModel, localizer);
        viewModel.SetDialogService(new ConfirmingDialogService());

        await viewModel.DeleteEntriesAsync([CreateRemoteEntry("/", "/", isDirectory: true)]);

        Assert.Equal(0, browser.DeleteCallCount);
        Assert.True(viewModel.IsErrorStatus);
        Assert.Equal(localizer["SftpErrorProtectedRoot"], viewModel.StatusText);
    }

    [Fact]
    public void CutSelectedCommand_CapturesSelectionAndSourceDirectoryAsCut()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src" };
        SftpFileInfo entry = CreateRemoteEntry("a.txt", "/src/a.txt", isDirectory: false);
        viewModel.SetSelection([entry], entry);

        viewModel.CutSelectedCommand.Execute(null);

        Assert.True(viewModel.HasClipboard);
        Assert.NotNull(viewModel.Clipboard);
        Assert.Equal(SftpClipboardMode.Cut, viewModel.Clipboard!.Mode);
        Assert.Equal("/src", viewModel.Clipboard.SourceDirectory);
        Assert.Single(viewModel.Clipboard.Entries);
    }

    [Fact]
    public void CopySelectedCommand_CapturesSelectionAsCopy()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src" };
        SftpFileInfo entry = CreateRemoteEntry("a.txt", "/src/a.txt", isDirectory: false);
        viewModel.SetSelection([entry], entry);

        viewModel.CopySelectedCommand.Execute(null);

        Assert.NotNull(viewModel.Clipboard);
        Assert.Equal(SftpClipboardMode.Copy, viewModel.Clipboard!.Mode);
    }

    [Fact]
    public async Task PasteClipboardAsync_CopyMode_CopiesIntoCurrentDirectoryAndKeepsClipboard()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src", IsConnected = true };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SftpFileInfo directory = CreateRemoteEntry("project", "/src/project", isDirectory: true);
        viewModel.SetSelection([directory], directory);
        viewModel.CopySelectedCommand.Execute(null);

        viewModel.CurrentPath = "/dst";
        viewModel.UnfilteredEntries = [];

        await viewModel.PasteClipboardAsync();

        (string Source, string Destination, bool Recursive) copy = Assert.Single(browser.CopyCalls);
        Assert.Equal("/src/project", copy.Source);
        Assert.Equal("/dst/project", copy.Destination);
        Assert.True(copy.Recursive);
        Assert.NotNull(viewModel.Clipboard);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutMode_MovesWithFullCrossDirectoryPathAndClearsClipboard()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src", IsConnected = true };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SftpFileInfo file = CreateRemoteEntry("a.txt", "/src/a.txt", isDirectory: false);
        viewModel.SetSelection([file], file);
        viewModel.CutSelectedCommand.Execute(null);

        viewModel.CurrentPath = "/dst";
        viewModel.UnfilteredEntries = [];

        await viewModel.PasteClipboardAsync();

        Assert.Equal(1, browser.RenameCallCount);
        Assert.Equal("/src/a.txt", browser.LastRenamedOldPath);
        Assert.Equal("/dst/a.txt", browser.LastRenamedNewPath);
        Assert.Null(viewModel.Clipboard);
        Assert.False(viewModel.HasClipboard);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutMode_SameDirectorySameName_SkipsSelfMoveButConsumesClipboard()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src", IsConnected = true };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SftpFileInfo file = CreateRemoteEntry("a.txt", "/src/a.txt", isDirectory: false);
        viewModel.SetSelection([file], file);
        viewModel.CutSelectedCommand.Execute(null);

        // Paste back into the same directory; the listing still contains the entry itself.
        viewModel.UnfilteredEntries = [file];

        await viewModel.PasteClipboardAsync();

        Assert.Equal(0, browser.RenameCallCount);
        Assert.Null(viewModel.Clipboard);
    }

    [Fact]
    public async Task DuplicateEntriesAsync_IntoSameDirectory_CopiesWithCollisionFreeCopyName()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src", IsConnected = true };
        FakeRemoteBrowser browser = new();
        SetBrowser(viewModel, browser);
        SftpFileInfo file = CreateRemoteEntry("report.txt", "/src/report.txt", isDirectory: false);
        viewModel.UnfilteredEntries = [file];

        await viewModel.DuplicateEntriesAsync([file]);

        (string Source, string Destination, bool Recursive) copy = Assert.Single(browser.CopyCalls);
        Assert.Equal("/src/report.txt", copy.Source);
        Assert.Equal("/src/report (copy).txt", copy.Destination);
        Assert.False(copy.Recursive);
    }

    [Fact]
    public async Task PasteClipboardAsync_CutMode_MidBatchFailure_RetainsUnmovedEntriesAndSurfacesError()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher) { CurrentPath = "/src", IsConnected = true };
        FakeRemoteBrowser browser = new() { RenameFailurePath = "/src/b.txt" };
        SetBrowser(viewModel, browser);
        SftpFileInfo a = CreateRemoteEntry("a.txt", "/src/a.txt", isDirectory: false);
        SftpFileInfo b = CreateRemoteEntry("b.txt", "/src/b.txt", isDirectory: false);
        SftpFileInfo c = CreateRemoteEntry("c.txt", "/src/c.txt", isDirectory: false);
        viewModel.SetSelection([a, b, c], a);
        viewModel.CutSelectedCommand.Execute(null);

        viewModel.CurrentPath = "/dst";
        viewModel.UnfilteredEntries = [];

        await viewModel.PasteClipboardAsync();

        // a moved, b threw, c was never reached: clipboard retains exactly b and c, still Cut.
        Assert.NotNull(viewModel.Clipboard);
        Assert.Equal(SftpClipboardMode.Cut, viewModel.Clipboard!.Mode);
        Assert.Equal("/src", viewModel.Clipboard.SourceDirectory);
        List<string> retained = viewModel.Clipboard.Entries.Select(e => e.FullPath).ToList();
        Assert.Equal(2, retained.Count);
        Assert.DoesNotContain("/src/a.txt", retained);
        Assert.Contains("/src/b.txt", retained);
        Assert.Contains("/src/c.txt", retained);
        Assert.True(viewModel.IsErrorStatus);
    }

    [Fact]
    public void CancelTransferCommand_NoTransferRunning_DoesNotThrow()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);

        Exception? exception = Record.Exception(() => viewModel.CancelTransferCommand.Execute(null));

        Assert.Null(exception);
    }

    [Fact]
    public void UpdateTransferProgress_UpdatesProgressValueAndStatusText()
    {
        FakeUiDispatcher dispatcher = new();
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SftpTransferProgress progress = new("app.log", 512, 1024, true);

        viewModel.UpdateTransferProgress(progress);

        Assert.Equal(50, viewModel.TransferProgressValue);
        string transferred = EmbeddedSftpViewModel.FormatSize(512);
        string total = EmbeddedSftpViewModel.FormatSize(1024);
        Assert.Equal($"\u2191 app.log \u2014 {transferred} / {total} (50%)", viewModel.TransferStatusText);
    }

    [Fact]
    public async Task DescribeTransferError_GenericException_ReturnsLocalizedMessageWithoutRawText()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetLocalizer(viewModel, localizer);
        const string rawMessage = "SSH.NET permission denied: subsystem request failed";

        string message = viewModel.DescribeTransferError(new InvalidOperationException(rawMessage));

        Assert.Equal(localizer["SftpStatusTransferFailed"], message);
        Assert.DoesNotContain(rawMessage, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(SudoFailureKind.PasswordUnavailable), "ErrorSudoPasswordUnavailable")]
    [InlineData(nameof(SudoFailureKind.PasswordRejected), "ErrorSudoPasswordRejected")]
    [InlineData(nameof(SudoFailureKind.None), "ErrorSudoAuthenticationFailed")]
    public async Task DescribeTransferError_SudoAuthenticationException_ReturnsLocalizedSudoMessage(
        string kindName,
        string expectedKey)
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetLocalizer(viewModel, localizer);
        var kind = Enum.Parse<SudoFailureKind>(kindName);
        const string rawStderr = "sudo raw stderr that must stay out of the UI";

        string message = viewModel.DescribeTransferError(new SudoAuthenticationException(kind, rawStderr));

        Assert.Equal(localizer[expectedKey], message);
        Assert.DoesNotContain(rawStderr, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeTransferError_SudoEditFileTooLargeException_ReturnsLocalizedSizeLimit()
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetLocalizer(viewModel, localizer);
        long fileSize = RemoteFileEditor.MaxSudoEditFileBytes + 1;
        var exception = new SudoEditFileTooLargeException(
            "/etc/ssh/config",
            fileSize,
            RemoteFileEditor.MaxSudoEditFileBytes);

        string message = viewModel.DescribeTransferError(exception);

        string expected = localizer.Format(
            "SftpErrorSudoEditFileTooLarge",
            EmbeddedSftpViewModel.FormatSize(fileSize),
            EmbeddedSftpViewModel.FormatSize(RemoteFileEditor.MaxSudoEditFileBytes));
        Assert.Equal(expected, message);
        Assert.DoesNotContain(exception.RemotePath, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        nameof(LocalUploadFileValidationFailure.Missing),
        "SftpErrorLocalUploadFileMissing")]
    [InlineData(
        nameof(LocalUploadFileValidationFailure.NotRegularFile),
        "SftpErrorLocalUploadNotRegularFile")]
    public async Task DescribeTransferError_LocalUploadValidationException_ReturnsLocalizedMessage(
        string failureName,
        string expectedKey)
    {
        FakeUiDispatcher dispatcher = new();
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(dispatcher);
        SetLocalizer(viewModel, localizer);
        var failure = Enum.Parse<LocalUploadFileValidationFailure>(failureName);
        var exception = new LocalUploadFileValidationException(@"C:\secret\source", failure);

        string message = viewModel.DescribeTransferError(exception);

        Assert.Equal(localizer[expectedKey], message);
        Assert.DoesNotContain(exception.LocalPath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnUiAsync_OffUiThread_PostsToDispatcher()
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: false);
        var viewModel = new EmbeddedSftpViewModel(dispatcher);
        var actionRuns = 0;

        await InvokeRunOnUiAsync(viewModel, () => actionRuns++);

        Assert.Equal(1, dispatcher.InvokeAsyncCalls);
        Assert.Equal(1, actionRuns);
    }

    private static Task InvokeRunOnUiAsync(EmbeddedSftpViewModel viewModel, Action action)
    {
        var method = typeof(EmbeddedSftpViewModel).GetMethod("RunOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(viewModel, [action]) as Task;
        return task ?? throw new InvalidOperationException("RunOnUiAsync did not return a Task.");
    }

    private static SftpFileInfo CreateRemoteEntry(
        string name,
        string fullPath,
        bool isDirectory)
    {
        return new SftpFileInfo(
            name,
            fullPath,
            isDirectory,
            0,
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

    private static void SetLocalizer(EmbeddedSftpViewModel viewModel, LocalizationManager localizer)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, localizer);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeRemoteBrowser : IRemoteBrowser
    {
        private int _downloadCallCount;
        private int _uploadCallCount;
        private int _createDirectoryCallCount;
        private int _chmodCallCount;
        private int _deleteCallCount;
        private int _listDirectoryCallCount;
        private int _renameCallCount;

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

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public int DownloadCallCount => Volatile.Read(ref _downloadCallCount);

        public int UploadCallCount => Volatile.Read(ref _uploadCallCount);

        public int CreateDirectoryCallCount => Volatile.Read(ref _createDirectoryCallCount);

        public int ChmodCallCount => Volatile.Read(ref _chmodCallCount);

        public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);

        public int ListDirectoryCallCount => Volatile.Read(ref _listDirectoryCallCount);

        public int RenameCallCount => Volatile.Read(ref _renameCallCount);

        public string? LastUploadedRemotePath { get; private set; }

        public Func<string, string, CancellationToken, Task>? UploadFileHandler { get; set; }

        public string? LastCreatedDirectoryPath { get; private set; }

        public string? LastChmodPath { get; private set; }

        public short LastChmodMode { get; private set; }

        public Exception? ChmodException { get; set; }

        public string? LastDeletedPath { get; private set; }

        public CancellationToken LastDeleteCancellationToken { get; private set; }

        public string? LastRenamedOldPath { get; private set; }

        public string? LastRenamedNewPath { get; private set; }

        public Func<string?, CancellationToken, Task<IReadOnlyList<SftpFileInfo>>>? ListDirectoryHandler { get; set; }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listDirectoryCallCount);
            if (ListDirectoryHandler is not null)
            {
                return ListDirectoryHandler(path, ct);
            }

            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
        {
            return Task.FromResult(CurrentDirectory);
        }

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(
            string remotePath,
            string localPath,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _downloadCallCount);
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            LastUploadedRemotePath = remotePath;
            Interlocked.Increment(ref _uploadCallCount);
            return UploadFileHandler is null
                ? Task.CompletedTask
                : UploadFileHandler(localPath, remotePath, ct);
        }

        /// <summary>When set, CreateDirectoryAsync throws this exception (to simulate an existing dir).</summary>
        public Exception? CreateDirectoryException { get; set; }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            LastCreatedDirectoryPath = path;
            Interlocked.Increment(ref _createDirectoryCallCount);
            return CreateDirectoryException is not null
                ? Task.FromException(CreateDirectoryException)
                : Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            LastDeletedPath = path;
            LastDeleteCancellationToken = ct;
            Interlocked.Increment(ref _deleteCallCount);
            return Task.CompletedTask;
        }

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
        {
            LastChmodPath = path;
            LastChmodMode = mode;
            Interlocked.Increment(ref _chmodCallCount);
            return ChmodException is null
                ? Task.CompletedTask
                : Task.FromException(ChmodException);
        }

        /// <summary>When set, RenameAsync throws for an entry whose old path equals this value.</summary>
        public string? RenameFailurePath { get; set; }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            LastRenamedOldPath = oldPath;
            LastRenamedNewPath = newPath;
            Interlocked.Increment(ref _renameCallCount);
            if (string.Equals(oldPath, RenameFailurePath, StringComparison.Ordinal))
            {
                return Task.FromException(new IOException($"rename failed for {oldPath}"));
            }

            return Task.CompletedTask;
        }

        public List<(string Source, string Destination, bool Recursive)> CopyCalls { get; } = [];

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
        {
            CopyCalls.Add((sourcePath, destinationPath, recursive));
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConfirmingDialogService : IDialogService
    {
        private readonly string? _input;
        private readonly bool _hasInput;

        public int InputCallCount { get; private set; }

        public ConfirmingDialogService()
        {
        }

        public ConfirmingDialogService(string? input)
        {
            _input = input;
            _hasInput = true;
        }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => throw new NotSupportedException();

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
        {
            InputCallCount++;
            return _hasInput ? Task.FromResult(_input) : throw new NotSupportedException();
        }

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => throw new NotSupportedException();

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => throw new NotSupportedException();

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => throw new NotSupportedException();

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => throw new NotSupportedException();

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => throw new NotSupportedException();

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => throw new NotSupportedException();

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }

    private sealed class CapturingOperationLog : ISessionOperationLog
    {
        public List<SessionOperationRecord> Records { get; } = [];

        public void LogOperation(SessionOperationRecord record)
        {
            Records.Add(record);
        }

        public void Dispose()
        {
        }
    }
}
