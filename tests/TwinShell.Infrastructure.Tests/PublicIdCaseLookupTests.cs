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
using TwinShell.Core.Models;
using TwinShell.Persistence.Schema;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// A public id stored in lower case, as schema step 1 wrote it for every row that existed
/// before the column, is found after the upgrade by the lookups the sync and its tombstones
/// rely on.
/// </summary>
/// <remarks>
/// The ids carry hex letters on purpose: an id made only of digits reads the same in either
/// case, and a test built on one passes whatever the lookup does.
/// </remarks>
public sealed class PublicIdCaseLookupTests
{
    private static readonly Guid ActionPublicId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly Guid TemplatePublicId = Guid.Parse("abcdefab-cdef-4abc-8def-abcdefabcdef");
    private static readonly Guid BatchPublicId = Guid.Parse("fedcbafe-dcba-4fed-8cba-fedcbafedcba");
    private static readonly Guid CategoryPublicId = Guid.Parse("cafebabe-cafe-4abe-8afe-cafebabecafe");

    [Fact]
    public async Task ALowerCasePublicId_WrittenAsSchemaStep1Did_IsFoundByEveryRepositoryAfterTheUpgrade()
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        await SeedAsync(fixture);
        (await ReadStoredActionPublicIdAsync(fixture)).Should().Be(
            ActionPublicId.ToString().ToUpperInvariant(),
            "EF writes a Guid as upper-case text, so lower case below is a real difference");

        await fixture.DbContext.Database.ExecuteSqlRawAsync("UPDATE Actions SET PublicId = lower(PublicId)");
        await fixture.DbContext.Database.ExecuteSqlRawAsync("UPDATE CommandTemplates SET PublicId = lower(PublicId)");
        await fixture.DbContext.Database.ExecuteSqlRawAsync("UPDATE CommandBatches SET PublicId = lower(PublicId)");
        await fixture.DbContext.Database.ExecuteSqlRawAsync("UPDATE CustomCategories SET PublicId = lower(PublicId)");
        fixture.DbContext.ChangeTracker.Clear();
        (await ReadStoredActionPublicIdAsync(fixture)).Should().Be(ActionPublicId.ToString());

        // Measured before the fix: SQLite compares text case-sensitively, so the lookup missed.
        (await fixture.Actions.GetByPublicIdAsync(ActionPublicId)).Should().BeNull(
            "this is the defect the upgrade corrects; if it ever finds the row, the step is moot");

        await SchemaUpgrader.UpgradeAsync(fixture.DbContext, TwinShellSchema.Steps);
        await SchemaUpgrader.UpgradeAsync(fixture.DbContext, TwinShellSchema.Steps);
        fixture.DbContext.ChangeTracker.Clear();

