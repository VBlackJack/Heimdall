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

using Heimdall.App.ViewModels;
using Heimdall.Core.Utilities;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// The root activity log: what it keeps, what it counts apart, how it writes a path the server
/// named, and that it never lets a subscriber break the act it describes.
/// </summary>
public sealed class SftpRootActivityLogTests
{
    private const int ParallelWriters = 8;
    private const int RecordsPerWriter = 100;
    private const int OverflowBy = 7;
    private const int OversizedPathLength = 5000;
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Operations_AreKeptNewestFirstAndBoundedButStillCounted()
    {
        SftpRootActivityLog log = new();
        int total = SftpRootActivityLog.MaxRecentOperations + OverflowBy;
        for (int index = 0; index < total; index++)
        {
            log.Record(SftpRootActKind.Delete, "/srv/f" + index, null, SftpRootActOutcome.Succeeded);
        }

        SftpRootActivitySnapshot snapshot = log.Snapshot();

        Assert.Equal(total, snapshot.OperationCount);
        Assert.Equal(SftpRootActivityLog.MaxRecentOperations, snapshot.RecentOperations.Count);
        Assert.Equal("/srv/f" + (total - 1), snapshot.RecentOperations[0].Path);
        Assert.Equal(
            "/srv/f" + (total - SftpRootActivityLog.MaxRecentOperations),
            snapshot.RecentOperations[^1].Path);
    }

    [Fact]
    public void Listings_AreCountedApartAndNeverEnterTheRecentList()
    {
        SftpRootActivityLog log = new();
        log.Record(SftpRootActKind.Listing, "/etc", null, SftpRootActOutcome.Succeeded);
        log.Record(SftpRootActKind.Listing, "/root", null, SftpRootActOutcome.Failed);
        log.Record(SftpRootActKind.Listing, "/var", null, SftpRootActOutcome.Cancelled);

        SftpRootActivitySnapshot snapshot = log.Snapshot();

        Assert.Empty(snapshot.RecentOperations);
        Assert.Equal(0, snapshot.OperationCount);
        Assert.Equal(0, snapshot.FailedOperationCount);
        Assert.Equal(3, snapshot.ListingCount);
        Assert.Equal(1, snapshot.FailedListingCount);
        Assert.NotNull(snapshot.LastListing);
        Assert.Equal("/var", snapshot.LastListing!.Path);
        Assert.Equal(SftpRootActOutcome.Cancelled, snapshot.LastListing.Outcome);
    }

    [Fact]
    public void FailedOperations_AreCountedAndCancelledOnesAreNotFailures()
    {
        SftpRootActivityLog log = new();
        log.Record(SftpRootActKind.Upload, "/a", null, SftpRootActOutcome.Succeeded);
        log.Record(SftpRootActKind.Upload, "/b", null, SftpRootActOutcome.Failed);
        log.Record(SftpRootActKind.Upload, "/c", null, SftpRootActOutcome.Cancelled);

        SftpRootActivitySnapshot snapshot = log.Snapshot();

        Assert.Equal(3, snapshot.OperationCount);
        Assert.Equal(1, snapshot.FailedOperationCount);
    }

    [Fact]
    public void ARename_KeepsBothPaths()
    {
        SftpRootActivityLog log = new();
        log.Record(SftpRootActKind.Rename, "/srv/old", "/srv/new", SftpRootActOutcome.Succeeded);

        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);

