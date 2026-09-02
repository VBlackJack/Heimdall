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

using System.Globalization;
using System.IO;
using System.Text.Json;
using Heimdall.App.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using TwinShell.Infrastructure.Services;
using TwinShell.Persistence;
using TwinShell.Persistence.Repositories;
using TwinShell.Persistence.Schema;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Services;

/// <summary>
/// Registers TwinShell services into Heimdall's DI container
/// and seeds the command library database on first launch.
/// </summary>
internal static class TwinShellBootstrapper
{
    private static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwinShell");

    private static readonly string DbPath = Path.Combine(DbDir, "twinshell.db");

    /// <summary>
    /// Registers TwinShell persistence and core services.
    /// Call from <see cref="App.ConfigureServices"/>.
    /// </summary>
    public static void RegisterServices(IServiceCollection services)
    {
        Directory.CreateDirectory(DbDir);

        services.AddDbContext<TwinShellDbContext>(options =>
            options.UseSqlite($"Data Source={DbPath}"));

        // Logging adapter (TwinShell repositories require ILogger<T>)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(new FileLoggerProvider(LogLevel.Warning));
        });

        // Memory cache (required by ActionRepository)
        services.AddMemoryCache();

        // Repositories
        services.AddScoped<IActionRepository, ActionRepository>();
        services.AddScoped<ICommandHistoryRepository, CommandHistoryRepository>();
        services.AddScoped<IFavoritesRepository, FavoritesRepository>();
        services.AddScoped<ICustomCategoryRepository, CustomCategoryRepository>();
        services.AddScoped<ICommandTemplateRepository, CommandTemplateRepository>();
        services.AddScoped<ISyncHistoryRepository, SyncHistoryRepository>();
        services.AddScoped<IBatchRepository, BatchRepository>();
        services.AddScoped<IUnitOfWork, TwinShell.Persistence.UnitOfWork>();

        // Localization bridge for CommandGeneratorService
        services.AddSingleton<ILocalizationService>(sp =>
        {
            var heimdallLocalizer = sp.GetService<Heimdall.Core.Localization.LocalizationManager>();
            return new HeimdallLocalizationBridge(heimdallLocalizer);
        });

        // Settings bridge for GitSyncService
        services.AddSingleton<ISettingsService>(sp =>
        {
            var configManager = sp.GetRequiredService<Heimdall.Core.Configuration.IConfigManager>();
            return new HeimdallSettingsBridge(configManager);
        });

        // Core services
        services.AddScoped<IActionService, ActionService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        services.AddScoped<ICommandHistoryService, CommandHistoryService>();
        services.AddScoped<IFavoritesService, FavoritesService>();

        // Git Sync services (from TwinShell.Infrastructure)
        services.AddScoped<ISyncService, JsonSyncService>();
        services.AddSingleton<IGitSyncService, GitSyncService>();
    }

    /// <summary>
    /// Ensures the database exists and seeds initial command data.
    /// Call once during application startup.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TwinShellDbContext>();

        await context.Database.EnsureCreatedAsync();
        await SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps);

        // Seed only if the database is empty
        var repo = scope.ServiceProvider.GetRequiredService<IActionRepository>();
        var count = await repo.CountAsync();
        if (count > 0)
        {
            Heimdall.Core.Logging.FileLogger.Info(
                $"[TwinShell] Command library ready ({count} actions)");
            return;
        }

        // Locate seed files: prefer output-dir copy, fall back to AppData
        var seedDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "seed", "actions");

        if (!Directory.Exists(seedDir))
        {
            seedDir = Path.Combine(DbDir, "data", "seed", "actions");
        }

        if (!Directory.Exists(seedDir))
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                "[TwinShell] No seed directory found - command library will be empty");
            return;
        }

        var jsonFiles = Directory.GetFiles(seedDir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .OrderBy(f => f)
            .ToArray();

        var options = JsonOptionsHelper.CaseInsensitive;
        var validActions = new List<ActionModel>(jsonFiles.Length);

        // Parse/validate/normalize pass: per-file try/catch so one malformed or oversized
        // file is logged and skipped without aborting the rest of the batch.
        foreach (var file in jsonFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                if (json.Length > Heimdall.Core.Configuration.AppConstants.MaxSeedFileSizeBytes) continue;

                var action = JsonSerializer.Deserialize<ActionModel>(json, options);
                if (action is null
                    || string.IsNullOrWhiteSpace(action.Title)
                    || string.IsNullOrWhiteSpace(action.Category))
                    continue;

                action.IsUserCreated = false;
                action.CreatedAt = DateTime.UtcNow;
                action.UpdatedAt = DateTime.UtcNow;

                validActions.Add(action);
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[TwinShell] Seed error ({Path.GetFileName(file)}): {ex.Message}");
            }
        }

        // Single-transaction batch insert: one SaveChanges for the whole seed instead of
        // ~514 serialized AddAsync transactions on the awaited startup path. The seed runs
        // only on an empty DB, so AddRangeAsync's within-batch template dedup is sufficient.
        // Atomicity tradeoff: a DbUpdateException (e.g. a duplicate Id across two seed files)
        // now fails the whole seed rather than skipping a single row. This is acceptable and
        // arguably preferable (all-or-nothing) for curated, version-controlled seed data.
        if (validActions.Count > 0)
        {
            await repo.AddRangeAsync(validActions);
        }

        Heimdall.Core.Logging.FileLogger.Info(
            $"[TwinShell] Seeded {validActions.Count} actions from {jsonFiles.Length} files");
    }

    /// <summary>
    /// Bridges Heimdall's <see cref="Heimdall.Core.Localization.LocalizationManager"/>
    /// to TwinShell's <see cref="ILocalizationService"/> interface.
    /// Only <see cref="GetString(string)"/> is used by CommandGeneratorService.
    /// </summary>
    private sealed class HeimdallLocalizationBridge : ILocalizationService
    {
        private readonly Heimdall.Core.Localization.LocalizationManager? _localizer;

        public HeimdallLocalizationBridge(
            Heimdall.Core.Localization.LocalizationManager? localizer)
        {
            _localizer = localizer;
        }

        public CultureInfo CurrentCulture => CultureInfo.CurrentUICulture;
        public CultureInfo[] SupportedCultures => [new("en"), new("fr")];

        public string GetString(string key) => _localizer?[key] ?? key;
        public string GetString(string key, string fallback) => _localizer?[key] ?? fallback;

        public string GetFormattedString(string key, params object[] args)
        {
            var template = GetString(key);
            try { return string.Format(template, args); }
            catch { return template; }
        }

        public void ChangeLanguage(CultureInfo culture) { }
        public void ChangeLanguage(string cultureCode) { }

#pragma warning disable CS0067 // Required by ILocalizationService but not used in bridge
        public event EventHandler? LanguageChanged;
#pragma warning restore CS0067
    }

    /// <summary>
    /// Bridges Heimdall's <see cref="Heimdall.Core.Configuration.IConfigManager"/>
    /// to TwinShell's <see cref="ISettingsService"/> interface for GitSyncService.
    /// </summary>
    private sealed class HeimdallSettingsBridge : ISettingsService
    {
        private readonly Heimdall.Core.Configuration.IConfigManager _configManager;
        private UserSettings _cached = new();

        public HeimdallSettingsBridge(Heimdall.Core.Configuration.IConfigManager configManager)
        {
            _configManager = configManager;
            _ = RefreshAsync();
            _configManager.SettingsChanged += OnSettingsChanged;
        }

        public UserSettings CurrentSettings => _cached;

        private void OnSettingsChanged(Heimdall.Core.Configuration.AppSettings s)
            => _cached = MapSettings(s);

        private static UserSettings MapSettings(Heimdall.Core.Configuration.AppSettings s) => new()
        {
            GitRemoteUrl = s.CmdLibGitSyncUrl,
            // Vault-aware read: v2 token when unlocked, legacy DPAPI when no vault,
            // null (Git deferred) when a v2 token is encountered while locked.
            GitAccessToken = GitTokenReader.Decrypt(s.CmdLibGitSyncToken),
            GitBranch = s.CmdLibGitSyncBranch,
            GitUserName = s.CmdLibGitSyncAuthorName,
            GitUserEmail = s.CmdLibGitSyncAuthorEmail,
            GitSyncOnStartup = s.CmdLibGitSyncOnStartup,
            GitAutoPush = s.CmdLibGitSyncAutoPush,
            GitRepositoryPath = Path.Combine(DbDir, "git-repo"),
            GitAuthMethod = "https"
        };

        public async Task<UserSettings> LoadSettingsAsync()
        {
            await RefreshAsync();
            return _cached;
        }

        public Task<bool> SaveSettingsAsync(UserSettings settings) => Task.FromResult(true);
        public Task<UserSettings> ResetToDefaultAsync() => Task.FromResult(new UserSettings());
        public string GetSettingsFilePath() => "";
        public bool ValidateSettings(UserSettings settings) => true;

        private async Task RefreshAsync()
        {
            try
            {
                var s = await _configManager.LoadSettingsAsync();
                _cached = MapSettings(s);
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"[TwinShell] Settings bridge refresh failed: {ex.Message}");
            }
        }
    }
}
