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
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.App.ViewModels.Settings;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.SessionHealth;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Core.Updates;
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
    private MainViewModel? _mainViewModel;
    private string? _notesStoragePath;

    // Timeout for the single long-lived updater HttpClient (no magic number inline).
    private static readonly TimeSpan UpdateHttpTimeout = TimeSpan.FromSeconds(30);

    // Vault unlock-gate brute-force lockout (defense-in-depth on top of Argon2id,
    // which is the primary per-attempt rate-limiter). Mirrors the PIN gate defaults.
    private const int VaultUnlockMaxAttempts = 5;
    private static readonly TimeSpan VaultUnlockLockoutDuration = TimeSpan.FromMinutes(5);

    public IServiceProvider? Services => _serviceProvider;

    public bool IsShuttingDown { get; internal set; }

    // WPF's startup hook is event-like. Keeping async void here lets the splash
    // stay visible while awaited initialization completes on the dispatcher.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless privilege launch mode: the app was re-launched elevated
        // via UAC to perform token-based process creation (SYSTEM / TrustedInstaller).
        // Do the work and exit immediately — no UI, no DI, no splash.
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
        var splash = CreateSplashWindow();

        // Register global exception handlers BEFORE any awaits — async void
        // resumes on the dispatcher, so unhandled exceptions from awaited calls
        // must already be caught at this point.
        DispatcherUnhandledException += (_, args) =>
        {
            Heimdall.Core.Logging.FileLogger.Error("Unhandled exception", args.Exception);
            ShowUnhandledException(args.Exception);
            args.Handled = true;
        };

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

        // Initialize file logger
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Heimdall.Core.Logging.FileLogger.Initialize(logDir);
        Heimdall.Core.Logging.ConnectionHistory.Initialize(logDir);
        Heimdall.Core.Logging.FileLogger.Info("Heimdall starting");
        LogMsTscAxRegistration();

        // Register Windows-1252 codepage for MobaXterm .ini import
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Initialize core services
            var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
            await configManager.InitializeAsync();

            var localization = _serviceProvider.GetRequiredService<LocalizationManager>();
            var settings = await configManager.LoadSettingsAsync();
            _notesStoragePath = ResolveNotesStoragePath(settings, AppDomain.CurrentDomain.BaseDirectory);

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
            var hostKeyTrustService = _serviceProvider.GetRequiredService<IHostKeyTrustService>();
            hostKeyTrustService.EntryTrusted += (key, entry) =>
            {
                _ = PersistTrustedHostKeyEntryAsync(configManager, key, entry);
            };
            hostKeyTrustService.EntryReplaced += (key, oldEntry, entry) =>
            {
                _ = PersistTrustedHostKeyEntryAsync(configManager, key, entry);
            };

            hostKeyStore.HostKeyEvent += (key, fingerprint, trusted) =>
            {
                if (!trusted)
                {
                    return;
                }

                _ = PersistTrustedHostKeyAsync(configManager, key, fingerprint);
            };

            var ftpsCertificateStore = _serviceProvider.GetRequiredService<FtpsCertificateStore>();
            ftpsCertificateStore.LoadEntriesFromConfig(settings.TrustedFtpsCertificates);
            ftpsCertificateStore.CertificateTrusted += (key, entry) =>
            {
                _ = PersistTrustedFtpsCertificateEntryAsync(configManager, key, entry);
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
                VaultUnlockDialogViewModel vaultViewModel = new VaultUnlockDialogViewModel(
                    masterPassword => vaultLifecycle.UnlockAsync(masterPassword),
                    vaultLockout,
                    localization,
                    settings.VaultMigrationState == VaultMigrationState.InProgress);

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

    private void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IConfigManager>(_ => new ConfigManager(
            AppDomain.CurrentDomain.BaseDirectory));
        services.AddSingleton<ConfigManager>(sp =>
            (ConfigManager)sp.GetRequiredService<IConfigManager>());
        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ConnectionStateMachine>();
        services.AddSingleton<ApplicationStatusMachine>();
        services.AddSingleton<HostKeyStore>();
        services.AddSingleton<IHostKeyTrustService, HostKeyTrustService>();
        services.AddSingleton<KnownHostsStartupSync>();
        services.AddSingleton<IHostKeyVerifier, DialogHostKeyVerifier>();
        services.AddSingleton<FtpsCertificateStore>();
        services.AddSingleton<IFtpsCertificateVerifier, DialogFtpsCertificateVerifier>();
        services.AddSingleton<PinManager>();

        // Vault lifecycle owns the session DEK holder, so a single instance.
        services.AddSingleton<VaultLifecycleService>();
        services.AddSingleton<WorkspaceLockService>();

        // SSH/Tunnel services
        services.AddSingleton<TunnelManager>();
        services.AddSingleton<IPlinkHostKeyProbe, DefaultPlinkHostKeyProbe>();
        services.AddSingleton<ITunnelService, TunnelService>();
        services.AddSingleton<IRecentConnectionTracker, RecentConnectionTracker>();

        // Application services
        services.AddSingleton<X11ServerManager>();
        services.AddSingleton<ExternalToolProviderService>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<HeimdallThemeService>();

        // Updater services (no UI wiring yet)
        services.AddSingleton<IAppVersionProvider, AppVersionProvider>();
        services.AddSingleton(_ => new HttpClient { Timeout = UpdateHttpTimeout });
        services.AddSingleton<IGitHubReleaseClient>(sp => new GitHubReleaseClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAppVersionProvider>().Current?.ToString() ?? "unknown"));
        services.AddSingleton<IVariantDetector>(_ => new VariantDetector());
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IUpdateInstallerHost, SystemUpdateInstallerHost>();
        services.AddSingleton<IUpdateInstaller, UpdateInstaller>();
        services.AddSingleton<IApplicationLifecycle, ApplicationLifecycle>();
        services.AddSingleton<IUpdateInstallFlow, UpdateInstallFlow>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<IConnectionService, ConnectionService>();
        services.AddSingleton<ConnectionService>(sp =>
            (ConnectionService)sp.GetRequiredService<IConnectionService>());
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
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

            // FileLogger writes to "<base>/logs"; root session logs under the same writable base so
            // a relative SessionLogDirectory (default "logs/sessions") lands beside heimdall_*.log.
            string fileLoggerLogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string sessionLogBaseDirectory = Directory.GetParent(fileLoggerLogDirectory)?.FullName
                ?? AppDomain.CurrentDomain.BaseDirectory;
            string root = SessionLogPathResolver.Resolve(currentSettings, sessionLogBaseDirectory);

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

            string fileLoggerLogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string sessionLogBaseDirectory = Directory.GetParent(fileLoggerLogDirectory)?.FullName
                ?? AppDomain.CurrentDomain.BaseDirectory;
            string root = SessionLogPathResolver.Resolve(currentSettings, sessionLogBaseDirectory);

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

            string fileLoggerLogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string sessionLogBaseDirectory = Directory.GetParent(fileLoggerLogDirectory)?.FullName
                ?? AppDomain.CurrentDomain.BaseDirectory;
            string root = SessionLogPathResolver.Resolve(currentSettings, sessionLogBaseDirectory);

            return new SessionOperationLog(
                root,
                AppConstants.DefaultSessionOperationLogMaxBytes,
                AppConstants.SessionLogFlushIntervalMs);
        });
        services.AddSingleton<IEmbeddedSessionManager, EmbeddedSessionManager>();
        services.AddSingleton<EmbeddedSessionManager>(sp =>
            (EmbeddedSessionManager)sp.GetRequiredService<IEmbeddedSessionManager>());
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
        services.AddSingleton<IDialogService, WpfDialogService>();

        // TwinShell command library
        TwinShellBootstrapper.RegisterServices(services);

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<UpdateBannerViewModel>();
        services.AddTransient<ServerListViewModel>();
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<TrustedHostKeysSettingsViewModel>();
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

        return Path.Combine(basePath, "config", "notes");
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

        // Standard host:port — split on the last colon only (handles bare IPv6 without brackets)
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
                // Force COM activation of MsTscAx — loads mstscax.dll + all static dependencies.
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
                "Failed to initialize HMAC key — credentials will use plain DPAPI", ex);
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

        var confirmed = await dialogService.ShowConfirmAsync(
            localization["MigrationTitle"],
            localization["MigrationDetectedPrompt"],
            "info");

        if (!confirmed)
        {
            return;
        }

        var migrationService = new MigrationService(configManager, localization);
        var result = await migrationService.ImportFromLegacyAsync(legacyPath);

        if (result.Success)
        {
            dialogService.ShowInfo(
                localization["MigrationTitle"],
                localization.Format("MigrationSuccess", result.ServersImported));
        }
        else
        {
            dialogService.ShowWarning(
                localization["MigrationTitle"],
                localization.Format("MigrationFailed", result.Error ?? ""));
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
        _notesStoragePath = ResolveNotesStoragePath(newSettings, AppDomain.CurrentDomain.BaseDirectory);

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
            ?? ResolveNotesStoragePath(new AppSettings(), AppDomain.CurrentDomain.BaseDirectory);
    }

    private void ShowUnhandledException(Exception exception)
    {
        // Hardcoded last-resort copy for the case where localization itself is broken.
        // English-only by design — this path runs when DI / locale loading failed.
        const string LastResortTitle = "Heimdall Error";
        const string LastResortBody = "An unexpected error occurred. Full details have been written to the log file.";

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
            // Themed dialog path. Never includes the stack trace — that goes to the log only.
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
        // call allowed in the codebase — see audit-UX-A and codex/ux-a1-dialog-service.
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

        if (_serviceProvider is not null)
        {
            try
            {
                var snapshotService = _serviceProvider.GetService<ISessionSnapshotService>();
                if (snapshotService is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _mainViewModel!.StatusText = _mainViewModel.Localize("StatusSnapshotSaving");
                    var sessions = _mainViewModel.GetSessionSnapshotEntries();
                    if (sessions.Count > 0)
                    {
                        await snapshotService.SaveAsync(sessions, cts.Token);
                    }
                    else
                    {
                        await snapshotService.ClearAsync(cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Core.Logging.FileLogger.Warn("[App] session snapshot save timed out during shutdown.");
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"[App] session snapshot save failed: {ex.Message}");
            }

            // Close all active sessions (SSH, SFTP, RDP, Local — disposes host controls + kills processes)
            try
            {
                _mainViewModel?.Connection.CloseAllSessionsSilently();
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] session cleanup: {ex.Message}"); }

            // Close all active tunnels (Plink tunnel processes)
            try
            {
                var tunnelManager = _serviceProvider.GetService<TunnelManager>();
                tunnelManager?.Dispose();
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] tunnel cleanup: {ex.Message}"); }

            // Stop scheduled task engine
            try
            {
                _mainViewModel?.StopScheduler();
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] scheduler cleanup: {ex.Message}"); }

            // Stop managed X11 server
            try
            {
                var x11Manager = _serviceProvider.GetService<X11ServerManager>();
                x11Manager?.Stop();
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] X11 cleanup: {ex.Message}"); }

            // Release sleep prevention
            try
            {
                SleepPrevention.ForceRelease();
            }
            catch (Exception ex) { Core.Logging.FileLogger.Warn($"[App] sleep prevention cleanup: {ex.Message}"); }
        }

        // ServiceProvider must be disposed asynchronously because some registered services
        // (e.g. FileShareService) only implement IAsyncDisposable. A sync Dispose() on the
        // container would throw "type only implements IAsyncDisposable. Use DisposeAsync".
        if (_serviceProvider is IAsyncDisposable asyncProvider)
        {
            await asyncProvider.DisposeAsync();
        }
        else
        {
            _serviceProvider?.Dispose();
        }

        Core.Logging.FileLogger.Info("Heimdall shutdown complete");
        Core.Logging.FileLogger.Flush();
        base.OnExit(e);
    }
}