        Assert.Equal("/srv/old", act.Path);
        Assert.Equal("/srv/new", act.TargetPath);
    }

    [Fact]
    public void APath_IsWrittenOutVisiblyBeforeItIsKept()
    {
        SftpRootActivityLog log = new();
        string forged = "/srv/a\nb" + (char)0x202E + "gpj.txt";

        log.Record(SftpRootActKind.Delete, forged, forged, SftpRootActOutcome.Succeeded);

        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.DoesNotContain('\n', act.Path);
        Assert.DoesNotContain((char)0x202E, act.Path);
        Assert.DoesNotContain('\n', act.TargetPath!);
        Assert.DoesNotContain((char)0x202E, act.TargetPath!);
        Assert.NotEqual(forged, act.Path);
    }

    [Fact]
    public void APath_IsCutAtTheBound()
    {
        SftpRootActivityLog log = new();

        log.Record(SftpRootActKind.Delete, new string('x', OversizedPathLength), null, SftpRootActOutcome.Succeeded);

        SftpRootAct act = Assert.Single(log.Snapshot().RecentOperations);
        Assert.True(
            act.Path.Length <= SftpRootActivityLog.MaxShownPathLength + UntrustedText.TruncationMarker.Length,
            $"the path was kept at {act.Path.Length} characters");
        Assert.EndsWith(UntrustedText.TruncationMarker, act.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClock_StampsEachAct()
    {
        DateTimeOffset start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        FakeTimeProvider clock = new(start);
        SftpRootActivityLog log = new(clock);

        log.Record(SftpRootActKind.Delete, "/a", null, SftpRootActOutcome.Succeeded);
        clock.Advance(TimeSpan.FromMinutes(1));
        log.Record(SftpRootActKind.Delete, "/b", null, SftpRootActOutcome.Succeeded);

        IReadOnlyList<SftpRootAct> recent = log.Snapshot().RecentOperations;
        Assert.Equal(start.AddMinutes(1), recent[0].Timestamp);
        Assert.Equal(start, recent[1].Timestamp);
    }

    [Fact]
    public void Changed_IsRaisedForEachActAndOutsideTheLock()
    {
        SftpRootActivityLog log = new();
        int raised = 0;
        bool readableFromAnotherThread = false;
        log.Changed += () =>
        {
            raised++;
            readableFromAnotherThread = Task.Run(() => log.Snapshot()).Wait(HandlerWait);
        };

        log.Record(SftpRootActKind.Upload, "/a", null, SftpRootActOutcome.Succeeded);
        log.Record(SftpRootActKind.Listing, "/b", null, SftpRootActOutcome.Succeeded);

        Assert.Equal(2, raised);
        Assert.True(readableFromAnotherThread, "a subscriber ran while the log still held its lock");
    }

    [Fact]
    public void AFailingSubscriber_NeverBreaksTheRecord()
    {
        SftpRootActivityLog log = new();
        log.Changed += () => throw new InvalidOperationException("subscriber");

        log.Record(SftpRootActKind.Upload, "/a", null, SftpRootActOutcome.Succeeded);

        Assert.Equal(1, log.Snapshot().OperationCount);
    }

    [Fact]
    public async Task ParallelRecords_AreAllCounted()
    {
        SftpRootActivityLog log = new();
        Task[] writers = Enumerable
            .Range(0, ParallelWriters)
            .Select(_ => Task.Run(() =>
            {
                for (int index = 0; index < RecordsPerWriter; index++)
                {
                    log.Record(SftpRootActKind.Upload, "/a", null, SftpRootActOutcome.Succeeded);
                    log.Record(SftpRootActKind.Listing, "/b", null, SftpRootActOutcome.Succeeded);
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);

        SftpRootActivitySnapshot snapshot = log.Snapshot();
        Assert.Equal(ParallelWriters * RecordsPerWriter, snapshot.OperationCount);
        Assert.Equal(ParallelWriters * RecordsPerWriter, snapshot.ListingCount);
        Assert.Equal(SftpRootActivityLog.MaxRecentOperations, snapshot.RecentOperations.Count);
    }

    [Fact]
    public void ASnapshot_IsACopy()
    {
        SftpRootActivityLog log = new();
        log.Record(SftpRootActKind.Upload, "/a", null, SftpRootActOutcome.Succeeded);
        SftpRootActivitySnapshot before = log.Snapshot();

        log.Record(SftpRootActKind.Upload, "/b", null, SftpRootActOutcome.Succeeded);

        Assert.Single(before.RecentOperations);
        Assert.Equal(1, before.OperationCount);
    }
}
