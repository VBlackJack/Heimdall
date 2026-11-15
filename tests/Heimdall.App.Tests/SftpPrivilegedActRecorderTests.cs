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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The funnel every root act goes through: the session journal receives exactly what it did before,
/// the indicator records every act whether or not the journal is open, and a failure leaves as the
/// same exception object.
/// </summary>
public sealed class SftpPrivilegedActRecorderTests
{
    private const string Protocol = "SFTP";
    private const string Host = "host.example";
    private const long ReportedBytes = 4096;

    private static (SftpPrivilegedActRecorder Recorder, SftpRootActivityLog Log, JournalSpy Journal) Create(
        bool journalOpen = true)
    {
        JournalSpy journal = new();
        SessionOperationEmitter emitter = new(journal, () => journalOpen, Protocol, Host);
        SftpRootActivityLog log = new();
        return (new SftpPrivilegedActRecorder(emitter, log), log, journal);
    }

    [Fact]
    public async Task ASuccessfulOperation_IsJournaledOncePrivilegedAndRecordedAsSucceeded()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();

        await recorder.RunMkdirAsync("/srv/new", () => Task.CompletedTask);

        SessionOperationRecord record = Assert.Single(journal.Records);
        Assert.Equal(SessionOperationKind.Mkdir, record.Op);
        Assert.Equal(SessionOperationResult.Success, record.Result);
        Assert.True(record.Privileged);
        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.Equal(SftpRootActKind.NewFolder, act.Kind);
        Assert.Equal("/srv/new", act.Path);
        Assert.Equal(SftpRootActOutcome.Succeeded, act.Outcome);
    }

    [Fact]
    public async Task AFailedOperation_IsRecordedAsFailedAndTheSameExceptionInstancePropagates()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();
        InvalidOperationException failure = new("denied");

        InvalidOperationException caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.RunDeleteAsync("/srv/x", () => Task.FromException(failure)));

        Assert.Same(failure, caught);
        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.Equal(SftpRootActKind.Delete, act.Kind);
        Assert.Equal(SftpRootActOutcome.Failed, act.Outcome);
        Assert.Equal(1, log.Snapshot().FailedOperationCount);
        SessionOperationRecord record = Assert.Single(journal.Records);
        Assert.Equal(SessionOperationResult.Error, record.Result);
        Assert.True(record.Privileged);
    }

    [Fact]
    public async Task ACancelledOperation_IsRecordedAsCancelledAndTheSameExceptionInstancePropagates()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();
        OperationCanceledException cancellation = new();

        OperationCanceledException caught = await Assert.ThrowsAsync<OperationCanceledException>(
            () => recorder.RunChmodAsync("/srv/x", () => Task.FromException(cancellation)));

        Assert.Same(cancellation, caught);
        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.Equal(SftpRootActKind.PermissionChange, act.Kind);
        Assert.Equal(SftpRootActOutcome.Cancelled, act.Outcome);
        Assert.Equal(0, log.Snapshot().FailedOperationCount);
        Assert.Equal(SessionOperationResult.Cancelled, Assert.Single(journal.Records).Result);
    }

    [Fact]
    public async Task AnOperationThatThrowsBeforeItReturnsATask_IsStillRecordedAsFailed()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, _) = Create();
        InvalidOperationException failure = new("synchronous");

        InvalidOperationException caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.RunDeleteAsync("/srv/x", () => throw failure));

        Assert.Same(failure, caught);
        Assert.Equal(SftpRootActOutcome.Failed, Assert.Single(log.Snapshot().RecentOperations).Outcome);
    }

    [Fact]
    public async Task WithTheJournalClosed_TheIndicatorStillRecordsAndTheJournalReceivesNothing()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create(journalOpen: false);

        await recorder.RunRenameAsync("/srv/a", "/srv/b", () => Task.CompletedTask);

        Assert.Empty(journal.Records);
        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.Equal(SftpRootActKind.Rename, act.Kind);
        Assert.Equal("/srv/a", act.Path);
        Assert.Equal("/srv/b", act.TargetPath);
    }

    [Fact]
    public async Task WithNoJournalWired_TheIndicatorStillRecords()
    {
        SftpRootActivityLog log = new();
        SftpPrivilegedActRecorder recorder = new(SessionOperationEmitter.Disabled, log);

        await recorder.RunDownloadAsync("/srv/a", @"C:\dl\a", () => Task.CompletedTask, () => ReportedBytes);

        Assert.Equal(SftpRootActKind.Download, Assert.Single(log.Snapshot().RecentOperations).Kind);
    }

    [Fact]
    public async Task AnUpload_IsRecordedUnderTheKindItWasGiven()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();

        await recorder.RunUploadAsync(
            SftpRootActKind.Overwrite,
            @"C:\up\a",
            "/srv/a",
            () => Task.CompletedTask,
            () => ReportedBytes);

        Assert.Equal(SftpRootActKind.Overwrite, Assert.Single(log.Snapshot().RecentOperations).Kind);
        SessionOperationRecord record = Assert.Single(journal.Records);
        Assert.Equal(SessionOperationKind.Upload, record.Op);
        Assert.Equal(ReportedBytes, record.Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEditorSave_IsJournaledAsBeforeAndRecorded(bool success)
    {
        SftpRootActOutcome expectedOutcome = success ? SftpRootActOutcome.Succeeded : SftpRootActOutcome.Failed;
        SessionOperationResult expectedResult = success ? SessionOperationResult.Success : SessionOperationResult.Error;
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();

        recorder.RecordEditorSave(@"C:\tmp\a", "/etc/a", success, () => ReportedBytes);

        SessionOperationRecord record = Assert.Single(journal.Records);
        Assert.Equal(expectedResult, record.Result);
        Assert.True(record.Privileged);
        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.Equal(SftpRootActKind.ExternalEditorSave, act.Kind);
        Assert.Equal("/etc/a", act.Path);
        Assert.Equal(expectedOutcome, act.Outcome);
    }

    [Fact]
    public void AnEditorSave_IsRecordedWithTheJournalClosed()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create(journalOpen: false);

        recorder.RecordEditorSave(@"C:\tmp\a", "/etc/a", success: true, () => ReportedBytes);

        Assert.Empty(journal.Records);
        Assert.Equal(1, log.Snapshot().OperationCount);
    }

    [Fact]
    public async Task AListing_PassesItsValueThroughIsNotJournaledAndIsCountedApart()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();
        List<string> listed = ["a", "b"];

        List<string> result = await recorder.RunListingAsync("/root", () => Task.FromResult(listed));

        Assert.Same(listed, result);
        Assert.Empty(journal.Records);
        SftpRootActivitySnapshot snapshot = log.Snapshot();
        Assert.Empty(snapshot.RecentOperations);
        Assert.Equal(0, snapshot.OperationCount);
        Assert.Equal(1, snapshot.ListingCount);
        Assert.Equal("/root", snapshot.LastListing!.Path);
    }

    [Fact]
    public async Task AFailedListing_RethrowsTheSameInstanceAndCountsAsAFailedListing()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, _) = Create();
        InvalidOperationException failure = new("find failed");

        InvalidOperationException caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.RunListingAsync<int>("/root", () => Task.FromException<int>(failure)));

        Assert.Same(failure, caught);
        Assert.Equal(1, log.Snapshot().FailedListingCount);
        Assert.Equal(0, log.Snapshot().FailedOperationCount);
    }

    [Fact]
    public async Task OpeningAFileForEditing_IsRecordedButNotJournaled()
    {
        (SftpPrivilegedActRecorder recorder, SftpRootActivityLog log, JournalSpy journal) = Create();

        await recorder.RunEditOpenAsync("/etc/hosts", () => Task.CompletedTask);

        Assert.Empty(journal.Records);
        Assert.Equal(SftpRootActKind.EditOpen, Assert.Single(log.Snapshot().RecentOperations).Kind);
    }

    private sealed class JournalSpy : ISessionOperationLog
    {
        public List<SessionOperationRecord> Records { get; } = [];

        public void LogOperation(SessionOperationRecord record) => Records.Add(record);

        public void Dispose()
        {
        }
    }
}
