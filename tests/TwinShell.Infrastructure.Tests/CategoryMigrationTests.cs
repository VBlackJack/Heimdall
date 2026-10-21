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
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// An export or a sync folder taken before the shipped categories became keys lands its
/// actions in the groups the shipped ones now use.
/// </summary>
public sealed class CategoryMigrationTests
{
    // The padlock, a space and the old English label: the string 84 shipped actions carried.
    private const string OldSecurity = "\U0001F512 Security & Encryption";

    [Fact]
    public void TheOldSecurityString_MapsToItsKey()
    {
        CategoryMigration.Normalize(OldSecurity).Should().Be(CommandCategories.Security);
        CategoryMigration.Normalize("  " + OldSecurity + " ").Should().Be(CommandCategories.Security);
    }

    [Theory]
    [InlineData("Security & Encryption")]
    [InlineData("My scripts")]
    [InlineData("security")]
    [InlineData("")]
    public void AnythingElse_IsReturnedUnchanged(string category)
    {
        CategoryMigration.Normalize(category).Should().Be(category);
    }

    [Fact]
    public void EveryShippedKey_HasExactlyOneOldString()
    {
        CategoryMigration.Legacy.Values.Should().BeEquivalentTo(CommandCategories.All);
        CommandCategories.All.Should().HaveCount(13);
    }

    [Fact]
    public async Task AGitSyncImport_MapsAnOldCategoryToItsKey()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimdall_category_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using TwinShellSyncFixture source = await TwinShellSyncFixture.CreateAsync();
            ActionModel action = new()
            {
                Id = "legacy",
                Title = "Legacy security action",
                Category = OldSecurity,
                Platform = Platform.Windows,
                Level = CriticalityLevel.Info,
                IsUserCreated = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            await source.Actions.AddAsync(action);
            (await source.SyncService.ExportDataToYamlAsync(folder)).Success.Should().BeTrue();

            await using TwinShellSyncFixture target = await TwinShellSyncFixture.CreateAsync();
            SyncImportResult imported = await target.SyncService.ImportDataFromYamlAsync(folder);

            imported.ActionsCreated.Should().Be(1);
            (await target.Actions.GetByPublicIdAsync(action.PublicId))!.Category.Should().Be(CommandCategories.Security);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