        (await fixture.Actions.GetByPublicIdAsync(ActionPublicId)).Should().NotBeNull();
        (await fixture.Templates.GetByPublicIdAsync(TemplatePublicId)).Should().NotBeNull();
        (await fixture.Batches.GetByPublicIdAsync(BatchPublicId)).Should().NotBeNull();
        (await fixture.Categories.GetByPublicIdAsync(CategoryPublicId)).Should().NotBeNull();
    }

    // The twin is what the lookup bug made: the import missed the lower-case original and
    // created an upper-case copy with the same id. Upper-casing the original would break the
    // unique index and roll the whole upgrade back.
    [Theory]
    [InlineData("Actions", "act-case", "act-twin")]
    [InlineData("CommandTemplates", "tpl-case", "tpl-twin")]
    [InlineData("CommandBatches", "rb-case", "rb-twin")]
    [InlineData("CustomCategories", "cat-case", "cat-twin")]
    public async Task ALowerCaseOriginalWithAnUpperCaseTwin_GetsANewId_AndBothRowsStay(
        string table,
        string twinId,
        string originalId)
    {
        await using TwinShellSyncFixture fixture = await TwinShellSyncFixture.CreateAsync();
        await SeedAsync(fixture);
        await SeedOriginalsAsync(fixture);
        await LowerCaseOriginalAsTwinAsync(fixture, table, originalId, twinId);
        string twinPublicId = await ReadPublicIdAsync(fixture, table, twinId);

        await SchemaUpgrader.UpgradeAsync(fixture.DbContext, TwinShellSchema.Steps);
        string afterFirst = await ReadPublicIdAsync(fixture, table, originalId);
        await SchemaUpgrader.UpgradeAsync(fixture.DbContext, TwinShellSchema.Steps);

        (await ReadPublicIdAsync(fixture, table, twinId)).Should().Be(twinPublicId, "the copy the folder matches keeps the sync identity");
        string original = await ReadPublicIdAsync(fixture, table, originalId);
        original.Should().NotBe(twinPublicId);
        original.Should().Be(original.ToUpperInvariant());
        Guid.TryParse(original, out _).Should().BeTrue();
        original.Should().Be(afterFirst, "a second run changes nothing");
    }

    /// <summary>A second row of each kind, the "original" a twin pairs with.</summary>
    private static async Task SeedOriginalsAsync(TwinShellSyncFixture fixture)
    {
        await fixture.Actions.AddAsync(new ActionModel
        {
            Id = "act-twin",
            Title = "Original action",
            Category = CommandCategories.Development,
            Platform = Platform.Linux,
            Level = CriticalityLevel.Info,
            IsUserCreated = true
        });
        await fixture.Templates.AddAsync(new CommandTemplate
        {
            Id = "tpl-twin",
            Name = "Original template",
            Platform = Platform.Linux,
            CommandPattern = "echo original"
        });
        await fixture.Batches.AddAsync(new CommandBatch { Id = "rb-twin", Name = "Original runbook", IsUserCreated = true });
        await fixture.Categories.CreateAsync(new CustomCategory { Id = "cat-twin", Name = "Original category" });
    }

    /// <summary>
    /// Gives the row <paramref name="originalId"/> the lower-case form of the public id of
    /// <paramref name="twinId"/>, as step 1 left the original before the import duplicated it.
    /// </summary>
    private static async Task LowerCaseOriginalAsTwinAsync(TwinShellSyncFixture fixture, string table, string originalId, string twinId)
    {
        string twin = await ReadPublicIdAsync(fixture, table, twinId);
        string lower = twin.ToLowerInvariant();
        lower.Should().NotBe(twin, "the id must carry letters for case to matter");
        int updated = table switch
        {
            "Actions" => await fixture.DbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE Actions SET PublicId = {lower} WHERE Id = {originalId}"),
            "CommandTemplates" => await fixture.DbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE CommandTemplates SET PublicId = {lower} WHERE Id = {originalId}"),
            "CommandBatches" => await fixture.DbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE CommandBatches SET PublicId = {lower} WHERE Id = {originalId}"),
            _ => await fixture.DbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE CustomCategories SET PublicId = {lower} WHERE Id = {originalId}")
        };
        updated.Should().Be(1);
        fixture.DbContext.ChangeTracker.Clear();
    }

    private static Task<string> ReadPublicIdAsync(TwinShellSyncFixture fixture, string table, string id)
        => (table switch
        {
            "Actions" => fixture.DbContext.Database.SqlQuery<string>($"SELECT PublicId AS Value FROM Actions WHERE Id = {id}"),
            "CommandTemplates" => fixture.DbContext.Database.SqlQuery<string>($"SELECT PublicId AS Value FROM CommandTemplates WHERE Id = {id}"),
            "CommandBatches" => fixture.DbContext.Database.SqlQuery<string>($"SELECT PublicId AS Value FROM CommandBatches WHERE Id = {id}"),
            _ => fixture.DbContext.Database.SqlQuery<string>($"SELECT PublicId AS Value FROM CustomCategories WHERE Id = {id}")
        }).SingleAsync();

    private static Task<string> ReadStoredActionPublicIdAsync(TwinShellSyncFixture fixture)
        => fixture.DbContext.Database
            .SqlQueryRaw<string>("SELECT PublicId AS Value FROM Actions WHERE Id = {0}", "act-case")
            .SingleAsync();

    private static async Task SeedAsync(TwinShellSyncFixture fixture)
    {
        CommandTemplate template = new()
        {
            Id = "tpl-case",
            PublicId = TemplatePublicId,
            Name = "Case template",
            Platform = Platform.Linux,
            CommandPattern = "echo case"
        };
        await fixture.Actions.AddAsync(new ActionModel
        {
            Id = "act-case",
            PublicId = ActionPublicId,
            Title = "Case action",
            Category = CommandCategories.Development,
            Platform = Platform.Linux,
            Level = CriticalityLevel.Info,
            LinuxCommandTemplateId = template.Id,
            LinuxCommandTemplate = template,
            IsUserCreated = true
        });
        await fixture.Batches.AddAsync(new CommandBatch
        {
            Id = "rb-case",
            PublicId = BatchPublicId,
            Name = "Case runbook",
            IsUserCreated = true
        });
        await fixture.Categories.CreateAsync(new CustomCategory
        {
            Id = "cat-case",
            PublicId = CategoryPublicId,
            Name = "Case category"
        });
    }
}
