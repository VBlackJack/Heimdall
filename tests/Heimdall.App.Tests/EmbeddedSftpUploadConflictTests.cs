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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpUploadConflictTests
{
    [Fact]
    public async Task Inventory_ListsEachDistinctParentExactlyOnce_AndResolutionAddsNoCall()
    {
        RecordingRemoteBrowser browser = new();
        browser.Listings["/dst"] =
        [
            CreateRemoteEntry("a", "/dst/a", isDirectory: true),
            CreateRemoteEntry("b", "/dst/b", isDirectory: true),
        ];
        browser.Listings["/dst/a"] = [];
        browser.Listings["/dst/b"] = [];
        IReadOnlyList<RemoteUploadOp> ops =
        [
            new RemoteUploadOp(RemoteUploadOpKind.MakeDirectory, "local-a", "/dst/a"),
            new RemoteUploadOp(RemoteUploadOpKind.UploadFile, "local-one", "/dst/a/one.txt"),
            new RemoteUploadOp(RemoteUploadOpKind.UploadFile, "local-two", "/dst/a/two.txt"),
            new RemoteUploadOp(RemoteUploadOpKind.MakeDirectory, "local-b", "/dst/b"),
            new RemoteUploadOp(RemoteUploadOpKind.UploadFile, "local-three", "/dst/b/three.txt"),
        ];

        EmbeddedSftpViewModel.RemoteUploadConflictInventory inventory =
            await EmbeddedSftpViewModel.BuildRemoteUploadConflictInventoryAsync(
                browser,
                ops,
                CancellationToken.None);

        Assert.Equal(["/dst", "/dst/a", "/dst/b"], browser.ListDirectoryCalls);
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            ToPlanItems(ops),
            inventory.GetTargetKind,
            StringComparer.Ordinal);
        int callsBeforeResolution = browser.ListDirectoryCalls.Count;

        IReadOnlyList<FileConflictResolvedItem> resolved = FileConflictPlanner.Resolve(
            analysis,
            [],
            inventory.TargetExists,
            StringComparer.Ordinal);

        Assert.Equal(ops.Count, resolved.Count);
        Assert.Equal(callsBeforeResolution, browser.ListDirectoryCalls.Count);
    }

    [Fact]
    public async Task Inventory_AbsentParent_IsEmptyAndProducesNoConflict()
    {
        RecordingRemoteBrowser browser = new();
        browser.MissingDirectories.Add("/missing");
        IReadOnlyList<RemoteUploadOp> ops =
        [
            new RemoteUploadOp(RemoteUploadOpKind.UploadFile, "local", "/missing/file.txt"),
        ];

        EmbeddedSftpViewModel.RemoteUploadConflictInventory inventory =
            await EmbeddedSftpViewModel.BuildRemoteUploadConflictInventoryAsync(
                browser,
                ops,
                CancellationToken.None);
        FileConflictAnalysisItem item = Assert.Single(FileConflictPlanner.Analyze(
            ToPlanItems(ops),
            inventory.GetTargetKind,
            StringComparer.Ordinal));

        Assert.Equal(["/missing"], browser.ListDirectoryCalls);
        Assert.False(item.HasConflict);
        Assert.Null(item.ExistingTargetKind);
    }

    [Fact]
    public async Task UploadEntriesAsync_NoConflict_DoesNotShowDialog()
    {
        using TempDirectory temp = new();
        string localFile = Path.Combine(temp.Path, "alpha.txt");
        await File.WriteAllTextAsync(localFile, "payload");
        RecordingRemoteBrowser browser = new();
        browser.Listings["/srv"] = [];
        RecordingConflictPresenter presenter = new(_ =>
            throw new InvalidOperationException("The dialog must not be shown."));
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([localFile], "/srv");

        Assert.Equal(0, presenter.CallCount);
        Assert.Collection(
            browser.UploadCalls,
            call => Assert.Equal((localFile, "/srv/alpha.txt"), call));
    }

    [Fact]
    public async Task UploadEntriesAsync_CancelledConflict_UploadsNothing()
    {
        using TempDirectory temp = new();
        string localFile = Path.Combine(temp.Path, "alpha.txt");
        await File.WriteAllTextAsync(localFile, "payload");
        RecordingRemoteBrowser browser = new();
        browser.Listings["/srv"] =
        [
            CreateRemoteEntry("alpha.txt", "/srv/alpha.txt", isDirectory: false),
        ];
        RecordingConflictPresenter presenter = new(_ => null);
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([localFile], "/srv");

        Assert.Equal(1, presenter.CallCount);
        Assert.Empty(browser.UploadCalls);
        Assert.Empty(browser.CreateDirectoryCalls);
        Assert.Equal("Transfer cancelled", viewModel.StatusText);
        Assert.False(viewModel.IsTransferInProgress);
    }

    [Fact]
    public async Task UploadEntriesAsync_DirectoryOverFileSkip_ExcludesWholeSubtree()
    {
        using TempDirectory temp = new();
        string localDirectory = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(localDirectory);
        await File.WriteAllTextAsync(Path.Combine(localDirectory, "child.txt"), "payload");
        RecordingRemoteBrowser browser = new();
        browser.Listings["/srv"] =
        [
            CreateRemoteEntry("project", "/srv/project", isDirectory: false),
        ];
        browser.MissingDirectories.Add("/srv/project");
        RecordingConflictPresenter presenter = new(viewModel =>
        {
            FileConflictRowViewModel row = Assert.Single(viewModel.Rows);
            Assert.Equal(
                [FileConflictResolutionChoice.Skip],
                row.ConflictOptions.Select(option => option.Value));
            Assert.True(row.HasDetail);
            return new FileConflictDialogResult(
                [new FileConflictDecision(row.ItemIndex, FileConflictResolutionChoice.Skip)]);
        });
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([localDirectory], "/srv");

        Assert.Equal(1, presenter.CallCount);
        Assert.Empty(browser.CreateDirectoryCalls);
        Assert.Empty(browser.UploadCalls);
    }

    private static IReadOnlyList<FileConflictPlanItem> ToPlanItems(IReadOnlyList<RemoteUploadOp> ops)
        => ops.Select(op => new FileConflictPlanItem(
            op.LocalPath,
            op.RemotePath,
            op.Kind == RemoteUploadOpKind.MakeDirectory
                ? FileConflictItemKind.Directory
                : FileConflictItemKind.File))
            .ToList();

    private static EmbeddedSftpViewModel CreateViewModel(
        IRemoteBrowser browser,
        IFileConflictDialogPresenter presenter)
    {
        EmbeddedSftpViewModel viewModel = new(
            new FakeUiDispatcher(),
            new RemoteClipboardService(),
            presenter);
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_browser",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, browser);
        return viewModel;
    }

    private static SftpFileInfo CreateRemoteEntry(
        string name,
        string fullPath,
        bool isDirectory)
        => new(
            name,
            fullPath,
            isDirectory,
            isDirectory ? 0 : 1,
            DateTime.UnixEpoch,
            isDirectory ? "rwxr-xr-x" : "rw-r--r--",
            "1000",
            "1000");

    private sealed class RecordingConflictPresenter(
        Func<FileConflictDialogViewModel, FileConflictDialogResult?> show)
        : IFileConflictDialogPresenter
    {
        internal int CallCount { get; private set; }

        public Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel)
        {
            CallCount++;
            return Task.FromResult(show(viewModel));
        }
    }

    private sealed class RecordingRemoteBrowser : IRemoteBrowser
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

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        internal Dictionary<string, IReadOnlyList<SftpFileInfo>> Listings { get; } =
            new(StringComparer.Ordinal);

        internal HashSet<string> MissingDirectories { get; } = new(StringComparer.Ordinal);

        internal List<string> ListDirectoryCalls { get; } = [];

        internal List<(string LocalPath, string RemotePath)> UploadCalls { get; } = [];

        internal List<string> CreateDirectoryCalls { get; } = [];

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
        {
            string targetPath = path ?? CurrentDirectory;
            ListDirectoryCalls.Add(targetPath);
            if (MissingDirectories.Contains(targetPath))
            {
                throw new IOException($"Missing directory: {targetPath}");
            }

            return Task.FromResult(
                Listings.TryGetValue(targetPath, out IReadOnlyList<SftpFileInfo>? entries)
                    ? entries
                    : (IReadOnlyList<SftpFileInfo>)[]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(
            string remotePath,
            string localPath,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadFileAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            UploadCalls.Add((localPath, remotePath));
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            CreateDirectoryCalls.Add(path);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Heimdall-C3a2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
