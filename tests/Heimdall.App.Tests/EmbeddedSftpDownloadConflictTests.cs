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

public sealed class EmbeddedSftpDownloadConflictTests
{
    [Fact]
    public async Task DownloadFilesAsync_NoConflict_DoesNotShowDialog()
    {
        using TempDirectory target = new();
        var presenter = new RecordingConflictPresenter(_ =>
            throw new InvalidOperationException("The dialog must not be shown."));
        var browser = new RecordingRemoteBrowser();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync(
            [CreateFile("alpha.txt"), CreateFile("beta.txt")],
            target.Path);

        Assert.Equal(0, presenter.CallCount);
        Assert.Collection(
            browser.DownloadCalls,
            call => Assert.Equal(System.IO.Path.Combine(target.Path, "alpha.txt"), call.LocalPath),
            call => Assert.Equal(System.IO.Path.Combine(target.Path, "beta.txt"), call.LocalPath));
    }

    [Fact]
    public async Task DownloadFilesAsync_CancelledConflict_AbortsEntireBatch()
    {
        using TempDirectory target = new();
        File.WriteAllText(System.IO.Path.Combine(target.Path, "alpha.txt"), "existing");
        var presenter = new RecordingConflictPresenter(_ => null);
        var browser = new RecordingRemoteBrowser();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync(
            [CreateFile("alpha.txt"), CreateFile("beta.txt")],
            target.Path);

        Assert.Equal(1, presenter.CallCount);
        Assert.Empty(browser.DownloadCalls);
        Assert.Equal("Transfer cancelled", viewModel.StatusText);
    }

    [Fact]
    public async Task DownloadFilesAsync_SkipAndReplace_AppliesEveryRowDecision()
    {
        using TempDirectory target = new();
        string alphaTarget = System.IO.Path.Combine(target.Path, "alpha.txt");
        string betaTarget = System.IO.Path.Combine(target.Path, "beta.txt");
        File.WriteAllText(alphaTarget, "existing");
        File.WriteAllText(betaTarget, "existing");
        var presenter = new RecordingConflictPresenter(viewModel =>
            new FileConflictDialogResult(
            [
                new FileConflictDecision(viewModel.Rows[0].ItemIndex, FileConflictResolutionChoice.Skip),
                new FileConflictDecision(viewModel.Rows[1].ItemIndex, FileConflictResolutionChoice.Replace),
            ]));
        var browser = new RecordingRemoteBrowser();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync(
            [CreateFile("alpha.txt"), CreateFile("beta.txt")],
            target.Path);

        Assert.Equal(1, presenter.CallCount);
        Assert.Collection(
            browser.DownloadCalls,
            call =>
            {
                Assert.Equal("/remote/beta.txt", call.RemotePath);
                Assert.Equal(betaTarget, call.LocalPath);
            });
    }

    [Fact]
    public async Task DownloadFilesAsync_AutoRename_UsesFirstFreeLocalTarget()
    {
        using TempDirectory target = new();
        File.WriteAllText(System.IO.Path.Combine(target.Path, "alpha.txt"), "existing");
        File.WriteAllText(System.IO.Path.Combine(target.Path, "alpha (copy).txt"), "existing");
        var presenter = new RecordingConflictPresenter(viewModel =>
            new FileConflictDialogResult(
            [
                new FileConflictDecision(
                    viewModel.Rows.Single().ItemIndex,
                    FileConflictResolutionChoice.AutoRename),
            ]));
        var browser = new RecordingRemoteBrowser();
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync([CreateFile("alpha.txt")], target.Path);

        Assert.Equal(1, presenter.CallCount);
        Assert.Collection(
            browser.DownloadCalls,
            call => Assert.Equal(
                System.IO.Path.Combine(target.Path, "alpha (copy 2).txt"),
                call.LocalPath));
    }

    private static EmbeddedSftpViewModel CreateViewModel(
        IRemoteBrowser browser,
        IFileConflictDialogPresenter presenter)
    {
        var viewModel = new EmbeddedSftpViewModel(
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

    private static SftpFileInfo CreateFile(string name)
        => new SftpFileInfo(name, $"/remote/{name}", RemoteEntryKind.File, 1, DateTime.UnixEpoch, "rw-r--r--", "1000", "1000");

    private sealed class RecordingConflictPresenter(
        Func<FileConflictDialogViewModel, FileConflictDialogResult?> show)
        : IFileConflictDialogPresenter
    {
        public int CallCount { get; private set; }

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

        public string CurrentDirectory => "/remote";

        public bool IsConnected => true;

        public List<(string RemotePath, string LocalPath)> DownloadCalls { get; } = [];

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(
            string remotePath,
            string localPath,
            CancellationToken ct = default)
        {
            DownloadCalls.Add((remotePath, localPath));
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

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
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Heimdall-C3a1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
