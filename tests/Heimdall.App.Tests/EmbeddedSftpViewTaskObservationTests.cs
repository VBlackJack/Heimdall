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

using Heimdall.App.Views;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewTaskObservationTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The waits it bounds complete on
    /// an event: a disconnect-started signal or a teardown task finishing. The value
    /// only has to be generous enough that a saturated thread pool cannot exhaust it
    /// before that work is scheduled at all, and it is paid only on failure.
    /// </summary>
    private static readonly TimeSpan SignalBackstop = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true, "WarnFtpsDataChannelIdentityBadge")]
    [InlineData(false, "WarnFtpCleartextBadge")]
    public void GetFtpSecurityNoticeLocalizationKey_ReturnsProtocolAppropriateNotice(
        bool isTlsEnabled,
        string expected)
    {
        Assert.Equal(
            expected,
            EmbeddedSftpView.GetFtpSecurityNoticeLocalizationKey(isTlsEnabled));
    }

    [Fact]
    public async Task ObserveFaultedTask_LogsFaultWithoutThrowing()
    {
        List<string> warnings = [];
        var fault = new InvalidOperationException("dialog setup failed");

        Task observer = EmbeddedSftpView.ObserveFaultedTask(
            Task.FromException(fault),
            "test prologue",
            warnings.Add);

        await observer;

        string warning = Assert.Single(warnings);
        Assert.Contains("test prologue", warning, StringComparison.Ordinal);
        Assert.Contains(fault.Message, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract is that the caller is not held for the duration of the teardown:
    /// DisposeBrowserAsync hands back a task while the disconnect is still running, and
    /// the browser is disposed once it finishes.
    ///
    /// This deliberately does not assert which thread the teardown runs on. Thread
    /// identity is not a witness for that: the work is queued rather than started, the
    /// caller returns its own thread to the pool as soon as it awaits, and the pool is
    /// then free to run the queued work on that very thread. The assertion held only as
    /// long as the pool never made that choice, which it eventually did.
    /// </summary>
    [Fact]
    public async Task DisposeBrowserAsync_ReturnsBeforeTeardownCompletesAndDisposes()
    {
        using var releaseDisconnect = new ManualResetEventSlim();
        var browser = new BlockingRemoteBrowser(releaseDisconnect);

        Task teardown = EmbeddedSftpView.DisposeBrowserAsync(browser);

        await browser.DisconnectStarted.Task.WaitAsync(SignalBackstop);
        Assert.False(teardown.IsCompleted);

        releaseDisconnect.Set();
        await teardown.WaitAsync(SignalBackstop);

        Assert.True(browser.DisposeCalled);
    }

    [Fact]
    public async Task DisposeBrowserAsync_WhenDisconnectFails_DisposesAndFaultIsObserved()
    {
        List<string> warnings = [];
        var browser = new BlockingRemoteBrowser(
            releaseDisconnect: null,
            disconnectException: new InvalidOperationException("disconnect failed"));

        Task observer = EmbeddedSftpView.ObserveFaultedTask(
            EmbeddedSftpView.DisposeBrowserAsync(browser),
            "browser teardown",
            warnings.Add);

        await observer.WaitAsync(SignalBackstop);

        Assert.True(browser.DisposeCalled);
        string warning = Assert.Single(warnings);
        Assert.Contains("browser teardown", warning, StringComparison.Ordinal);
        Assert.Contains("disconnect failed", warning, StringComparison.Ordinal);
    }

    private sealed class BlockingRemoteBrowser(
        ManualResetEventSlim? releaseDisconnect,
        Exception? disconnectException = null) : IRemoteBrowser
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

        public TaskCompletionSource DisconnectStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DisposeCalled { get; private set; }

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public void Disconnect()
        {
            DisconnectStarted.TrySetResult();
            releaseDisconnect?.Wait();

            if (disconnectException is not null)
            {
                throw disconnectException;
            }
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
        {
            return Task.FromResult("/");
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
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string oldPath,
            string newPath,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
