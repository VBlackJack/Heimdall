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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Repositories;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

// Producer: ActionRepository.AddRangeAsync (src/TwinShell.Persistence/Repositories/ActionRepository.cs:321).
// This is the batch path the Command Library first-launch seed now uses instead of a per-file
// AddAsync loop (TwinShellBootstrapper.SeedAsync), so these tests pin its single-transaction
// behavior: persist actions with their templates, dedup templates by Id within the batch, and
// invalidate the categories cache once.
public sealed class ActionRepositoryBatchTests
{
    [Fact]
    public async Task AddRangeAsync_PersistsActionsWithWindowsAndLinuxTemplates()
    {
        await using TempActionRepository repository = await TempActionRepository.CreateAsync();

        ActionModel first = CreateAction(
            "act-1",
            "Alpha",
            CreateTemplate("tpl-1-win", Platform.Windows),
            CreateTemplate("tpl-1-lin", Platform.Linux));
        ActionModel second = CreateAction(
            "act-2",
            "Beta",
            CreateTemplate("tpl-2-win", Platform.Windows),
            CreateTemplate("tpl-2-lin", Platform.Linux));

        await repository.Actions.AddRangeAsync([first, second]);

        int actionCount = await repository.DbContext.Actions.CountAsync();
        int templateCount = await repository.DbContext.CommandTemplates.CountAsync();
        List<string> templateIds = await repository.DbContext.CommandTemplates
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToListAsync();

        actionCount.Should().Be(2);
        templateCount.Should().Be(4);
        templateIds.Should().Equal("tpl-1-lin", "tpl-1-win", "tpl-2-lin", "tpl-2-win");

        ActionEntity persistedFirst = await repository.DbContext.Actions
            .AsNoTracking()
            .SingleAsync(a => a.Id == "act-1");
        persistedFirst.WindowsCommandTemplateId.Should().Be("tpl-1-win");
        persistedFirst.LinuxCommandTemplateId.Should().Be("tpl-1-lin");
        persistedFirst.IsUserCreated.Should().BeFalse();
    }

    [Fact]
    public async Task AddRangeAsync_TemplatesSharingId_AreInsertedOnce()
    {
        await using TempActionRepository repository = await TempActionRepository.CreateAsync();

        // Both actions reference the SAME Windows template Id — the seed can legitimately
        // share a template across actions, and AddRangeAsync dedups by Id within the batch.
        ActionModel first = CreateAction("act-1", "Alpha", CreateTemplate("shared-win", Platform.Windows), null);
        ActionModel second = CreateAction("act-2", "Beta", CreateTemplate("shared-win", Platform.Windows), null);

        await repository.Actions.AddRangeAsync([first, second]);

        int templateCount = await repository.DbContext.CommandTemplates.CountAsync();
        int sharedCount = await repository.DbContext.CommandTemplates
            .CountAsync(t => t.Id == "shared-win");

        templateCount.Should().Be(1);
        sharedCount.Should().Be(1);
    }

    [Fact]
    public async Task AddRangeAsync_InvalidatesCategoriesCache()
    {
        await using TempActionRepository repository = await TempActionRepository.CreateAsync();

        await repository.Actions.AddAsync(CreateAction("act-seed", "Alpha", null, null));

        // Prime the categories cache; without invalidation it would stay stale at ["Alpha"].
        IEnumerable<string> beforeBatch = await repository.Actions.GetAllCategoriesAsync();
        beforeBatch.Should().Equal("Alpha");

        await repository.Actions.AddRangeAsync([CreateAction("act-batch", "Beta", null, null)]);

        IEnumerable<string> afterBatch = await repository.Actions.GetAllCategoriesAsync();
        afterBatch.Should().Equal("Alpha", "Beta");
    }

    private static ActionModel CreateAction(
        string id,
        string category,
        CommandTemplate? windowsTemplate,
        CommandTemplate? linuxTemplate)
    {
        DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return new ActionModel
        {
            Id = id,
            Title = "Title " + id,
            Description = "Description " + id,
            Category = category,
            Platform = Platform.Both,
            Level = CriticalityLevel.Info,
            WindowsCommandTemplateId = windowsTemplate?.Id,
            WindowsCommandTemplate = windowsTemplate,
            LinuxCommandTemplateId = linuxTemplate?.Id,
            LinuxCommandTemplate = linuxTemplate,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            IsUserCreated = false
        };
    }

    private static CommandTemplate CreateTemplate(string id, Platform platform)
    {
        return new CommandTemplate
        {
            Id = id,
            Name = "Template " + id,
            Platform = platform,
            CommandPattern = "echo {value}"
        };
    }

    private sealed class TempActionRepository : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly MemoryCache _memoryCache;

        private TempActionRepository(
            string rootPath,
            TwinShellDbContext dbContext,
            MemoryCache memoryCache,
            IActionRepository actions)
        {
            _rootPath = rootPath;
            DbContext = dbContext;
            _memoryCache = memoryCache;
            Actions = actions;
        }

        internal TwinShellDbContext DbContext { get; }

        internal IActionRepository Actions { get; }

        internal static async Task<TempActionRepository> CreateAsync()
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_twinshell_actionbatch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            string databasePath = Path.Combine(rootPath, "actions.db");
            DbContextOptions<TwinShellDbContext> options = new DbContextOptionsBuilder<TwinShellDbContext>()
                .UseSqlite("Data Source=" + databasePath)
                .Options;
            TwinShellDbContext dbContext = new(options);
            await dbContext.Database.EnsureCreatedAsync();

            MemoryCache memoryCache = new(new MemoryCacheOptions());
            IActionRepository actions = new ActionRepository(
                dbContext,
                memoryCache,
                NullLogger<ActionRepository>.Instance);

            return new TempActionRepository(rootPath, dbContext, memoryCache, actions);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            _memoryCache.Dispose();
            DeleteDirectory(_rootPath);
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
