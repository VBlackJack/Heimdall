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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;
// File-local aliases resolve two name clashes between Heimdall.App.Services
// and TwinShell.Core.Interfaces (IDialogService) and between System and
// TwinShell.Core.Models (Action). All other partials use the field/parameter
// types declared here so they don't need their own aliases.
using IDialogService = Heimdall.App.Services.IDialogService;
using TemplateParameter = TwinShell.Core.Models.TemplateParameter;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel orchestrating the embedded TwinShell command library tool:
/// loading actions, search/filter, parameterized command generation, CRUD,
/// favorites, history, and Git sync.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped service handling:</b> All TwinShell core services
/// (<see cref="IActionService"/>, <see cref="ISearchService"/>,
/// <see cref="ICommandGeneratorService"/>, <see cref="ICommandHistoryService"/>,
/// <see cref="IFavoritesService"/>) are registered as <c>Scoped</c> in
/// <see cref="TwinShellBootstrapper"/> because they share a per-request
/// <c>DbContext</c>. Injecting them directly into this transient VM would
/// pin them to the root scope (captive dependency) and leak DbContext
/// change-tracker state across operations. Instead the VM stores an
/// <see cref="IServiceProvider"/> and creates per-operation scopes.
/// </para>
/// <para>
/// A long-lived "session scope" is kept while the tool is open for
/// <see cref="ISearchService"/> and <see cref="ICommandGeneratorService"/>
/// (queried frequently as the user types). It is rebuilt on every reload
/// and disposed in <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed partial class CommandLibraryViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly IGitSyncService _gitSyncService;
    private readonly ICommandLibraryTransferService _transferService;

    private IServiceScope? _sessionScope;
    private ISearchService? _searchService;
    private ICommandGeneratorService? _commandGenerator;

    private readonly ObservableCollection<CommandLibraryActionEntry> _allEntries = new();
    private readonly ObservableCollection<CommandLibraryParameterEntry> _parameters = new();
    private HashSet<string> _favoriteIds = new(StringComparer.Ordinal);
    private List<string> _categoryList = new();

    private List<string>? _searchRankedIds;
    private HashSet<string>? _searchMatchIds;
    private string _lastSearchTerm = string.Empty;
    private CancellationTokenSource? _searchCts;

    private ActionModel? _selectedAction;
    private CommandTemplate? _activeTemplate;
    private string? _targetHost;
    private bool _disposed;
    private bool _loadingSelection;

    private ListCollectionView? _actionsView;
    private DispatcherTimer? _historyCopyTimer;

    /// <summary>
    /// Raised after every reload so the view can re-run any visual-only
    /// setup (e.g., re-focus the search box). The old category-combo rebuild
    /// path has moved into <see cref="CategoryFilterItems"/>.
    /// </summary>
    public event Action? LibraryReloaded;

    /// <summary>
    /// Creates a new VM with the dependencies it needs to talk to TwinShell
    /// and Heimdall's dialog/config infrastructure.
    /// </summary>
    public CommandLibraryViewModel(
        IServiceProvider serviceProvider,
        IConfigManager configManager,
        LocalizationManager localizer,
        IDialogService dialogService,
        IGitSyncService gitSyncService,
        ICommandLibraryTransferService transferService)
    {
        _serviceProvider = serviceProvider;
        _configManager = configManager;
        _localizer = localizer;
        _dialogService = dialogService;
        _gitSyncService = gitSyncService;
        _transferService = transferService;
    }

    // ── Observable UI state ───────────────────────────────────────

    /// <summary>True while a CRUD or import/export operation is in flight.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True while a Git sync operation is in flight.</summary>
    [ObservableProperty]
    private bool _isSyncing;

    /// <summary>True once the initial load has succeeded and the view can be interacted with.</summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>Current search term (raw TextBox text — debouncing is internal).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    /// <summary>0 = All, 1+ = index into <see cref="Categories"/>.</summary>
    [ObservableProperty]
    private int _categoryFilterIndex;

    /// <summary>0 = All, 1 = Windows, 2 = Linux.</summary>
    [ObservableProperty]
    private int _platformFilterIndex;

    /// <summary>0 = All, 1 = Info, 2 = Run, 3 = Dangerous.</summary>
    [ObservableProperty]
    private int _riskFilterIndex;

    /// <summary>Whether the favorites-only filter toggle is active.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoritesFilterBrushKey))]
    private bool _favoritesFilterActive;

    /// <summary>Whether the help panel is currently visible.</summary>
    [ObservableProperty]
    private bool _isHelpVisible;

    /// <summary>Whether the history panel is currently visible.</summary>
    [ObservableProperty]
    private bool _isHistoryVisible;

    /// <summary>Whether the generator panel is currently visible (an action is selected).</summary>
    [ObservableProperty]
    private bool _isGeneratorVisible;

    /// <summary>Whether the active selection has both Windows and Linux templates.</summary>
    [ObservableProperty]
    private bool _hasMultipleTemplates;

    /// <summary>True if Windows template is the active one, false for Linux.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLinuxTemplate))]
    private bool _useWindowsTemplate;

    /// <summary>Inverse of <see cref="UseWindowsTemplate"/> used by the Linux radio.</summary>
    public bool UseLinuxTemplate
    {
        get => !UseWindowsTemplate;
        set
        {
            if (value != UseLinuxTemplate)
            {
                UseWindowsTemplate = !value;
            }
        }
    }

    /// <summary>Whether Edit/Delete buttons should be enabled (user-created actions only).</summary>
    [ObservableProperty]
    private bool _isSelectedActionEditable;

    /// <summary>Display name of the active template.</summary>
    [ObservableProperty]
    private string _activeTemplateName = string.Empty;

    /// <summary>The currently generated command text (or the raw pattern if invalid).</summary>
    [ObservableProperty]
    private string _generatedCommand = string.Empty;

    /// <summary>True when the current parameter set produces a valid command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendEnabled))]
    private bool _isCommandValid;

    /// <summary>Multi-line validation error text (one bullet per error).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _validationError = string.Empty;

    /// <summary>Status text shown next to the title during sync operations.</summary>
    [ObservableProperty]
    private string _syncStatusMessage = string.Empty;

    /// <summary>Status text shown in the empty state placeholder.</summary>
    [ObservableProperty]
    private string _emptyStateMessage = string.Empty;

    /// <summary>Localized help-panel body text.</summary>
    [ObservableProperty]
    private string _helpContentText = string.Empty;

    /// <summary>
    /// True when a <see cref="SendCommandHandler"/> is wired up — controls
    /// the visibility of the Send button in the generator panel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendEnabled))]
    private bool _isSendVisible;

    /// <summary>Currently selected entry in the action list (bound from the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    private CommandLibraryActionEntry? _selectedEntry;

    /// <summary>Notes text for the currently selected action (empty when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNotes))]
    [NotifyPropertyChangedFor(nameof(HasDetailContent))]
    private string _selectedActionNotes = string.Empty;

    /// <summary>Transient "Copied to clipboard" banner shown in the history panel.</summary>
    [ObservableProperty]
    private bool _isHistoryCopyFeedbackVisible;

    // ── Public read-only state ────────────────────────────────────

    /// <summary>All loaded action entries (unfiltered).</summary>
    public ObservableCollection<CommandLibraryActionEntry> AllEntries => _allEntries;

    /// <summary>Localized category names (excluding the "All" sentinel).</summary>
    public IReadOnlyList<string> Categories => _categoryList;

    /// <summary>Parameter list bound to the generator panel.</summary>
    public ObservableCollection<CommandLibraryParameterEntry> Parameters => _parameters;

    /// <summary>History entries bound to the history panel.</summary>
    public ObservableCollection<CommandLibraryHistoryEntry> HistoryEntries { get; } = new();

    /// <summary>Links exposed for the currently selected action (may be empty).</summary>
    public ObservableCollection<TwinShell.Core.Models.ExternalLink> SelectedActionLinks { get; } = new();

    /// <summary>Examples exposed for the active template (may be empty).</summary>
    public ObservableCollection<TwinShell.Core.Models.CommandExample> SelectedActionExamples { get; } = new();

    /// <summary>Localized items for the Category filter combo.</summary>
    public ObservableCollection<string> CategoryFilterItems { get; } = new();

    /// <summary>Localized items for the Platform filter combo.</summary>
    public ObservableCollection<string> PlatformFilterItems { get; } = new();

    /// <summary>Localized items for the Risk filter combo.</summary>
    public ObservableCollection<string> RiskFilterItems { get; } = new();

    /// <summary>
    /// Filtered / sorted / grouped view over <see cref="AllEntries"/>. Bound
    /// to the action <c>ListView</c> by <see cref="ActionsView"/> getter.
    /// </summary>
    public ICollectionView? ActionsView => _actionsView;

    /// <summary>True when the search box contains any text (drives clear-button visibility).</summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    /// <summary>True when <see cref="ValidationError"/> is non-empty.</summary>
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>True when the Send button should be interactive.</summary>
    public bool IsSendEnabled => IsCommandValid && IsSendVisible;

    /// <summary>True when the selected action can be edited or deleted.</summary>
    public bool CanEditSelected => IsSelectedActionEditable;

    /// <summary>True when the current selection exposes notes text.</summary>
    public bool HasSelectedNotes => !string.IsNullOrEmpty(SelectedActionNotes);

    /// <summary>
    /// True when at least one of notes / examples / links has content,
    /// driving the detail panel's collapsed state.
    /// </summary>
    public bool HasDetailContent
        => HasSelectedNotes
        || SelectedActionExamples.Count > 0
        || SelectedActionLinks.Count > 0;

    /// <summary>Resource key of the brush used for the favorite filter toggle.</summary>
    public string FavoritesFilterBrushKey
        => FavoritesFilterActive ? "WarningBrush" : "TextSecondaryBrush";

    /// <summary>Localized "X of Y results" counter shown under the filter bar.</summary>
    public string ResultCountText
    {
        get
        {
            var visible = _actionsView?.Cast<object>().Count() ?? _allEntries.Count;
            return FormatResultCount(visible);
        }
    }

    /// <summary>True when the "no results for current filters" placeholder should show.</summary>
    public bool IsNoResultsVisible
    {
        get
        {
            var visible = _actionsView?.Cast<object>().Count() ?? _allEntries.Count;
            return visible == 0 && HasActiveFilters;
        }
    }

    /// <summary>True when the "empty library" placeholder should show.</summary>
    public bool IsEmptyStateVisible => _allEntries.Count == 0 || !IsReady;

    /// <summary>The currently selected action, or null when none is selected.</summary>
    public ActionModel? SelectedAction => _selectedAction;

    /// <summary>The active template (Windows or Linux), or null when none is selected.</summary>
    public CommandTemplate? ActiveTemplate => _activeTemplate;

    /// <summary>
    /// True when a non-empty search term is currently filtering the action list.
    /// Used by the view to decide whether to apply ranked-relevance sort.
    /// </summary>
    public bool HasActiveSearch => _searchMatchIds is not null;

    /// <summary>
    /// The handler invoked when the user clicks "Send" — populated by the
    /// view from <see cref="Heimdall.Core.Models.ToolContext.SendCommandAction"/>.
    /// When null, the Send button is hidden.
    /// </summary>
    public Action<string>? SendCommandHandler
    {
        get => _sendCommandHandler;
        set
        {
            _sendCommandHandler = value;
            IsSendVisible = value is not null;
        }
    }
    private Action<string>? _sendCommandHandler;

    /// <summary>
    /// Callback installed by the view to write text to the system clipboard.
    /// Returns <c>true</c> when the write succeeded, <c>false</c> when the
    /// clipboard was unavailable. Keeps WPF references out of the VM so it
    /// remains testable.
    /// </summary>
    public Func<string, bool>? SetClipboardText { get; set; }

    /// <summary>
    /// Callback installed by the view to flash a transient "copied" animation
    /// on a named button ("copy" / "send" / "example" / "history"). Optional.
    /// </summary>
    public Action<string>? ShowCopyFeedback { get; set; }

    /// <summary>
    /// Async callback the view installs to display the Add/Edit dialog.
    /// Returns a populated dialog VM when the user saved, null otherwise.
    /// </summary>
    public Func<Dialogs.CommandActionDialogViewModel, Task<bool>>? ShowActionDialogAsync { get; set; }

    /// <summary>
    /// Callback the view installs to show a "Save File" dialog. Returns the
    /// chosen path or null when cancelled.
    /// </summary>
    public Func<string, string, string?>? ShowSaveFileDialog { get; set; }

    /// <summary>
    /// Callback the view installs to show an "Open File" dialog. Returns the
    /// chosen path or null when cancelled.
    /// </summary>
    public Func<string, string?>? ShowOpenFileDialog { get; set; }

    /// <summary>
    /// True when the view can be closed without losing in-flight work.
    /// Wired from <see cref="Heimdall.Core.Models.IToolView.CanClose"/>.
    /// </summary>
    public bool CanClose => !IsBusy && !IsSyncing;

    // ── Partial property-change hooks ─────────────────────────────

    /// <summary>Regenerates the command output whenever any parameter value changes.</summary>
    private void OnParameterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandLibraryParameterEntry.Value))
        {
            RegenerateCommand();
        }
    }

    partial void OnCategoryFilterIndexChanged(int value) => RefreshActionsView();
    partial void OnPlatformFilterIndexChanged(int value) => RefreshActionsView();
    partial void OnRiskFilterIndexChanged(int value) => RefreshActionsView();
    partial void OnFavoritesFilterActiveChanged(bool value) => RefreshActionsView();

    partial void OnSearchTextChanged(string value)
    {
        if (_loadingSelection) return;
        _ = ApplySearchAsync(value);
    }

    partial void OnSelectedEntryChanged(CommandLibraryActionEntry? value)
    {
        if (_loadingSelection) return;
        SelectAction(value);
    }

    partial void OnIsSyncingChanged(bool value)
    {
        CancelSyncCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanClose));
    }

    partial void OnUseWindowsTemplateChanged(bool value)
    {
        if (_loadingSelection) return;
        SelectTemplatePlatform(value);
    }

    partial void OnIsSelectedActionEditableChanged(bool value)
        => OnPropertyChanged(nameof(CanEditSelected));

    /// <summary>
    /// Refreshes <see cref="ActionsView"/> and pushes derived visibility flags.
    /// Called whenever a filter index or the favorites toggle changes.
    /// </summary>
    public void RefreshActionsView()
    {
        _actionsView?.Refresh();
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(IsNoResultsVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }

    // ── Initialization ────────────────────────────────────────────

    /// <summary>
    /// Loads the command library from the database, refreshes the categories,
    /// and prepares the long-lived session scope. Safe to call repeatedly:
    /// the existing scope is disposed first.
    /// </summary>
    /// <param name="targetHost">
    /// Optional host string from the originating server tab; used to prefill
    /// host-typed parameters when a template is selected.
    /// </param>
    public async Task InitializeAsync(string? targetHost)
    {
        _targetHost = string.IsNullOrWhiteSpace(targetHost) ? null : targetHost.Trim();
        EmptyStateMessage = LocalizeKey("ToolCmdLibStatusLoading");
        HelpContentText = LocalizeKey("ToolHelpCMDLIB").Replace("\\n", "\n");

        PopulateStaticFilterItems(autoSelectPlatform: true);

        try
        {
            _sessionScope?.Dispose();
            _sessionScope = _serviceProvider.CreateScope();
            var sp = _sessionScope.ServiceProvider;
            _searchService = sp.GetRequiredService<ISearchService>();
            _commandGenerator = sp.GetRequiredService<ICommandGeneratorService>();

            var actionService = sp.GetRequiredService<IActionService>();
            var favoritesService = sp.GetRequiredService<IFavoritesService>();

            var favorites = await favoritesService.GetAllFavoritesAsync();
            _favoriteIds = new HashSet<string>(
                favorites.Select(f => f.ActionId), StringComparer.Ordinal);

            var actions = (await actionService.GetAllActionsAsync()).ToList();

            ResetParameterSubscriptions();
            _allEntries.Clear();
            foreach (var action in actions)
            {
                _allEntries.Add(new CommandLibraryActionEntry(action, this));
            }

            _categoryList = actions
                .Select(a => a.Category)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            PopulateCategoryFilterItems();

            if (_actionsView is null)
            {
                _actionsView = new ListCollectionView(_allEntries)
                {
                    Filter = obj => obj is CommandLibraryActionEntry e && ShouldShowAction(e)
                };
                ConfigureDefaultGrouping();
                OnPropertyChanged(nameof(ActionsView));
            }
            else
            {
                _actionsView.Refresh();
            }

            EmptyStateMessage = _allEntries.Count == 0
                ? LocalizeKey("ToolCmdLibEmptyLibrary")
                : string.Empty;

            IsReady = true;
            LibraryReloaded?.Invoke();
            OnPropertyChanged(nameof(ResultCountText));
            OnPropertyChanged(nameof(IsNoResultsVisible));
            OnPropertyChanged(nameof(IsEmptyStateVisible));
        }
        catch (Exception ex)
        {
            var fmt = LocalizeKey("ToolCmdLibStatusError");
            EmptyStateMessage = fmt.Contains("{0}", StringComparison.Ordinal)
                ? string.Format(fmt, ex.Message)
                : ex.Message;
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Failed to load actions: {ex.Message}");
        }
    }

    /// <summary>
    /// Populates the platform/risk filter combo items from current locale
    /// strings and optionally auto-selects a platform based on the originating
    /// connection type. Called on first init — combos are not rebuilt on
    /// subsequent reloads because the preserved index avoids a re-select
    /// flicker.
    /// </summary>
    private void PopulateStaticFilterItems(bool autoSelectPlatform)
    {
        PlatformFilterItems.Clear();
        PlatformFilterItems.Add(LocalizeKey("ToolCmdLibFilterPlatformAll"));
        PlatformFilterItems.Add(LocalizeKey("ToolCmdLibPlatformWindows"));
        PlatformFilterItems.Add(LocalizeKey("ToolCmdLibPlatformLinux"));

        RiskFilterItems.Clear();
        RiskFilterItems.Add(LocalizeKey("ToolCmdLibFilterRiskAll"));
        RiskFilterItems.Add(LocalizeKey("ToolCmdLibRiskInfo"));
        RiskFilterItems.Add(LocalizeKey("ToolCmdLibRiskRun"));
        RiskFilterItems.Add(LocalizeKey("ToolCmdLibRiskDangerous"));
    }

    /// <summary>
    /// Rebuilds the category combo items after a library reload, preserving
    /// the caller-chosen index when possible.
    /// </summary>
    private void PopulateCategoryFilterItems()
    {
        var prev = CategoryFilterIndex;
        CategoryFilterItems.Clear();
        CategoryFilterItems.Add(LocalizeKey("ToolCmdLibFilterCategoryAll"));
        foreach (var cat in _categoryList)
        {
            CategoryFilterItems.Add(cat);
        }
        var max = CategoryFilterItems.Count - 1;
        CategoryFilterIndex = prev < 0 ? 0 : Math.Min(prev, max);
    }

    /// <summary>
    /// Applies the default category grouping to <see cref="ActionsView"/>
    /// and clears any search-rank sort descriptions.
    /// </summary>
    private void ConfigureDefaultGrouping()
    {
        if (_actionsView is null) return;
        _actionsView.SortDescriptions.Clear();
        _actionsView.GroupDescriptions.Clear();
        _actionsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(CommandLibraryActionEntry.Category)));
    }

    /// <summary>
    /// Pre-seeds the platform filter index using the originating tool context's
    /// connection type (SSH → Linux, RDP/LOCAL → Windows, otherwise All).
    /// The view calls this once after <see cref="InitializeAsync"/>.
    /// </summary>
    public void AutoSelectPlatform(string? connectionType)
    {
        PlatformFilterIndex = connectionType?.ToUpperInvariant() switch
        {
            "SSH" => 2,
            "LOCAL" => 1,
            "RDP" => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Reloads the entire library (used after CRUD and Git sync). Preserves
    /// the current filter state by re-applying the search after the reload.
    /// </summary>
    public async Task ReloadAsync()
    {
        var preservedSearch = SearchText;
        ResetSelection();
        ResetSearchState();
        await InitializeAsync(_targetHost);
        await ApplySearchAsync(preservedSearch);
    }

    // ── Selection ─────────────────────────────────────────────────

    /// <summary>
    /// Updates VM state for a new action selection. Pass null to clear the
    /// selection and hide the generator panel.
    /// </summary>
    /// <returns>
    /// True if a generator was successfully prepared, false if the selection
    /// was cleared or no template was available.
    /// </returns>
    public bool SelectAction(CommandLibraryActionEntry? entry)
    {
        if (entry is null)
        {
            ResetSelection();
            return false;
        }

        _loadingSelection = true;
        try
        {
            _selectedAction = entry.Source;
            IsSelectedActionEditable = entry.Source.IsUserCreated;

            var hasWin = _selectedAction.WindowsCommandTemplate is not null;
            var hasLinux = _selectedAction.LinuxCommandTemplate is not null;
            HasMultipleTemplates = hasWin && hasLinux;

            if (hasWin && hasLinux)
            {
                var preferWindows = PlatformFilterIndex != 2;
                UseWindowsTemplate = preferWindows;
                _activeTemplate = preferWindows
                    ? _selectedAction.WindowsCommandTemplate
                    : _selectedAction.LinuxCommandTemplate;
            }
            else
            {
                UseWindowsTemplate = hasWin;
                _activeTemplate = hasWin
                    ? _selectedAction.WindowsCommandTemplate
                    : _selectedAction.LinuxCommandTemplate;
            }

            if (_activeTemplate is null)
            {
                ResetSelection();
                return false;
            }

            ApplyActiveTemplate();
            IsHistoryVisible = false;
            IsGeneratorVisible = true;
        }
        finally
        {
            _loadingSelection = false;
        }
        return true;
    }

    /// <summary>
    /// Switches between Windows and Linux templates for the current selection.
    /// No-op when only one template is available.
    /// </summary>
    public void SelectTemplatePlatform(bool useWindows)
    {
        if (_selectedAction is null || !HasMultipleTemplates) return;

        _loadingSelection = true;
        try
        {
            UseWindowsTemplate = useWindows;
            _activeTemplate = useWindows
                ? _selectedAction.WindowsCommandTemplate
                : _selectedAction.LinuxCommandTemplate;

            if (_activeTemplate is not null)
            {
                ApplyActiveTemplate();
            }
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    /// <summary>
    /// Recomputes <see cref="GeneratedCommand"/> from the current parameter
    /// values. Called by the view's <c>OnParameterChanged</c> event handler
    /// in Phase A; will become automatic once parameter entries gain
    /// observable <c>Value</c> properties in Phase B.
    /// </summary>
    public void RegenerateCommand()
    {
        if (_activeTemplate is null || _commandGenerator is null) return;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in _parameters)
        {
            values[p.Name] = p.Value;
        }

        try
        {
            var valid = _commandGenerator.ValidateParameters(_activeTemplate, values, out var errors);
            if (valid && errors.Count == 0)
            {
                GeneratedCommand = _commandGenerator.GenerateCommand(_activeTemplate, values);
                ValidationError = string.Empty;
                IsCommandValid = true;
            }
            else
            {
                GeneratedCommand = _activeTemplate.CommandPattern;
                ValidationError = string.Join(
                    Environment.NewLine,
                    errors.Select(static error => $"- {error}"));
                IsCommandValid = false;
            }
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Generate failed: {ex.Message}");
            GeneratedCommand = _activeTemplate.CommandPattern;
            ValidationError = string.Format(LocalizeKey("ToolCmdLibGenerateError"), ex.Message);
            IsCommandValid = false;
        }
    }

    /// <summary>
    /// Overwrites the generator output with a literal example command, marks
    /// it valid, and clears any prior validation error. Called by the
    /// <see cref="ApplyExample"/> relay command when the user clicks an
    /// example row in the detail panel.
    /// </summary>
    public void ApplyExampleText(string command)
    {
        GeneratedCommand = command;
        ValidationError = string.Empty;
        IsCommandValid = true;
    }

    private void ApplyActiveTemplate()
    {
        if (_activeTemplate is null) return;

        ActiveTemplateName = _activeTemplate.Name;

        ResetParameterSubscriptions();
        _parameters.Clear();
        foreach (var p in _activeTemplate.Parameters)
        {
            var entry = new CommandLibraryParameterEntry
            {
                Name = p.Name,
                Label = p.Label,
                Value = GetInitialParameterValue(p),
                Required = p.Required,
                Description = p.Description,
                Type = p.Type ?? "string"
            };
            entry.PropertyChanged += OnParameterValueChanged;
            _parameters.Add(entry);
        }

        RefreshDetailPanels();
        RegenerateCommand();
    }

    /// <summary>
    /// Rebuilds the observable notes / examples / links collections for the
    /// current selection so the detail panel reflects the active template.
    /// </summary>
    private void RefreshDetailPanels()
    {
        var action = _selectedAction;
        var template = _activeTemplate;

        SelectedActionNotes = action is null || string.IsNullOrWhiteSpace(action.Notes)
            ? string.Empty
            : action.Notes!.Trim();

        SelectedActionExamples.Clear();
        if (action is not null)
        {
            var examples = (template?.Platform == TwinShell.Core.Enums.Platform.Windows
                ? action.WindowsExamples
                : action.LinuxExamples) ?? [];
            if (examples.Count == 0)
            {
                examples = action.Examples ?? [];
            }
            foreach (var ex in examples)
            {
                SelectedActionExamples.Add(ex);
            }
        }

        SelectedActionLinks.Clear();
        if (action is not null)
        {
            foreach (var link in action.Links ?? [])
            {
                SelectedActionLinks.Add(link);
            }
        }

        OnPropertyChanged(nameof(HasDetailContent));
    }

    private void ResetParameterSubscriptions()
    {
        foreach (var p in _parameters)
        {
            p.PropertyChanged -= OnParameterValueChanged;
        }
    }

    private void ResetSelection()
    {
        ResetParameterSubscriptions();
        _selectedAction = null;
        _activeTemplate = null;
        _parameters.Clear();
        IsGeneratorVisible = false;
        HasMultipleTemplates = false;
        IsSelectedActionEditable = false;
        ActiveTemplateName = string.Empty;
        GeneratedCommand = string.Empty;
        ValidationError = string.Empty;
        IsCommandValid = false;

        SelectedActionNotes = string.Empty;
        SelectedActionExamples.Clear();
        SelectedActionLinks.Clear();
        OnPropertyChanged(nameof(HasDetailContent));
    }

    private string GetInitialParameterValue(TemplateParameter parameter)
    {
        if (!string.IsNullOrWhiteSpace(_targetHost) && CanPrefillTargetHost(parameter, _targetHost!))
        {
            return _targetHost!;
        }
        return parameter.DefaultValue ?? string.Empty;
    }

    private static bool CanPrefillTargetHost(TemplateParameter parameter, string targetHost)
    {
        if (string.Equals(parameter.Type, "ipaddress", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.TryParse(targetHost, out _);
        }
        return IsHostParameter(parameter.Name, parameter.Type);
    }

    private static bool IsHostParameter(string name, string? type)
    {
        if (string.Equals(type, "hostname", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "ipaddress", StringComparison.OrdinalIgnoreCase))
            return true;

        var n = name.ToLowerInvariant();
        return n is "host" or "hostname" or "target" or "targethost"
            or "computername" or "server" or "ip" or "ipaddress"
            or "remotehost" or "remotecomputer";
    }

    // ── Helpers exposed to the entry display models ───────────────

    /// <summary>True when the given action is currently marked as a favorite.</summary>
    public bool IsFavorite(string actionId) => _favoriteIds.Contains(actionId);

    /// <summary>
    /// Search rank for the given action ID (lower = more relevant), or
    /// <see cref="int.MaxValue"/> when no search is active.
    /// </summary>
    public int GetSearchRank(string actionId)
    {
        if (_searchRankedIds is null) return int.MaxValue;
        var idx = _searchRankedIds.IndexOf(actionId);
        return idx >= 0 ? idx : int.MaxValue;
    }

    /// <summary>Resolves a localization key against the injected localizer.</summary>
    public string LocalizeKey(string key) => _localizer[key];

    // ── Disposal ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        ResetParameterSubscriptions();

        if (_historyCopyTimer is not null)
        {
            _historyCopyTimer.Stop();
            _historyCopyTimer = null;
        }

        _sessionScope?.Dispose();
        _sessionScope = null;
        _searchService = null;
        _commandGenerator = null;
    }
}
