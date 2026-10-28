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
using Heimdall.Core.Configuration;
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
using TwinShell.Persistence.Security;
using TwinShell.Persistence.Seeding;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Services;

/// <summary>
/// Registers TwinShell services into Heimdall's DI container
/// and seeds the command library database on first launch.
/// </summary>
internal static class TwinShellBootstrapper
{
    private static readonly string DbDir = ApplicationDataPathResolver.ResolveTwinShellDirectory();

    private static readonly string DbPath = Path.Combine(DbDir, "twinshell.db");

    /// <summary>The stages of the library's initialization, as the startup log names them.</summary>
    private const string EnsureCreatedStage = "TwinShell EnsureCreated";
    private const string SchemaUpgradeStage = "TwinShell schema upgrade";
    private const string PruneHistoryStage = "TwinShell history prune";
    private const string SeedHashStage = "TwinShell seed fingerprint and hash";
    private const string ActionCountStage = "TwinShell action count";

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
        // Stateless and holds no per-request state, so it is shared rather than rebuilt
        // per scope. It is what keeps the two command-history fields that can carry what
        // the user typed out of the plain SQLite file.
        services.AddSingleton<ISecretProtector, CredentialProtectorAdapter>();

        services.AddScoped<IActionRepository, ActionRepository>();
        services.AddScoped<ICommandHistoryRepository, CommandHistoryRepository>();
        services.AddScoped<IFavoritesRepository, FavoritesRepository>();
        services.AddScoped<ICustomCategoryRepository, CustomCategoryRepository>();
        services.AddScoped<ICommandTemplateRepository, CommandTemplateRepository>();
        services.AddScoped<ISyncHistoryRepository, SyncHistoryRepository>();
        services.AddScoped<IBatchRepository, BatchRepository>();
        services.AddScoped<ISyncTombstoneRepository, SyncTombstoneRepository>();
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
        services.AddScoped<IBatchService, BatchService>();

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

        await StartupStageTimer.RunAsync(EnsureCreatedStage, () => context.Database.EnsureCreatedAsync());
        await StartupStageTimer.RunAsync(
            SchemaUpgradeStage, () => SchemaUpgrader.UpgradeAsync(context, TwinShellSchema.Steps));
        await PruneCommandHistoryAsync(scope.ServiceProvider);

