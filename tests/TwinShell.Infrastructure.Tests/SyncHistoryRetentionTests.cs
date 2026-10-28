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

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Constants;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Utilities;
using TwinShell.Infrastructure.Services;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Repositories;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// The Git sync history keeps the newest entries of each remote and drops the rest after each
/// sync, instead of growing with every sync forever.
/// </summary>
public sealed class SyncHistoryRetentionTests
{
    private const string RemoteA = "https://git.example.invalid/team/commands.git";
    private const string RemoteB = "https://git.example.invalid/other/commands.git";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeleteBeyondNewestPerRemote_KeepsTheNewestOfEachRemote_AndLeavesASmallerRemoteAlone()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        await AddEntriesAsync(fixture, RemoteA, 600);
        await AddEntriesAsync(fixture, RemoteB, 40);
        var repository = new SyncHistoryRepository(fixture.DbContext);

        int deleted = await repository.DeleteBeyondNewestPerRemoteAsync(500);

        deleted.Should().Be(100);
        (await CountAsync(fixture, RemoteA)).Should().Be(500);
        (await CountAsync(fixture, RemoteB)).Should().Be(40);
        DateTime oldestKept = await fixture.DbContext.SyncHistories
            .Where(e => e.RemoteUrl == RemoteA)
            .MinAsync(e => e.StartedAt);
        oldestKept.Should().Be(Origin.AddMinutes(100), "the 100 oldest entries are the ones that go");
    }

    [Fact]
    public async Task DeleteBeyondNewestPerRemote_ATableInsideItsBound_DeletesNothing()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        await AddEntriesAsync(fixture, RemoteA, 500);

        int deleted = await new SyncHistoryRepository(fixture.DbContext).DeleteBeyondNewestPerRemoteAsync(500);

        deleted.Should().Be(0);
        (await CountAsync(fixture, RemoteA)).Should().Be(500);
    }

    /// <summary>
    /// The purge runs after each sync the service records, so a table never outgrows its bound
    /// by more than the entry just added. Driven through the service: the sync that fails for
    /// want of a configuration still writes its history entry.
    /// </summary>
    [Fact]
    public async Task ASyncThatIsRecorded_PurgesTheHistoryBeyondTheBound()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        // An unconfigured sync records its remote as the sanitizer's placeholder for "none".
        string unconfiguredRemote = GitUrlSanitizer.SanitizeForLogging(null);
        await AddEntriesAsync(fixture, unconfiguredRemote, ValidationConstants.MaxSyncHistoryEntriesPerRemote + 100);
        using GitSyncService service = new(
            new GitSyncServiceCancellationTests.FakeSettingsService(new UserSettings()),
            NullLogger<GitSyncService>.Instance,
            new GitSyncServiceCancellationTests.FakeLocalizationService(),
            ScopeFactoryFor(new SyncHistoryRepository(fixture.DbContext)));

        GitOperationResult result = await service.InitializeRepositoryAsync();

        result.Success.Should().BeFalse("nothing is configured, which is what makes the entry cheap to produce");
        (await fixture.DbContext.SyncHistories.CountAsync())
            .Should().Be(ValidationConstants.MaxSyncHistoryEntriesPerRemote);
    }

    private static IServiceScopeFactory ScopeFactoryFor(ISyncHistoryRepository repository)
    {
        ServiceCollection services = new();
        services.AddSingleton(repository);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task AddEntriesAsync(TwinShellSyncFixture fixture, string? remoteUrl, int count)
    {
        for (int index = 0; index < count; index++)
        {
            fixture.DbContext.SyncHistories.Add(new SyncHistoryEntity
            {
                OperationType = "Pull",
                Success = true,
                Message = "ok",
                RemoteUrl = remoteUrl,
                StartedAt = Origin.AddMinutes(index),
                CompletedAt = Origin.AddMinutes(index)
            });
        }

        await fixture.DbContext.SaveChangesAsync();
        fixture.DbContext.ChangeTracker.Clear();
    }

    private static Task<int> CountAsync(TwinShellSyncFixture fixture, string remoteUrl) =>
        fixture.DbContext.SyncHistories.CountAsync(e => e.RemoteUrl == remoteUrl);
}
