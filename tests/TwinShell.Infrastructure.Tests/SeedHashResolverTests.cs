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

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TwinShell.Persistence.Seeding;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// The launch reads the seed files only when their metadata moved since the content hash was
/// taken; otherwise the stored hash stands in. A skipped read is only worth having if a changed
/// seed is still caught, so each way a file can change is pinned next to the skip.
/// </summary>
public sealed class SeedHashResolverTests : IDisposable
{
    private static readonly DateTime FixedWriteTime = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "heimdall_seed_resolver_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _files = [];

    public SeedHashResolverTests()
    {
        Directory.CreateDirectory(_folder);
        WriteSeed("a.json", "{\"id\":\"a\"}");
        WriteSeed("b.json", "{\"id\":\"b\"}");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ADatabaseWithNoFingerprint_ReadsEveryFile_AndRecordsTheFingerprintOnceTheHashIsTheAppliedOne()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);

        // A database seeded before the fingerprint existed: a hash, no fingerprint.
        await refresher.RefreshAsync([], SeedCatalogueHash.Compute(_files));
        var reads = new ReadCounter();

        SeedHashResolution first = await Resolver(refresher, reads).ResolveAsync(_files);
        SeedHashResolution second = await Resolver(refresher, reads).ResolveAsync(_files);

        first.ContentRead.Should().BeTrue("there was no fingerprint to compare with");
        first.MatchesApplied.Should().BeTrue();
        reads.Count.Should().Be(_files.Count, "the first launch hashes every file once");
        second.ContentRead.Should().BeFalse("the fingerprint recorded by the first launch is unchanged");
        reads.Count.Should().Be(_files.Count, "the second launch opened no file");
        (await refresher.ReadAppliedFingerprintAsync()).Should().Be(first.Fingerprint);
    }

    // The skip itself. The mutant that always computes the content hash reads every file again
    // and fails the count below.
    [Fact]
    public async Task AnUnchangedFingerprint_SkipsTheContentRead_AndReusesTheStoredHash()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        SeedHashResolution applied = await AppliedAsync(refresher);
        var reads = new ReadCounter();

        SeedHashResolution next = await Resolver(refresher, reads).ResolveAsync(_files);

        reads.Count.Should().Be(0);
        next.ContentRead.Should().BeFalse();
        next.MatchesApplied.Should().BeTrue();
        next.Hash.Should().Be(applied.Hash);
    }

    [Fact]
    public async Task AChangedLastWriteTime_TriggersTheFullHash_AndAnIdenticalContentRefreshesTheFingerprint()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        SeedHashResolution applied = await AppliedAsync(refresher);
        File.SetLastWriteTimeUtc(_files[0], FixedWriteTime.AddHours(1));
        var reads = new ReadCounter();

        SeedHashResolution touched = await Resolver(refresher, reads).ResolveAsync(_files);
        SeedHashResolution afterwards = await Resolver(refresher, reads).ResolveAsync(_files);

        touched.ContentRead.Should().BeTrue("the metadata moved, so the content must be looked at");
        reads.Count.Should().Be(_files.Count);
        touched.Hash.Should().Be(applied.Hash, "the bytes did not change, only the time");
        touched.MatchesApplied.Should().BeTrue();
        touched.Fingerprint.Should().NotBe(applied.Fingerprint);
        afterwards.ContentRead.Should().BeFalse("the new fingerprint was stored, so the next launch is fast again");
        reads.Count.Should().Be(_files.Count);
    }

    [Fact]
    public async Task AChangedSize_TriggersTheFullHash_AndTheNewContentIsNotTheAppliedOne()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        SeedHashResolution applied = await AppliedAsync(refresher);

        // The time is put back, so the size is the only thing that moved.
        File.WriteAllText(_files[1], "{\"id\":\"b\",\"title\":\"longer\"}");
        File.SetLastWriteTimeUtc(_files[1], FixedWriteTime);
        var reads = new ReadCounter();

        SeedHashResolution changed = await Resolver(refresher, reads).ResolveAsync(_files);

        changed.ContentRead.Should().BeTrue();
        reads.Count.Should().Be(_files.Count);
        changed.Hash.Should().NotBe(applied.Hash);
        changed.MatchesApplied.Should().BeFalse("a refresh is due");
        (await refresher.ReadAppliedFingerprintAsync()).Should().Be(
            applied.Fingerprint,
            "the fingerprint is only moved with the hash it describes, by the refresh");
    }

    [Fact]
    public async Task ARefreshWithTheNewFingerprint_StoresItWithTheNewHash_SoTheNextLaunchSkipsTheRead()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await AppliedAsync(refresher);
        File.WriteAllText(_files[1], "{\"id\":\"b\",\"title\":\"longer\"}");
        SeedHashResolution changed = await Resolver(refresher, new ReadCounter()).ResolveAsync(_files);

        await refresher.RefreshAsync([], changed.Hash, changed.Fingerprint);
        var reads = new ReadCounter();
        SeedHashResolution next = await Resolver(refresher, reads).ResolveAsync(_files);

        reads.Count.Should().Be(0);
        next.Hash.Should().Be(changed.Hash);
        next.MatchesApplied.Should().BeTrue();
    }

    [Fact]
    public async Task ARefreshWithNoFingerprint_DropsTheOneLeftFromTheEarlierSeed()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await AppliedAsync(refresher);

        await refresher.RefreshAsync([], "another-hash");

        (await refresher.ReadAppliedFingerprintAsync()).Should().BeNull(
            "a fingerprint left behind would describe a seed that is no longer the applied one");
    }

    [Fact]
    public async Task RecordingAFingerprintTwice_IsIdempotent()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);

        await refresher.RecordFingerprintAsync("fingerprint-1");
        await refresher.RecordFingerprintAsync("fingerprint-1");
        await refresher.RecordFingerprintAsync("fingerprint-2");

        (await refresher.ReadAppliedFingerprintAsync()).Should().Be("fingerprint-2");
        (await fixture.DbContext.SeedStates.CountAsync(s => s.Key == SystemSeedRefresher.SeedFingerprintKey))
            .Should().Be(1);
    }

    [Fact]
    public async Task TheFingerprintAndTheContentRead_RunOffTheCallingThread()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        var threads = new ConcurrentBag<int>();
        var resolver = new SeedHashResolver(
            refresher,
            path =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                return File.ReadAllBytes(path);
            },
            path =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                return SeedFileStamp.FromFileSystem(path);
            });

        // The calling thread stands in for the UI thread: it owns a context that sends every
        // continuation back to it, so work that is not moved off it shows up as running on it.
        int callingThread = 0;
        await SingleThreadContext.RunAsync(async () =>
        {
            callingThread = Environment.CurrentManagedThreadId;
            await resolver.ResolveAsync(_files);
        });

        threads.Should().NotBeEmpty();
        threads.Should().NotContain(callingThread, "the file work is not allowed to hold the launch's UI thread");
    }

    [Fact]
    public void TheFingerprint_FollowsNamesSizesAndTimes_NotTheOrderGiven()
    {
        string first = SeedCatalogueFingerprint.Compute(_files);

        SeedCatalogueFingerprint.Compute([_files[1], _files[0]]).Should().Be(first);
        first.Should().MatchRegex("^[0-9a-f]{64}$");

        File.SetLastWriteTimeUtc(_files[0], FixedWriteTime.AddSeconds(1));
        string retimed = SeedCatalogueFingerprint.Compute(_files);
        retimed.Should().NotBe(first);

        File.WriteAllText(_files[1], "{\"id\":\"b-longer\"}");
        File.SetLastWriteTimeUtc(_files[1], FixedWriteTime);
        SeedCatalogueFingerprint.Compute(_files).Should().NotBe(retimed);

        string renamed = Path.Combine(_folder, "c.json");
        File.Move(_files[0], renamed);
        SeedCatalogueFingerprint.Compute([renamed, _files[1]]).Should().NotBe(retimed);
    }

    private async Task<SeedHashResolution> AppliedAsync(SystemSeedRefresher refresher)
    {
        SeedHashResolution resolution = await Resolver(refresher, new ReadCounter()).ResolveAsync(_files);
        await refresher.RefreshAsync([], resolution.Hash, resolution.Fingerprint);
        return resolution;
    }

    private static SeedHashResolver Resolver(SystemSeedRefresher refresher, ReadCounter reads)
        => new(refresher, reads.Read, SeedFileStamp.FromFileSystem);

    private void WriteSeed(string name, string content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, FixedWriteTime);
        _files.Add(path);
    }

    private sealed class ReadCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public byte[] Read(string path)
        {
            Interlocked.Increment(ref _count);
            return File.ReadAllBytes(path);
        }
    }

    /// <summary>
    /// Runs an async body on one dedicated thread whose synchronization context posts every
    /// continuation back to that thread, the way a UI thread does.
    /// </summary>
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public static Task RunAsync(Func<Task> body)
        {
            var completion = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                var context = new SingleThreadContext();
                SetSynchronizationContext(context);
                Task task = body();
                _ = task.ContinueWith(
                    finished =>
                    {
                        if (finished.IsFaulted)
                        {
                            completion.SetException(finished.Exception!.InnerExceptions);
                        }
                        else
                        {
                            completion.SetResult();
                        }

                        context._queue.CompleteAdding();
                    },
                    TaskScheduler.Default);

                foreach ((SendOrPostCallback callback, object? state) in context._queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            });
            thread.Start();
            return completion.Task;
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    }
}