        var repo = scope.ServiceProvider.GetRequiredService<IActionRepository>();

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
                "[TwinShell] No seed directory found - command library keeps what it holds");
            return;
        }

        var jsonFiles = Directory.GetFiles(seedDir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .OrderBy(f => f)
            .ToArray();

        // The seed reaches a database whenever it differs from the one last applied, not
        // only on the first launch: see SystemSeedRefresher. The seed files are read only when
        // their metadata moved since the hash was taken, and then off the UI thread: reading all
        // of them on every launch cost seconds after an update or a reboot.
        var refresher = new SystemSeedRefresher(context);
        var seedResolution = await StartupStageTimer.RunAsync(
            SeedHashStage, () => new SeedHashResolver(refresher).ResolveAsync(jsonFiles));
        var seedHash = seedResolution.Hash;
        Heimdall.Core.Logging.FileLogger.Info(
            seedResolution.ContentRead
                ? $"[TwinShell] Seed fingerprint changed or unknown: read {jsonFiles.Length} files"
                : $"[TwinShell] Seed fingerprint unchanged: {jsonFiles.Length} files not read");
        if (seedResolution.MatchesApplied)
        {
            var actionCount = await StartupStageTimer.RunAsync(ActionCountStage, () => repo.CountAsync());
            Heimdall.Core.Logging.FileLogger.Info(
                $"[TwinShell] Command library ready ({actionCount} actions)");
            return;
        }

        var validActions = await ReadSeedActionsAsync(jsonFiles);

        // An empty parse is a broken install, not an empty catalogue: applying it would delete
        // every shipped action.
        if (validActions.Count == 0)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[TwinShell] No valid seed action in {jsonFiles.Length} files - command library keeps what it holds");
            return;
        }

        // One transaction for the whole refresh: a failure (a duplicate id across two seed
        // files, say) rolls back and leaves the previous catalogue in place, so the library
        // still opens on what it had.
        try
        {
            var result = await refresher.RefreshAsync(validActions, seedHash, seedResolution.Fingerprint);
            Heimdall.Core.Logging.FileLogger.Info(
                $"[TwinShell] Seed applied from {jsonFiles.Length} files: {result.Inserted} inserted, "
                + $"{result.Updated} updated, {result.Removed} removed, {result.Retired} retired, "
                + $"{result.FavoritesRemoved} orphan favorite(s) removed");
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Error(
                "[TwinShell] Seed refresh failed and was rolled back; the previous catalogue is kept", ex);
        }
    }

    /// <summary>
    /// Parses the seed files into system actions, skipping any file that is malformed,
    /// oversized, untitled, uncategorized, or in the workstation debloat category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-file try/catch, so one bad file is logged and skipped without aborting the rest.
    /// </para>
    /// <para>
    /// The debloat category belongs to the optional pack under <c>data/packs</c>. The
    /// installer copies over an existing installation without deleting what the new build no
    /// longer ships, so the 72 files that moved to the pack stay in the seed directory of
    /// every upgraded copy; read as seeds, they would come back as shipped actions on the
    /// next launch. Their category is matched after <see cref="CategoryMigration"/>, because
    /// a file left by an older build still carries the old string.
    /// </para>
    /// </remarks>
    internal static async Task<List<ActionModel>> ReadSeedActionsAsync(IReadOnlyCollection<string> files)
    {
        var options = JsonOptionsHelper.CaseInsensitive;
        var validActions = new List<ActionModel>(files.Count);

        foreach (var file in files)
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

                action.Category = CategoryMigration.Normalize(action.Category);
                if (string.Equals(action.Category, CommandCategories.WindowsDebloat, StringComparison.Ordinal))
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

        return validActions;
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
        public CultureInfo[] SupportedCultures => [new("en"), new("fr"), new("es")];

        public string GetString(string key) => _localizer?[key] ?? key;

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

    /// <summary>
    /// Whether the command library is synchronised with its Git repository once the
    /// workspace is open.
    /// </summary>
    internal static bool ShouldSyncOnStartup(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.CmdLibGitSyncOnStartup
            && settings.CmdLibGitSyncEnabled
            && !string.IsNullOrWhiteSpace(settings.CmdLibGitSyncUrl);
    }

    /// <summary>
    /// Runs the full Git sync of the command library when the settings ask for it at startup.
    /// </summary>
    /// <remarks>
    /// <para>The option was offered in Settings and read by nothing. Call it after the vault
    /// unlock gate: the settings bridge first cached the settings before the gate, when a token
    /// held by the vault could not be read yet, so it is refreshed here before the sync.</para>
    /// <para>A startup sync the user did not start does not open a dialog. The outcome is logged,
    /// a failure never stops the application, and the caller reports a failure on the status bar:
    /// a sync that failed in silence left the library stale with nothing to say why.</para>
    /// </remarks>
    /// <returns>Whether a sync ran and failed. A sync not asked for, or cancelled, is not a failure.</returns>
    internal static async Task<bool> SyncOnStartupAsync(
        AppSettings settings,
        ISettingsService settingsBridge,
        IGitSyncService gitSync)
    {
        ArgumentNullException.ThrowIfNull(settingsBridge);
        ArgumentNullException.ThrowIfNull(gitSync);
        if (!ShouldSyncOnStartup(settings))
        {
            return false;
        }

        try
        {
            await settingsBridge.LoadSettingsAsync();
            GitOperationResult result = await gitSync.FullSyncAsync();
            if (result.Success)
            {
                Heimdall.Core.Logging.FileLogger.Info(
                    $"[TwinShell] Startup Git sync done: {result.ItemsImported} imported, {result.ItemsExported} exported");
                return false;
            }

            Heimdall.Core.Logging.FileLogger.Warn(
                $"[TwinShell] Startup Git sync failed ({result.ErrorCode}): {result.Message}");
            return result.ErrorCode != GitSyncErrorCode.Cancelled;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[TwinShell] Startup Git sync failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Drops command-history entries older than
    /// <see cref="AppConstants.CommandHistoryRetentionDays"/>, once per run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleanup routine has existed since the history was written and was never called,
    /// so the table grew for the life of the install. That mattered little while history
    /// stored an un-substituted pattern; it matters now that it stores what the operator
    /// actually typed.
    /// </para>
    /// <para>
    /// Startup is the right moment because it happens once, before any tab exists, and the
    /// database is already open here. A failure is logged and swallowed: a history that
    /// could not be pruned is not a reason to refuse to start.
    /// </para>
    /// </remarks>
    internal static async Task PruneCommandHistoryAsync(IServiceProvider scopedProvider)
    {
        try
        {
            await StartupStageTimer.RunAsync(
                PruneHistoryStage,
                () => PruneOlderThanRetentionAsync(scopedProvider.GetRequiredService<ICommandHistoryService>()));
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[TwinShell] Could not prune command history: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the declared retention window.
    /// </summary>
    /// <remarks>
    /// One line, in a method of its own, so a guard can read it. The window has to be
    /// passed rather than left to the service default, and those two happen to be the same
    /// number today: a test of the recorded value cannot tell them apart, so the decision
    /// is pinned where it is actually made. Inside the caller's try block the statement
    /// sits one level too deep for the source predicate to see it.
    /// </remarks>
    private static async Task PruneOlderThanRetentionAsync(ICommandHistoryService history)
    {
        await history.CleanupOldEntriesAsync(AppConstants.CommandHistoryRetentionDays);
    }
}
