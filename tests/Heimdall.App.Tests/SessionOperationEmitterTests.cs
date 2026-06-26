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

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOperationEmitter"/>: the sudo emitter records privileged
/// operation records on success, classifies errors, distinguishes cancellation, honours the live
/// gate, and the <see cref="SessionOperationEmitter.Disabled"/> instance never records.
/// </summary>
public sealed class SessionOperationEmitterTests
{
    private const string Protocol = "SFTP";
    private const string Host = "host.example";

    private static SessionOperationEmitter Create(
        CapturingOperationLog sink, bool gateEnabled = true, string host = Host)
        => new SessionOperationEmitter(sink, () => gateEnabled, Protocol, host);

    [Fact]
    public async Task RunMkdirAsync_Success_EmitsPrivilegedSuccessRecord()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        await emitter.RunMkdirAsync("/srv/data/newdir", () => Task.CompletedTask, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Mkdir);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.Privileged.Should().BeTrue();
        record.RemotePath.Should().Be("/srv/data/newdir");
        record.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task RunRenameAsync_Success_EmitsRemotePathTo()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        await emitter.RunRenameAsync("/srv/a.txt", "/srv/b.txt", () => Task.CompletedTask, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Rename);
        record.RemotePathTo.Should().Be("/srv/b.txt");
        record.Privileged.Should().BeTrue();
    }

    [Fact]
    public async Task RunDownloadAsync_Success_EmitsBytesFromCallback()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        await emitter.RunDownloadAsync(
            "/srv/data/file.bin", @"C:\dl\file.bin", () => Task.CompletedTask, () => 4096L, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Download);
        record.Bytes.Should().Be(4096);
        record.LocalPath.Should().Be(@"C:\dl\file.bin");
        record.Privileged.Should().BeTrue();
    }

    [Fact]
    public async Task RunUploadAsync_Success_EmitsBytesFromCallback()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        await emitter.RunUploadAsync(
            @"C:\local\file.bin", "/srv/data/file.bin", () => Task.CompletedTask, () => 128L, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Upload);
        record.RemotePath.Should().Be("/srv/data/file.bin");
        record.Bytes.Should().Be(128);
        record.Privileged.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_OperationThrows_EmitsErrorWithCategoryAndRethrows()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        Func<Task> act = () => emitter.RunDeleteAsync(
            "/srv/data/old.bin",
            () => throw new IOException("disk full"),
            privileged: true);

        await act.Should().ThrowAsync<IOException>();
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Result.Should().Be(SessionOperationResult.Error);
        record.ErrorCategory.Should().Be("io");
        record.Privileged.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_OperationCancelled_EmitsCancelledAndRethrows()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        Func<Task> act = () => emitter.RunDeleteAsync(
            "/srv/data/old.bin",
            () => throw new OperationCanceledException(),
            privileged: true);

        await act.Should().ThrowAsync<OperationCanceledException>();
        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Result.Should().Be(SessionOperationResult.Cancelled);
        record.ErrorCategory.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GateDisabled_RunsOperationButEmitsNothing()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink, gateEnabled: false);
        bool ran = false;

        await emitter.RunMkdirAsync("/srv/data/newdir", () => { ran = true; return Task.CompletedTask; }, privileged: true);

        ran.Should().BeTrue();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_GateDisabled_StillRethrows()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink, gateEnabled: false);

        Func<Task> act = () => emitter.RunDeleteAsync(
            "/srv/data/old.bin", () => throw new IOException("boom"), privileged: true);

        await act.Should().ThrowAsync<IOException>();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_RunsOperationButNeverRecords()
    {
        bool ran = false;

        await SessionOperationEmitter.Disabled.RunMkdirAsync(
            "/srv/data/newdir", () => { ran = true; return Task.CompletedTask; }, privileged: true);

        ran.Should().BeTrue();
    }

    [Fact]
    public void EmitUploadCompleted_Success_EmitsPrivilegedUploadWithBytes()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);

        emitter.EmitUploadCompleted(
            @"C:\edit\app.conf", "/etc/app.conf", success: true, bytesOnSuccess: () => 321L, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Op.Should().Be(SessionOperationKind.Upload);
        record.Result.Should().Be(SessionOperationResult.Success);
        record.RemotePath.Should().Be("/etc/app.conf");
        record.LocalPath.Should().Be(@"C:\edit\app.conf");
        record.Bytes.Should().Be(321);
        record.DurationMs.Should().Be(0);
        record.Privileged.Should().BeTrue();
    }

    [Fact]
    public void EmitUploadCompleted_Failure_EmitsErrorOtherWithoutBytes()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink);
        bool bytesRead = false;

        emitter.EmitUploadCompleted(
            @"C:\edit\app.conf", "/etc/app.conf", success: false,
            bytesOnSuccess: () => { bytesRead = true; return 1L; }, privileged: true);

        SessionOperationRecord record = sink.Records.Should().ContainSingle().Subject;
        record.Result.Should().Be(SessionOperationResult.Error);
        record.ErrorCategory.Should().Be("other");
        record.Bytes.Should().BeNull();
        record.Privileged.Should().BeTrue();
        bytesRead.Should().BeFalse("the byte count is read only on success");
    }

    [Fact]
    public void EmitUploadCompleted_GateDisabled_EmitsNothingAndDoesNotReadBytes()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink, gateEnabled: false);
        bool bytesRead = false;

        emitter.EmitUploadCompleted(
            @"C:\edit\app.conf", "/etc/app.conf", success: true,
            bytesOnSuccess: () => { bytesRead = true; return 1L; }, privileged: true);

        sink.Records.Should().BeEmpty();
        bytesRead.Should().BeFalse();
    }

    [Fact]
    public async Task Host_WithUserPrefix_IsStripped()
    {
        CapturingOperationLog sink = new();
        SessionOperationEmitter emitter = Create(sink, host: "admin@10.0.0.5");

        await emitter.RunDeleteAsync("/srv/data/old.bin", () => Task.CompletedTask, privileged: true);

        sink.Records.Should().ContainSingle().Which.Host.Should().Be("10.0.0.5");
    }

    private sealed class CapturingOperationLog : ISessionOperationLog
    {
        public List<SessionOperationRecord> Records { get; } = [];

        public void LogOperation(SessionOperationRecord record) => Records.Add(record);

        public void Dispose()
        {
        }
    }
}
