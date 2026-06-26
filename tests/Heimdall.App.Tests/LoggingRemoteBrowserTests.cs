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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="LoggingRemoteBrowser"/>: each of the five transfer operations emits
/// exactly one operation record (success with bytes for transfers, error with the classified
/// category, or cancelled), every exception still propagates, the gate suppresses logging, events
/// forward from the inner browser, and the decorator never disposes the inner browser.
/// </summary>
public sealed class LoggingRemoteBrowserTests : IDisposable
{
    private const string Protocol = "SFTP";
    private const string Host = "host.example";

    private readonly List<string> _tempFiles = [];

    private string NewTempFileOfSize(int bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "HeimdallLoggingRemoteBrowserTests_" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[bytes]);
        _tempFiles.Add(path);
        return path;
    }

    private static LoggingRemoteBrowser Create(
        IRemoteBrowser inner,
        CapturingOperationLog sink,
        bool gateEnabled = true,
        string host = Host)
        => new LoggingRemoteBrowser(inner, sink, () => gateEnabled, Protocol, host);

    [Fact]
    public async Task UploadFileAsync_Success_LogsSuccessWithBytes()
    {
        string localPath = NewTempFileOfSize(2048);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.UploadFileAsync(localPath, "/srv/data/file.bin");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Upload);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.Protocol.Should().Be("SFTP");
        record.Host.Should().Be(Host);
        record.RemotePath.Should().Be("/srv/data/file.bin");
        record.LocalPath.Should().Be(localPath);
        record.Bytes.Should().Be(2048);
        record.ErrorCategory.Should().BeNull();
        record.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task DownloadFileAsync_Success_LogsSuccessWithBytes()
    {
        string localPath = NewTempFileOfSize(512);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.DownloadFileAsync("/srv/data/file.bin", localPath);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Download);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.LocalPath.Should().Be(localPath);
        record.Bytes.Should().Be(512);
    }

    [Fact]
    public async Task CreateDirectoryAsync_Success_LogsMkdirWithoutBytesOrLocalPath()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.CreateDirectoryAsync("/srv/data/newdir");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Mkdir);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/srv/data/newdir");
        record.Bytes.Should().BeNull();
        record.LocalPath.Should().BeNull();
        record.RemotePathTo.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_Success_LogsDeleteWithoutBytes()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.DeleteAsync("/srv/data/old.bin");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Delete);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/srv/data/old.bin");
        record.Bytes.Should().BeNull();
        record.LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task RenameAsync_Success_LogsRenameWithRemotePathTo()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.RenameAsync("/srv/data/a.txt", "/srv/data/b.txt");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Rename);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/srv/data/a.txt");
        record.RemotePathTo.Should().Be("/srv/data/b.txt");
        record.Bytes.Should().BeNull();
    }

    [Fact]
    public async Task Operation_InnerThrows_LogsErrorWithCategoryAndRethrows()
    {
        string localPath = NewTempFileOfSize(16);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new() { OpException = new SftpPermissionDeniedException("denied") };
        LoggingRemoteBrowser decorator = Create(inner, sink);

        Func<Task> act = () => decorator.UploadFileAsync(localPath, "/srv/data/file.bin");

        await act.Should().ThrowAsync<SftpPermissionDeniedException>();
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Result.Should().Be(SessionOperationResult.Error);
        record.ErrorCategory.Should().Be("permission");
        // A failed transfer carries no byte count.
        record.Bytes.Should().BeNull();
    }

    [Fact]
    public async Task Operation_InnerThrowsIoException_ClassifiesAsIo()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new() { OpException = new IOException("disk full") };
        LoggingRemoteBrowser decorator = Create(inner, sink);

        Func<Task> act = () => decorator.DeleteAsync("/srv/data/old.bin");

        await act.Should().ThrowAsync<IOException>();
        sink.Records.Should().ContainSingle()
            .Which.ErrorCategory.Should().Be("io");
    }

    [Fact]
    public async Task Operation_InnerCancelled_LogsCancelledAndRethrows()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new() { OpException = new OperationCanceledException() };
        LoggingRemoteBrowser decorator = Create(inner, sink);

        Func<Task> act = () => decorator.DeleteAsync("/srv/data/old.bin");

        await act.Should().ThrowAsync<OperationCanceledException>();
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Result.Should().Be(SessionOperationResult.Cancelled);
        record.ErrorCategory.Should().BeNull();
    }

    [Fact]
    public async Task Operation_GateDisabled_LogsNothing()
    {
        string localPath = NewTempFileOfSize(8);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink, gateEnabled: false);

        await decorator.UploadFileAsync(localPath, "/srv/data/file.bin");
        await decorator.DeleteAsync("/srv/data/old.bin");

        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Operation_GateDisabled_StillRethrowsInnerException()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new() { OpException = new IOException("boom") };
        LoggingRemoteBrowser decorator = Create(inner, sink, gateEnabled: false);

        Func<Task> act = () => decorator.DeleteAsync("/srv/data/old.bin");

        await act.Should().ThrowAsync<IOException>();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Host_WithUserPrefix_IsStripped()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink, host: "admin@10.0.0.5");

        await decorator.DeleteAsync("/srv/data/old.bin");

        sink.Records.Should().ContainSingle().Which.Host.Should().Be("10.0.0.5");
    }

    [Fact]
    public void Events_AreForwardedFromInner()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        string? directory = null;
        SftpTransferProgress? progress = null;
        string? disconnectMessage = null;
        bool disconnectRaised = false;

        decorator.DirectoryChanged += d => directory = d;
        decorator.TransferProgress += p => progress = p;
        decorator.Disconnected += m => { disconnectMessage = m; disconnectRaised = true; };

        inner.RaiseDirectoryChanged("/new/dir");
        inner.RaiseTransferProgress(new SftpTransferProgress("file.bin", 10, 20, IsUpload: true));
        inner.RaiseDisconnected("dropped");

        directory.Should().Be("/new/dir");
        progress!.FileName.Should().Be("file.bin");
        disconnectRaised.Should().BeTrue();
        disconnectMessage.Should().Be("dropped");
    }

    [Fact]
    public void Dispose_DoesNotDisposeInner()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        decorator.Dispose();

        inner.DisposeCount.Should().Be(0);
    }

    public void Dispose()
    {
        foreach (string file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp artifacts.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of temp artifacts.
            }
        }
    }

    private sealed class CapturingOperationLog : ISessionOperationLog
    {
        public List<SessionOperationRecord> Records { get; } = [];

        public void LogOperation(SessionOperationRecord record) => Records.Add(record);

        public void Dispose()
        {
        }
    }

    private sealed class FakeRemoteBrowser : IRemoteBrowser
    {
        /// <summary>When set, every operation fails with this exception.</summary>
        public Exception? OpException { get; set; }

        public int DisposeCount { get; private set; }

        public bool DisconnectCalled { get; private set; }

        public event Action<string>? DirectoryChanged;

        public event Action<SftpTransferProgress>? TransferProgress;

        public event Action<string?>? Disconnected;

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public void RaiseDirectoryChanged(string path) => DirectoryChanged?.Invoke(path);

        public void RaiseTransferProgress(SftpTransferProgress progress) => TransferProgress?.Invoke(progress);

        public void RaiseDisconnected(string? message) => Disconnected?.Invoke(message);

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default) => Task.FromResult("/");

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default) => Run();

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default) => Run();

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default) => Run();

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Run();

        public Task DeleteAsync(string path, CancellationToken ct = default) => Run();

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default) => Run();

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Run();

        public void Disconnect() => DisconnectCalled = true;

        public void Dispose() => DisposeCount++;

        private Task Run() => OpException is null ? Task.CompletedTask : Task.FromException(OpException);
    }
}
