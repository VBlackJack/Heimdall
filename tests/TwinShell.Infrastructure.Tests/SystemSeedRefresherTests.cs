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
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Seeding;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// A changed seed reaches a database seeded by an older build; an unchanged one costs
/// nothing; the operator's own actions and favorites survive either way.
/// </summary>
public sealed class SystemSeedRefresherTests
{
    private const string FirstHash = "hash-1";
    private const string SecondHash = "hash-2";

    [Fact]
    public async Task AChangedSeed_ReplacesSystemActions_AndKeepsAUserCreatedOne()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await refresher.RefreshAsync([SystemAction("kept", "Old title"), SystemAction("retired", "Retired")], FirstHash);
        await fixture.Actions.AddAsync(UserAction("mine"));

        SeedRefreshResult result = await refresher.RefreshAsync(
            [SystemAction("kept", "New title"), SystemAction("added", "Added")],
            SecondHash);

        result.Should().Be(new SeedRefreshResult(true, Inserted: 1, Updated: 1, Removed: 0, FavoritesRemoved: 0, Retired: 1));
        List<ActionModel> actions = (await fixture.Actions.GetAllAsync()).ToList();
        actions.Select(a => a.Id).Should().BeEquivalentTo(["kept", "added", "mine"]);
        actions.Single(a => a.Id == "kept").Title.Should().Be("New title");
        actions.Single(a => a.Id == "mine").IsUserCreated.Should().BeTrue();
        (await refresher.ReadAppliedHashAsync()).Should().Be(SecondHash);
        (await fixture.DbContext.CommandTemplates.Select(t => t.Id).ToListAsync())
            .Should().BeEquivalentTo(["kept-lin", "added-lin", "mine-lin", "retired-lin"], "a retired action keeps its template");
        (await fixture.Actions.GetByIdAsync("retired"))!.IsRetired.Should().BeTrue("a dropped action is retired, never deleted");
    }

    [Fact]
    public async Task AnUnchangedHash_DoesNothing()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await refresher.RefreshAsync([SystemAction("kept", "Old title")], FirstHash);

        SeedRefreshResult result = await refresher.RefreshAsync([SystemAction("kept", "New title")], FirstHash);

        result.Should().Be(SeedRefreshResult.Unchanged);
        (await fixture.Actions.GetByIdAsync("kept"))!.Title.Should().Be("Old title");
    }

    // A retired action that a favorite points at is kept as retired since D-04: the favorite
    // stays with it, and the library no longer lists the action.
    [Fact]
    public async Task AFavoriteOnASurvivingId_Survives_AndOneOnARetiredIdStaysWithItsRetiredAction()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await refresher.RefreshAsync([SystemAction("kept", "Kept"), SystemAction("retired", "Retired")], FirstHash);
        fixture.DbContext.UserFavorites.AddRange(
            new UserFavoriteEntity { Id = "fav-kept", ActionId = "kept", CreatedAt = DateTime.UtcNow },
            new UserFavoriteEntity { Id = "fav-retired", ActionId = "retired", CreatedAt = DateTime.UtcNow, DisplayOrder = 1 });
        await fixture.DbContext.SaveChangesAsync();
        fixture.DbContext.ChangeTracker.Clear();

        SeedRefreshResult result = await refresher.RefreshAsync([SystemAction("kept", "Kept, reworded")], SecondHash);

        result.Retired.Should().Be(1);
        result.Removed.Should().Be(0);
        (await fixture.DbContext.UserFavorites.Select(f => f.Id).ToListAsync())
            .Should().BeEquivalentTo(["fav-kept", "fav-retired"]);
        (await fixture.Actions.GetAllAsync()).Select(a => a.Id).Should().Equal("kept");
    }

    // D-04: retiring a shipped action used to delete its row, and its whole command history
    // went with it through the cascade. It is now always retired, history or not.
    [Fact]
    public async Task ARetiredActionWithHistory_StaysRetiredWithItsHistory_AndComesBackWhenShippedAgain()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        await refresher.RefreshAsync([SystemAction("kept", "Kept"), SystemAction("retired", "Retired")], FirstHash);
        fixture.DbContext.CommandHistories.Add(new CommandHistoryEntity
        {
            Id = "history-retired",
            ActionId = "retired",
            GeneratedCommand = "echo retired",
            Platform = Platform.Linux,
            CreatedAt = DateTime.UtcNow,
            Category = CommandCategories.Development,
            ActionTitle = "Retired"
        });
        await fixture.DbContext.SaveChangesAsync();
        fixture.DbContext.ChangeTracker.Clear();

        SeedRefreshResult result = await refresher.RefreshAsync([SystemAction("kept", "Kept")], SecondHash);

        result.Retired.Should().Be(1);
        result.Removed.Should().Be(0);
        (await fixture.DbContext.CommandHistories.Select(h => h.Id).ToListAsync()).Should().Equal("history-retired");
        (await fixture.Actions.GetAllAsync()).Select(a => a.Id).Should().Equal("kept");
        (await fixture.Actions.GetByIdAsync("retired"))!.IsRetired.Should().BeTrue();

        await refresher.RefreshAsync([SystemAction("kept", "Kept"), SystemAction("retired", "Retired")], "hash-3");

        (await fixture.Actions.GetAllAsync()).Select(a => a.Id).Should().BeEquivalentTo(["kept", "retired"]);
        (await fixture.Actions.GetByIdAsync("retired"))!.IsRetired.Should().BeFalse();
    }

    [Fact]
    public async Task APopulatedDatabaseWithNoHash_IsRefreshedOnce_AndCarriesTranslationsAndPublicIds()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        ActionModel legacy = SystemAction("kept", "Old title");
        await fixture.Actions.AddRangeAsync([legacy]);
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        (await refresher.ReadAppliedHashAsync()).Should().BeNull();

        ActionModel incoming = SystemAction("kept", "New title");
        incoming.Translations = [new ActionTranslation { CultureCode = "fr", Title = "Nouveau titre" }];
        SeedRefreshResult result = await refresher.RefreshAsync([incoming], FirstHash);

        result.Updated.Should().Be(1);
        ActionModel stored = (await fixture.Actions.GetByIdAsync("kept"))!;
        stored.PublicId.Should().Be(SeedPublicIds.ForAction("kept"), "a shipped action has the same public id on every install");
        stored.Translations.Should().ContainSingle().Which.Title.Should().Be("Nouveau titre");
    }

    [Fact]
    public async Task AUserActionWithAnOldShippedCategory_GetsItsKey()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        ActionModel mine = UserAction("mine");
        mine.Category = "\U0001F512 Security & Encryption";
        await fixture.Actions.AddAsync(mine);

        await new SystemSeedRefresher(fixture.DbContext).RefreshAsync([SystemAction("kept", "Kept")], FirstHash);

        (await fixture.Actions.GetByIdAsync("mine"))!.Category.Should().Be(CommandCategories.Security);
    }

    [Fact]
    public async Task AFailure_RollsBack_AndLeavesThePreviousSeed()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        var refresher = new SystemSeedRefresher(fixture.DbContext);
        ActionModel kept = SystemAction("kept", "Old title");
        kept.Translations = [new ActionTranslation { CultureCode = "fr", Title = "Ancien titre" }];
        await refresher.RefreshAsync([kept, SystemAction("other", "Other")], FirstHash);

        // A title is NOT NULL in the database: the insert of this row fails after the rewrite
        // of "kept" and the delete of "other" were queued.
        ActionModel broken = SystemAction("broken", "Broken");
        broken.Title = null!;
        Func<Task> refresh = () => refresher.RefreshAsync([SystemAction("kept", "New title"), broken], SecondHash);

        await refresh.Should().ThrowAsync<Exception>();
        (await fixture.Actions.GetAllAsync()).Select(a => a.Title).Should().BeEquivalentTo(["Old title", "Other"]);
        (await refresher.ReadAppliedHashAsync()).Should().Be(FirstHash);
        (await fixture.Actions.GetByIdAsync("kept"))!.Translations.Should().ContainSingle(
            "the translations deleted ahead of the failed save come back with the rollback");
    }

    [Fact]
    public void TheHash_FollowsNamesAndBytes_NotTheOrderGiven()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_seed_hash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string a = Path.Combine(folder, "a.json");
            string b = Path.Combine(folder, "b.json");
            File.WriteAllText(a, "{\"id\":\"a\"}");
            File.WriteAllText(b, "{\"id\":\"b\"}");

            string first = SeedCatalogueHash.Compute([a, b]);
            SeedCatalogueHash.Compute([b, a]).Should().Be(first);
            first.Should().MatchRegex("^[0-9a-f]{64}$");

            File.WriteAllText(b, "{\"id\":\"B\"}");
            SeedCatalogueHash.Compute([a, b]).Should().NotBe(first);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ActionModel SystemAction(string id, string title) => CreateAction(id, title, isUserCreated: false);

    private static ActionModel UserAction(string id) => CreateAction(id, "User " + id, isUserCreated: true);

    private static ActionModel CreateAction(string id, string title, bool isUserCreated)
    {
        DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var template = new CommandTemplate
        {
            Id = id + "-lin",
            Name = title ?? id,
            Platform = Platform.Linux,
            CommandPattern = "echo " + id
        };

        return new ActionModel
        {
            Id = id,
            Title = title!,
            Category = CommandCategories.Development,
            Platform = Platform.Linux,
            Level = CriticalityLevel.Info,
            LinuxCommandTemplateId = template.Id,
            LinuxCommandTemplate = template,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            IsUserCreated = isUserCreated
        };
    }
}
