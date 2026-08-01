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
            return Task.CompletedTask;
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

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
        {
            EnsureConnected();
            CopyCalls.Add((sourcePath, destinationPath, recursive));
            return Task.CompletedTask;
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
