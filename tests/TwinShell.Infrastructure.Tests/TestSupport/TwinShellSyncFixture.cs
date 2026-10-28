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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;
using TwinShell.Persistence;
using TwinShell.Persistence.Repositories;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

internal sealed class TwinShellSyncFixture : IDisposable, IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly MemoryCache _memoryCache;
    private bool _disposed;

    private TwinShellSyncFixture(
        string rootPath,
        TwinShellDbContext dbContext,
        MemoryCache memoryCache,
        IActionRepository actions,
        IBatchRepository batches,
        ICustomCategoryRepository categories,
        ICommandTemplateRepository templates,
        ISyncTombstoneRepository tombstones,
        JsonSyncService syncService)
    {
        _rootPath = rootPath;
        DbContext = dbContext;
        _memoryCache = memoryCache;
        Actions = actions;
        Batches = batches;
        Categories = categories;
        Templates = templates;
        Tombstones = tombstones;
        SyncService = syncService;
    }

    internal TwinShellDbContext DbContext { get; }

    internal IActionRepository Actions { get; }

    internal IBatchRepository Batches { get; }

    internal ICustomCategoryRepository Categories { get; }

    internal ICommandTemplateRepository Templates { get; }

    internal ISyncTombstoneRepository Tombstones { get; }

    internal JsonSyncService SyncService { get; }

    internal static async Task<TwinShellSyncFixture> CreateAsync()
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall_twinshell_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        string databasePath = Path.Combine(rootPath, "sync.db");
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
        IBatchRepository batches = new BatchRepository(dbContext);
        ICustomCategoryRepository categories = new CustomCategoryRepository(dbContext);
        ICommandTemplateRepository templates = new CommandTemplateRepository(dbContext);
        IUnitOfWork unitOfWork = new UnitOfWork(dbContext);
        ISyncTombstoneRepository tombstones = new SyncTombstoneRepository(dbContext);
        JsonSyncService syncService = new(
            actions,
            batches,
            categories,
            templates,
            unitOfWork,
            tombstoneRepository: tombstones);

        return new TwinShellSyncFixture(
            rootPath,
            dbContext,
            memoryCache,
            actions,
            batches,
            categories,
            templates,
            tombstones,
            syncService);
    }

    internal async Task<SeededSyncData> SeedAsync()
    {
        DateTime createdAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime updatedAt = new(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc);

        CustomCategory category = new()
        {
            Id = "category-identity",
            PublicId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Name = "Identity",
            IconKey = "users",
            ColorHex = "#336699",
            IsSystemCategory = false,
            DisplayOrder = 10,
            IsHidden = false,
            Description = "Identity administration",
            CreatedAt = createdAt
        };
        await Categories.CreateAsync(category);

        CommandTemplate template = new()
        {
            Id = "template-list-users",
            PublicId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Name = "List Users Template",
            Platform = Platform.Windows,
            CommandPattern = "Get-ADUser -Filter * -SearchBase {searchBase}",
            Parameters =
            [
                new TemplateParameter
                {
                    Name = "searchBase",
                    Label = "Search base",
                    Type = "string",
                    DefaultValue = "DC=example,DC=local",
                    Required = true,
                    Description = "LDAP search base"
                }
            ]
        };
        await Templates.AddAsync(template);

        ActionModel firstAction = new()
        {
            Id = "action-list-users",
            PublicId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Title = "List Users",
            Description = "Lists directory users.",
            Category = category.Name,
            Platform = Platform.Windows,
            Level = CriticalityLevel.Info,
            Tags = ["identity", "users"],
            WindowsCommandTemplateId = template.Id,
            WindowsCommandTemplate = template,
            Examples =
            [
                new CommandExample
                {
                    Command = "Get-ADUser -Filter *",
                    Description = "List all users",
                    Platform = Platform.Windows
                }
            ],
            Notes = "Read-only inventory command.",
            Links =
            [
                new ExternalLink
                {
                    Title = "Get-ADUser",
                    Url = "https://learn.microsoft.com/powershell/module/activedirectory/get-aduser"
                }
            ],
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            IsUserCreated = true
        };
        await Actions.AddAsync(firstAction);

        ActionModel secondAction = new()
        {
            Id = "action-show-groups",
            PublicId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            Title = "Show Groups",
            Description = "Lists directory groups.",
            Category = category.Name,
            Platform = Platform.Windows,
            Level = CriticalityLevel.Info,
            Tags = ["identity", "groups"],
            CreatedAt = createdAt,
            UpdatedAt = updatedAt.AddMinutes(1),
            IsUserCreated = true
        };
        await Actions.AddAsync(secondAction);

        CommandBatch batch = new()
        {
            Id = "batch-identity-audit",
            PublicId = Guid.Parse("55555555-5555-4555-8555-555555555555"),
            Name = "Identity Audit",
            Description = "Runs identity inventory commands.",
            ExecutionMode = BatchExecutionMode.StopOnError,
            Tags = ["identity", "audit"],
            Commands =
            [
                new BatchCommandItem
                {
                    Id = "batch-command-list-users",
                    Order = 0,
                    ActionId = firstAction.Id,
                    ActionTitle = firstAction.Title,
                    Command = "Get-ADUser -Filter *",
                    Platform = Platform.Windows,
                    Description = "Collect user inventory"
                }
            ],
            CreatedAt = createdAt,
            UpdatedAt = updatedAt.AddMinutes(2),
            IsUserCreated = true
        };
        await Batches.AddAsync(batch);

        return new SeededSyncData(
            category,
            template,
            [firstAction, secondAction],
            batch);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DbContext.DisposeAsync();
        _memoryCache.Dispose();
        DeleteDirectory(_rootPath);
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DbContext.Dispose();
        _memoryCache.Dispose();
        DeleteDirectory(_rootPath);
        _disposed = true;
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

internal sealed class SeededSyncData
{
    internal SeededSyncData(
        CustomCategory category,
        CommandTemplate template,
        IReadOnlyList<ActionModel> actions,
        CommandBatch batch)
    {
        Category = category;
        Template = template;
        Actions = actions;
        Batch = batch;
    }

    internal CustomCategory Category { get; }

    internal CommandTemplate Template { get; }

    internal IReadOnlyList<ActionModel> Actions { get; }

    internal CommandBatch Batch { get; }
}
