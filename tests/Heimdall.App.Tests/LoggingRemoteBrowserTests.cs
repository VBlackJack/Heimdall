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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Sftp;
using Heimdall.Ssh;
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
        string host = Host,
        bool? sessionLoggingOverride = null)
        => new LoggingRemoteBrowser(
            inner,
            sink,
            () => gateEnabled,
            Protocol,
            host,
            sessionLoggingOverride);

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
    public async Task CopyAsync_Success_ForwardsToInnerAndLogsSingleCopyRecord()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.CopyAsync("/srv/data/a.txt", "/srv/data/a-copy.txt", recursive: false);

        inner.CopyCalls.Should().ContainSingle()
            .Which.Should().Be(("/srv/data/a.txt", "/srv/data/a-copy.txt", false));
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Copy);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/srv/data/a.txt");
        record.RemotePathTo.Should().Be("/srv/data/a-copy.txt");
        record.Bytes.Should().BeNull();
        record.LocalPath.Should().BeNull();
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
    public async Task Operation_OverrideOn_LogsWhenGlobalDisabled()
    {
        string localPath = NewTempFileOfSize(8);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink, gateEnabled: false, sessionLoggingOverride: true);

        await decorator.UploadFileAsync(localPath, "/srv/data/file.bin");

        sink.Records.Should().ContainSingle()
            .Which.Result.Should().Be(SessionOperationResult.Success);
    }

    [Fact]
    public async Task Operation_OverrideOff_LogsNothingWhenGlobalEnabled()
    {
        string localPath = NewTempFileOfSize(8);
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink, gateEnabled: true, sessionLoggingOverride: false);

        await decorator.UploadFileAsync(localPath, "/srv/data/file.bin");

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
    public void OperationWarningRaised_IsForwardedFromInner()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);
        RemoteOperationWarning warning = RemoteOperationWarning.CreateNonAtomicReplacement(
            "/srv/data/file.bin");
        RemoteOperationWarning? receivedWarning = null;

        decorator.OperationWarningRaised += received => receivedWarning = received;

        inner.RaiseOperationWarning(warning);

        receivedWarning.Should().BeSameAs(warning);
    }

    [Fact]
    public void OperationWarningRaised_IsNotForwardedAfterUnsubscribe()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();
        LoggingRemoteBrowser decorator = Create(inner, sink);
        int warningCount = 0;
        Action<RemoteOperationWarning> handler = _ => warningCount++;

        decorator.OperationWarningRaised += handler;
        decorator.OperationWarningRaised -= handler;

        inner.RaiseOperationWarning(RemoteOperationWarning.CreateNonAtomicReplacement(
            "/srv/data/file.bin"));

        warningCount.Should().Be(0);
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

    // A decorator that answered for itself would hand the clipboard a publisher whose inner browser
    // cannot publish without replacing. The capability must mirror the inner browser's own answer.
    [Fact]
    public void NoClobberPublisher_WhenInnerDoesNotImplementTheCapability_IsNull()
    {
        CapturingOperationLog sink = new();
        FakeRemoteBrowser inner = new();

        LoggingRemoteBrowser decorator = Create(inner, sink);

        decorator.Should().BeAssignableTo<IRemoteNoClobberCapability>();
        decorator.NoClobberPublisher.Should().BeNull();
    }

    [Fact]
    public void NoClobberPublisher_WhenInnerDeclaresNoPublisher_IsNull()
    {
        CapturingOperationLog sink = new();
        CapableRemoteBrowser inner = new(new FakeRemoteBrowser(), publisher: null);

        LoggingRemoteBrowser decorator = Create(inner, sink);

        decorator.NoClobberPublisher.Should().BeNull();
    }

    [Fact]
    public async Task NoClobberPublisher_WhenInnerCanPublish_ForwardsVerbatim_ThroughItsOwnWrapper()
    {
        string localPath = NewTempFileOfSize(1024);
        CapturingOperationLog sink = new();
        RecordingPublisher publisher = new();
        CapableRemoteBrowser inner = new(new FakeRemoteBrowser(), publisher);
        LoggingRemoteBrowser decorator = Create(inner, sink);

        decorator.NoClobberPublisher.Should().NotBeNull();

        // Not the inner publisher itself: returning it unwrapped would forward correctly and record
        // nothing, which is the failure mode a null check cannot see.
        decorator.NoClobberPublisher.Should().NotBeSameAs(publisher);

        await decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");
        await decorator.NoClobberPublisher!.CreateDirectoryExclusiveAsync("/srv/data/dir");

        publisher.PublishCalls.Should().ContainSingle()
            .Which.Should().Be((localPath, "/srv/data/file.bin"));
        publisher.ReserveCalls.Should().ContainSingle().Which.Should().Be("/srv/data/dir");
    }

    [Fact]
    public async Task PublishFileIfAbsentAsync_Success_LogsOneUploadRecordNamingTheFinalDestination()
    {
        string localPath = NewTempFileOfSize(4096);
        CapturingOperationLog sink = new();
        CapableRemoteBrowser inner = new(new FakeRemoteBrowser(), new RecordingPublisher());
        LoggingRemoteBrowser decorator = Create(inner, sink);

        await decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Upload);
        record.Result.Should().Be(SessionOperationResult.Success);

        // The final destination, never the staging name the inner browser reserved: a record naming
        // the staging file would describe a path that no longer exists once the publish succeeded.
        record.RemotePath.Should().Be("/srv/data/file.bin");
        record.LocalPath.Should().Be(localPath);
        record.Bytes.Should().Be(4096);
        record.ErrorCategory.Should().BeNull();
    }

    // A refusal and an unconfirmed outcome must never be recorded as a success. An operator reading a
    // success line concludes the destination now holds this file, which is precisely what an
    // unconfirmed publication cannot establish.
    //
    // The exception is asserted by IDENTITY, not by type. A decorator that caught the refusal and threw
    // its own IOException wrapping it would satisfy a type check while destroying what the caller needs:
    // the clipboard distinguishes a collision from an unconfirmed outcome by the exception it receives,
    // and treats only the second as "may already exist on the server".
    [Fact]
    public async Task PublishFileIfAbsentAsync_DestinationExists_LogsAnError_AndRethrowsTheSameInstance()
    {
        string localPath = NewTempFileOfSize(16);
        CapturingOperationLog sink = new();
        RemoteDestinationExistsException injected = new("/srv/data/file.bin");
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> publish = () =>
            decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");

        (await publish.Should().ThrowAsync<RemoteDestinationExistsException>())
            .Which.Should().BeSameAs(injected);

        AssertSingleErrorRecord(sink, SessionOperationKind.Upload, "/srv/data/file.bin");
    }

    [Fact]
    public async Task PublishFileIfAbsentAsync_UnconfirmedOutcome_LogsAnError_AndRethrowsTheSameInstance()
    {
        string localPath = NewTempFileOfSize(16);
        CapturingOperationLog sink = new();
        RemoteNoClobberPublishUnavailableException injected =
            new("/srv/data/file.bin", "the channel dropped");
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> publish = () =>
            decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");

        (await publish.Should().ThrowAsync<RemoteNoClobberPublishUnavailableException>())
            .Which.Should().BeSameAs(injected);

        AssertSingleErrorRecord(sink, SessionOperationKind.Upload, "/srv/data/file.bin");
    }

    [Fact]
    public async Task PublishFileIfAbsentAsync_Cancelled_LogsCancelled_AndRethrowsTheSameInstance()
    {
        string localPath = NewTempFileOfSize(16);
        CapturingOperationLog sink = new();
        OperationCanceledException injected = new();
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> publish = () =>
            decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");

        (await publish.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().BeSameAs(injected);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Upload);
        record.Result.Should().Be(SessionOperationResult.Cancelled);
    }

    [Fact]
    public async Task CreateDirectoryExclusiveAsync_Success_LogsOneMkdirRecord()
    {
        CapturingOperationLog sink = new();
        LoggingRemoteBrowser decorator = Create(
            new CapableRemoteBrowser(new FakeRemoteBrowser(), new RecordingPublisher()),
            sink);

        await decorator.NoClobberPublisher!.CreateDirectoryExclusiveAsync("/srv/data/dir");

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Mkdir);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/srv/data/dir");
    }

    // The directory path carries the same three failure modes as the file path and had only its success
    // covered. A reservation that failed while being recorded as a success is how a paste continues into
    // a subtree it never reserved.
    [Fact]
    public async Task CreateDirectoryExclusiveAsync_PathExists_LogsMkdirError_AndRethrowsTheSameInstance()
    {
        CapturingOperationLog sink = new();
        RemoteDestinationExistsException injected = new("/srv/data/dir");
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> reserve = () => decorator.NoClobberPublisher!.CreateDirectoryExclusiveAsync("/srv/data/dir");

        (await reserve.Should().ThrowAsync<RemoteDestinationExistsException>())
            .Which.Should().BeSameAs(injected);

        AssertSingleErrorRecord(sink, SessionOperationKind.Mkdir, "/srv/data/dir");
    }

    [Fact]
    public async Task CreateDirectoryExclusiveAsync_UnconfirmedOutcome_LogsMkdirError_AndRethrowsTheSameInstance()
    {
        CapturingOperationLog sink = new();
        RemoteNoClobberPublishUnavailableException injected = new("/srv/data/dir", "the channel dropped");
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> reserve = () => decorator.NoClobberPublisher!.CreateDirectoryExclusiveAsync("/srv/data/dir");

        (await reserve.Should().ThrowAsync<RemoteNoClobberPublishUnavailableException>())
            .Which.Should().BeSameAs(injected);

        AssertSingleErrorRecord(sink, SessionOperationKind.Mkdir, "/srv/data/dir");
    }

    [Fact]
    public async Task CreateDirectoryExclusiveAsync_Cancelled_LogsMkdirCancelled_AndRethrowsTheSameInstance()
    {
        CapturingOperationLog sink = new();
        OperationCanceledException injected = new();
        RecordingPublisher publisher = new() { PublishException = injected };
        LoggingRemoteBrowser decorator = Create(new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher), sink);

        Func<Task> reserve = () => decorator.NoClobberPublisher!.CreateDirectoryExclusiveAsync("/srv/data/dir");

        (await reserve.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().BeSameAs(injected);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Mkdir);
        record.Result.Should().Be(SessionOperationResult.Cancelled);

        // An Upload record must not stand in for the reservation: the two are different operations and
        // an operator filtering on Mkdir would see the directory step simply missing.
        sink.Records.Should().NotContain(candidate => candidate.Op == SessionOperationKind.Upload);
    }

    private static void AssertSingleErrorRecord(
        CapturingOperationLog sink,
        SessionOperationKind expectedKind,
        string expectedRemotePath)
    {
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(expectedKind);
        record.Result.Should().Be(SessionOperationResult.Error);
        record.RemotePath.Should().Be(expectedRemotePath);
        record.ErrorCategory.Should().NotBeNull();

        // Never a success, and never the other operation's record standing in for this one.
        record.Result.Should().NotBe(SessionOperationResult.Success);
        sink.Records.Should().NotContain(candidate => candidate.Op != expectedKind);
    }

    // The gate silences the record, never the operation. A publication dropped because logging was off
    // would turn a preference into data loss.
    [Fact]
    public async Task Publish_WhenGateDisabled_StillPublishes_AndRecordsNothing()
    {
        string localPath = NewTempFileOfSize(16);
        CapturingOperationLog sink = new();
        RecordingPublisher publisher = new();
        LoggingRemoteBrowser decorator = Create(
            new CapableRemoteBrowser(new FakeRemoteBrowser(), publisher),
            sink,
            gateEnabled: false);

        await decorator.NoClobberPublisher!.PublishFileIfAbsentAsync(localPath, "/srv/data/file.bin");

        publisher.PublishCalls.Should().ContainSingle();
        sink.Records.Should().BeEmpty();
    }

    // The undecorated path. When no sink is wired the view returns the raw browser, so a browser that
    // can publish must keep saying so: the capability probe has to work decorated and undecorated
    // alike, which is why it is a seam and not a type test.
    [Fact]
    public void CreateOperationsBrowser_WithoutASink_ReturnsTheRawBrowserUnwrapped()
    {
        string source = ReadRepositoryFile("src/Heimdall.App/Views/EmbeddedSftpView.xaml.cs");

        int methodIndex = source.IndexOf(
            "private IRemoteBrowser CreateOperationsBrowser(",
            StringComparison.Ordinal);
        methodIndex.Should().BeGreaterThanOrEqualTo(0);

        int guardIndex = source.IndexOf("SessionOperationLog is null", methodIndex, StringComparison.Ordinal);
        int decorateIndex = source.IndexOf("new LoggingRemoteBrowser(", methodIndex, StringComparison.Ordinal);
        guardIndex.Should().BeGreaterThanOrEqualTo(0);
        decorateIndex.Should().BeGreaterThan(guardIndex);

        // Between the guard and the decoration there is exactly one early return, and it returns the
        // browser as it came in. Wrapping it in anything here would drop the inner capability.
        string guardBlock = source[guardIndex..decorateIndex];
        guardBlock.Should().Contain("return browser;");
        guardBlock.Should().NotContain("new LoggingRemoteBrowser(");
    }

    // The wrapper the view actually hands to the view model, not an isolated normalizer. Two distinct
    // FTP servers must produce two distinct keys through it: when they both produced the empty key, the
    // clipboard treated them as one endpoint and routed the paste to the same-server path, which never
    // consults the no-clobber gate and commits with a replacing rename.
    [Fact]
    public void FromConnection_ThroughTheDecorator_KeepsEachFtpEndpointDistinct()
    {
        using FtpBrowser first = CreateFtpBrowserAt("ftp-one.example.test", 21, "alice");
        using FtpBrowser second = CreateFtpBrowserAt("ftp-two.example.test", 2121, "bob");
        CapturingOperationLog sink = new();

        LoggingRemoteBrowser firstWrapper = Create(first, sink, host: "ftp-one.example.test");
        LoggingRemoteBrowser secondWrapper = Create(second, sink, host: "ftp-two.example.test");

        string firstKey = RemoteClipboardEndpointKey.FromConnection(
            firstWrapper, endpoint: string.Empty, sshParams: null);
        string secondKey = RemoteClipboardEndpointKey.FromConnection(
            secondWrapper, endpoint: string.Empty, sshParams: null);

        firstKey.Should().Be("protocol=ftp;host=ftp-one.example.test;port=21;user=alice");
        secondKey.Should().Be("protocol=ftp;host=ftp-two.example.test;port=2121;user=bob");
        firstKey.Should().NotBeEmpty();
        secondKey.Should().NotBeEmpty();
        firstKey.Should().NotBe(secondKey);
    }

    // A wrapper around a wrapper must keep reaching the raw browser: the identity is resolved through
    // the inner browser rather than copied once, so nesting cannot quietly erase it.
    [Fact]
    public void FromConnection_ThroughTwoDecorators_StillResolvesTheIdentity()
    {
        using FtpBrowser inner = CreateFtpBrowserAt("ftp-one.example.test", 21, "alice");
        CapturingOperationLog sink = new();

        LoggingRemoteBrowser once = Create(inner, sink, host: "ftp-one.example.test");
        LoggingRemoteBrowser twice = Create(once, sink, host: "ftp-one.example.test");

        RemoteClipboardEndpointKey.FromConnection(twice, endpoint: string.Empty, sshParams: null)
            .Should().Be("protocol=ftp;host=ftp-one.example.test;port=21;user=alice");
    }

    // SSH parameters keep priority, so an SFTP session's logical-host identity is not replaced by
    // whatever socket the browser happens to report.
    [Fact]
    public void FromConnection_WithSshParams_PrefersTheSshIdentity()
    {
        using FtpBrowser inner = CreateFtpBrowserAt("ftp-one.example.test", 21, "alice");
        CapturingOperationLog sink = new();
        LoggingRemoteBrowser wrapper = Create(inner, sink);
        SshConnectionParams sshParams = new()
        {
            Host = "sftp.example.test",
            Port = 22,
            Username = "carol",
        };

        RemoteClipboardEndpointKey.FromConnection(wrapper, endpoint: string.Empty, sshParams)
            .Should().Be(RemoteClipboardEndpointKey.FromSsh(sshParams));
    }

    /// <summary>
    /// Builds a disconnected <see cref="FtpBrowser"/> carrying the endpoint metadata of a connected one.
    /// </summary>
    /// <remarks>
    /// The fields are set directly because connecting would need a real FTP server. What is under test
    /// is the identity chain through the decorator, not the transport: the browser only has to report
    /// the same host/port/user a connected one would.
    /// </remarks>
    private static FtpBrowser CreateFtpBrowserAt(string host, int port, string username)
    {
        FtpBrowser browser = new();
        SetPrivateField(browser, "_host", host);
        SetPrivateField(browser, "_port", port);
        SetPrivateField(browser, "_username", username);

        browser.Host.Should().Be(host, "the metadata seam must still exist for this oracle to mean anything");
        browser.Port.Should().Be(port);
        browser.Username.Should().Be(username);

        return browser;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name}.{fieldName} was not found; this oracle would be vacuous");
        field.SetValue(target, value);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("a source oracle that reads nothing proves nothing");

        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }

    private sealed class RecordingPublisher : IRemoteNoClobberPublisher
    {
        public List<(string LocalPath, string RemotePath)> PublishCalls { get; } = [];

        public List<string> ReserveCalls { get; } = [];

        /// <summary>When set, the publication fails with this exception.</summary>
        public Exception? PublishException { get; set; }

        public Task PublishFileIfAbsentAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            PublishCalls.Add((localPath, remotePath));
            return PublishException is null ? Task.CompletedTask : Task.FromException(PublishException);
        }

        public Task CreateDirectoryExclusiveAsync(string remotePath, CancellationToken ct = default)
        {
            ReserveCalls.Add(remotePath);
            return PublishException is null ? Task.CompletedTask : Task.FromException(PublishException);
        }
    }

    /// <summary>
    /// A browser that carries the no-clobber capability, so the decorator has something to forward.
    /// </summary>
    private sealed class CapableRemoteBrowser : IRemoteBrowser, IRemoteNoClobberCapability
    {
        private readonly FakeRemoteBrowser _inner;

        internal CapableRemoteBrowser(FakeRemoteBrowser inner, IRemoteNoClobberPublisher? publisher)
        {
            _inner = inner;
            NoClobberPublisher = publisher;
        }

        public IRemoteNoClobberPublisher? NoClobberPublisher { get; }

        public event Action<string>? DirectoryChanged
        {
            add => _inner.DirectoryChanged += value;
            remove => _inner.DirectoryChanged -= value;
        }

        public event Action<SftpTransferProgress>? TransferProgress
        {
            add => _inner.TransferProgress += value;
            remove => _inner.TransferProgress -= value;
        }

        public event Action<RemoteOperationWarning>? OperationWarningRaised
        {
            add => _inner.OperationWarningRaised += value;
            remove => _inner.OperationWarningRaised -= value;
        }

        public event Action<string?>? Disconnected
        {
            add => _inner.Disconnected += value;
            remove => _inner.Disconnected -= value;
        }

        public string CurrentDirectory => _inner.CurrentDirectory;

        public bool IsConnected => _inner.IsConnected;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => _inner.ListDirectoryAsync(path, ct);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
            => _inner.GetCurrentDirectoryAsync(ct);

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
            => _inner.ChangeDirectoryAsync(path, ct);

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => _inner.DownloadFileAsync(remotePath, localPath, ct);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => _inner.UploadFileAsync(localPath, remotePath, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
            => _inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default) => _inner.DeleteAsync(path, ct);

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
            => _inner.ChmodAsync(path, mode, ct);

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => _inner.RenameAsync(oldPath, newPath, ct);

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => _inner.CopyAsync(sourcePath, destinationPath, recursive, ct);

        public void Disconnect() => _inner.Disconnect();

        public void Dispose() => _inner.Dispose();
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

        public event Action<RemoteOperationWarning>? OperationWarningRaised;

        public event Action<string?>? Disconnected;

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public void RaiseDirectoryChanged(string path) => DirectoryChanged?.Invoke(path);

        public void RaiseTransferProgress(SftpTransferProgress progress) => TransferProgress?.Invoke(progress);

        public void RaiseOperationWarning(RemoteOperationWarning warning) => OperationWarningRaised?.Invoke(warning);

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

        /// <summary>Records every CopyAsync call so forwarding can be asserted.</summary>
        public List<(string Source, string Destination, bool Recursive)> CopyCalls { get; } = [];

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
        {
            CopyCalls.Add((sourcePath, destinationPath, recursive));
            return Run();
        }

        public void Disconnect() => DisconnectCalled = true;

        public void Dispose() => DisposeCount++;

        private Task Run() => OpException is null ? Task.CompletedTask : Task.FromException(OpException);
    }
}
