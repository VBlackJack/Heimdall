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

using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Heimdall.App;

/// <summary>
/// Main application window. All logic lives in <see cref="MainViewModel"/>.
/// Code-behind is limited to keyboard shortcut routing, TreeView interaction, and window lifecycle.
/// </summary>
public partial class MainWindow : Window, IContextMenuCallbacks, ISessionTabContextCallbacks
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// Centralized tool registry — injected from the ViewModel. Replaces the former
    /// static <c>ToolTypeDefinitions</c> and <c>NetworkTools</c> arrays.
    /// </summary>
    private ToolRegistry ToolRegistry => ((MainViewModel)DataContext).ToolRegistry;

    private readonly TreeInteractionState _treeState = new();
    private readonly TabInteractionState _tabState = new();
    private readonly Services.HeimdallThemeService _themeService;
    private readonly ContextMenuFactory _contextMenuFactory;
    private readonly SessionTabContextMenuFactory _sessionTabContextMenuFactory;
    private readonly ISessionWindowService _sessionWindowService;
    private readonly FileShareService _fileShareService;
    private readonly KeyboardShortcutService _keyboardShortcutService;
    private readonly IForegroundWatchService _foregroundWatchService;
    private readonly IToolContextProvider _toolContext;
    private readonly CredentialProviderPresetService _credentialProviderPresetService;
    private readonly CommandLibrarySettingsService _commandLibrarySettingsService;
    private readonly ExternalToolSettingsService _externalToolSettingsService;
    private readonly ExternalToolLaunchService _externalToolLaunchService;
    private readonly IDialogService _dialogService;
    private readonly NetworkScannerService _networkScannerService;
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly WindowUIState _uiState = new();
    private object? _lastKeyEventSource;
    private readonly ToolsTabPopulationService _toolsTabPopulation;
    private bool _closeConfirmed;
    private bool _closeInProgress;
    private bool _suppressSidebarLaunch;
    private bool _isRdpImportDragActive;
    private bool _suppressFileShareStartDialog;
    private bool _settingsRuntimeBridgeInitialized;
    private bool _suppressFileShareTftpSettingBridge;
    private OnboardingFlowViewModel? _onboardingVm;
    private bool _threadPreprocessMessageHooked;

    public FileShareService FileShareService => _fileShareService;

    private void OnTrustedHostKeysSorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
        {
            return;
        }

        e.Handled = true;
        viewModel.Settings.TrustedHostKeys.SortByCommand.Execute(e.Column.SortMemberPath);

        if (sender is System.Windows.Controls.DataGrid grid)
        {
            foreach (var column in grid.Columns)
            {
                column.SortDirection = null;
            }
        }

        e.Column.SortDirection = viewModel.Settings.TrustedHostKeys.SortAscending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
    }

    // Stored delegate references so the long-lived service/ViewModel events
    // wired in the constructor can be unsubscribed in OnClosed. Without this,
    // the captured-`this` lambdas would keep the window alive past Close().
    private System.ComponentModel.PropertyChangedEventHandler? _connectionPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _serverListPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _settingsPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _selectedExternalToolPropertyChangedHandler;
    private Action? _externalToolsChangedHandler;
    private Action<string>? _localeChangedHandler;
    private ExternalToolItemViewModel? _trackedExternalToolForPreview;

    public MainWindow(
        MainViewModel viewModel,
        Services.HeimdallThemeService themeService,
        ContextMenuFactory contextMenuFactory,
        SessionTabContextMenuFactory sessionTabContextMenuFactory,
        ISessionWindowService sessionWindowService,
        ToolsTabPopulationService toolsTabPopulation,
        FileShareService fileShareService,
        KeyboardShortcutService keyboardShortcutService,
        IForegroundWatchService foregroundWatchService,
        IToolContextProvider toolContext,
        CredentialProviderPresetService credentialProviderPresetService,
        CommandLibrarySettingsService commandLibrarySettingsService,
        ExternalToolSettingsService externalToolSettingsService,
        ExternalToolLaunchService externalToolLaunchService,
        NetworkScannerService networkScannerService,
        IDialogService dialogService,
        IAppVersionProvider appVersionProvider)
    {
        _fileShareService = fileShareService;
        _foregroundWatchService = foregroundWatchService;
        _toolContext = toolContext;
        _credentialProviderPresetService = credentialProviderPresetService;
        _commandLibrarySettingsService = commandLibrarySettingsService;
        _externalToolSettingsService = externalToolSettingsService;
        _externalToolLaunchService = externalToolLaunchService;
        _networkScannerService = networkScannerService;
        _dialogService = dialogService;
        _appVersionProvider = appVersionProvider;
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContext = viewModel;
        viewModel.CommandPalette.SetClipboardText = SetClipboardSafely;
        _themeService = themeService;
        _contextMenuFactory = contextMenuFactory;
        _sessionTabContextMenuFactory = sessionTabContextMenuFactory;
        _sessionWindowService = sessionWindowService;
        _sessionWindowService.SplitPaletteRequested += OnSplitPaletteRequested;
        _toolsTabPopulation = toolsTabPopulation;
        _keyboardShortcutService = keyboardShortcutService;
        RegisterKeyboardShortcuts();
        _foregroundWatchService.ForegroundChanged += OnForegroundChangedOutsideProcess;
        _fileShareService.SharingStarted += OnFileShareSharingStarted;
        _fileShareService.SharingStopped += OnFileShareSharingStopped;
        _fileShareService.FileServed += OnFileShareFileServed;
        _themeService.ThemeChanged += OnThemeServiceThemeChanged;
        viewModel.ToolsTab.SectionsInvalidated += OnToolsTabSectionsInvalidated;
        ApplyLocalization();
        RefreshVmDrivenLocalization(viewModel);

        TabSessions.Checked += OnSessionsTabChecked;
        TabTunnels.Checked += OnTunnelsTabChecked;
        TabScheduled.Checked += OnScheduledTabChecked;
        TabTools.Checked += OnToolsTabChecked;
        TabSettings.Checked += OnSettingsTabChecked;
        TabAbout.Checked += OnAboutTabChecked;
        _connectionPropertyChangedHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ConnectionViewModel.HasActiveSessions), StringComparison.Ordinal))
            {
                Heimdall.Core.Logging.FileLogger.Info(
                    $"Navigation session state changed: hasSessions={viewModel.Connection.HasActiveSessions}, selectedTab={viewModel.SelectedTab}");
                UpdateTabVisibility(viewModel);
            }
        };
        viewModel.Connection.PropertyChanged += _connectionPropertyChangedHandler;

        _serverListPropertyChangedHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ServerListViewModel.SelectedServer), StringComparison.Ordinal))
            {
                Dispatcher.Invoke(() =>
                {
                    _toolContext.SetSelectedServer(viewModel.ServerList.SelectedServer);
                    RefreshExternalToolSettingsUi(viewModel);
                });
            }
        };
        viewModel.ServerList.PropertyChanged += _serverListPropertyChangedHandler;
        _toolContext.SetSelectedServer(viewModel.ServerList.SelectedServer);

        _selectedExternalToolPropertyChangedHandler = (_, _) =>
        {
            Dispatcher.BeginInvoke(() => RefreshExternalToolSettingsUi(viewModel));
        };

        _settingsPropertyChangedHandler = (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedExternalTool), StringComparison.Ordinal))
            {
                AttachSelectedExternalToolPreviewTracking(viewModel.Settings.SelectedExternalTool);
                Dispatcher.BeginInvoke(() => RefreshExternalToolSettingsUi(viewModel));
            }
            else if (string.Equals(e.PropertyName, nameof(SettingsViewModel.FileShareEnableTftp), StringComparison.Ordinal)
                     && _settingsRuntimeBridgeInitialized
                     && !_suppressFileShareTftpSettingBridge)
            {
                _ = ApplyFileShareTftpSettingAsync(viewModel, viewModel.Settings.FileShareEnableTftp);
            }
        };
        viewModel.Settings.PropertyChanged += _settingsPropertyChangedHandler;
        AttachSelectedExternalToolPreviewTracking(viewModel.Settings.SelectedExternalTool);

        // Refresh Tools tab and Settings status when background scan discovers external tools
        _externalToolsChangedHandler = () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                viewModel.ToolsTab.OnExternalToolsChanged();
                viewModel.Sidebar.OnExternalToolsChanged();
                Mw_SettingsExtProvStatus.Text = _externalToolSettingsService.BuildDetectedToolsStatus();
            });
        };
        viewModel.ToolRegistry.ExternalToolsChanged += _externalToolsChangedHandler;

        // Wire split button callback from embedded views
        viewModel.EmbeddedSessionManager.SplitRequestedCallback = OnEmbeddedSplitRequested;

        Loaded += async (_, _) =>
        {
            if (viewModel.LoadCommand.CanExecute(null))
            {
                await viewModel.LoadCommand.ExecuteAsync(null);
            }

            if (viewModel.CurrentSettings is { } loadedSettings)
            {
                RestoreWindowBounds(loadedSettings);
            }
            PopulateAboutSection();

            // Restore the active sidebar tab (Servers / Tools) from persisted setting.
            // ShowToolsPanel is reused as a bool flag: true = Tools tab, false = Servers tab.
            // The OneWay binding picks up the VM state change and drives both RadioButtons.
            viewModel.Sidebar.SetActiveTab(viewModel.Settings.ShowToolsPanel);
            viewModel.Sidebar.EnablePersistence();

            // Show onboarding overlay on first launch
            if (viewModel.CurrentSettings is not null && !viewModel.CurrentSettings.OnboardingCompleted)
            {
                ShowOnboardingOverlay(viewModel.CurrentSettings);
            }

            // Reports a previous update attempt that did not apply. A sibling of the
            // startup check rather than a step inside it: that check returns early when
            // updates are disabled and again inside its throttle window, which is exactly
            // where a relaunch minutes after a failed update lands.
            try
            {
                await viewModel.Update.ReportPreviousAttemptAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"[Updates] previous attempt report: {ex.Message}");
            }

            // Throttled background update check (usually a no-op). Never blocks the UI.
            try
            {
                await viewModel.Update.CheckOnStartupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"[Updates] startup check: {ex.Message}");
            }

            _settingsRuntimeBridgeInitialized = true;
        };

        // Re-apply all localized strings when the user switches language at runtime
        _localeChangedHandler = (_) =>
        {
            Dispatcher.Invoke(() =>
            {
                ApplyLocalization();
                if (DataContext is MainViewModel vm)
                {
                    RefreshVmDrivenLocalization(vm);
                    // The update status string is set imperatively and does not re-localize live.
                    vm.Settings.UpdateStatusText = string.Empty;
                }
            });
        };
        viewModel.GetLocalizer().LocaleChanged += _localeChangedHandler;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        KeyDown += OnKeyDown;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        CommandPalettePopup.Closed += OnCommandPaletteClosed;
        Mw_FilterBox.TextChanged += OnFilterBoxTextChanged;
    }

    /// <summary>
    /// Handles split requests from embedded view header buttons by delegating
    /// to <see cref="ISessionWindowService.HandleEmbeddedSplitRequest"/>.
    /// </summary>
    private void OnEmbeddedSplitRequested(SessionTabViewModel session)
    {
        if (DataContext is not MainViewModel vm) return;
        _sessionWindowService.HandleEmbeddedSplitRequest(session, vm);
    }

    private void OnSplitPaletteRequested(object? sender, EventArgs e)
    {
        BeginFocusCommandPalette();
    }

    private static bool SetClipboardSafely(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static void RefreshVmDrivenLocalization(MainViewModel vm)
    {
        vm.ServerList.RefreshLocalizedState();
        vm.ToolsTab.RefreshHeaderText();
        vm.ToolsTab.InvalidateSections();
    }

    /// <summary>
    /// Re-applies transient localized Settings UI content that is not represented by direct XAML bindings.
    /// </summary>
    private void ApplyLocalization()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        ApplySettingsLocalization(vm);
    }

    private void ApplySettingsLocalization(MainViewModel vm)
    {
        PopulateCredentialProviderPresets();
        PopulateCredentialProviderUnlockSecret(vm);
        _ = RefreshCommandLibraryTokenStatusAsync();
        RefreshExternalToolSettingsUi(vm);
    }

    // Pushes the (decrypted) unlock secret from the view-model into the PasswordBox.
    // Suppressed so the resulting PasswordChanged event does not mark settings dirty.
    private void PopulateCredentialProviderUnlockSecret(MainViewModel vm)
    {
        _suppressCredProvUnlockChange = true;
        try
        {
            Mw_SettingsCredProvUnlock.Password = vm.Settings.CredentialProviderUnlockSecret;
        }
        finally
        {
            _suppressCredProvUnlockChange = false;
        }
    }

    private void PopulateCredentialProviderPresets()
    {
        Mw_SettingsCredProvPreset.Items.Clear();

        foreach (var label in _credentialProviderPresetService.GetPresetLabels())
        {
            Mw_SettingsCredProvPreset.Items.Add(label);
        }

        Mw_SettingsCredProvPreset.SelectedIndex = 0;
    }

    private async Task RefreshCommandLibraryTokenStatusAsync()
    {
        bool hasToken = await _commandLibrarySettingsService.HasSavedTokenAsync().ConfigureAwait(true);
        ApplyCommandLibraryTokenStatus(hasToken);
    }

    private void ApplyCommandLibraryTokenStatus(bool hasToken)
    {
        CommandLibraryTokenStatus status = _commandLibrarySettingsService.GetTokenStatus(hasToken);
        Mw_SettingsCmdLibSyncTokenStatus.Text = status.StatusText;
        Mw_SettingsCmdLibSyncTokenStatus.SetResourceReference(
            TextBlock.ForegroundProperty,
            "SuccessTextBrush");
        Mw_SettingsCmdLibSyncTokenClear.Visibility = status.ClearButtonVisibility;
    }

    private void ApplyCommandLibraryTokenSaveError()
    {
        Mw_SettingsCmdLibSyncTokenStatus.Text = _commandLibrarySettingsService.GetSaveErrorStatusText();
        Mw_SettingsCmdLibSyncTokenStatus.SetResourceReference(
            TextBlock.ForegroundProperty,
            "ErrorTextBrush");
        Mw_SettingsCmdLibSyncTokenClear.Visibility = Visibility.Collapsed;
    }

    private void RefreshExternalToolSettingsUi(MainViewModel vm)
    {
        Mw_SettingsExtProvStatus.Text = _externalToolSettingsService.BuildDetectedToolsStatus();
        PopulateExternalToolPlaceholderList();
        Mw_ExtToolPreview.Text = _externalToolSettingsService.BuildPreview(
            vm.Settings.SelectedExternalTool,
            vm.ServerList.SelectedServer);
    }

    private void AttachSelectedExternalToolPreviewTracking(ExternalToolItemViewModel? tool)
    {
        if (_trackedExternalToolForPreview is not null
            && _selectedExternalToolPropertyChangedHandler is not null)
        {
            _trackedExternalToolForPreview.PropertyChanged -= _selectedExternalToolPropertyChangedHandler;
        }

        _trackedExternalToolForPreview = tool;

        if (_trackedExternalToolForPreview is not null
            && _selectedExternalToolPropertyChangedHandler is not null)
        {
            _trackedExternalToolForPreview.PropertyChanged += _selectedExternalToolPropertyChangedHandler;
        }
    }

    private void PopulateExternalToolPlaceholderList()
    {
        Mw_ExtToolPlaceholderList.Items.Clear();

        var captionFontSize = (double)FindResource("FontSizeCaption");
        foreach (var item in _externalToolSettingsService.BuildPlaceholderItems(captionFontSize))
        {
            Mw_ExtToolPlaceholderList.Items.Add(item);
        }
    }

    private void PopulateAboutSection()
    {
        AboutVersionText.Text = string.Format(LocalizeWindowString("AboutVersion"), _appVersionProvider.InformationalVersion);
        AboutRuntimeText.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        AboutPlatformText.Text = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

        // WebView2 version
        try
        {
            AboutWebView2Text.Text = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            AboutWebView2Text.Text = "N/A";
        }

        // SSH.NET version
        var sshNetAsm = typeof(Renci.SshNet.SshClient).Assembly;
        var sshNetVersion = sshNetAsm.GetName().Version;
        AboutSshNetText.Text = sshNetVersion is not null ? sshNetVersion.ToString() : "N/A";

        // Build date from informational version (format: YYYY.MMDDxx)
        AboutBuildDateText.Text = _appVersionProvider.BuildDate is { } buildDate
            ? buildDate.ToString("yyyy-MM-dd")
            : _appVersionProvider.InformationalVersion;

        // Data section
        PopulateAboutDataSection();
    }

    private async void PopulateAboutDataSection()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            var servers = await vm.ConfigManager.LoadServersAsync();
            var settings = await vm.ConfigManager.LoadSettingsAsync();
            AboutServersText.Text = servers.Count.ToString();
            AboutGatewaysText.Text = settings.SshGateways.Count.ToString();
        }
        catch
        {
            AboutServersText.Text = "?";
            AboutGatewaysText.Text = "?";
        }

        var configPath = vm.ConfigManager.ConfigPath;
        var logPath = ApplicationDataPathResolver.GetLogsDirectory(
            ApplicationDataPathResolver.Resolve());
        AboutConfigPathText.Text = configPath;
        AboutConfigPathText.ToolTip = configPath;
        AboutLogPathText.Text = logPath;
        AboutLogPathText.ToolTip = logPath;
    }

    private void OnAboutOpenConfigClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            OpenFolderInExplorer(vm.ConfigManager.ConfigPath);
        }
    }

    private void OnAboutOpenLogsClick(object sender, RoutedEventArgs e)
    {
        var logPath = ApplicationDataPathResolver.GetLogsDirectory(
            ApplicationDataPathResolver.Resolve());
        OpenFolderInExplorer(logPath);
    }

    private void OnAboutGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/VBlackJack/Heimdall",
                UseShellExecute = true
            });
        }
        catch { /* best effort */ }
    }

    private static void OpenFolderInExplorer(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch { /* best effort */ }
    }

    private string LocalizeWindowString(string key)
    {
        if (DataContext is MainViewModel vm)
        {
            return vm.Localize(key);
        }

        return LocalizationSource.Instance[key];
    }

    private string BuildFileDialogFilter(string primaryLabelKey, string patterns)
    {
        var primaryLabel = LocalizeWindowString(primaryLabelKey);
        var allFilesLabel = LocalizeWindowString("FilterAllFiles");
        return $"{primaryLabel} ({patterns})|{patterns}|{allFilesLabel} (*.*)|*.*";
    }

    private async void OnSessionsTabChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckUnsavedSettingsAsync()) { TabSettings.IsChecked = true; return; }
            SwitchToTab(ShellTab.Sessions);
        }
        catch (Exception ex) { Core.Logging.FileLogger.Error($"Tab switch failed: {ex.Message}"); }
    }

    private async void OnTunnelsTabChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckUnsavedSettingsAsync()) { TabSettings.IsChecked = true; return; }
            SwitchToTab(ShellTab.Tunnels);
        }
        catch (Exception ex) { Core.Logging.FileLogger.Error($"Tab switch failed: {ex.Message}"); }
    }

    private async void OnScheduledTabChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckUnsavedSettingsAsync()) { TabSettings.IsChecked = true; return; }
            SwitchToTab(ShellTab.Scheduled);
        }
        catch (Exception ex) { Core.Logging.FileLogger.Error($"Tab switch failed: {ex.Message}"); }
    }

    private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
    {
        SwitchToTab(ShellTab.Settings);
    }

    private void OnNavigateToGatewaySettings(object sender, RoutedEventArgs e)
    {
        TabSettings.IsChecked = true;
        SwitchToTab(ShellTab.Settings);
        Mw_SettingsSubTabControl.SelectedItem = Mw_SettingsTabSsh;
    }

    /// <summary>
    /// Prompts the user to discard unsaved settings changes when navigating away.
    /// Returns true if navigation should proceed, false if it should be cancelled.
    /// </summary>
    private async Task<bool> CheckUnsavedSettingsAsync()
    {
        if (DataContext is not MainViewModel vm) return true;
        if (vm.SelectedTab != ShellTab.Settings) return true;
        if (!vm.Settings.IsDirty) return true;

        var discard = await vm.DialogService.ShowConfirmAsync(
            vm.Localize("SettingsUnsavedWarningTitle"),
            vm.Localize("SettingsUnsavedWarning"),
            "warning");

        if (discard)
        {
            await vm.Settings.DiscardChangesAsync();
        }

        return discard;
    }

    private async void OnAddFolderFromMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var name = await vm.DialogService.ShowInputAsync(
            vm.Localize("TreeCtxNewGroup"),
            vm.Localize("ServerFieldGroup"));

        if (!string.IsNullOrWhiteSpace(name))
        {
            var settings = await vm.ConfigManager.LoadSettingsAsync();
            var path = name.Trim();
            if (!settings.EmptyGroups.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                settings.EmptyGroups.Add(path);
                await vm.ConfigManager.SaveSettingsAsync(settings);
                var servers = await vm.ConfigManager.LoadServersAsync();
                vm.ServerList.LoadServers(servers, settings);
            }
        }
    }

    private async void OnAddToolMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || DataContext is not MainViewModel vm)
        {
            return;
        }

        await ShowAddToolPickerAsync(vm, item.Tag as string);
    }

    private async Task ShowAddToolPickerAsync(MainViewModel vm, string? group = null)
    {
        try
        {
            var dialog = new Views.Dialogs.ToolPickerDialog(
                vm.GetLocalizer(),
                ToolRegistry.All,
                group,
                vm.ServerList.SelectedServer?.RemoteServer)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || dialog.SelectedTool is null)
            {
                return;
            }

            var host = dialog.SelectedTool.IsNetworkTool
                ? dialog.SelectedHost
                : string.Empty;

            var dto = new Core.Configuration.ServerProfileDto
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = dialog.SelectedDisplayName,
                ConnectionType = dialog.SelectedTool.ToolType,
                RemoteServer = host,
                Group = string.IsNullOrWhiteSpace(group) ? null : group
            };

            List<Core.Configuration.ServerProfileDto> servers =
                await vm.ConfigManager.MutateServersAsync(inventory =>
                {
                    inventory.Add(dto);
                    return inventory;
                });

            var settings = await vm.ConfigManager.LoadSettingsAsync();
            vm.ServerList.LoadServers(servers, settings);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"Add tool failed: {ex.Message}");
        }
    }

    private async void OnToolsTabChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckUnsavedSettingsAsync()) { TabSettings.IsChecked = true; return; }
            SwitchToTab(ShellTab.Tools);
            if (DataContext is MainViewModel vm)
            {
                vm.ToolsTab.RefreshHeaderText();
                vm.ToolsTab.InvalidateSections();
            }
        }
        catch (Exception ex) { Core.Logging.FileLogger.Error($"Tab switch failed: {ex.Message}"); }
    }

    private void OnAboutTabChecked(object sender, RoutedEventArgs e)
    {
        SwitchToTab(ShellTab.About);
    }

    private void SwitchToTab(string tabName)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Close the Command Palette when switching tabs
        if (vm.CommandPalette.IsOpen)
        {
            vm.CommandPalette.CloseCommand.Execute(null);
        }

        // Save TreeView scroll position when leaving Sessions tab
        if (vm.IsSessionsTabSelected && !string.Equals(tabName, ShellTab.Sessions, StringComparison.Ordinal))
        {
            SaveTreeViewScrollPosition();
        }

        Heimdall.Core.Logging.FileLogger.Info(
            $"Navigation request: tab={tabName}, current={vm.SelectedTab}, hasSessions={vm.Connection.HasActiveSessions}");

        vm.SelectedTab = tabName;
        UpdateTabVisibility(vm);

        // Restore TreeView scroll position when returning to Sessions tab
        if (string.Equals(tabName, ShellTab.Sessions, StringComparison.Ordinal))
        {
            RestoreTreeViewScrollPosition();
        }

        Heimdall.Core.Logging.FileLogger.Info(
            $"Navigation applied: tab={vm.SelectedTab}, sessionsVisible={vm.IsSessionsTabSelected}, tunnelsVisible={vm.IsTunnelsTabSelected}, scheduledVisible={vm.IsScheduledTabSelected}, settingsVisible={vm.IsSettingsTabSelected}");
    }

    private void OnAddButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void OnSidebarOverflowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void OnImportRdpClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = BuildFileDialogFilter("ExportFormatRdp", "*.rdp"),
            DefaultExt = ".rdp",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Title = LocalizeWindowString("DialogImportRdpTitle")
        };

        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        await vm.ServerList.ImportRdpFilesAsync(dialog.FileNames);
    }

    private async void OnImportOpenSshConfigClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDirectory = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : System.IO.Path.Combine(userProfile, ".ssh");

        var dialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Title = LocalizeWindowString("DialogTitleImportOpenSshConfig"),
            FileName = "config"
        };

        if (!string.IsNullOrWhiteSpace(sshDirectory) && Directory.Exists(sshDirectory))
        {
            dialog.InitialDirectory = sshDirectory;
        }

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        await vm.ServerList.ImportOpenSshConfigAsync(dialog.FileName);
    }

    private async void OnImportPuttySessionsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.ServerList.ImportPuttySessionsAsync();
    }

    private void OnScheduledTaskDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && vm.Scheduled.SelectedTask is not null)
        {
            vm.Scheduled.EditTaskCommand.Execute(null);
        }
    }

    private void OnFilterClearClick(object sender, RoutedEventArgs e)
    {
        Mw_FilterBox.Text = string.Empty;
        Mw_FilterBox.Focus();
    }

    private void OnFilterBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(Mw_FilterBox.Text);
        Mw_FilterClearBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWindowPreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("HeimdallServer"))
        {
            return;
        }

        var files = TryGetRdpFiles(e.Data);
        if (files.Length == 0)
        {
            SetRdpImportOverlayVisible(false);
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        SetRdpImportOverlayVisible(true);
    }

    private void OnWindowPreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("HeimdallServer"))
        {
            return;
        }

        SetRdpImportOverlayVisible(false);
    }

    private async void OnWindowPreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("HeimdallServer"))
        {
            return;
        }

        var files = TryGetRdpFiles(e.Data);
        SetRdpImportOverlayVisible(false);

        if (files.Length == 0 || DataContext is not MainViewModel vm)
        {
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        await vm.ServerList.ImportRdpFilesAsync(files);
    }

    private static string[] TryGetRdpFiles(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            return [];
        }

        return files
            .Where(path => string.Equals(System.IO.Path.GetExtension(path), ".rdp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void SetRdpImportOverlayVisible(bool isVisible)
    {
        if (_isRdpImportDragActive == isVisible)
        {
            return;
        }

        _isRdpImportDragActive = isVisible;
        RdpImportDropOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record SettingsSearchEntry(FrameworkElement Target, TabItem TopTab, TabItem? SubTab);

    private List<SettingsSearchEntry>? _settingsSearchIndex;

    private List<SettingsSearchEntry> _settingsSearchMatches = [];

    private int _settingsSearchMatchIndex = -1;

    private FrameworkElement? _settingsSearchHighlightElement;

    private Brush? _settingsSearchHighlightOriginalBackground;

    private DispatcherTimer? _settingsSearchHighlightTimer;

    private void OnSettingsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = Mw_SettingsSearchBox.Text?.Trim() ?? string.Empty;
        bool hasText = !string.IsNullOrEmpty(query);
        Mw_SettingsSearchClear.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        _settingsSearchMatchIndex = -1;

        if (!hasText)
        {
            _settingsSearchMatches = [];
            Mw_SettingsSearchHint.Visibility = Visibility.Collapsed;
            return;
        }

        _settingsSearchMatches = GetSettingsSearchIndex()
            .Where((SettingsSearchEntry entry) =>
                GetSettingsSearchEntryText(entry).IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0)
            .ToList();

        if (DataContext is MainViewModel vm)
        {
            Mw_SettingsSearchHintText.Text = _settingsSearchMatches.Count == 0
                ? vm.Localize("SettingsSearchNoResults")
                : string.Format(vm.Localize("SettingsSearchResultCount"), _settingsSearchMatches.Count);
            Mw_SettingsSearchHint.Visibility = Visibility.Visible;
        }
    }

    private void OnSettingsSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _settingsSearchMatches.Count == 0)
        {
            return;
        }

        _settingsSearchMatchIndex = (_settingsSearchMatchIndex + 1) % _settingsSearchMatches.Count;
        SettingsSearchEntry entry = _settingsSearchMatches[_settingsSearchMatchIndex];
        Mw_SettingsSubTabControl.SelectedItem = entry.TopTab;
        if (entry.SubTab is not null)
        {
            entry.SubTab.IsSelected = true;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                entry.TopTab.UpdateLayout();
                entry.SubTab?.UpdateLayout();
                entry.Target.UpdateLayout();
                Rect targetArea = new(
                    0,
                    0,
                    Math.Max(entry.Target.ActualWidth, 1),
                    Math.Max(entry.Target.ActualHeight, 1) + 72);
                entry.Target.BringIntoView(targetArea);
                HighlightSettingsSearchTarget(entry.Target);
            }));

        e.Handled = true;
    }

    private List<SettingsSearchEntry> GetSettingsSearchIndex()
    {
        if (_settingsSearchIndex is not null)
        {
            return _settingsSearchIndex;
        }

        List<SettingsSearchEntry> entries = [];
        TabItem[] topTabs =
        [
            Mw_SettingsTabGeneral,
            Mw_SettingsTabTerminal,
            Mw_SettingsTabSsh,
            Mw_SettingsTabRdp,
            Mw_SettingsTabSecurity,
            Mw_SettingsTabAdvanced,
        ];

        foreach (TabItem topTab in topTabs)
        {
            if (topTab.Content is DependencyObject content)
            {
                AddSettingsSearchEntries(content, topTab, null, entries);
            }
        }

        _settingsSearchIndex = entries;
        return _settingsSearchIndex;
    }

    private static void AddSettingsSearchEntries(
        DependencyObject node,
        TabItem topTab,
        TabItem? subTab,
        List<SettingsSearchEntry> entries)
    {
        if (node is System.Windows.Controls.TabControl tabControl)
        {
            foreach (object item in tabControl.Items)
            {
                if (item is TabItem tabItem && tabItem.Content is DependencyObject tabContent)
                {
                    AddSettingsSearchEntries(tabContent, topTab, tabItem, entries);
                }
            }

            return;
        }

        if (node is TextBlock or System.Windows.Controls.CheckBox)
        {
            entries.Add(new SettingsSearchEntry((FrameworkElement)node, topTab, subTab));
            return;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyObject)
            {
                AddSettingsSearchEntries(dependencyObject, topTab, subTab, entries);
            }
        }
    }

    private static string GetSettingsSearchEntryText(SettingsSearchEntry entry)
    {
        if (entry.Target is TextBlock textBlock)
        {
            return textBlock.Text ?? string.Empty;
        }

        if (entry.Target is System.Windows.Controls.CheckBox { Content: string text })
        {
            return text;
        }

        return string.Empty;
    }

    private void HighlightSettingsSearchTarget(FrameworkElement target)
    {
        ClearSettingsSearchHighlight();

        if (TryFindResource("AccentBrush") is not Brush accentBrush)
        {
            return;
        }

        FrameworkElement highlightElement = GetSettingsSearchHighlightElement(target);
        Brush highlightBrush = accentBrush.CloneCurrentValue();
        highlightBrush.Opacity = 0.55;

        _settingsSearchHighlightElement = highlightElement;
        _settingsSearchHighlightOriginalBackground = GetSettingsSearchBackground(highlightElement);
        SetSettingsSearchBackground(highlightElement, highlightBrush);

        DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        timer.Tick += OnSettingsSearchHighlightTimerTick;
        _settingsSearchHighlightTimer = timer;
        timer.Start();
    }

    private void OnSettingsSearchHighlightTimerTick(object? sender, EventArgs e)
    {
        ClearSettingsSearchHighlight();
    }

    private void ClearSettingsSearchHighlight()
    {
        if (_settingsSearchHighlightTimer is not null)
        {
            _settingsSearchHighlightTimer.Stop();
            _settingsSearchHighlightTimer.Tick -= OnSettingsSearchHighlightTimerTick;
            _settingsSearchHighlightTimer = null;
        }

        if (_settingsSearchHighlightElement is not null)
        {
            SetSettingsSearchBackground(_settingsSearchHighlightElement, _settingsSearchHighlightOriginalBackground);
            _settingsSearchHighlightElement = null;
            _settingsSearchHighlightOriginalBackground = null;
        }
    }

    private static FrameworkElement GetSettingsSearchHighlightElement(FrameworkElement target)
    {
        if (target is System.Windows.Controls.CheckBox)
        {
            System.Windows.Controls.Panel? panel = FindAncestor<System.Windows.Controls.Panel>(target);
            if (panel is not null)
            {
                return panel;
            }

            Border? border = FindAncestor<Border>(target);
            if (border is not null)
            {
                return border;
            }
        }

        return target;
    }

    private static Brush? GetSettingsSearchBackground(FrameworkElement element)
    {
        if (element is TextBlock textBlock)
        {
            return textBlock.Background;
        }

        if (element is System.Windows.Controls.Panel panel)
        {
            return panel.Background;
        }

        if (element is Border border)
        {
            return border.Background;
        }

        if (element is System.Windows.Controls.Control control)
        {
            return control.Background;
        }

        return null;
    }

    private static void SetSettingsSearchBackground(FrameworkElement element, Brush? background)
    {
        if (element is TextBlock textBlock)
        {
            textBlock.Background = background;
            return;
        }

        if (element is System.Windows.Controls.Panel panel)
        {
            panel.Background = background;
            return;
        }

        if (element is Border border)
        {
            border.Background = background;
            return;
        }

        if (element is System.Windows.Controls.Control control)
        {
            control.Background = background;
        }
    }

    private void OnSettingsSearchClearClick(object sender, RoutedEventArgs e)
    {
        Mw_SettingsSearchBox.Text = string.Empty;
        Mw_SettingsSearchBox.Focus();
    }

    /// <summary>
    /// Returns true when the keyboard focus is inside embedded content (terminal
    /// WebView2 or WebView2-based tool view), meaning single-modifier shortcuts
    /// should be forwarded to the content instead of being intercepted by the shell.
    /// Includes a fallback for tool sessions where WebView2 HWND focus tracking
    /// is unreliable (draw.io iframe, etc.).
    /// </summary>
    private bool IsEmbeddedContentFocused()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;

        // Direct WebView2 focus detection (works when WPF properly tracks HWND focus)
        if (focused is Microsoft.Web.WebView2.Wpf.WebView2
            || FindAncestor<Views.EmbeddedSshView>(focused) is not null
            || FindAncestor<Views.EmbeddedVncView>(focused) is not null)
        {
            return true;
        }

        return false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        _lastKeyEventSource = e.OriginalSource;
        if (_keyboardShortcutService.TryHandle(e))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Wires every Heimdall keyboard shortcut into the
    /// <see cref="KeyboardShortcutService"/>. Called once from the
    /// constructor after the service is resolved from DI.
    /// </summary>
    private void RegisterKeyboardShortcuts()
    {
        // ── Server list ──────────────────────────────────────────────
        // Ctrl+N: add server (always — never gated by terminal focus, matches legacy)
        _keyboardShortcutService.Register(Key.N, ModifierKeys.Control, () =>
        {
            if (GetMainVm() is { } vm)
                TryExecute(vm.ServerList.AddServerCommand);
        });

        // Delete: delete selected server (single-key, modifier-laxist, terminal-gated)
        _keyboardShortcutService.Register(Key.Delete, ModifierKeys.None, () =>
        {
            if (GetMainVm() is { } vm && vm.ServerList.SelectedServer is { } selected)
                TryExecute(vm.ServerList.DeleteServerCommand, selected);
        }, canExecute: () => !IsTerminalFocusedContext());

        // Ctrl+E: edit selected server (terminal-gated)
        _keyboardShortcutService.Register(Key.E, ModifierKeys.Control, () =>
        {
            if (GetMainVm() is { } vm && vm.ServerList.SelectedServer is { } selected)
                TryExecute(vm.ServerList.EditServerCommand, selected);
        }, canExecute: () => !IsTerminalFocusedContext());

        // ── Sidebar / filter (terminal-gated) / palette ──────────────
        // Ctrl+F: focus filter box
        _keyboardShortcutService.Register(Key.F, ModifierKeys.Control, () =>
        {
            Mw_FilterBox.Focus();
            Mw_FilterBox.SelectAll();
        }, canExecute: () => !IsTerminalFocusedContext());

        // Ctrl+B: toggle sidebar
        _keyboardShortcutService.Register(Key.B, ModifierKeys.Control,
            ToggleSidebar,
            canExecute: () => !IsTerminalFocusedContext());

        // Ctrl+K: open command palette (never terminal-gated; quick access must work
        // from inside a session too, so the palette intentionally wins over the
        // terminal's own Ctrl+K binding).
        _keyboardShortcutService.Register(
            Key.K,
            ModifierKeys.Control,
            OpenCommandPalette);

        // Ctrl+L (lock workspace) is intentionally NOT registered here: it is handled by
        // the system-wide low-level keyboard hook (OnLowLevelKeyboardHookKeyDown) so it
        // wins even while an out-of-process WebView2 terminal or an ActiveX RDP surface
        // owns focus (the bubbling KeyDown path used by this service is swallowed by those
        // surfaces, and a shell binds Ctrl+L to clear-screen). See MainWindow.WindowUI.cs.

        // Ctrl+Alt+B: toggle broadcast mode. Deliberately not terminal-gated so
        // it works while typing inside a session terminal; Ctrl+B alone already
        // toggles the sidebar, so the Alt modifier avoids that conflict.
        _keyboardShortcutService.Register(Key.B, ModifierKeys.Control | ModifierKeys.Alt, () =>
        {
            if (GetMainVm() is { } vm)
                vm.Session.ToggleBroadcastCommand.Execute(null);
        });

        // ── Ctrl+Shift combos (NOT terminal-gated) ───────────────────
        // Ctrl+Shift+S: screenshot active session
        _keyboardShortcutService.Register(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is { } vm)
                CaptureActiveSessionScreenshot(vm);
        });

        // Ctrl+Shift+N: launch network scanner
        _keyboardShortcutService.Register(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is { } vm)
                _ = LaunchNetworkScannerAsync(vm);
        });

        // Ctrl+Shift+T: toggle sidebar tab (Sessions ↔ Tools)
        _keyboardShortcutService.Register(Key.T, ModifierKeys.Control | ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is { } vm)
                vm.Sidebar.ToggleTabCommand.Execute(null);
        });

        // Ctrl+Shift+O: toggle split orientation on the active split session
        _keyboardShortcutService.Register(Key.O, ModifierKeys.Control | ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is { Connection.ActiveSession: { IsSplit: true } splitSession } vm)
                vm.ToggleSplitOrientation(splitSession);
        });

        // ── Session lifecycle ────────────────────────────────────────
        // Ctrl+W: close active session (terminal-gated)
        _keyboardShortcutService.Register(Key.W, ModifierKeys.Control, () =>
        {
            if (GetMainVm() is { Connection.ActiveSession: { } active } vm)
                TryExecute(vm.Connection.CloseSessionCommand, active);
        }, canExecute: () => !IsTerminalFocusedContext());

        // Ctrl+Tab: cycle to next session
        _keyboardShortcutService.Register(Key.Tab, ModifierKeys.Control, () =>
        {
            if (GetMainVm() is not { } vm) return;
            var sessions = vm.Connection.ActiveSessions;
            if (sessions.Count > 1 && vm.Connection.ActiveSession is not null)
            {
                var idx = sessions.IndexOf(vm.Connection.ActiveSession);
                vm.Connection.ActiveSession = sessions[(idx + 1) % sessions.Count];
            }
        });

        // Ctrl+Shift+Tab: cycle to previous session
        _keyboardShortcutService.Register(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is not { } vm) return;
            var sessions = vm.Connection.ActiveSessions;
            if (sessions.Count > 1 && vm.Connection.ActiveSession is not null)
            {
                var idx = sessions.IndexOf(vm.Connection.ActiveSession);
                vm.Connection.ActiveSession = sessions[(idx - 1 + sessions.Count) % sessions.Count];
            }
        });

        // ── Modifier-laxist function keys (legacy: fire regardless of modifiers) ──
        // F1: show keyboard shortcut help
        _keyboardShortcutService.Register(Key.F1, ModifierKeys.None, ShowKeyboardShortcutHelp);

        // F11: toggle fullscreen. Keep this strict even though the shortcut
        // service treats None as laxist for legacy bindings; modified F11
        // belongs to the focused app/session.
        _keyboardShortcutService.Register(
            Key.F11,
            ModifierKeys.None,
            ToggleFullscreen,
            canExecute: () => Keyboard.Modifiers == ModifierKeys.None);

        // Escape: exit fullscreen (only when fullscreen)
        _keyboardShortcutService.Register(Key.Escape, ModifierKeys.None,
            ToggleFullscreen,
            canExecute: () => _uiState.IsFullscreen && Keyboard.Modifiers == ModifierKeys.None);

        // ── TreeView context menu ────────────────────────────────────
        // Apps key: open context menu (terminal-gated, modifier-laxist)
        _keyboardShortcutService.Register(Key.Apps, ModifierKeys.None, () =>
        {
            if (GetMainVm() is { } vm)
                OpenTreeViewKeyboardContextMenu(vm);
        }, canExecute: () => !IsTerminalFocusedContext());

        // Shift+F10: open context menu (strict modifier, terminal-gated)
        _keyboardShortcutService.Register(Key.F10, ModifierKeys.Shift, () =>
        {
            if (GetMainVm() is { } vm)
                OpenTreeViewKeyboardContextMenu(vm);
        }, canExecute: () => !IsTerminalFocusedContext());
    }

    private MainViewModel? GetMainVm() => DataContext as MainViewModel;

    // ── Workspace lock overlay ───────────────────────────────────────────────

    private void OnLockOverlayUnlockClick(object sender, RoutedEventArgs e) => SubmitLockOverlay();

    private void OnLockOverlayPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitLockOverlay();
            e.Handled = true;
        }
    }

    private void OnLockOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is System.Windows.Controls.PasswordBox box)
        {
            box.Focus();
        }
    }

    private void SubmitLockOverlay()
    {
        if (GetMainVm()?.LockOverlayViewModel is not { } vm)
        {
            return;
        }

        // Read via SecurePassword and marshal to a pinned char[]; the VM zeroes it.
        using var secure = Mw_LockOverlayPasswordBox.SecurePassword;
        var password = Services.SecurePasswordHelper.ToChars(secure);
        Mw_LockOverlayPasswordBox.Clear();
        vm.UnlockCommand.Execute(password);
    }

    private void OpenCommandPalette()
    {
        if (GetMainVm() is not { } vm)
        {
            return;
        }

        vm.CommandPalette.OpenCommand.Execute(null);
        BeginFocusCommandPalette();
    }

    private bool IsTerminalFocusedContext()
        => KeyboardShortcutService.IsTerminalFocused(_lastKeyEventSource ?? new object(), IsEmbeddedContentFocused);

    private static void TryExecute(System.Windows.Input.ICommand? command, object? parameter = null)
    {
        if (command is not null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    private bool _shortcutHelpDialogOpen;
    private bool _networkScannerRunning;

    private void ShowKeyboardShortcutHelp()
    {
        if (DataContext is not MainViewModel vm) return;

        // Reentrancy guard: ShowInfo is modal and runs a nested message loop.
        // F1 KeyDown messages already queued (fast taps, auto-repeat) are still
        // dispatched to this window by that loop, re-entering this handler and
        // stacking identical help dialogs.
        if (_shortcutHelpDialogOpen) return;

        _shortcutHelpDialogOpen = true;
        try
        {
            _dialogService.ShowInfo(
                vm.Localize("HelpShortcutsTitle"),
                vm.Localize("HelpShortcutsContent"));
        }
        finally
        {
            _shortcutHelpDialogOpen = false;
        }
    }

    /// <summary>
    /// Captures the active session tab content as an image and copies it to the clipboard.
    /// </summary>
    private void CaptureActiveSessionScreenshot(MainViewModel vm)
    {
        try
        {
            var session = vm.Connection.ActiveSession;
            if (session?.HostControl is not UIElement element)
            {
                return;
            }

            var bounds = new System.Windows.Size(element.RenderSize.Width, element.RenderSize.Height);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap(
                (int)(bounds.Width * dpi.DpiScaleX),
                (int)(bounds.Height * dpi.DpiScaleY),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);

            bitmap.Render(element);
            Clipboard.SetImage(bitmap);
            vm.StatusText = vm.Localize("ScreenshotCaptured");
        }
        catch
        {
            vm.StatusText = vm.Localize("ScreenshotFailed");
        }
    }

    private async Task LaunchNetworkScannerAsync(MainViewModel vm)
    {
        if (_networkScannerRunning) return;

        _networkScannerRunning = true;
        try
        {
            var progress = new Progress<(int Done, int Total, string Cidr)>(p =>
                vm.StatusText = string.Format(
                    vm.Localize("NetworkScannerScanning"),
                    p.Cidr,
                    p.Done,
                    p.Total));

            var result = await _networkScannerService.ScanAndPromptAsync(vm.Localize, progress);

            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                vm.StatusText = result.StatusMessage;
            }

            if (result.AddedToInventory)
            {
                await vm.ReloadConfigurationAsync(await vm.ConfigManager.LoadSettingsAsync());
            }
        }
        finally
        {
            _networkScannerRunning = false;
        }
    }

    // ── Sidebar Tab Selector (Servers / Tools) ──────────────────────────

    /// <summary>
    /// Switches the sidebar to the Tools tab. Wired from the empty-state
    /// "Explore tools" link button.
    /// </summary>
    private void OnEmptyExploreToolsClick(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.Sidebar.SetActiveTab(isTools: true);
    }

    // ── Sidebar Tools tab — view-layer glue (state lives in SidebarViewModel) ──

    private void OnSidebarTabToolsChecked(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.Sidebar.SetActiveTab(isTools: true);
    }

    private void OnSidebarTabSessionsChecked(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.Sidebar.SetActiveTab(isTools: false);
    }

    private void OnSidebarToolsRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var treeViewItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem?.DataContext is not ViewModels.SidebarToolItemViewModel toolItem
            || DataContext is not MainViewModel vm)
        {
            _suppressSidebarLaunch = false;
            return;
        }

        _suppressSidebarLaunch = true;
        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
        e.Handled = true;

        var menuItem = new MenuItem
        {
            Tag = toolItem.Id,
            Header = vm.Localize(
                vm.FavoriteToolIds.Contains(toolItem.Id, StringComparer.OrdinalIgnoreCase)
                    ? "TreeCtxRemoveFavorite"
                    : "TreeCtxAddFavorite")
        };
        AutomationProperties.SetName(
            menuItem,
            vm.Localize(
                vm.FavoriteToolIds.Contains(toolItem.Id, StringComparer.OrdinalIgnoreCase)
                    ? "A11yUnpinTool"
                    : "A11yPinTool"));
        menuItem.Click += OnSidebarToolContextMenuClick;

        var contextMenu = new ContextMenu
        {
            PlacementTarget = treeViewItem,
            Placement = PlacementMode.MousePoint
        };
        contextMenu.Closed += (_, _) => _suppressSidebarLaunch = false;
        contextMenu.Items.Add(menuItem);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => contextMenu.IsOpen = true));
    }

    private async void OnSidebarToolsSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSidebarLaunch)
        {
            _suppressSidebarLaunch = false;
            return;
        }

        if (e.NewValue is not ViewModels.SidebarToolItemViewModel item) return;
        if (DataContext is not MainViewModel vm) return;

        // Match the full-page Tools tab: make sure the session panel is visible
        // before opening the tool tab. Tab-strip navigation stays in the view.
        TabSessions.IsChecked = true;
        SwitchToTab(ShellTab.Sessions);

        await vm.Sidebar.LaunchToolAsync(item);
    }

    private async void OnSidebarToolContextMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem
            || menuItem.Tag is not string toolId
            || DataContext is not MainViewModel vm)
        {
            return;
        }

        e.Handled = true;
        _suppressSidebarLaunch = false;
        await vm.ToggleFavoriteToolAsync(toolId);
    }

    // ── Tools Tab (dedicated full-page browser) ──────────────────────
    // State, search, filter and context labels live in ToolsTabViewModel.
    // The view keeps only: (a) the SectionsInvalidated handler that re-runs
    // the imperative ToolsTabPopulationService card rendering, (b) the two
    // card callbacks that are passed into that service, and (c) tab-strip
    // navigation on card click (which stays in the view layer).

    private void OnToolsTabSectionsInvalidated(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var visibleCount = _toolsTabPopulation.RefreshToolsTabSections(
            ToolsTabContent,
            vm,
            vm.ToolsTab.SearchText,
            OnToolsTabCardClickInternal,
            OnToolPinClickInternal);
        vm.ToolsTab.UpdateVisibleCount(visibleCount);
    }

    private void OnToolPinClickInternal(string toolId)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToolsTab.ToggleFavoriteCommand.Execute(toolId);
        }
    }

    private void OnToolsTabCardClickInternal(Core.Models.ToolDescriptor descriptor)
    {
        if (DataContext is not MainViewModel vm) return;
        TabSessions.IsChecked = true;
        SwitchToTab(ShellTab.Sessions);
        vm.ToolsTab.LaunchToolCommand.Execute(descriptor);
    }

    // ── Onboarding overlay ───────────────────────────────────────────

    private void ShowOnboardingOverlay(Heimdall.Core.Configuration.AppSettings settings)
    {
        var services = ((App)Application.Current).Services
            ?? throw new InvalidOperationException("Application service provider is not initialized.");

        _onboardingVm = services.GetRequiredService<OnboardingFlowViewModel>();
        _onboardingVm.Attach(settings);
        _onboardingVm.StepCompleted += OnOnboardingStepCompleted;
        _onboardingVm.Completed += OnOnboardingCompleted;
        OnboardingOverlay.DataContext = _onboardingVm;
        _onboardingVm.Start();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => OnboardingNextBtn.Focus());
    }

    private void OnOnboardingStepCompleted(object? sender, int stepIndex)
    {
        switch (stepIndex)
        {
            case 0: // Step 1 done → navigate to Sessions tab
                TabSessions.IsChecked = true;
                SwitchToTab(ShellTab.Sessions);
                break;
            case 1: // Step 2 done → navigate to Tools tab
                TabTools.IsChecked = true;
                SwitchToTab(ShellTab.Tools);
                break;
                // Step 3 (index 2): sidebar switch happens in OnOnboardingCompleted
                // once persistence has succeeded.
        }
    }

    private void OnOnboardingCompleted(object? sender, EventArgs e)
    {
        // End where a new user has something to do. Each step navigates as it is
        // read, so without this the tour finishes on whichever tab the last step
        // left behind and the empty-state panel that invites the first session is
        // never seen at all.
        TabSessions.IsChecked = true;
        SwitchToTab(ShellTab.Sessions);

        // Switch to Tools tab via the VM. The OneWay binding propagates the
        // change back to the RadioButton group and the VM persists the choice.
        if (DataContext is MainViewModel vm)
            vm.Sidebar.SetActiveTab(isTools: true);

        if (_onboardingVm is not null)
        {
            _onboardingVm.StepCompleted -= OnOnboardingStepCompleted;
            _onboardingVm.Completed -= OnOnboardingCompleted;
            _onboardingVm = null;
        }
    }

    private async void OnToolsPanelItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Core.Models.ToolDescriptor descriptor
            || DataContext is not MainViewModel vm)
            return;

        try
        {
            var context = ToolsTabPopulationService.CreateInheritedToolContext(descriptor, vm);

            await vm.OpenToolTabAsync(
                descriptor.Id,
                ToolsTabPopulationService.ResolveToolTabTitle(descriptor, context, vm),
                context);

            vm.TrackRecentTool(descriptor.Id);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"Tool panel launch failed: {descriptor.Id}", ex);
            vm.StatusText = string.Format(vm.Localize("ErrorToolLaunchFailed"), descriptor.Id, ex.Message);
        }
    }

    private void OnExtToolBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = BuildFileDialogFilter("FilterExecutables", "*.exe;*.bat;*.cmd;*.ps1")
        };

        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm
            && vm.Settings.SelectedExternalTool is not null)
        {
            vm.Settings.SelectedExternalTool.ExecutablePath = dialog.FileName;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnExtToolWorkDirBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();

        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm
            && vm.Settings.SelectedExternalTool is not null)
        {
            vm.Settings.SelectedExternalTool.WorkingDirectory = dialog.FolderName;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnExtToolTestClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.Settings.SelectedExternalTool is null) return;

        var tool = vm.Settings.SelectedExternalTool;
        if (string.IsNullOrWhiteSpace(tool.ExecutablePath)) return;

        _externalToolLaunchService.LaunchConfigured(
            new Core.Configuration.ExternalToolDefinition
            {
                Name = tool.Name,
                ExecutablePath = tool.ExecutablePath,
                Arguments = tool.Arguments,
                WorkingDirectory = tool.WorkingDirectory,
                RunAsAdministrator = tool.RunAsAdministrator,
                RunHidden = tool.RunHidden
            },
            null,
            vm.Localize);
    }

    private void OnCredProvDbBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = BuildFileDialogFilter("FilterDatabaseFiles", "*.kdbx;*.db;*.gpg")
        };

        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm)
        {
            vm.Settings.CredentialProviderDatabase = dialog.FileName;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnCredProvKeyFileBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = BuildFileDialogFilter("FilterKeyFiles", "*.keyx;*.key")
        };

        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm)
        {
            vm.Settings.CredentialProviderKeyFile = dialog.FileName;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnCredProvPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (!_credentialProviderPresetService.TryGetCommand(
                Mw_SettingsCredProvPreset.SelectedIndex,
                out var command))
        {
            return;
        }

        vm.Settings.CredentialProviderCommand = command;
        vm.Settings.IsDirty = true;
    }

    // Guards the unlock PasswordBox so programmatic population on load does not mark dirty.
    private bool _suppressCredProvUnlockChange;

    private void OnCredProvUnlockChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressCredProvUnlockChange || DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.Settings.CredentialProviderUnlockSecret = Mw_SettingsCredProvUnlock.Password;
        vm.Settings.IsDirty = true;
    }

    private async void OnCmdLibSyncTokenChanged(object sender, RoutedEventArgs e)
    {
        string password = Mw_SettingsCmdLibSyncToken.Password;
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        bool saved = await _commandLibrarySettingsService.TrySaveTokenAsync(password).ConfigureAwait(true);
        if (saved)
        {
            ApplyCommandLibraryTokenStatus(true);
        }
        else
        {
            ApplyCommandLibraryTokenSaveError();
        }
    }

    private async void OnCmdLibSyncTokenClear(object sender, RoutedEventArgs e)
    {
        bool cleared = await _commandLibrarySettingsService.TryClearTokenAsync().ConfigureAwait(true);
        Mw_SettingsCmdLibSyncToken.Password = "";
        if (cleared)
        {
            ApplyCommandLibraryTokenStatus(false);
        }
        else
        {
            ApplyCommandLibraryTokenSaveError();
        }
    }

    private async void OnCmdLibSyncTestClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var app = System.Windows.Application.Current as App;
        var gitSync = app?.Services?
            .GetService(typeof(TwinShell.Core.Interfaces.IGitSyncService))
                as TwinShell.Core.Interfaces.IGitSyncService;
        if (gitSync is null) return;

        Mw_SettingsCmdLibSyncTestBtn.IsEnabled = false;
        Mw_SettingsCmdLibSyncTestBtn.Content = "...";

        try
        {
            var result = await gitSync.TestConnectionAsync();
            Mw_SettingsCmdLibSyncTestBtn.Content = result.Success
                ? vm.Localize("SettingsCmdLibSyncTestSuccess")
                : vm.Localize("SettingsCmdLibSyncTestFailed");
        }
        catch (Exception ex)
        {
            Mw_SettingsCmdLibSyncTestBtn.Content = vm.Localize("SettingsCmdLibSyncTestFailed");
            Core.Logging.FileLogger.Warn($"[GitSync] Test connection failed: {ex.Message}");
        }
        finally
        {
            Mw_SettingsCmdLibSyncTestBtn.IsEnabled = true;
        }
    }

    private async void OnRescanExternalToolsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var app = System.Windows.Application.Current as App;
        var providerService = app?.Services?
            .GetService(typeof(Services.ExternalToolProviderService)) as Services.ExternalToolProviderService;
        var toolRegistry = app?.Services?
            .GetService(typeof(Services.ToolRegistry)) as Services.ToolRegistry;
        var configManager = app?.Services?.GetService<Core.Configuration.IConfigManager>();

        if (providerService is null || toolRegistry is null || configManager is null) return;

        Mw_SettingsExtProvStatus.Text = vm.Localize("ExtToolStatusScanning");
        Mw_SettingsBtnRescan.IsEnabled = false;

        // Use the ViewModel's current (unsaved) path values for scanning.
        // Paths are only persisted when the user clicks Save, not on Rescan.
        var scanSettings = await configManager.LoadSettingsAsync();
        scanSettings.SysinternalsPath = string.IsNullOrWhiteSpace(vm.Settings.SysinternalsPath) ? null : vm.Settings.SysinternalsPath;
        scanSettings.NirSoftPath = string.IsNullOrWhiteSpace(vm.Settings.NirSoftPath) ? null : vm.Settings.NirSoftPath;
        scanSettings.NanaRunPath = string.IsNullOrWhiteSpace(vm.Settings.NanaRunPath) ? null : vm.Settings.NanaRunPath;

        await Task.Run(() =>
        {
            providerService.ScanAll(scanSettings);
            toolRegistry.RegisterExternalTools(providerService.DetectedTools);
        });

        Mw_SettingsBtnRescan.IsEnabled = true;
        Mw_SettingsExtProvStatus.Text = _externalToolSettingsService.BuildDetectedToolsStatus();

        // Refresh tools tab to show newly detected tools
        vm.ToolsTab.OnExternalToolsChanged();
    }

    private void OnSysinternalsPathBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Sysinternals",
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrEmpty(vm.Settings.SysinternalsPath)
            && System.IO.Directory.Exists(vm.Settings.SysinternalsPath))
            dlg.SelectedPath = vm.Settings.SysinternalsPath;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.Settings.SysinternalsPath = dlg.SelectedPath;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnNirSoftPathBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "NirSoft",
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrEmpty(vm.Settings.NirSoftPath)
            && System.IO.Directory.Exists(vm.Settings.NirSoftPath))
            dlg.SelectedPath = vm.Settings.NirSoftPath;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.Settings.NirSoftPath = dlg.SelectedPath;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnNanaRunPathBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "NanaRun",
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrEmpty(vm.Settings.NanaRunPath)
            && System.IO.Directory.Exists(vm.Settings.NanaRunPath))
            dlg.SelectedPath = vm.Settings.NanaRunPath;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            vm.Settings.NanaRunPath = dlg.SelectedPath;
            vm.Settings.IsDirty = true;
        }
    }

    private void OnBrowseEditorPathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = vm.Localize("BrowseEditorPathTitle"),
            Filter = vm.Localize("BrowseEditorPathFilter")
        };
        if (!string.IsNullOrEmpty(vm.Settings.ExternalEditorPath))
        {
            var dir = System.IO.Path.GetDirectoryName(vm.Settings.ExternalEditorPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            vm.Settings.ExternalEditorPath = dlg.FileName;
    }

    private void OnBrowsePlinkPathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = vm.Localize("BrowsePlinkTitle"),
            Filter = vm.Localize("BrowsePlinkFilter")
        };
        if (!string.IsNullOrEmpty(vm.Settings.PlinkPath))
        {
            var dir = System.IO.Path.GetDirectoryName(vm.Settings.PlinkPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            vm.Settings.PlinkPath = dlg.FileName;
    }

    private void OnBrowsePuttyPathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = vm.Localize("BrowsePuttyTitle"),
            Filter = vm.Localize("BrowsePuttyFilter")
        };
        if (!string.IsNullOrEmpty(vm.Settings.PuttyPath))
        {
            var dir = System.IO.Path.GetDirectoryName(vm.Settings.PuttyPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            vm.Settings.PuttyPath = dlg.FileName;
    }

    private void OnBrowseX11PathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = vm.Localize("BrowseX11PathTitle"),
            Filter = vm.Localize("BrowseX11PathFilter")
        };
        if (!string.IsNullOrEmpty(vm.Settings.X11ServerPath))
        {
            var dir = System.IO.Path.GetDirectoryName(vm.Settings.X11ServerPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            vm.Settings.X11ServerPath = dlg.FileName;
    }

    private void OnBrowseSessionLogDirClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = vm.Localize("BrowseSessionLogDirTitle"),
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrEmpty(vm.Settings.SessionLogDirectory)
            && System.IO.Directory.Exists(vm.Settings.SessionLogDirectory))
        {
            dlg.SelectedPath = vm.Settings.SessionLogDirectory;
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            vm.Settings.SessionLogDirectory = dlg.SelectedPath;
    }

    private static void HideTabStripPanel(System.Windows.Controls.TabControl tabControl, bool hide)
    {
        // Find the TabPanel in the TabControl's visual tree and collapse it
        tabControl.ApplyTemplate();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(tabControl); i++)
        {
            var child = VisualTreeHelper.GetChild(tabControl, i);
            HideTabStripPanelRecursive(child, hide);
        }
    }

    private static void HideTabStripPanelRecursive(DependencyObject parent, bool hide)
    {
        if (parent is System.Windows.Controls.Primitives.TabPanel tabPanel)
        {
            tabPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            HideTabStripPanelRecursive(VisualTreeHelper.GetChild(parent, i), hide);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = GetParentObject(current);
        }

        return null;
    }

    private static bool IsSelfOrDescendantOf(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetParentObject(current);
        }

        return false;
    }

    private static DependencyObject? GetParentObject(DependencyObject current)
    {
        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        if (current is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement);
        }

        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return LogicalTreeHelper.GetParent(current);
    }

    // ── Tab detachment ─────────────────────────────────────────────────

    // ── Session tab context menu handlers ──────────────────────────────

    private void OnSessionTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Resolve the tab that was actually right-clicked by walking the visual tree
        var clickSource = e.OriginalSource as DependencyObject;
        var clickedTabItem = FindAncestor<TabItem>(clickSource);
        var session = clickedTabItem?.DataContext as SessionTabViewModel
                      ?? vm.Connection.ActiveSession;
        if (session is null) return;

        // Select the right-clicked tab so it becomes active
        if (clickedTabItem is not null)
        {
            vm.Connection.ActiveSession = session;
        }

        // Views with their own context menus: SFTP, local file browser, and tool DataGrids.
        // Check the visual tree source to avoid overriding them.
        if (clickSource is not null)
        {
            // Notes owns a TreeView-specific context menu in its sidebar.
            // Skip the session menu so the tool can open its own commands.
            if (FindAncestor<Views.Tools.NotesToolView>(clickSource) is not null
                && FindAncestor<System.Windows.Controls.TreeView>(clickSource) is not null)
            {
                return;
            }

            // Check for any ancestor that has its own context menu
            if (FindAncestor<Views.EmbeddedSftpView>(clickSource) is not null
                || FindAncestor<Views.LocalFileBrowserView>(clickSource) is not null
                || FindAncestor<Views.Tools.HackerSimulatorView>(clickSource) is not null)
            {
                return;
            }

            // Tool views with DataGrid context menus (NetworkCartography, PortScanner, etc.)
            var dataGrid = FindAncestor<System.Windows.Controls.DataGrid>(clickSource);
            if (dataGrid is not null && ConnectionTypeCatalog.IsToolConnectionType(session.ConnectionType))
            {
                return; // Let the tool's own ContextMenuOpening handler take over
            }

            // Tool views with TreeView context menus (e.g. NotesToolView)
            var treeView = FindAncestor<System.Windows.Controls.TreeView>(clickSource);
            if (treeView is not null && ConnectionTypeCatalog.IsToolConnectionType(session.ConnectionType))
            {
                return;
            }

            // Also check for ListViewItem inside a split pane (the view might not
            // be a direct ancestor due to ContentPresenter wrapping)
            var listViewItem = FindAncestor<System.Windows.Controls.ListViewItem>(clickSource);
            if (listViewItem is not null && session.IsSplit)
            {
                return;
            }
        }

        var menu = _sessionTabContextMenuFactory.CreateMenu(session, vm, this);
        menu.PlacementTarget = SessionTabControl;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async Task OnResolutionChangedAsync(SessionPaneModel pane, ResolutionChoice choice)
    {
        if (DataContext is not MainViewModel vm) return;
        if (pane.HostControl is not Views.EmbeddedRdpView rdpView) return;

        switch (choice.Kind)
        {
            case ResolutionChoiceKind.Custom:
                var input = await vm.DialogService.ShowInputAsync(
                    vm.Localize("RdpResolutionCustomTitle"),
                    vm.Localize("RdpResolutionCustomPrompt"),
                    "1920x1080");
                if (!TryParseCustomResolution(input, out var customPreset))
                {
                    vm.StatusText = vm.Localize("RdpResolutionCustomInvalid");
                    return;
                }

                await rdpView.ApplyResolutionChoiceAsync(
                    ResolutionChoice.Fixed(customPreset.Width, customPreset.Height));
                break;

            case ResolutionChoiceKind.SaveAsDefaultForServer:
                await SaveRdpResolutionDefaultForServerAsync(vm, pane, rdpView);
                break;

            default:
                await rdpView.ApplyResolutionChoiceAsync(choice);
                break;
        }
    }

    private static bool TryParseCustomResolution(string? input, out ResolutionPreset preset)
    {
        preset = default;
        if (!ResolutionPresetCatalog.TryParse(input, out var parsed))
        {
            return false;
        }

        if (parsed.Width is < 200 or > 7680 || parsed.Height is < 200 or > 4320)
        {
            return false;
        }

        preset = new ResolutionPreset(
            Heimdall.Rdp.RdpDisplayHelper.SnapToMultipleOf(parsed.Width, 4),
            parsed.Height);
        return preset.Width > 0;
    }

    private static async Task SaveRdpResolutionDefaultForServerAsync(
        MainViewModel vm,
        SessionPaneModel pane,
        Views.EmbeddedRdpView rdpView)
    {
        string lookupId = pane.ProfileLookupServerId;
        if (string.IsNullOrWhiteSpace(lookupId))
        {
            vm.StatusText = vm.Localize("RdpResolutionSaveDefaultUnavailable");
            return;
        }

        var choice = rdpView.GetCurrentResolutionChoice();
        bool updated = await vm.ConfigManager.MutateServersAsync(servers =>
        {
            Core.Configuration.ServerProfileDto? server = servers.FirstOrDefault(
                candidate => string.Equals(candidate.Id, lookupId, StringComparison.Ordinal));
            if (server is null)
            {
                return false;
            }

            if (choice.Kind == ResolutionChoiceKind.Fixed)
            {
                server.RdpResolutionMode = RdpResolutionMode.Fixed;
                server.RdpFixedWidth = choice.Width;
                server.RdpFixedHeight = choice.Height;
            }
            else
            {
                server.RdpResolutionMode = RdpResolutionMode.FitWindow;
                server.RdpFixedWidth = 0;
                server.RdpFixedHeight = 0;
                server.RdpDynamicResolution = true;
            }

            return true;
        });

        if (!updated)
        {
            vm.StatusText = vm.Localize("RdpResolutionSaveDefaultUnavailable");
            return;
        }

        vm.StatusText = vm.Localize("RdpResolutionSaveDefaultDone");
    }

    /// <summary>
    /// When switching to Tunnels/Scheduled/Settings while sessions are active,
    /// the Sessions Grid must stay visible (for sessions) but TreeView hides.
    /// When returning to Sessions, TreeView restores.
    /// </summary>
    private void UpdateTabVisibility(MainViewModel vm)
    {
        bool isSessions = vm.SelectedTab == ShellTab.Sessions;
        bool hasSessions = vm.Connection.HasActiveSessions;
        bool suppressSidebar = !isSessions && hasSessions;

        Heimdall.Core.Logging.FileLogger.Info(
            $"UpdateTabVisibility: selectedTab={vm.SelectedTab}, isSessions={isSessions}, hasSessions={hasSessions}, sidebarHidden={_uiState.IsSidebarHidden}");

        ApplySidebarLayout(_uiState.SetSidebarSuppressedByTab(
            suppressSidebar,
            SessionTreeColumn.ActualWidth));
    }

    // --- Command Palette event handlers ---

    private void OnCommandPaletteOpened(object sender, EventArgs e)
    {
        UpdatePaletteModeLabel();
        BeginFocusCommandPalette();
    }

    private void UpdatePaletteModeLabel()
    {
        if (DataContext is not MainViewModel vm)
        {
            PaletteModeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        System.Windows.Automation.AutomationProperties.SetName(
            PaletteInput,
            string.IsNullOrWhiteSpace(vm.CommandPalette.Placeholder)
                ? vm.Localize("PaletteSearchPlaceholder")
                : vm.CommandPalette.Placeholder);

        if (vm.CommandPalette.IsInSplitMode)
        {
            PaletteModeLabel.Text = vm.Localize("PaletteModeSplit");
            PaletteModeLabel.Visibility = Visibility.Visible;
        }
        else
        {
            PaletteModeLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void BeginFocusCommandPalette()
    {
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(FocusCommandPalette));
    }

    private void FocusCommandPalette()
    {
        if (!CommandPalettePopup.IsOpen || !PaletteInput.IsVisible)
        {
            return;
        }

        if (PresentationSource.FromVisual(PaletteInput) is HwndSource source &&
            source.Handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(source.Handle);
            _ = SetActiveWindow(source.Handle);
            _ = SetFocus(source.Handle);
        }

        _foregroundWatchService.Start();
        Keyboard.Focus(PaletteInput);
        PaletteInput.Focus();
        PaletteInput.SelectAll();
    }

    private void OnCommandPaletteClosed(object? sender, EventArgs e)
    {
        _foregroundWatchService.Stop();
    }

    private void OnForegroundChangedOutsideProcess(object? sender, IntPtr foregroundHwnd)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal,
            new Action(CloseCommandPaletteFromForegroundChange));
    }

    private void CloseCommandPaletteFromForegroundChange()
    {
        if (DataContext is not MainViewModel vm || !vm.CommandPalette.IsOpen)
        {
            return;
        }

        _foregroundWatchService.Stop();
        vm.CommandPalette.CloseCommand.Execute(null);
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Snippet drill-in detail mode owns the keyboard: Enter=send,
        // Ctrl+Enter=copy, Esc=back, Up/Down=move variant selection. Any other
        // key falls through so typing in parameter TextBoxes still works.
        if (vm.CommandPalette.IsSnippetDetailOpen)
        {
            if (e.Key == Key.Escape)
            {
                vm.CommandPalette.BackFromSnippetDetailCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                vm.CommandPalette.CopySnippetCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                vm.CommandPalette.SendSnippetCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (PaletteVariantsList.SelectedIndex < PaletteVariantsList.Items.Count - 1)
                {
                    PaletteVariantsList.SelectedIndex++;
                    PaletteVariantsList.ScrollIntoView(PaletteVariantsList.SelectedItem);
                    FocusSelectedVariantItem();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (PaletteVariantsList.SelectedIndex > 0)
                {
                    PaletteVariantsList.SelectedIndex--;
                    PaletteVariantsList.ScrollIntoView(PaletteVariantsList.SelectedItem);
                    FocusSelectedVariantItem();
                }
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CommandPalette.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Ctrl+Enter = connect in split pane
            var target = vm.CommandPalette.SelectedItem ?? vm.CommandPalette.Results.FirstOrDefault();
            _ = vm.CommandPalette.ConnectSplitFromPaletteCommand.ExecuteAsync(target);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _ = vm.CommandPalette.ConnectFromPaletteCommand.ExecuteAsync(
                vm.CommandPalette.SelectedItem ?? vm.CommandPalette.Results.FirstOrDefault());
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (PaletteResultsList.SelectedIndex < PaletteResultsList.Items.Count - 1)
            {
                PaletteResultsList.SelectedIndex++;
                PaletteResultsList.ScrollIntoView(PaletteResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (PaletteResultsList.SelectedIndex > 0)
            {
                PaletteResultsList.SelectedIndex--;
                PaletteResultsList.ScrollIntoView(PaletteResultsList.SelectedItem);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Moves keyboard focus to the snippet variants list when the drill-in detail
    /// panel becomes visible, selecting the first variant if none is selected yet.
    /// </summary>
    private void OnSnippetDetailVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!CommandPalettePopup.IsOpen || !PaletteDetailPanel.IsVisible)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(FocusSnippetVariants));
    }

    private void FocusSnippetVariants()
    {
        if (!CommandPalettePopup.IsOpen || !PaletteVariantsList.IsVisible)
        {
            return;
        }

        if (PaletteVariantsList.SelectedIndex < 0 && PaletteVariantsList.Items.Count > 0)
        {
            PaletteVariantsList.SelectedIndex = 0;
        }

        if (PaletteVariantsList.ItemContainerGenerator
                .ContainerFromIndex(PaletteVariantsList.SelectedIndex) is ListBoxItem item)
        {
            _ = item.Focus();
        }
        else
        {
            _ = PaletteVariantsList.Focus();
            FocusSelectedVariantItem();
        }
    }

    /// <summary>
    /// Moves keyboard focus to the currently selected variant's container so the
    /// focus ring and the UIA focus track the selection during arrow navigation.
    /// Deferred to Input priority so the container is realized before focusing.
    /// </summary>
    private void FocusSelectedVariantItem()
    {
        if (PaletteVariantsList.SelectedIndex < 0) return;
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (PaletteVariantsList.ItemContainerGenerator
                        .ContainerFromIndex(PaletteVariantsList.SelectedIndex) is ListBoxItem item)
                {
                    _ = item.Focus();
                }
            }));
    }

    /// <summary>
    /// Opens an external documentation link from the palette snippet detail in the
    /// default browser. Mirrors CommandLibraryView.OnLinkNavigate: http/https only,
    /// shell-executed, failures logged without a modal.
    /// </summary>
    private void OnPaletteLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            if (e.Uri.Scheme is not ("http" or "https")) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandPalette] Failed to open link: {ex.Message}");
        }
        e.Handled = true;
    }

    /// <summary>
    /// Dismiss the command palette when the user clicks anywhere in the main window
    /// (outside the Popup). The Popup is a separate HWND, so clicks on the main
    /// window surface never reach it — we intercept them here instead.
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Some Popup configurations still route clicks through Window.PreviewMouseDown.
        // Guard against dismissing the palette when the click originates in its popup child.
        if (IsCommandPalettePopupClick(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (DataContext is MainViewModel vm && vm.CommandPalette.IsOpen)
        {
            vm.CommandPalette.CloseCommand.Execute(null);
        }
    }

    private bool IsCommandPalettePopupClick(DependencyObject? source)
    {
        if (!CommandPalettePopup.IsOpen || CommandPalettePopup.Child is not DependencyObject child)
        {
            return false;
        }

        if (source is not null && IsSelfOrDescendantOf(source, child))
        {
            return true;
        }

        if (child is UIElement { IsMouseOver: true })
        {
            return true;
        }

        if (child is FrameworkElement element)
        {
            var point = Mouse.GetPosition(element);
            return point.X >= 0
                   && point.Y >= 0
                   && point.X <= element.ActualWidth
                   && point.Y <= element.ActualHeight;
        }

        return false;
    }

    private void OnPaletteBorderClick(object sender, MouseButtonEventArgs e)
    {
        // No-op kept for XAML binding. Palette dismissal is centralized in
        // OnWindowPreviewMouseDown, which filters clicks inside this popup.
    }

    /// <summary>
    /// Executes the double-clicked palette item (connects or opens tool).
    /// Captures split state synchronously BEFORE the async command runs,
    /// preventing a race condition where Popup deactivation clears
    /// <c>_splitPaletteSession</c> before <c>ConnectFromPaletteAsync</c> reads it.
    /// Single click only selects (highlights) via standard ListBox behavior.
    /// </summary>
    private void OnPaletteItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject) is not
            { DataContext: ViewModels.ServerItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        vm.CommandPalette.ExecuteSelection(item);
    }

    private void OnQuickConnectButtonClick(object sender, RoutedEventArgs e)
    {
        OpenCommandPalette();
    }

    // ── Quick File Server (FileShareService bridge) ──────────────────

    private async void OnShareFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (_fileShareService.IsSharing)
        {
            await _fileShareService.StopAsync();
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        await _fileShareService.StartAsync(dialog.SelectedPath, vm.CurrentSettings);
    }

    private async Task ApplyFileShareTftpSettingAsync(MainViewModel vm, bool enableTftp)
    {
        if (vm.CurrentSettings is null)
        {
            return;
        }

        var previousValue = vm.CurrentSettings.FileShareEnableTftp;

        try
        {
            vm.CurrentSettings.FileShareEnableTftp = enableTftp;
            await vm.ConfigManager.MergeSettingAsync(settings => settings.FileShareEnableTftp = enableTftp);

            if (_fileShareService.IsSharing && _fileShareService.CurrentDirectory is { } currentDirectory)
            {
                _suppressFileShareStartDialog = true;
                try
                {
                    await _fileShareService.StopAsync();
                    await _fileShareService.StartAsync(currentDirectory, vm.CurrentSettings);
                }
                finally
                {
                    _suppressFileShareStartDialog = false;
                }
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error($"[MainWindow] Failed to update TFTP file share setting: {ex.Message}");
            vm.CurrentSettings.FileShareEnableTftp = previousValue;
            _suppressFileShareTftpSettingBridge = true;
            try
            {
                vm.Settings.FileShareEnableTftp = previousValue;
            }
            finally
            {
                _suppressFileShareTftpSettingBridge = false;
            }
        }
    }

    // ── IContextMenuCallbacks ─────────────────────────────────────────
    // Window-layer callbacks surfaced to ContextMenuFactory for actions that
    // require DataContext access, modal dialog ownership, or window-local state.

    /// <inheritdoc />
    void IContextMenuCallbacks.OpenNotesForServer(
        ServerItemViewModel server,
        NoteTemplateKind templateKind)
    {
        if (DataContext is not MainViewModel vm) return;

        var context = new Core.Models.ToolContext(
            TargetHost: server.RemoteServer,
            TargetPort: server.RemotePort > 0 ? server.RemotePort : null,
            Argument: $"template:{templateKind.ToString().ToLowerInvariant()}",
            DisplayName: server.DisplayName,
            Username: server.Username,
            ProjectName: server.ProjectName,
            GroupName: server.Group,
            ConnectionType: server.ConnectionType);

        _ = vm.OpenToolTabAsync("NOTES", server.DisplayName, context);
    }

    /// <inheritdoc />
    void IContextMenuCallbacks.LaunchExternalTool(
        ServerItemViewModel server,
        Core.Configuration.ExternalToolDefinition tool)
    {
        if (DataContext is not MainViewModel vm) return;
        _externalToolLaunchService.LaunchConfigured(tool, server, vm.Localize);
    }

    /// <inheritdoc />
    void IContextMenuCallbacks.LaunchDetectedTool(
        ServerItemViewModel server,
        Core.Configuration.ExternalToolInfo tool)
    {
        if (DataContext is not MainViewModel vm) return;
        _externalToolLaunchService.LaunchDetected(tool, server, vm.Localize);
    }

    /// <inheritdoc />
    void IContextMenuCallbacks.AddToolFromMenu(string? group)
    {
        if (DataContext is not MainViewModel vm) return;
        _ = ShowAddToolPickerAsync(vm, group);
    }

    /// <inheritdoc />
    void IContextMenuCallbacks.SelectFolder(string fullPath)
    {
        if (DataContext is not MainViewModel vm) return;

        FolderViewModel? folder = FindFolderByPath(vm.ServerList.GroupedServers, fullPath);
        if (folder is null) return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                TreeViewItem? container =
                    GetOrRealizeSessionTreeItem(folder);
                if (container is not null)
                {
                    container.IsSelected = true;
                    container.BringIntoView();
                }
            }));
    }

    private static FolderViewModel? FindFolderByPath(
        IEnumerable<FolderViewModel> folders,
        string fullPath)
    {
        foreach (FolderViewModel folder in folders)
        {
            if (string.Equals(folder.FullPath, fullPath, StringComparison.Ordinal))
            {
                return folder;
            }

            FolderViewModel? nested = FindFolderByPath(folder.SubFolders, fullPath);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // ── ISessionTabContextCallbacks ───────────────────────────────────
    // OnResolutionChanged and ToggleFullscreen stay in MainWindow
    // (fullscreen touches named XAML elements via MainWindow.WindowUI.cs;
    // resolution dispatches to the active RDP view). All split/merge/detach
    // operations delegate to SessionWindowService.

    /// <inheritdoc />
    void ISessionTabContextCallbacks.OnResolutionChanged(SessionPaneModel pane, ResolutionChoice choice)
        => _ = OnResolutionChangedAsync(pane, choice);

    /// <inheritdoc />
    void ISessionTabContextCallbacks.ToggleFullscreen()
        => ToggleFullscreen();

    /// <inheritdoc />
    void ISessionTabContextCallbacks.DetachSessionToFloatingWindow(SessionTabViewModel session)
    {
        if (DataContext is not MainViewModel vm) return;
        _sessionWindowService.DetachSessionToFloatingWindow(session, vm);
    }

    /// <inheritdoc />
    void ISessionTabContextCallbacks.DetachSecondaryToFloatingWindow(SessionTabViewModel session)
    {
        if (DataContext is not MainViewModel vm) return;
        _sessionWindowService.DetachSecondaryToFloatingWindow(session, vm);
    }

    /// <inheritdoc />
    void ISessionTabContextCallbacks.RequestSplitSession(
        SessionTabViewModel session,
        Heimdall.Core.Models.SplitOrientation orientation)
    {
        if (DataContext is not MainViewModel vm) return;
        _sessionWindowService.RequestSplitSession(session, orientation, vm);
    }

    /// <inheritdoc />
    void ISessionTabContextCallbacks.UnsplitSession(SessionTabViewModel session)
    {
        if (DataContext is not MainViewModel vm) return;
        _sessionWindowService.UnsplitSession(session, vm);
    }

    /// <inheritdoc />
    void ISessionTabContextCallbacks.RevealServerInTree(string serverId)
    {
        if (string.IsNullOrEmpty(serverId) || DataContext is not MainViewModel vm)
        {
            return;
        }

        // Make sure the sessions tree is actually on screen: expand a hidden
        // sidebar and switch it to the Sessions tab.
        if (_uiState.IsSidebarHidden)
        {
            ToggleSidebar();
        }

        SidebarTabSessions.IsChecked = true;

        // Relax an active search filter so a filtered-out node becomes visible.
        if (!string.IsNullOrWhiteSpace(vm.ServerList.SearchText))
        {
            vm.ServerList.SearchText = string.Empty;
        }

        var server = vm.ServerList.Servers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
        if (server is null)
        {
            return;
        }

        // Expand every ancestor folder so the leaf container can be realized.
        ExpandFoldersToServer(vm.ServerList.GroupedServers, server);

        // Defer until WPF has generated the newly expanded containers, then select
        // and scroll. Selecting the container raises SelectedItemChanged, which runs
        // the existing SelectSingle + ShowTreeSelection path.
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                var container = GetOrRealizeSessionTreeItem(server);
                if (container is not null)
                {
                    container.IsSelected = true;
                    container.BringIntoView();
                }
                else
                {
                    // Container not realized: at least drive the VM selection + detail panel.
                    vm.ServerList.SelectSingle(server);
                    ShowTreeSelection(vm, server);
                }
            }));
    }

    /// <summary>
    /// Expands every folder on the path to <paramref name="server"/> so its
    /// TreeViewItem becomes realizable. Returns <c>true</c> when the server was
    /// located under <paramref name="folders"/>.
    /// </summary>
    private static bool ExpandFoldersToServer(
        IEnumerable<FolderViewModel> folders,
        ServerItemViewModel server)
    {
        foreach (var folder in folders)
        {
            var containsHere = folder.Servers.Contains(server);
            var containsBelow = ExpandFoldersToServer(folder.SubFolders, server);
            if (containsHere || containsBelow)
            {
                folder.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private void OnFileShareSharingStarted(object? sender, FileShareStartedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        Mw_SharingStatus.Text = e.BaseUrl;
        Mw_SharingStatusPanel.Visibility = Visibility.Visible;
        Mw_TftpCommandText.Text = e.TftpCommand ?? string.Empty;
        bool showTftpCommand = e.IsTftpEnabled && !string.IsNullOrWhiteSpace(e.TftpCommand);
        Mw_TftpTemplatePanel.Visibility = showTftpCommand
            ? Visibility.Visible
            : Visibility.Collapsed;
        Mw_TftpUnauthenticatedWarning.Visibility = showTftpCommand
            ? Visibility.Visible
            : Visibility.Collapsed;
        Mw_TftpDisabledHint.Visibility = !e.IsTftpEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_suppressFileShareStartDialog)
        {
            try { Clipboard.SetText(e.BaseUrl); }
            catch { /* clipboard may fail in some RDP sessions */ }
        }

        string helpMessage = string.Format(vm.Localize("ToolsSharingHelp"),
            e.FolderName,
            e.BaseUrl,
            e.WgetCommand,
            e.CurlCommand,
            e.TftpCommand ?? vm.Localize("LblTftpDisabledHint"));
        if (e.IsTftpEnabled)
        {
            helpMessage += Environment.NewLine + Environment.NewLine + vm.Localize("LblTftpNoAuthWarning");
        }

        vm.StatusText = string.Format(vm.Localize("ToolsSharingReady"), e.BaseUrl);

        if (!_suppressFileShareStartDialog)
        {
            _dialogService.ShowInfo(vm.Localize("ToolsSharingHelpTitle"), helpMessage);
        }
    }

    private void OnFileShareSharingStopped(object? sender, EventArgs e)
    {
        Mw_SharingStatusPanel.Visibility = Visibility.Collapsed;
        Mw_SharingStatus.Text = string.Empty;
        Mw_TftpCommandText.Text = string.Empty;
        Mw_TftpTemplatePanel.Visibility = Visibility.Collapsed;
        Mw_TftpUnauthenticatedWarning.Visibility = Visibility.Collapsed;
        Mw_TftpDisabledHint.Visibility = Visibility.Collapsed;

        if (DataContext is MainViewModel vm)
        {
            vm.StatusText = vm.Localize("ToolsSharingStopped");
        }
    }

    private void OnFileShareFileServed(object? sender, string fileName)
    {
        Dispatcher.Invoke(() =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.StatusText = string.Format(
                vm.Localize("ToolsSharingServed"),
                fileName,
                _fileShareService.BaseUrl);
        });
    }

    private void OnCopyShareUrlClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_fileShareService.BaseUrl))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_fileShareService.BaseUrl);

            if (DataContext is MainViewModel vm)
            {
                vm.StatusText = string.Format(vm.Localize("StatusCopiedToClipboard"), _fileShareService.BaseUrl);
            }
        }
        catch
        {
            // Clipboard access may fail in some remote desktop contexts.
        }
    }

    private void RestoreWindowBounds(Heimdall.Core.Configuration.AppSettings settings)
    {
        SidebarLayoutProjection sidebarLayout = WindowBoundsPersistence.RestoreSidebarState(
            _uiState,
            settings);
        ApplySidebarLayout(sidebarLayout);

        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            Rect savedBounds = new(
                settings.WindowLeft,
                settings.WindowTop,
                settings.WindowWidth,
                settings.WindowHeight);
            DpiScale dpiScale = VisualTreeHelper.GetDpi(this);
            IReadOnlyList<Rect> workingAreas = WindowWorkingAreaProvider.GetWorkingAreas(dpiScale);
            Rect? restoredBounds = WindowBoundsRestorationPolicy.Resolve(
                savedBounds,
                workingAreas);

            if (restoredBounds is Rect bounds)
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;
            }

            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    private Task SaveWindowBoundsAsync(MainViewModel vm)
    {
        // Save Normal-state bounds even when maximized
        bool isMaximized = WindowState == WindowState.Maximized;
        Rect bounds = isMaximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        WindowBoundsSnapshot snapshot = WindowBoundsPersistence.CaptureSnapshot(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            isMaximized,
            _uiState,
            SessionTreeColumn.ActualWidth);
        return WindowBoundsPersistence.PersistAsync(vm.ConfigManager, snapshot);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>async void</c> is intentional and required: <see cref="Window.OnClosing"/>
    /// is a synchronous override, but the unsaved-settings decision and all config
    /// writes must be awaited without blocking the dispatcher. The standard WPF
    /// pattern is to cancel the close, await preparation, then re-invoke
    /// <see cref="Window.Close"/>.
    /// The <see cref="_closeConfirmed"/> flag prevents infinite recursion on the
    /// second pass through this handler.
    /// </remarks>
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        if (DataContext is not MainViewModel vm) return;

        if (Application.Current is Heimdall.App.App { IsShuttingDown: true })
        {
            return;
        }

        if (_closeConfirmed)
        {
            // This window will close on the current pass. Arm the shutdown guard
            // before WPF broadcasts Unloaded to the visual tree, so session panes
            // skip reparenting teardown during application shutdown.
            if (Application.Current is Heimdall.App.App app)
            {
                app.IsShuttingDown = true;
            }

            return;
        }

        // Every first close pass is cancelled so all persistence can complete
        // before a guarded second Close() re-enters this handler.
        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            string warningTitle = vm.Localize("SettingsCloseSaveFailedTitle");
            string warningMessage = vm.Localize("SettingsCloseSaveFailedMessage");
            int connectedSessionCount = vm.Connection.ActiveSessions.Count(session =>
                Heimdall.Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent)
                    .Any(pane => string.Equals(
                        pane.Status,
                        "Connected",
                        StringComparison.Ordinal)));
            bool prepared = await WindowClosingFlow.TryPrepareCloseAsync(
                vm.Settings.IsDirty,
                connectedSessionCount,
                () => vm.DialogService.ShowSaveDiscardCancelAsync(
                    vm.Localize("SettingsUnsavedWarningTitle"),
                    vm.Localize("SettingsUnsavedWarning")),
                () => vm.Settings.TrySaveAsync(),
                () => vm.DialogService.ShowConfirmAsync(
                    vm.Localize("ConfirmCloseAllTabs"),
                    string.Format(
                        vm.Localize("ConfirmCloseAllTabsMessage"),
                        connectedSessionCount),
                    "warning"),
                () => vm.ServerList.FlushExpandStateForCloseAsync(),
                () => SaveWindowBoundsAsync(vm),
                () => vm.DialogService.ShowWarning(warningTitle, warningMessage));

            if (!prepared)
            {
                return;
            }

            _closeConfirmed = true;
            if (IsLoaded)
            {
                Close();
            }
            else
            {
                _closeConfirmed = false;
            }
        }
        catch (Exception ex)
        {
            _closeConfirmed = false;
            Core.Logging.FileLogger.Error("Guarded main window close failed", ex);
            vm.DialogService.ShowWarning(
                vm.Localize("SettingsCloseSaveFailedTitle"),
                vm.Localize("SettingsCloseSaveFailedMessage"));
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (!_threadPreprocessMessageHooked)
        {
            ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
            _threadPreprocessMessageHooked = true;
        }

        InstallLowLevelKeyboardHook();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (Application.Current is Heimdall.App.App app)
        {
            app.IsShuttingDown = true;
        }

        StopFullscreenChrome();
        DisposeLowLevelKeyboardHook();
        if (_threadPreprocessMessageHooked)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
            _threadPreprocessMessageHooked = false;
        }

        PreviewKeyDown -= OnWindowPreviewKeyDown;
        KeyDown -= OnKeyDown;
        PreviewMouseDown -= OnWindowPreviewMouseDown;

        _themeService.ThemeChanged -= OnThemeServiceThemeChanged;

        // Unsubscribe from long-lived service/ViewModel events. The DataContext
        // is the same MainViewModel instance the constructor wired up; without
        // these unsubscriptions the captured-`this` lambdas would keep this
        // window alive past Close().
        if (DataContext is MainViewModel vm)
        {
            if (_connectionPropertyChangedHandler is not null)
                vm.Connection.PropertyChanged -= _connectionPropertyChangedHandler;
            if (_serverListPropertyChangedHandler is not null)
                vm.ServerList.PropertyChanged -= _serverListPropertyChangedHandler;
            if (_settingsPropertyChangedHandler is not null)
                vm.Settings.PropertyChanged -= _settingsPropertyChangedHandler;
            if (_trackedExternalToolForPreview is not null
                && _selectedExternalToolPropertyChangedHandler is not null)
            {
                _trackedExternalToolForPreview.PropertyChanged -= _selectedExternalToolPropertyChangedHandler;
            }
            if (_externalToolsChangedHandler is not null)
                vm.ToolRegistry.ExternalToolsChanged -= _externalToolsChangedHandler;
            if (_localeChangedHandler is not null)
                vm.GetLocalizer().LocaleChanged -= _localeChangedHandler;
            vm.ToolsTab.SectionsInvalidated -= OnToolsTabSectionsInvalidated;
        }

        _fileShareService.SharingStarted -= OnFileShareSharingStarted;
        _fileShareService.SharingStopped -= OnFileShareSharingStopped;
        _fileShareService.FileServed -= OnFileShareFileServed;
        _foregroundWatchService.ForegroundChanged -= OnForegroundChangedOutsideProcess;
        _foregroundWatchService.Stop();
        _sessionWindowService.SplitPaletteRequested -= OnSplitPaletteRequested;
        CommandPalettePopup.Closed -= OnCommandPaletteClosed;
        _ = _fileShareService.DisposeAsync();
        base.OnClosed(e);
    }

    /// <summary>
    /// Rebuilds any code-built tool panel UI after a runtime theme swap.
    /// Most brush properties on the generated elements use
    /// <see cref="FrameworkElement.SetResourceReference(DependencyProperty, object)"/>
    /// so they update automatically; this handler is the safety net for anything
    /// that can't be expressed as a dynamic resource reference (e.g. hover-state
    /// brush toggling driven from code-behind).
    /// </summary>
    private void OnThemeServiceThemeChanged(string themeName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (!string.Equals(vm.SelectedTab, ShellTab.Tools, StringComparison.Ordinal)) return;

            // Re-render the cards so brushes reflect the new theme.
            vm.ToolsTab.InvalidateSections();
        });
    }
}
