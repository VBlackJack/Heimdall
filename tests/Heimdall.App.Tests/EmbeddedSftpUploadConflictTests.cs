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

using System.Diagnostics;
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

    [Theory]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    [InlineData(RemoteEntryKind.Fifo)]
    [InlineData(RemoteEntryKind.Socket)]
    [InlineData(RemoteEntryKind.Device)]
    public async Task Inventory_UnsupportedEntry_IsExcludedFromConflictAnalysis(
        RemoteEntryKind kind)
    {
        RecordingRemoteBrowser browser = new();
        browser.Listings["/dst"] =
        [
            CreateRemoteEntry("entry", "/dst/entry", kind),
        ];
        IReadOnlyList<RemoteUploadOp> ops =
        [
            new RemoteUploadOp(RemoteUploadOpKind.UploadFile, "local", "/dst/entry"),
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

        Assert.Null(inventory.GetTargetKind("/dst/entry"));
        Assert.True(inventory.IsUnsupportedTarget("/dst/entry"));
        Assert.False(item.HasConflict);
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
    public async Task UploadEntriesAsync_SymbolicLinkDestination_SkipsUploadAndShowsWarning()
    {
        using TempDirectory temp = new();
        string localFile = Path.Combine(temp.Path, "entry");
        await File.WriteAllTextAsync(localFile, "payload");
        RecordingRemoteBrowser browser = new();
        browser.Listings["/dst"] =
        [
            CreateRemoteEntry("entry", "/dst/entry", RemoteEntryKind.SymbolicLink),
        ];
        RecordingConflictPresenter presenter = new(_ =>
            throw new InvalidOperationException("The dialog must not be shown."));
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([localFile], "/dst");

        Assert.Empty(browser.UploadCalls);
        Assert.Equal(0, presenter.CallCount);
        Assert.Equal(
            "Skipped 1 upload(s): the destination already exists and is not a regular file. See the log for details.",
            viewModel.StatusText);
    }

    [Fact]
    public async Task UploadEntriesAsync_FolderMappedToSymbolicLink_SkipsWholeSubtree()
    {
        using TempDirectory temp = new();
        string localDirectory = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(localDirectory);
        await File.WriteAllTextAsync(Path.Combine(localDirectory, "child.txt"), "payload");
        RecordingRemoteBrowser browser = new();
        browser.Listings["/dst"] =
        [
            CreateRemoteEntry("project", "/dst/project", RemoteEntryKind.SymbolicLink),
        ];
        RecordingConflictPresenter presenter = new(_ =>
            throw new InvalidOperationException("The dialog must not be shown."));
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([localDirectory], "/dst");

        Assert.Empty(browser.UploadCalls);
        Assert.Empty(browser.CreateDirectoryCalls);
        Assert.Equal(0, presenter.CallCount);
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

    [Fact]
    public void ClassifyLocalUploadChild_FileReparsePoint_IsSkipped()
    {
        const string localPath = @"C:\source\file-link.txt";
        List<string> skippedPaths = [];

        LocalUploadEntry? entry = EmbeddedSftpViewModel.ClassifyLocalUploadChild(
            localPath,
            FileAttributes.ReparsePoint,
            skippedPaths);

        Assert.Null(entry);
        Assert.Equal([localPath], skippedPaths);
    }

    [Fact]
    public void ClassifyLocalUploadChild_DirectoryReparsePoint_IsSkipped()
    {
        const string localPath = @"C:\source\directory-link";
        List<string> skippedPaths = [];

        LocalUploadEntry? entry = EmbeddedSftpViewModel.ClassifyLocalUploadChild(
            localPath,
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            skippedPaths);

        Assert.Null(entry);
        Assert.Equal([localPath], skippedPaths);
    }

    // A root is refused on the same fail-closed rule as a child. The existence probes follow
    // links, so accepting a selected link uploaded the target's content from outside the
    // selection. These two oracles previously asserted the opposite and were inverted.
    [Fact]
    public void ClassifyLocalUploadRoot_FileLinkReportedByFileExists_IsRefused()
    {
        const string selectedLink = @"C:\source\selected-link.txt";
        List<string> skippedPaths = [];

        LocalUploadEntry? entry = EmbeddedSftpViewModel.ClassifyLocalUploadRoot(
            selectedLink,
            directoryExists: false,
            fileExists: true,
            FileAttributes.ReparsePoint,
            skippedPaths);

        Assert.Null(entry);
        Assert.Equal([selectedLink], skippedPaths);
    }

    [Fact]
    public void ClassifyLocalUploadRoot_RegularFile_IsStillAccepted()
    {
        const string selectedFile = @"C:\source\regular.txt";
        List<string> skippedPaths = [];

        LocalUploadEntry? entry = EmbeddedSftpViewModel.ClassifyLocalUploadRoot(
            selectedFile,
            directoryExists: false,
            fileExists: true,
            FileAttributes.Normal,
            skippedPaths);

        Assert.NotNull(entry);
        Assert.False(entry!.IsDirectory);
        Assert.Equal(selectedFile, entry.FullPath);
        Assert.Equal("regular.txt", entry.Name);
        Assert.Empty(skippedPaths);
    }

    // A source deleted between the existence probe and the classification stays an ignored
    // disappearance. It must not be counted as a refused link, which would surface a warning
    // for a path the user never selected as a link.
    [Fact]
    public void ClassifyLocalUploadRoot_VanishedSource_IsIgnoredWithoutBeingCounted()
    {
        const string vanished = @"C:\source\deleted-between-probes.txt";
        List<string> skippedPaths = [];

        LocalUploadEntry? entry = EmbeddedSftpViewModel.ClassifyLocalUploadRoot(
            vanished,
            directoryExists: false,
            fileExists: false,
            FileAttributes.ReparsePoint,
            skippedPaths);

        Assert.Null(entry);
        Assert.Empty(skippedPaths);
    }

    [Fact]
    public async Task UploadEntriesAsync_RootDirectoryLink_IsRefusedAndNothingIsTransferred()
    {
        using TempDirectory temp = new();
        string targetDirectory = Path.Combine(temp.Path, "target-directory");
        string selectedLink = Path.Combine(temp.Path, "selected-link");
        Directory.CreateDirectory(targetDirectory);
        string childFile = Path.Combine(targetDirectory, "child.txt");
        await File.WriteAllTextAsync(childFile, "payload");
        await CreateJunctionAsync(selectedLink, targetDirectory);
        try
        {
            RecordingRemoteBrowser browser = new();
            RecordingConflictPresenter presenter = new(_ =>
                throw new InvalidOperationException("The dialog must not be shown."));
            EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

            await viewModel.UploadEntriesAsync([selectedLink], "/dst");

            Assert.Equal(0, presenter.CallCount);
            Assert.Empty(browser.UploadCalls);
            Assert.Empty(browser.CreateDirectoryCalls);
            Assert.Equal(
                "Skipped 1 local link(s), selected as upload sources or found inside the selected tree. See the log for details.",
                viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(selectedLink);
        }
    }

    [Fact]
    public async Task UploadEntriesAsync_MixedTree_SkipsChildJunctionAndSurfacesCount()
    {
        using TempDirectory temp = new();
        string selectedDirectory = Path.Combine(temp.Path, "source");
        string nestedDirectory = Path.Combine(selectedDirectory, "nested");
        string externalDirectory = Path.Combine(temp.Path, "external-directory");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(externalDirectory);
        string regularFile = Path.Combine(selectedDirectory, "regular.txt");
        string nestedFile = Path.Combine(nestedDirectory, "nested.txt");
        await File.WriteAllTextAsync(regularFile, "regular");
        await File.WriteAllTextAsync(nestedFile, "nested");
        await File.WriteAllTextAsync(
            Path.Combine(externalDirectory, "must-not-upload.txt"),
            "external");
        string directoryLink = Path.Combine(selectedDirectory, "directory-link");
        await CreateJunctionAsync(directoryLink, externalDirectory);
        try
        {
            RecordingRemoteBrowser browser = new();
            RecordingConflictPresenter presenter = new(_ =>
                throw new InvalidOperationException("The dialog must not be shown."));
            EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

            await viewModel.UploadEntriesAsync([selectedDirectory], "/dst");

            Assert.Equal(0, presenter.CallCount);
            Assert.Equal(2, browser.UploadCalls.Count);
            Assert.Contains((regularFile, "/dst/source/regular.txt"), browser.UploadCalls);
            Assert.Contains((nestedFile, "/dst/source/nested/nested.txt"), browser.UploadCalls);
            Assert.DoesNotContain(
                browser.UploadCalls,
                call => call.RemotePath.StartsWith("/dst/source/directory-link", StringComparison.Ordinal));
            Assert.DoesNotContain("/dst/source/directory-link", browser.CreateDirectoryCalls);
            Assert.Equal(
                "Skipped 1 local link(s), selected as upload sources or found inside the selected tree. See the log for details.",
                viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(directoryLink);
        }
    }

    private static async Task CreateJunctionAsync(string junctionPath, string targetPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using Process process = new() { StartInfo = startInfo };
        Assert.True(process.Start());
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Failed to create junction '{junctionPath}' -> '{targetPath}'. stdout={standardOutput}; stderr={standardError}");
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
        => new SftpFileInfo(
            name,
            fullPath,
            isDirectory ? RemoteEntryKind.Directory : RemoteEntryKind.File,
            isDirectory ? 0 : 1,
            DateTime.UnixEpoch,
            isDirectory ? "rwxr-xr-x" : "rw-r--r--",
            "1000",
            "1000");

    private static SftpFileInfo CreateRemoteEntry(
        string name,
        string fullPath,
        RemoteEntryKind kind)
        => new SftpFileInfo(
            name,
            fullPath,
            kind,
            kind == RemoteEntryKind.Directory ? 0 : 1,
            DateTime.UnixEpoch,
            kind == RemoteEntryKind.Directory ? "rwxr-xr-x" : "rw-r--r--",
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
