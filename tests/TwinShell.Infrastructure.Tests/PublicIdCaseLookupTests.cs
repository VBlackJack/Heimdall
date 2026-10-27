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
