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

using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.App.ViewModels.Settings;
using Heimdall.App.ViewModels.Shell;
using Heimdall.App.ViewModels.Tools;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.SessionHealth;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Core.Updates;
using Heimdall.Core.Utilities;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;
using SshKnownHostsExporter = Heimdall.Ssh.KnownHostsExporter;
using SshKnownHostsImporter = Heimdall.Ssh.KnownHostsImporter;

namespace Heimdall.App;

/// <summary>
/// Application entry point. Configures dependency injection
/// and initializes core services before showing the main window.
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>Held for the process lifetime while this instance owns the data root.</summary>
    private SingleInstanceGuard? _singleInstanceGuard;
    private MainViewModel? _mainViewModel;
    private string? _dataRoot;
    private string? _notesStoragePath;

    // Timeout for the single long-lived updater HttpClient (no magic number inline).
    private static readonly TimeSpan UpdateHttpTimeout = TimeSpan.FromSeconds(30);

    // Vault unlock-gate brute-force lockout (defense-in-depth on top of Argon2id,
    // which is the primary per-attempt rate-limiter). Mirrors the PIN gate defaults.
    private const int VaultUnlockMaxAttempts = 5;
    private static readonly TimeSpan VaultUnlockLockoutDuration = TimeSpan.FromMinutes(5);

    public IServiceProvider? Services => _serviceProvider;

    public bool IsShuttingDown { get; internal set; }

    /// <summary>How long exit waits for the session snapshot to reach disk.</summary>
    private static readonly TimeSpan ExitSnapshotSaveBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling on the service container's asynchronous disposal at exit. It was the only
    /// unbounded await on the exit path: a service whose DisposeAsync blocked hung the
    /// exit, and on the update path that is the relauncher's wait expiring.
    /// </summary>
    private static readonly TimeSpan ExitContainerDisposeBudget = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the trusted host key flush at exit.</summary>
    private static readonly TimeSpan ExitHostKeyFlushBudget = TimeSpan.FromSeconds(5);

    // WPF's startup hook is event-like. Keeping async void here lets the splash
    // stay visible while awaited initialization completes on the dispatcher.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless privilege launch mode: the app was re-launched elevated
        // via UAC to perform token-based process creation (SYSTEM / TrustedInstaller).
        // Do the work and exit immediately - no UI, no DI, no splash.
        var privExitCode = PrivilegeLauncher.HandlePrivilegeLaunchArgs(e.Args);
        if (privExitCode.HasValue)
        {
            Shutdown(privExitCode.Value);
            return;
        }

        // Show splash screen during initialization (custom window for controlled size).
        // Temporarily switch to explicit shutdown so closing the splash doesn't kill the app
        // (WPF treats the first Window shown as MainWindow when ShutdownMode is OnMainWindowClose).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Register global exception handlers BEFORE any awaits - async void
        // resumes on the dispatcher, so unhandled exceptions from awaited calls
        // must already be caught at this point.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Heimdall.Core.Logging.FileLogger.Error(
                "Unobserved task exception", args.Exception.InnerException ?? args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Error("AppDomain unhandled exception", ex);
                Dispatcher.Invoke(() => ShowUnhandledException(ex));
            }
        };

        _dataRoot = ApplicationDataPathResolver.Resolve();
        InitializeLogging(_dataRoot);
        Heimdall.Core.Logging.FileLogger.Info("Heimdall starting");

        // Before any configuration is read. Two instances sharing one data root both
        // load servers.json and both write it back, and the second write discards the
        // first one's edits without a word - ConfigManager's write lock is
        // process-local and says so itself.
        switch (SingleInstanceGuard.TryAcquire(
            _dataRoot, RequestActivationFromSecondInstance, out var instanceGuard))
        {
            case SingleInstanceOutcome.AlreadyRunning:
                Heimdall.Core.Logging.FileLogger.Info(
                    "Another Heimdall owns this configuration directory; handing over to it."
                    + $" (root={_dataRoot}, pid {Environment.ProcessId})");
                Heimdall.Core.Logging.FileLogger.Flush();
                Shutdown();
                return;

            case SingleInstanceOutcome.Owner:
                _singleInstanceGuard = instanceGuard;
                Heimdall.Core.Logging.FileLogger.Info(
                    $"[SingleInstance] owning {_dataRoot} (pid {Environment.ProcessId})");
                break;

            case SingleInstanceOutcome.Unavailable:
                // Already logged by the guard. Starting is the lesser failure.
                break;
        }

        // After the single-instance decision, deliberately: a second launch used to
        // flash the full splash before handing over to the running instance.
        var splash = CreateSplashWindow();

        LogMsTscAxRegistration();

        // Register Windows-1252 codepage for MobaXterm .ini import
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services, _dataRoot);
            _serviceProvider = services.BuildServiceProvider();

            // Initialize core services
            var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
            await configManager.InitializeAsync();

            var localization = _serviceProvider.GetRequiredService<LocalizationManager>();
            var settings = await configManager.LoadSettingsAsync();
            _notesStoragePath = ResolveNotesStoragePath(settings, _dataRoot);

            await localization.LoadAsync(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locales"),
                settings.DefaultLocale);

            // Bridge the DI LocalizationManager to the WPF binding system
            // so that {loc:Translate} markup extensions can resolve keys
            LocalizationSource.Instance.Initialize(localization);

            // Apply sleep prevention setting
            SleepPrevention.Enabled = settings.PreventSleepDuringSession;
            SleepPrevention.IntervalSeconds = settings.SleepPreventionIntervalSeconds;
            Heimdall.Sftp.RemoteFileEditor.UploadDebounceInterval =
                TimeSpan.FromMilliseconds(settings.SftpUploadDebounceMs);

            // Initialize TwinShell command library (DB + seed on first launch).
            // Awaited to ensure seed completes before tools can be opened.
            await TwinShellBootstrapper.InitializeAsync(_serviceProvider);

            // Pre-warm RDP COM/DLL chain and WinForms runtime on a background STA thread.
            // Forces loading of mstscax.dll + 22 static dependencies (~300-500ms) at startup
            // instead of on the first RDP connection.
            PreWarmRdpRuntime();

            // Remove editor working directories left by earlier sessions. They are kept
            // deliberately when the application is torn down while a save is in flight -
            // that retention preserves the user's typed text - but nothing ever removed
            // them afterwards, so each one survived forever holding a file's contents.
            // Off the startup path: tidying is never a reason to delay a launch.
            _ = Task.Run(() =>
            {
                int removed = EditorTempSweeper.Sweep(
                    EditorTempPaths.Root,
                    DateTimeOffset.UtcNow);
                if (removed > 0)
                {
                    Heimdall.Core.Logging.FileLogger.Info(
                        $"Swept {removed} stale editor working director{(removed == 1 ? "y" : "ies")}");
                }

                // The same janitor for external RDP launches: the temporary .rdp file and the
                // Credential Manager entry a launch writes are both released by a deferred task
                // that dies with the process. Before this ran only ahead of the next external
                // launch, so a stranded password waited for one.
                RdpHandler.SweepStaleArtifactsAtStartup();

                // And for update attempts: a staging directory survives every ending but
                // three, with the installer inside it, and the relauncher transcripts
                // accumulate one per attempt.
                string dataRoot = _dataRoot ?? ApplicationDataPathResolver.Resolve();
                int staging = UpdateStagingSweeper.SweepStaging(
                    ApplicationDataPathResolver.GetUpdatesDirectory(dataRoot),
                    DateTimeOffset.UtcNow);
                int logs = UpdateStagingSweeper.SweepRelaunchLogs(
                    ApplicationDataPathResolver.GetLogsDirectory(dataRoot),
                    DateTimeOffset.UtcNow);
                if (staging > 0 || logs > 0)
                {
                    Heimdall.Core.Logging.FileLogger.Info(
                        $"Swept {staging} stale update staging director{(staging == 1 ? "y" : "ies")} and {logs} relauncher transcript{(logs == 1 ? "" : "s")}");
                }
            });

            // Respect Windows "Show animations" accessibility setting (WCAG 2.1 § 2.3.3).
            // When disabled, override animation durations to zero for instant state transitions.
            if (!SystemParameters.MenuAnimation)
            {
                Resources["AnimationFast"] = new Duration(TimeSpan.Zero);
                Resources["AnimationMedium"] = new Duration(TimeSpan.Zero);
            }

            // Initialize HMAC key for credential protection
            await InitializeHmacKeyAsync(configManager, settings);

            // Make the write-downgrade guard live from the start: when a vault is
            // configured, Protect fails closed until the DEK is unlocked below.
            CredentialProtector.SetVaultEnabled(settings.VaultEnabled);

            // Load trusted SSH host keys into the TOFU store
            var hostKeyStore = _serviceProvider.GetRequiredService<HostKeyStore>();
            if (settings.TrustedHostKeysV2.Count > 0)
            {
                var entries = settings.TrustedHostKeysV2.Select(kvp =>
                {
                    ParseHostKeyEntry(kvp.Key, out var host, out var port);
                    return (host, port, (HostKeyEntry?)kvp.Value);
                });
                hostKeyStore.LoadEntriesFromConfig(entries);
            }
            else if (settings.TrustedHostKeys.Count > 0)
            {
                var entries = settings.TrustedHostKeys.Select(kvp =>
                {
                    ParseHostKeyEntry(kvp.Key, out var host, out var port);
                    return (host, port, (string?)kvp.Value);
                });
                hostKeyStore.LoadFromConfig(entries);
            }

            // Persist newly trusted host keys back to settings via transactional merge.
            // Fire-and-forget on purpose: TOFU acceptance must not block the caller path.
            // Batched: a known_hosts sync raises one event per line, and each write is a
            // full settings load, serialize, atomic write and settings-changed broadcast.
            var hostKeyTrustService = _serviceProvider.GetRequiredService<IHostKeyTrustService>();
            var hostKeyPersistence = _serviceProvider.GetRequiredService<HostKeyPersistenceCoalescer>();
            hostKeyTrustService.EntryTrusted += (key, entry) => hostKeyPersistence.Upsert(key, entry);
            hostKeyTrustService.EntryReplaced += (key, oldEntry, entry) => hostKeyPersistence.Upsert(key, entry);
            hostKeyTrustService.EntryRemoved += key => hostKeyPersistence.Remove(key);

            hostKeyStore.HostKeyEvent += (key, fingerprint, trusted) =>
            {
                if (!trusted)
                {
                    return;
                }

                if (hostKeyStore.GetAllEntries().TryGetValue(key, out var entry))
                {
                    hostKeyPersistence.Upsert(key, entry);
                }
                else
                {
                    hostKeyPersistence.UpsertFingerprint(key, fingerprint);
                }
            };

            var ftpsCertificateStore = _serviceProvider.GetRequiredService<FtpsCertificateStore>();
            ftpsCertificateStore.LoadEntriesFromConfig(settings.TrustedFtpsCertificates);
            ftpsCertificateStore.CertificateTrusted += (key, entry) =>
            {
                _ = PersistTrustedFtpsCertificateEntryAsync(configManager, key, entry);
            };

            var rdpCertificateStore = _serviceProvider.GetRequiredService<RdpCertificateTrustStore>();
            LoadTrustedRdpCertificates(rdpCertificateStore, settings);
            rdpCertificateStore.TrustChanged += (key, entries) =>
            {
                _ = PersistTrustedRdpCertificatesAsync(configManager, key, entries);
            };

            _serviceProvider.GetRequiredService<KnownHostsStartupSync>().StartIfEnabled(settings);

            // Subscribe to runtime settings changes for logging and theme updates
            configManager.SettingsChanged += OnSettingsChanged;

            // Apply the saved theme and accent before showing any window.
            var themeService = _serviceProvider.GetRequiredService<HeimdallThemeService>();
            themeService.ApplyTheme(settings.DefaultTheme);
            themeService.ApplyAccentTint(settings.AccentTint);

            // Check for legacy Heimdall installation and offer migration on first run
            await TryMigrateLegacyAsync(
                configManager,
                localization,
                _serviceProvider.GetRequiredService<IDialogService>());

            // Scan for external tools (NirSoft, Sysinternals) on a background thread.
            // Fire-and-forget: results land in ToolRegistry via Dispatcher callback.
            _ = Task.Run(() => ScanExternalTools(settings));

            // Boot the background session health monitor. It reads the latest
            // inventory from disk on every cycle so adds/removes via the server
            // dialog are picked up automatically; it also re-arms its timer when
            // the user changes the interval in Settings via SettingsChanged.
            _serviceProvider.GetRequiredService<SessionHealthMonitor>().Start(settings);

            // Close splash before showing main window
            splash.Close();

            // PIN gate: when a PIN is configured, require it before the main window is shown.
            // The lockout state is restored from (and persisted back to) settings so brute-force
            // protection survives an application restart (F1-D10-7).
            if (!string.IsNullOrEmpty(settings.PinHash) && !string.IsNullOrEmpty(settings.PinSalt))
            {
                PinManager pinManager = _serviceProvider.GetRequiredService<PinManager>();
                pinManager.RestoreLockoutState(settings.PinFailureCount, settings.PinLockoutUntilUtc);

                // Persist lockout state at the moment it changes, so a lockout reached mid-prompt
                // survives even if the process is killed without closing the dialog.
                pinManager.StateChanged += () =>
                {
                    _ = configManager.MergeSettingAsync((AppSettings persistedSettings) =>
                    {
                        persistedSettings.PinFailureCount = pinManager.FailureCount;
                        persistedSettings.PinLockoutUntilUtc = pinManager.LockoutUntilUtc;
                    });
                };

                IDialogService dialogService = _serviceProvider.GetRequiredService<IDialogService>();
                PinDialogViewModel pinViewModel =
                    new PinDialogViewModel(pinManager, localization, settings.PinHash!, settings.PinSalt!);
                await dialogService.ShowPinDialogAsync(pinViewModel);

                // Final awaited persist covers the clean-close path (verify resets; cancel keeps lockout).
                await configManager.MergeSettingAsync((AppSettings persistedSettings) =>
                {
                    persistedSettings.PinFailureCount = pinManager.FailureCount;
                    persistedSettings.PinLockoutUntilUtc = pinManager.LockoutUntilUtc;
                });

                if (!pinViewModel.IsVerified)
                {
                    Heimdall.Core.Logging.FileLogger.Info("PIN gate not satisfied; exiting.");
                    Shutdown(0);
                    return;
                }

                Heimdall.Core.Logging.FileLogger.Info("PIN gate satisfied.");
            }

            // Vault unlock gate: when a master-password vault is configured, the user
            // must enter the master password (unwrapping the DEK) before the main
            // window. Runs AFTER the PIN gate. Fail-closed: a cancel/close exits the
            // app, mirroring the PIN gate, so MainWindow is never reached locked.
            if (VaultUnlockGate.ShouldShowUnlockGate(settings))
            {
                VaultLifecycleService vaultLifecycle =
                    _serviceProvider.GetRequiredService<VaultLifecycleService>();

                PinManager vaultLockout = new PinManager(VaultUnlockMaxAttempts, VaultUnlockLockoutDuration);
                vaultLockout.RestoreLockoutState(
                    settings.VaultUnlockFailureCount, settings.VaultUnlockLockoutUntilUtc);

                // Persist lockout state as it changes, so a lockout reached mid-prompt
                // survives even if the process is killed without closing the dialog.
                vaultLockout.StateChanged += () =>
                {
                    _ = configManager.MergeSettingAsync((AppSettings persistedSettings) =>
                    {
                        persistedSettings.VaultUnlockFailureCount = vaultLockout.FailureCount;
                        persistedSettings.VaultUnlockLockoutUntilUtc = vaultLockout.LockoutUntilUtc;
                    });
                };

                IDialogService dialogService = _serviceProvider.GetRequiredService<IDialogService>();
                bool showHelloUnlock = VaultHelloUnlockOfferPolicy.ShouldOfferHelloUnlock(
                    settings,
                    DateTimeOffset.UtcNow);
                VaultUnlockDialogViewModel vaultViewModel = new VaultUnlockDialogViewModel(
                    masterPassword => vaultLifecycle.UnlockAsync(masterPassword),
                    vaultLockout,
                    localization,
                    settings.VaultMigrationState == VaultMigrationState.InProgress,
                    () => vaultLifecycle.UnlockWithHelloDetailedAsync(),
                    showHelloUnlock,
                    () => dialogService.ShowConfirmAsync(
                        localization["VaultHelloReenrollTitle"],
                        localization["VaultHelloReenrollPrompt"],
                        "warning"),
                    () => vaultLifecycle.EnrollHelloAsync());

                await dialogService.ShowVaultUnlockDialogAsync(vaultViewModel);

                // Final awaited persist covers the clean-close path.
                await configManager.MergeSettingAsync((AppSettings persistedSettings) =>
                {
                    persistedSettings.VaultUnlockFailureCount = vaultLockout.FailureCount;
                    persistedSettings.VaultUnlockLockoutUntilUtc = vaultLockout.LockoutUntilUtc;
                });

                if (!vaultViewModel.IsVerified)
                {
                    Heimdall.Core.Logging.FileLogger.Info("Vault unlock gate not satisfied; exiting.");
                    Shutdown(0);
                    return;
                }

                Heimdall.Core.Logging.FileLogger.Info("Vault unlock gate satisfied.");
            }

            // Configure the workspace lock (manual + idle auto-lock). No-op when no
            // vault is enabled. Re-applied on settings changes (idle minutes / policy).
            var workspaceLock = _serviceProvider.GetRequiredService<WorkspaceLockService>();
            workspaceLock.Configure(
                settings.VaultEnabled, settings.AutoLockIdleMinutes, settings.DisconnectOnLock);
            configManager.SettingsChanged += changed => Dispatcher.Invoke(() =>
                workspaceLock.Configure(
                    changed.VaultEnabled, changed.AutoLockIdleMinutes, changed.DisconnectOnLock));

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            _mainViewModel = mainWindow.DataContext as MainViewModel;
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Error("Startup failure", ex);

            try
            {
                splash.Close();
            }
            catch
            {
                // Best-effort splash cleanup during fatal startup failure.
            }

            ShowUnhandledException(ex);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// Creates a borderless splash window with the splash image scaled to 600x448
    /// (preserving the 2400x1792 aspect ratio). Centered on screen, topmost.
    /// </summary>
    private static Window CreateSplashWindow()
    {
        var splashPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "splash-screen.png");

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false,
            Width = 600,
            Height = 448,
            ResizeMode = ResizeMode.NoResize
        };

        if (System.IO.File.Exists(splashPath))
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(splashPath));
            window.Content = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
        }

        window.Show();
        return window;
    }

    private static void LogMsTscAxRegistration()
    {
        try
        {
            using var curVerKey = Registry.ClassesRoot.OpenSubKey(@"MsTscAx.MsTscAx\CurVer");
            var curVer = curVerKey?.GetValue(null)?.ToString();
            var resolvedProgId = string.IsNullOrWhiteSpace(curVer) ? "MsTscAx.MsTscAx" : curVer;

            using var progIdKey = Registry.ClassesRoot.OpenSubKey(resolvedProgId);
            using var clsidKey = progIdKey?.OpenSubKey("CLSID");
            var clsid = clsidKey?.GetValue(null)?.ToString();
            var comType = Type.GetTypeFromProgID("MsTscAx.MsTscAx", throwOnError: false);

            Heimdall.Core.Logging.FileLogger.Info(
                $"MsTscAx.MsTscAx registration: CurVer={curVer ?? "<missing>"} resolvedProgId={resolvedProgId} CLSID={clsid ?? "<missing>"} TypeFromProgID={comType?.FullName ?? "<null>"}");
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Info(
                $"MsTscAx.MsTscAx registration lookup threw {ex.GetType().FullName}: {ex.Message} HRESULT=0x{unchecked((uint)ex.HResult):X8}");
        }
    }

    /// <summary>
    /// Scans for external third-party tools (NirSoft, Sysinternals) and registers
    /// detected tools in the ToolRegistry so they appear in the External category.
    /// </summary>
    private void ScanExternalTools(AppSettings settings)
    {
        if (_serviceProvider is null) return;
        var providerService = _serviceProvider.GetRequiredService<ExternalToolProviderService>();
        var toolRegistry = _serviceProvider.GetRequiredService<ToolRegistry>();

        providerService.ScanAll(settings);

        if (providerService.DetectedTools.Count > 0)
        {
            toolRegistry.RegisterExternalTools(providerService.DetectedTools);
            Core.Logging.FileLogger.Info(
                $"[App] Registered {providerService.DetectedTools.Count} external tool(s)");
        }
    }

    private void ConfigureServices(IServiceCollection services, string dataRoot)
    {
        // Core services
        services.AddSingleton<IConfigManager>(_ => new ConfigManager(
            AppDomain.CurrentDomain.BaseDirectory,
            dataRoot));
        services.AddSingleton<ConfigManager>(sp =>
            (ConfigManager)sp.GetRequiredService<IConfigManager>());
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ConnectionStateMachine>();
        services.AddSingleton<ApplicationStatusMachine>();
        services.AddSingleton<HostKeyStore>();
        services.AddSingleton<IHostKeyTrustService, HostKeyTrustService>();
        services.AddSingleton<KnownHostsStartupSync>();
        services.AddSingleton(provider => new HostKeyPersistenceCoalescer(provider.GetRequiredService<IConfigManager>()));
        services.AddSingleton<TrustPromptCoordinator>();
        services.AddSingleton<IHostKeyVerifier, DialogHostKeyVerifier>();
        services.AddSingleton<FtpsCertificateStore>();
        services.AddSingleton<RdpCertificateTrustStore>();
        services.AddSingleton<IRdpCertificateProbe>(_ => new RdpCertificateProbe());
        services.AddSingleton<RdpTrustPromptSurfaceRegistry>();
        services.AddSingleton<RdpTrustQuestionCoalescer>();
        services.AddSingleton<IRdpCertificateTrustPrompt, PaneRdpCertificateTrustPrompt>();
        services.AddSingleton<RdpCertificateVerifier>();
        services.AddSingleton<IFtpsCertificateVerifier, DialogFtpsCertificateVerifier>();
        services.AddSingleton<PinManager>();

        // Vault lifecycle owns the session DEK holder, so a single instance.
        services.AddSingleton<ITpmPresenceService, WindowsTpmPresenceService>();
        services.AddSingleton<IVaultHelloService, WindowsVaultHelloService>();
        services.AddSingleton<VaultLifecycleService>();
        services.AddSingleton<WorkspaceLockService>();

        // SSH/Tunnel services
        services.AddSingleton<TunnelManager>();
        services.AddSingleton<IPlinkHostKeyProbe, DefaultPlinkHostKeyProbe>();
        services.AddSingleton<ITunnelService, TunnelService>();
        services.AddSingleton<IRecentConnectionTracker, RecentConnectionTracker>();

        // Application services
        services.AddSingleton<X11ServerManager>();
        services.AddSingleton<IX11ServerManager>(sp => sp.GetRequiredService<X11ServerManager>());
        services.AddSingleton<ExternalToolProviderService>();
        services.AddSingleton<ToolRegistry>();

        // The single place the real preset location is resolved. Neither the storage nor the
        // view model can reach it any more: their parameterless constructors are gone.
        services.AddSingleton<IPasswordPresetStorage>(
            _ => new PasswordPresetStorage(ApplicationDataPathResolver.Resolve()));
        services.AddSingleton<HeimdallThemeService>();

        // Updater services (no UI wiring yet)
        services.AddSingleton<IAppVersionProvider, AppVersionProvider>();
        services.AddSingleton(_ => new HttpClient { Timeout = UpdateHttpTimeout });
        services.AddSingleton<IGitHubReleaseClient>(sp => new GitHubReleaseClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAppVersionProvider>().Current?.ToString() ?? "unknown"));
        services.AddSingleton<IVariantDetector>(_ => new VariantDetector());
        services.AddSingleton<IUpdateService>(sp => new UpdateService(
            sp.GetRequiredService<IGitHubReleaseClient>(),
            sp.GetRequiredService<IVariantDetector>(),
            _dataRoot ?? ApplicationDataPathResolver.Resolve()));

        // The same root as the outcome store below: the relauncher writes its failure
        // record where the host says, and the store reads where it was told. Two
        // resolutions of the root were only ever equal by coincidence of production.
        services.AddSingleton<IUpdateInstallerHost>(_ => new SystemUpdateInstallerHost(
            _dataRoot ?? ApplicationDataPathResolver.Resolve()));
        services.AddSingleton<IUpdateInstaller, UpdateInstaller>();
        services.AddSingleton<IApplicationLifecycle, ApplicationLifecycle>();

        // Lives in the application's own data directory, not beside the installed
        // binaries: the installer replaces the install directory, so a record kept there
        // would not reliably survive the event it exists to describe.
        services.AddSingleton<IUpdateOutcomeStore>(_ => new UpdateOutcomeStore(
            ApplicationDataPathResolver.GetUpdatesDirectory(
                _dataRoot ?? ApplicationDataPathResolver.Resolve())));
        services.AddSingleton<IUpdateInstallFlow, UpdateInstallFlow>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<IConnectionService, ConnectionService>();
        services.AddSingleton<ConnectionService>(sp =>
            (ConnectionService)sp.GetRequiredService<IConnectionService>());
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ICredentialGuardService, CredentialGuardService>();
        services.AddSingleton<IRdpExternalClientLauncher, MstscRdpExternalClientLauncher>();
        services.AddSingleton<IProtocolHandler, RdpHandler>();
        services.AddSingleton<IProtocolHandler, SshHandler>();
        services.AddSingleton<IProtocolHandler, SftpHandler>();
        services.AddSingleton<IProtocolHandler, VncHandler>();
        services.AddSingleton<IProtocolHandler, TelnetHandler>();
        services.AddSingleton<IProtocolHandler, FtpHandler>();
        services.AddSingleton<IProtocolHandler, CitrixHandler>();
        services.AddSingleton<IProtocolHandler, LocalShellHandler>();
        services.AddSingleton<IProtocolHandler, WinRmHandler>();
        // Session transcript writer (Lot 1b). Root directory is resolved once at first
        // resolution (after settings load) from the writable base FileLogger uses; the gate that
        // auto-starts logging is applied per-connect in the terminal view.
        services.AddSingleton<ISessionLogService>(sp =>
        {
            ConfigManager configManager = sp.GetRequiredService<ConfigManager>();
            AppSettings currentSettings = configManager.CurrentSettings ?? new AppSettings();
            LocalizationManager localizer = sp.GetRequiredService<LocalizationManager>();
            ILogger<SessionLogService> logger = sp.GetRequiredService<ILogger<SessionLogService>>();

            // A relative SessionLogDirectory (default "logs/sessions") is rooted beneath the
            // same user-writable data root as the diagnostic logger.
            string root = SessionLogPathResolver.Resolve(currentSettings, dataRoot);

            return new SessionLogService(
                root,
                SessionLogOptions.CreateDefault(),
                logger,
                localizer.GetString);
        });

        // Session event log (Lot 2): single shared append-only NDJSON record of graphical-protocol
        // (RDP/VNC/Citrix) connect/disconnect events. Same writable base + root as the transcript
        // service. This sink is a dumb writer with no gate of its own; the per-connect protocol +
        // LIVE-toggle gate (SessionEventGatePolicy against ConfigManager.CurrentSettings) is applied
        // by the views, so the global toggle takes effect without a restart. The DI container
        // disposes this singleton on shutdown alongside the transcript service.
        services.AddSingleton<ISessionEventLog>(sp =>
        {
            ConfigManager configManager = sp.GetRequiredService<ConfigManager>();
            AppSettings currentSettings = configManager.CurrentSettings ?? new AppSettings();

            string root = SessionLogPathResolver.Resolve(currentSettings, dataRoot);

            return new SessionEventLog(
                root,
                AppConstants.DefaultSessionEventLogMaxBytes,
                AppConstants.SessionLogFlushIntervalMs);
        });

        // Session operations log (Lot 3): single shared append-only NDJSON record of SFTP/FTP
        // file-transfer operations (upload/download/delete/rename/mkdir). Same writable base + root as
        // the transcript and event logs. This sink is a dumb writer with no gate of its own; the
        // per-operation protocol + LIVE-toggle gate (SessionOperationGatePolicy against
        // ConfigManager.CurrentSettings) is applied at the seam, so the global toggle takes effect
        // without a restart. The DI container disposes this singleton on shutdown alongside the other
        // session logs.
        services.AddSingleton<ISessionOperationLog>(sp =>
        {
            ConfigManager configManager = sp.GetRequiredService<ConfigManager>();
            AppSettings currentSettings = configManager.CurrentSettings ?? new AppSettings();

            string root = SessionLogPathResolver.Resolve(currentSettings, dataRoot);

            return new SessionOperationLog(
                root,
                AppConstants.DefaultSessionOperationLogMaxBytes,
                AppConstants.SessionLogFlushIntervalMs);
        });
        services.AddSingleton<IEmbeddedSessionManager, EmbeddedSessionManager>();
        services.AddSingleton<EmbeddedSessionManager>(sp =>
            (EmbeddedSessionManager)sp.GetRequiredService<IEmbeddedSessionManager>());
        // Registered before the split service that consumes it. One instance for the whole app:
        // clearance obtained in a gesture must be visible to every close path that gesture reaches.
        services.AddSingleton<IPaneCloseArbiter, PaneCloseArbiter>();
        services.AddSingleton<ISplitService, SplitService>();
        services.AddSingleton<SplitService>(sp =>
            (SplitService)sp.GetRequiredService<ISplitService>());
        services.AddSingleton<ContextMenuFactory>();
        services.AddSingleton<SessionTabContextMenuFactory>();
        services.AddSingleton<ISessionWindowService, SessionWindowService>();
        services.AddSingleton<FileShareService>();
        services.AddSingleton<KeyboardShortcutService>();
        services.AddSingleton<IForegroundWatchService, ForegroundWatchService>();
        services.AddSingleton<IToolContextProvider, ToolContextProvider>();
        services.AddSingleton<CredentialProviderPresetService>();
        services.AddSingleton<Core.Security.ICredentialProviderFactory, Core.Security.CredentialProviderFactory>();
        services.AddSingleton<IWindowsHelloService, WindowsHelloService>();
        services.AddSingleton<CommandLibrarySettingsService>();
        services.AddSingleton<ExternalToolSettingsService>();
        services.AddSingleton<ExternalToolLaunchService>();
        services.AddSingleton<IHealthProbe, TcpHealthProbe>();
        services.AddSingleton<SessionHealthMonitor>();
        services.AddSingleton<NetworkScannerService>();
        services.AddSingleton<ToolsTabPopulationService>();
        services.AddSingleton<ICertificateGeneratorService, CertificateGeneratorService>();
        services.AddSingleton<IBase64ToolService, Base64ToolService>();
        services.AddSingleton<IUrlEncoderToolService, UrlEncoderToolService>();
        services.AddSingleton<ITextCaseConverterService, TextCaseConverterService>();
        services.AddSingleton<IIpConverterToolService, IpConverterToolService>();
        services.AddSingleton<IJsonFormatterToolService, JsonFormatterToolService>();
        services.AddSingleton<IArpTableReader, DefaultArpTableReader>();
        services.AddSingleton<NotesStorageService>(sp =>
            new NotesStorageService(GetNotesStoragePath()));
        services.AddSingleton<INotesStorageService>(sp =>
            sp.GetRequiredService<NotesStorageService>());
        services.AddSingleton<IRegexTesterToolService, RegexTesterToolService>();
        services.AddSingleton<ITextDiffToolService, TextDiffToolService>();
        services.AddSingleton<IUuidGeneratorToolService, UuidGeneratorToolService>();
        services.AddSingleton<IUlidGeneratorToolService, UlidGeneratorToolService>();
        services.AddSingleton<IDateTimeConverterToolService, DateTimeConverterToolService>();
        services.AddSingleton<IChmodCalculatorToolService, ChmodCalculatorToolService>();
        services.AddSingleton<IJwtParserToolService, JwtParserToolService>();
        services.AddSingleton<IHashGeneratorService, HashGeneratorService>();
        services.AddSingleton<IHmacGeneratorService, HmacGeneratorService>();
        services.AddSingleton<IOtpGeneratorService, OtpGeneratorService>();
        services.AddSingleton<ISessionSnapshotService, SessionSnapshotService>();
        services.AddSingleton<ISessionRestoreCoordinator, SessionRestoreCoordinator>();
        services.AddSingleton<IRdpImportService, RdpImportService>();
        services.AddSingleton<IProfileImportService, ProfileImportService>();
        services.AddSingleton<ICommandLibraryTransferService, CommandLibraryTransferService>();
        services.AddSingleton<IPuttySessionRegistrySource, WindowsPuttyRegistrySource>();
        services.AddTransient<OpenSshConfigImporter>();
        services.AddTransient<PuttySessionImporter>();
        services.AddTransient<KnownHostsImporter>(sp => new KnownHostsImporter(
            sp.GetRequiredService<IConfigManager>(),
            sp.GetRequiredService<IHostKeyTrustService>()));
        services.AddTransient<SshKnownHostsImporter>();
        services.AddTransient<SshKnownHostsExporter>();
        services.AddSingleton<IPostConnectSequenceRunner, PostConnectSequenceRunner>();
        services.AddSingleton<IPostConnectStepResolver, CommandLibraryStepResolver>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IRemoteClipboardService, RemoteClipboardService>();
        services.AddSingleton<IDialogService, WpfDialogService>();

        // TwinShell command library
        TwinShellBootstrapper.RegisterServices(services);

        // ViewModels
        // Transient like its owner: the palette is built per MainViewModel, and the factory holds
        // only dependencies that are themselves safe to resolve from the root.
        services.AddTransient<ICommandPaletteViewModelFactory, CommandPaletteViewModelFactory>();
        services.AddTransient<ITunnelsViewModelFactory, TunnelsViewModelFactory>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<UpdateBannerViewModel>();
        services.AddTransient<ServerListViewModel>();
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<TrustedHostKeysSettingsViewModel>();
        services.AddTransient<TrustedRdpCertificatesSettingsViewModel>();
        services.AddTransient<CommandLibraryViewModel>();
        services.AddTransient<ImportOpenSshConfigDialogViewModel>();
        services.AddTransient<ImportPuttySessionsDialogViewModel>();
        services.AddTransient<ImportKnownHostsDialogViewModel>();
        services.AddTransient<ImportKnownHostsConflictDialogViewModel>();
        services.AddTransient<TrustedHostKeyDetailsDialogViewModel>();
        services.AddTransient<NotesToolViewModel>();
        services.AddTransient<OnboardingFlowViewModel>();

        // Windows
        services.AddTransient<MainWindow>();
    }

    internal static string ResolveNotesStoragePath(AppSettings settings, string basePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        if (!string.IsNullOrWhiteSpace(settings.NotesDirectory))
        {
            return Path.IsPathRooted(settings.NotesDirectory)
                ? settings.NotesDirectory
                : Path.Combine(basePath, settings.NotesDirectory);
        }

        return Path.Combine(
            basePath,
            AppConstants.BundledConfigDirectoryName,
            AppConstants.NotesDirectoryName);
    }

    internal static void InitializeLogging(string dataRoot)
    {
        string logDirectory = ApplicationDataPathResolver.GetLogsDirectory(dataRoot);

        try
        {
            Heimdall.Core.Logging.FileLogger.Initialize(logDirectory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"File logging is unavailable: {ex.Message}");
        }

        try
        {
            Heimdall.Core.Logging.ConnectionHistory.Initialize(logDirectory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Connection history logging is unavailable: {ex.Message}");
        }
    }

    internal static async Task PersistTrustedHostKeyAsync(
        IConfigManager configManager,
        string key,
        string fingerprint)
    {
        try
        {
            await configManager.MergeHostKeyAsync(key, fingerprint);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"Failed to persist host key for {key}: {ex.Message}");
        }
    }

    internal static async Task PersistTrustedHostKeyEntryAsync(
        IConfigManager configManager,
        string key,
        HostKeyEntry entry)
    {
        try
        {
            await configManager.MergeSettingAsync(settings =>
            {
                settings.TrustedHostKeysV2[key] = entry;
                settings.TrustedHostKeys[key] = entry.Fingerprint;
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"Failed to persist host key metadata for {key}: {ex.Message}");
        }
    }

    /// <summary>Writes back the whole trusted set of one profile.</summary>
    /// <param name="configManager">Where settings are merged.</param>
    /// <param name="profileId">The profile whose set changed.</param>
    /// <param name="entries">The set as the store now holds it.</param>
    /// <remarks>
    /// Writes the SET the store handed over, never a delta: a persister that appended
    /// would keep a certificate the user just removed, and one that replaced with a single
    /// value would reproduce the Windows defect this whole feature exists to escape.
    /// <para>
    /// An empty set removes the key rather than storing an empty list, so forgetting the
    /// last certificate leaves no trace of the profile in the file.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Every durable RDP certificate approval the settings hold, both owners' scopes together, in
    /// the shape <see cref="RdpCertificateTrustStore.LoadFromConfig"/> takes.
    /// </summary>
    /// <remarks>
    /// One sequence for one load call: the store replaces its durable state wholesale on each
    /// load, so loading the two dictionaries in two calls would keep only the second.
    /// </remarks>
    internal static IEnumerable<(RdpTrustKey Key, IEnumerable<RdpCertificateEntry> Entries)>
        ReadTrustedRdpCertificates(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (KeyValuePair<string, List<RdpCertificateEntry>> pair in settings.TrustedRdpCertificates)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                yield return (RdpTrustKey.ForProfile(pair.Key), pair.Value ?? []);
            }
        }

        foreach (KeyValuePair<string, List<RdpCertificateEntry>> pair
            in settings.TrustedRdpCertificatesForTypedDestinations)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                yield return (RdpTrustKey.ForTypedDestination(pair.Key), pair.Value ?? []);
            }
        }
    }

    /// <summary>Loads both trust scopes into the store, in the one call the store requires.</summary>
    /// <remarks>
    /// A method of its own so a test can run the startup's load against a real store and real
    /// settings holding both dictionaries, and read both scopes back. The startup sequence
    /// itself runs inside a try block that no source reading in this repository can reach.
    /// </remarks>
    internal static void LoadTrustedRdpCertificates(RdpCertificateTrustStore store, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.LoadFromConfig(ReadTrustedRdpCertificates(settings));
    }

    internal static async Task PersistTrustedRdpCertificatesAsync(
        IConfigManager configManager,
        RdpTrustKey key,
        IReadOnlyCollection<RdpCertificateEntry> entries)
    {
        try
        {
            await configManager.MergeSettingAsync(settings =>
            {
                Dictionary<string, List<RdpCertificateEntry>> owners = key.Scope switch
                {
                    RdpTrustScope.TypedDestination => settings.TrustedRdpCertificatesForTypedDestinations,
                    _ => settings.TrustedRdpCertificates,
                };

                if (entries.Count == 0)
                {
                    owners.Remove(key.Identity);
                    return;
                }

                owners[key.Identity] = [.. entries];
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"Failed to persist trusted RDP certificates for {key}: {ex.Message}");
        }
    }

    internal static async Task PersistRemovedHostKeyAsync(
        IConfigManager configManager,
        string key)
    {
        try
        {
            await configManager.MergeSettingAsync(settings =>
            {
                settings.TrustedHostKeys.Remove(key);
                settings.TrustedHostKeysV2.Remove(key);
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"Failed to persist host key removal for {key}: {ex.Message}");
        }
    }

    internal static async Task PersistTrustedFtpsCertificateEntryAsync(
        IConfigManager configManager,
        string key,
        FtpsCertificateEntry entry)
    {
        try
        {
            await configManager.MergeSettingAsync(settings =>
            {
                settings.TrustedFtpsCertificates[key] = entry;
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"Failed to persist FTPS certificate metadata for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses a host key entry in the form "host:port", handling IPv6 bracket
    /// notation (e.g., "[2001:db8::1]:22") correctly.
    /// </summary>
    private static void ParseHostKeyEntry(string key, out string host, out int port)
    {
        port = 22;

        if (key.StartsWith('['))
        {
            // IPv6 bracket notation: [host]:port
            var closeBracket = key.IndexOf(']');
            if (closeBracket > 0)
            {
                host = key[1..closeBracket];
                if (closeBracket + 2 < key.Length && key[closeBracket + 1] == ':')
                {
                    int.TryParse(key[(closeBracket + 2)..], out port);
                }
                return;
            }
        }

        // Standard host:port - split on the last colon only (handles bare IPv6 without brackets)
        var lastColon = key.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(key[(lastColon + 1)..], out var parsedPort))
        {
            host = key[..lastColon];
            port = parsedPort;
        }
        else
        {
            host = key;
        }
    }

    /// <summary>
    /// Pre-warms the RDP COM control and WinForms runtime on a background STA thread.
    /// This forces mstscax.dll and its 22+ static dependencies into process memory,
    /// eliminating the 300-500ms cold-start penalty on the first actual RDP connection.
    /// </summary>
    private static void PreWarmRdpRuntime()
    {
        var thread = new System.Threading.Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Force COM activation of MsTscAx - loads mstscax.dll + all static dependencies.
                // Do NOT create WindowsFormsHost here: it is a WPF FrameworkElement and
                // initializing the WPF-WinForms bridge on a background thread corrupts
                // the interop layer for the real UI thread.
                using var host = new Heimdall.Rdp.ActiveX.RdpActiveXHost();
                _ = host.Handle;

                sw.Stop();
                Heimdall.Core.Logging.FileLogger.Info(
                    $"RDP COM pre-warm completed in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"RDP COM pre-warm failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>
    /// Ensures an HMAC key exists in settings and initializes the
    /// <see cref="CredentialProtector"/> for use across the application.
    /// Generates a new key on first run.
    /// </summary>
    private static async Task InitializeHmacKeyAsync(
        IConfigManager configManager, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.HmacKey))
        {
            // First run: generate and persist an HMAC key
            settings.HmacKey = HmacIntegrity.GenerateKey();
            settings.HmacKeyCreatedAt = DateTime.UtcNow;
            await configManager.SaveSettingsAsync(settings);
            Heimdall.Core.Logging.FileLogger.Info("HMAC key generated for credential integrity");
        }

        // Decrypt the DPAPI-protected HMAC key to raw form for CredentialProtector
        try
        {
            var rawKey = DpapiProvider.Unprotect(settings.HmacKey);
            CredentialProtector.Initialize(rawKey);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Error(
                "Failed to initialize HMAC key - credentials will use plain DPAPI", ex);
            CredentialProtector.Initialize(null);
        }
    }

    /// <summary>
    /// Detects a legacy Heimdall (PowerShell) installation by searching
    /// for the legacy app folder up the directory tree.
    /// Only prompts on first run (when servers.json does not yet contain data).
    /// </summary>
    private static async Task TryMigrateLegacyAsync(
        IConfigManager configManager,
        LocalizationManager localization,
        IDialogService dialogService)
    {
        // Only offer migration when servers.json is empty or missing (first run)
        var existingServers = await configManager.LoadServersAsync();
        if (existingServers.Count > 0)
        {
            return;
        }

        // Walk up from the base directory looking for the legacy app folder
        var searchDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string? legacyPath = null;

        while (searchDir?.Parent != null)
        {
            var candidate = Path.Combine(searchDir.Parent.FullName, AppConstants.LegacyAppFolderName);
            if (MigrationService.DetectLegacyInstallation(candidate))
            {
                legacyPath = candidate;
                break;
            }

            candidate = Path.Combine(searchDir.FullName, AppConstants.LegacyAppFolderName);
            if (MigrationService.DetectLegacyInstallation(candidate))
            {
                legacyPath = candidate;
                break;
            }

            searchDir = searchDir.Parent;
        }

        if (legacyPath is null)
        {
            return;
        }

        LegacyMigrationOffer? offer = null;
        try
        {
            offer = await LegacyMigrationDecisionPolicy.CreateOfferAsync(legacyPath);
        }
        catch (IOException ex)
        {
            Heimdall.Core.Logging.FileLogger.Error(
                "Could not fingerprint legacy migration source; decline will not be persisted.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Heimdall.Core.Logging.FileLogger.Error(
                "Could not fingerprint legacy migration source; decline will not be persisted.",
                ex);
        }

        if (offer is not null)
        {
            AppSettings settings = await configManager.LoadSettingsAsync();
            if (!LegacyMigrationDecisionPolicy.ShouldOffer(settings, offer))
            {
                return;
            }
        }

        var confirmed = await dialogService.ShowConfirmAsync(
            localization["MigrationTitle"],
            localization["MigrationDetectedPrompt"],
            "info");

        if (!confirmed)
        {
            if (offer is not null)
            {
                try
                {
                    await LegacyMigrationDecisionPolicy.RecordDeclineAsync(
                        configManager,
                        offer);
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Error(
                        "Could not persist the declined legacy migration offer.",
                        ex);
                }
            }

            return;
        }

        MigrationService migrationService = new(configManager, localization);
        MigrationResult result = await migrationService.ImportFromLegacyAsync(legacyPath);
        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localization);

        if (presentation.Kind == MigrationPresentationKind.Info)
        {
            dialogService.ShowInfo(
                localization["MigrationTitle"],
                presentation.Message);
        }
        else
        {
            dialogService.ShowWarning(
                localization["MigrationTitle"],
                presentation.Message);
        }
    }

    /// <summary>
    /// Handles runtime settings changes by updating logging state and theme.
    /// Invoked on the thread that saved the settings; theme swap is dispatched
    /// to the UI thread.
    /// </summary>
    private void OnSettingsChanged(Core.Configuration.AppSettings newSettings)
    {
        // Update logging state
        Core.Logging.FileLogger.SetEnabled(newSettings.EnableLogging);
        string dataRoot = _dataRoot ?? ApplicationDataPathResolver.Resolve();
        _notesStoragePath = ResolveNotesStoragePath(newSettings, dataRoot);

        // Delegate theme swap to the centralized service on the UI thread.
        // Idempotent: HeimdallThemeService skips the swap when the theme is unchanged.
        var themeService = _serviceProvider?.GetService<HeimdallThemeService>();
        if (themeService is not null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                themeService.ApplyTheme(newSettings.DefaultTheme);
                themeService.ApplyAccentTint(newSettings.AccentTint);
            });
        }
    }

    private string GetNotesStoragePath()
    {
        return _notesStoragePath
            ?? ResolveNotesStoragePath(
                new AppSettings(),
                _dataRoot ?? ApplicationDataPathResolver.Resolve());
    }

    /// <summary>
    /// Brings this instance forward when a later launch asks for it.
    /// </summary>
    /// <remarks>
    /// Runs on a thread-pool thread, so every touch of the window is marshalled.
    /// <see cref="Window.MainWindow" /> is read inside the dispatcher callback and
    /// not captured at registration time, because the guard is acquired before any
    /// window exists.
    /// </remarks>
    private void RequestActivationFromSecondInstance()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is not { } window)
                {
                    return;
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Show();
                window.Activate();
            });
        }
        catch (Exception ex)
        {
            // A failure to surface must not take down the instance that is working.
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[SingleInstance] could not surface the existing window: {ex.Message}");
        }
    }

    /// <summary>
    /// The dispatcher's unhandled-exception hook. Logged and flushed only while shutting
    /// down: a modal box on the way out stops the process from ending - the update
    /// relauncher's wait expires, and a logoff shows the "this app is preventing
    /// shutdown" screen.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Heimdall.Core.Logging.FileLogger.Error("Unhandled exception", args.Exception);
        if (ShutdownDecisions.ShouldShowUnhandledExceptionDialog(IsShuttingDown))
        {
            ShowUnhandledException(args.Exception);
        }
        else
        {
            Heimdall.Core.Logging.FileLogger.Flush();
        }

        args.Handled = true;
    }

    /// <summary>
    /// Windows logoff or shutdown. WPF closes every window with the cancellation ignored
    /// and then shuts down; without this the main window took its full interactive close
    /// pass - prompts during a logoff - and a refused or thrown confirmation returned
    /// before the expand state and window bounds were saved.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args)
    {
        IsShuttingDown = true;
        Heimdall.Core.Logging.FileLogger.Info("Session ending: persisting state before shutdown");
        if (MainWindow is MainWindow window)
        {
            _ = window.PersistStateForShutdownAsync();
        }
    }

    private void ShowUnhandledException(Exception exception)
    {
        // Written here rather than at the three call sites: this is the chokepoint every
        // unhandled failure passes through. Flushed immediately because the queue is
        // drained on a timer and on OnExit, neither of which is guaranteed to run when
        // the process is on its way down.
        Core.Logging.FileLogger.ErrorDetailed("Unhandled exception", exception);
        Core.Logging.FileLogger.Flush();

        // Hardcoded last-resort copy for the case where localization itself is broken.
        // English-only by design - this path runs when DI / locale loading failed.
        const string LastResortTitle = "Heimdall Error";
        const string LastResortBody = "An unexpected error occurred. A diagnostic log may be available in %LOCALAPPDATA%\\Heimdall\\logs.";

        string errorTitle = LastResortTitle;
        string errorBody = $"{LastResortBody}\n\n{exception.Message}";

        try
        {
            var loc = _serviceProvider?.GetService<LocalizationManager>();
            if (loc is not null)
            {
                errorTitle = loc["ErrorUnhandledTitle"];
                errorBody = loc.Format("ErrorUnhandledBody", exception.Message);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[App] localization lookup: {ex.Message}");
        }

        try
        {
            // Themed dialog path. Never includes the stack trace - that goes to the log only.
            var dialogService = _serviceProvider?.GetService<IDialogService>();
            if (dialogService is not null)
            {
                dialogService.ShowError(errorTitle, errorBody);
                return;
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[App] themed error dialog failed: {ex.Message}");
        }

        // Last-resort fallback: the themed path is unreachable (DI broken, dispatcher
        // shutting down, theme resources missing). This is the ONLY MessageBox.Show
        // call allowed in the codebase - see audit-UX-A and codex/ux-a1-dialog-service.
        MessageBox.Show(
            errorBody,
            errorTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Some WPF teardown paths can unload visual children after window
        // closing has completed. Keep the shutdown guard armed for those
        // late Unloaded broadcasts as well.
        IsShuttingDown = true;

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

        // Started before the first await, awaited at the end: a trust change made in the last
        // quiet window would otherwise be lost with the process.
        Task hostKeyPersistenceFlush = _serviceProvider?.GetService<HostKeyPersistenceCoalescer>()?.FlushAsync()
            ?? Task.CompletedTask;

        // Everything that releases a resource outside this process runs here, BEFORE the
        // first await: WPF clears Application.Current the moment an asynchronous OnExit
        // returns to DoShutdown, which is its first incomplete await, and the continuation
        // then runs inside an application that no longer exists. The session close was
        // moved above that line for this reason; these steps were left behind it. Each is
        // a step of this body, where the exit-path guard reads their order.
        ReleaseRdpArtifacts();
        ReleaseTunnels();
        StopScheduler();
        StopX11Server();
        ReleaseSleepPrevention();

        // The log so far, while the process is still whole. The final flush below sits
        // behind two awaits, and the tail of a shutdown is the part hardest to reproduce.
        Core.Logging.FileLogger.Flush();

        await SaveSnapshotAndCloseSessionsAsync();
        await DisposeContainerBoundedAsync();
        await ExitStep.RunBoundedAsync(
            "trusted host key flush",
            () => hostKeyPersistenceFlush,
            ExitHostKeyFlushBudget,
            Core.Logging.FileLogger.Warn);

        Core.Logging.FileLogger.Info("Heimdall shutdown complete");
        Core.Logging.FileLogger.Flush();
        base.OnExit(e);
    }

    /// <summary>
    /// Releases the .rdp files and Credential Manager entries whose deferred cleanup has
    /// not fired yet. The cleanup task is unobserved and dies with the process; without
    /// this an exit inside the window leaves the password readable until logoff.
    /// </summary>
    private void ReleaseRdpArtifacts()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        try
        {
            foreach (RdpHandler rdpHandler in
                _serviceProvider.GetServices<IProtocolHandler>().OfType<RdpHandler>())
            {
                rdpHandler.FlushPendingCleanups();
            }
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] RDP artifact cleanup: {ex.Message}"); }
    }

    /// <summary>Closes every active tunnel (Plink tunnel processes).</summary>
    private void ReleaseTunnels()
    {
        try
        {
            _serviceProvider?.GetService<TunnelManager>()?.Dispose();
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] tunnel cleanup: {ex.Message}"); }
    }

    /// <summary>Stops the scheduled task engine.</summary>
    private void StopScheduler()
    {
        try
        {
            _mainViewModel?.StopScheduler();
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] scheduler cleanup: {ex.Message}"); }
    }

    /// <summary>Stops the managed X11 server.</summary>
    private void StopX11Server()
    {
        try
        {
            _serviceProvider?.GetService<X11ServerManager>()?.Stop();
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] X11 cleanup: {ex.Message}"); }
    }

    /// <summary>Releases sleep prevention.</summary>
    private static void ReleaseSleepPrevention()
    {
        try
        {
            SleepPrevention.ForceRelease();
        }
        catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] sleep prevention cleanup: {ex.Message}"); }
    }

    /// <summary>
    /// The sessions are closed BEFORE this method first yields, and the sequence records
    /// why: WPF clears Application.Current the moment an asynchronous OnExit returns to
    /// DoShutdown, which is its first incomplete await. The snapshot save is that await.
    /// Closing after it tore every host down inside an application that no longer existed.
    /// </summary>
    private async Task SaveSnapshotAndCloseSessionsAsync()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        ISessionSnapshotService? snapshotService = _mainViewModel is null
            ? null
            : _serviceProvider.GetService<ISessionSnapshotService>();

        await ApplicationExitSequence.SaveSnapshotAndCloseSessionsAsync(
            () => _mainViewModel!.GetSessionSnapshotEntries(),
            () =>
            {
                _mainViewModel?.Connection.CloseAllSessionsSilently();

                // The closed sessions hand their RDP controls back to the pool; release
                // the pool here, on the UI thread and before the first await, so the idle
                // controls are torn down inside an application that still exists.
                _serviceProvider.GetService<EmbeddedSessionManager>()?.Dispose();
            },
            snapshotService,
            ExitSnapshotSaveBudget,
            Core.Logging.FileLogger.Warn);
    }

    /// <summary>
    /// The container must be disposed asynchronously because some registered services
    /// (e.g. FileShareService) only implement IAsyncDisposable; a sync Dispose() on it
    /// would throw. Bounded: it was the only unbounded await on the exit path.
    /// </summary>
    private async Task DisposeContainerBoundedAsync()
    {
        if (_serviceProvider is not IAsyncDisposable asyncProvider)
        {
            _serviceProvider?.Dispose();
            return;
        }

        await ExitStep.RunBoundedAsync(
            "service container disposal",
            () => asyncProvider.DisposeAsync().AsTask(),
            ExitContainerDisposeBudget,
            Core.Logging.FileLogger.Warn);
    }
}
