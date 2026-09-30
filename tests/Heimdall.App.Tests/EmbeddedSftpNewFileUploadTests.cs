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

/// <summary>
/// An upload of a file the listing proved absent reaches the transport, whatever the transport.
/// </summary>
/// <remarks>
/// Once a new file stopped carrying consent to replace, the interface's default upload overload
/// refused every transport that did not override it, and routed every one that advertised the
/// exec-based no-clobber publisher through it. FTP and FTPS therefore could not upload a new file
/// at all, and SFTP opened an exec channel on a second SSH connection per file, which chrooted
/// <c>internal-sftp</c> accounts refuse. The existing view-model fakes all override the overload,
/// which is what hid it: the fake here deliberately does not.
/// </remarks>
public sealed class EmbeddedSftpNewFileUploadTests
{
    /// <summary>
    /// A transport with no exclusive commit and no publisher, which is what FTP is to the view model.
    /// </summary>
    [Fact]
    public async Task NewFile_OnATransportWithoutAnExclusiveCommit_IsUploaded()
    {
        using TempDirectory temp = new();
        string localFile = Path.Combine(temp.Path, "alpha.txt");
        await File.WriteAllTextAsync(localFile, "payload");
        DefaultOverloadBrowser browser = new(advertisesPublisher: false);
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser);

        await viewModel.UploadEntriesAsync([localFile], "/srv");

        Assert.Equal((localFile, "/srv/alpha.txt"), Assert.Single(browser.UploadCalls));
    }

    /// <summary>
    /// A transport that advertises the exec-based publisher, which is what SFTP is to the view model:
    /// the new file must not go through it.
    /// </summary>
    [Fact]
    public async Task NewFile_OnATransportWithTheExecPublisher_DoesNotUseThePublisher()
    {
        using TempDirectory temp = new();
        string localFile = Path.Combine(temp.Path, "alpha.txt");
        await File.WriteAllTextAsync(localFile, "payload");
        DefaultOverloadBrowser browser = new(advertisesPublisher: true);
        EmbeddedSftpViewModel viewModel = CreateViewModel(browser);

        await viewModel.UploadEntriesAsync([localFile], "/srv");

        Assert.Equal(0, browser.Publisher.PublishCalls);
        Assert.Equal((localFile, "/srv/alpha.txt"), Assert.Single(browser.UploadCalls));
    }

    private static EmbeddedSftpViewModel CreateViewModel(IRemoteBrowser browser)
    {
        EmbeddedSftpViewModel viewModel = new(
            new FakeUiDispatcher(),
            new RemoteClipboardService(),
            new NeverShownConflictPresenter());
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_browser",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, browser);
        return viewModel;
    }

    private sealed class NeverShownConflictPresenter : IFileConflictDialogPresenter
    {
        public Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel)
            => throw new InvalidOperationException("No conflict exists, so the dialog must not be shown.");
    }

    /// <summary>
    /// Counts publications, standing for the exec channel and the extra connection each one costs.
    /// </summary>
    private sealed class CountingPublisher : IRemoteNoClobberPublisher
    {
        internal int PublishCalls { get; private set; }

        public Task PublishFileIfAbsentAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            PublishCalls++;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryExclusiveAsync(string remotePath, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Implements only the ordinary upload, so the overload taking the replacement choice is the
    /// interface's own default.
    /// </summary>
    private sealed class DefaultOverloadBrowser(bool advertisesPublisher) : IRemoteBrowser, IRemoteNoClobberCapability
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

        internal CountingPublisher Publisher { get; } = new();

        public IRemoteNoClobberPublisher? NoClobberPublisher => advertisesPublisher ? Publisher : null;

        internal List<(string LocalPath, string RemotePath)> UploadCalls { get; } = [];

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            UploadCalls.Add((localPath, remotePath));
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
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
                $"Heimdall-NewFileUpload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
