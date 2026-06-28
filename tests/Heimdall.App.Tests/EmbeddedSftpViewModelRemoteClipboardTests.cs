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
    public void PasteCommand_DifferentEndpoint_ReturnsFalse()
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

    private static SftpClipboardContent CreateContent(string endpointKey)
    {
        return new SftpClipboardContent(
            [CreateEntry("a.txt", "/src/a.txt", isDirectory: false)],
            "/src",
            SftpClipboardMode.Copy,
            endpointKey);
    }

    private static SftpFileInfo CreateEntry(string name, string fullPath, bool isDirectory)
    {
        return new SftpFileInfo(
            name,
            fullPath,
            isDirectory,
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

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public int RenameCallCount { get; private set; }

        public string? LastRenamedOldPath { get; private set; }

        public string? LastRenamedNewPath { get; private set; }

        public List<(string Source, string Destination, bool Recursive)> CopyCalls { get; } = [];

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => Task.FromResult(CurrentDirectory);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            RenameCallCount++;
            LastRenamedOldPath = oldPath;
            LastRenamedNewPath = newPath;
            return Task.CompletedTask;
        }

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
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
}
