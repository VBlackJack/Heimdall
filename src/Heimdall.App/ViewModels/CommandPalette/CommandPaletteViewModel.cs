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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Extensions;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Session;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// View-model backing the Ctrl+K Command Palette popup in
/// <c>MainWindow</c>: fuzzy search across servers + tools + external tools,
/// ad-hoc SSH/RDP parsing, and dispatch of the selected item to either a
/// normal connection, a tool tab, or a split-session merge.
/// </summary>
/// <remarks>
/// <para>
/// Composition: instantiated inside <see cref="MainViewModel"/>'s
/// constructor (<see cref="MainViewModel.CommandPalette"/>) - there is no
/// DI registration. Matches the sidebar / tools-tab / onboarding sub-VM
/// pattern established in earlier phases.
/// </para>
/// <para>
/// Split palette state (<see cref="SessionTabViewModel"/> being split,
/// orientation, optional pane id) is held in private fields that are reset
/// by <see cref="Open"/>, <see cref="Close"/> and
/// <see cref="ExecuteSelection"/> to avoid stale captures when the popup
/// closes.
/// </para>
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly LocalizationManager _localizer;
    private readonly Heimdall.App.Services.IDialogService _dialogService;
    private readonly ToolRegistry _toolRegistry;
    private readonly IConfigManager _configManager;
    private readonly IEmbeddedSessionManager _embeddedSessionManager;
    private readonly ExternalToolLaunchService _externalToolLaunchService;
    private readonly IRecentConnectionTracker _recentConnections;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// In-memory snapshot of the TwinShell action library used by the fuzzy
    /// ranker. Refreshed every time the palette opens so newly-imported or
    /// Git-synced snippets surface without an app restart.
    /// </summary>
    private IReadOnlyList<ActionModel> _cachedSnippets = Array.Empty<ActionModel>();

    /// <summary>Read-only accessor used by the search partial.</summary>
    internal IReadOnlyList<ActionModel> Snippets => _cachedSnippets;

    /// <summary>
    /// Callback installed by the WPF host to write text to the system clipboard.
    /// </summary>
    public Func<string, bool>? SetClipboardText { get; set; }

    /// <summary>
    /// When non-null, the palette is in "split mode": selecting a server
    /// will split the captured session instead of opening a new tab.
    /// </summary>
    private SessionTabViewModel? _splitPaletteSession;
    private SplitOrientation _splitPaletteOrientation;
    private string? _splitPalettePaneId;

    /// <summary>
    /// Creates a new Command Palette VM bound to the given host.
    /// </summary>
    public CommandPaletteViewModel(
        MainViewModel main,
        LocalizationManager localizer,
        Heimdall.App.Services.IDialogService dialogService,
        ToolRegistry toolRegistry,
        IConfigManager configManager,
        IEmbeddedSessionManager embeddedSessionManager,
        ExternalToolLaunchService externalToolLaunchService,
        IRecentConnectionTracker recentConnections,
        IServiceScopeFactory scopeFactory)
    {
        _main = main;
        _localizer = localizer;
        _dialogService = dialogService;
        _toolRegistry = toolRegistry;
        _configManager = configManager;
        _embeddedSessionManager = embeddedSessionManager;
        _externalToolLaunchService = externalToolLaunchService;
        _recentConnections = recentConnections;
        _scopeFactory = scopeFactory;
    }

    // ── Observable state ─────────────────────────────────────────────

    /// <summary>Localized placeholder text shown inside the search TextBox.</summary>
    [ObservableProperty]
    private string _placeholder = string.Empty;

    /// <summary>True while the palette popup is currently open.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Current search text (two-way bound to the palette TextBox).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Result set shown in the palette ListBox.</summary>
    [ObservableProperty]
    private ObservableCollection<ServerItemViewModel> _results = new();

    /// <summary>Currently selected palette result (driven by arrow keys / click).</summary>
    [ObservableProperty]
    private ServerItemViewModel? _selectedItem;

    /// <summary>
    /// True when the palette is in "split mode" (was opened via
    /// <see cref="OpenSplit"/> rather than <see cref="Open"/>).
    /// </summary>
    public bool IsInSplitMode => _splitPaletteSession is not null;

    /// <summary>True when the current result set is empty.</summary>
    public bool HasNoResults => Results.Count == 0;

    /// <summary>
    /// Generated partial: refreshes the <see cref="HasNoResults"/>
    /// notification whenever the result collection is replaced.
    /// </summary>
    partial void OnResultsChanged(ObservableCollection<ServerItemViewModel> value)
    {
        OnPropertyChanged(nameof(HasNoResults));
    }

    // ── Open / Close / OpenSplit ─────────────────────────────────────

    /// <summary>
    /// Opens the palette in normal mode (Ctrl+K or Quick Connect button).
    /// Clears any previous split mode state.
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        _splitPaletteSession = null;
        CloseSnippetDetail();
        Placeholder = _localizer["PaletteSearchPlaceholder"];
        SearchText = string.Empty;
        IsOpen = true;
        OnSearchTextChanged(string.Empty);
        SelectedItem = Results.FirstOrDefault();
        _ = RefreshSnippetCacheAsync();
    }

    /// <summary>
    /// Opens the palette in "split mode": selecting a server will split
    /// the given session instead of opening a new tab. Replaces the legacy
    /// context-menu picker that did not scale past ~20 servers.
    /// </summary>
    public void OpenSplit(SessionTabViewModel session, SplitOrientation orientation, string? paneId = null)
    {
        CloseSnippetDetail();
        _splitPaletteSession = session;
        _splitPaletteOrientation = orientation;
        _splitPalettePaneId = paneId;
        Placeholder = _localizer["SplitPaletteHint"];
        SearchText = string.Empty;
        IsOpen = true;
        OnSearchTextChanged(string.Empty);
        SelectedItem = Results.FirstOrDefault();
        _ = RefreshSnippetCacheAsync();
    }

    /// <summary>
    /// Loads the latest action snapshot from the TwinShell repository into
    /// the in-memory cache. Runs as fire-and-forget on every palette open so
    /// the search ranker always sees fresh data without blocking the popup.
    /// Failures (database not initialised, scope disposed) are logged and
    /// leave the previous cache untouched.
    /// </summary>
    private async Task RefreshSnippetCacheAsync()
    {
        try
        {
            List<ActionModel> actions;
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                var actionService = scope.ServiceProvider.GetRequiredService<IActionService>();
                actions = (await actionService.GetAllActionsAsync()).ToList();
            }
            _cachedSnippets = actions;

            // Awaited without ConfigureAwait(false) so this continuation resumes
            // on the UI thread (Open/OpenSplit run there). Re-rank once fresh data
            // lands so a query typed before the cache populated surfaces snippets
            // without requiring another keystroke.
            if (IsOpen && !IsSnippetDetailOpen && !string.IsNullOrWhiteSpace(SearchText))
            {
                OnSearchTextChanged(SearchText);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Palette snippet cache refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the most useful copy-paste payload for the given action:
    /// Windows template > Linux template > first example > action title.
    /// </summary>
    internal static string ResolveSnippetCommand(ActionModel action)
    {
        var windowsTemplate = action.WindowsCommandTemplate?.CommandPattern;
        if (!string.IsNullOrWhiteSpace(windowsTemplate)) return windowsTemplate;

        var linuxTemplate = action.LinuxCommandTemplate?.CommandPattern;
        if (!string.IsNullOrWhiteSpace(linuxTemplate)) return linuxTemplate;

        var firstExample = action.Examples.FirstOrDefault()?.Command;
        if (!string.IsNullOrWhiteSpace(firstExample)) return firstExample;

        return action.Title;
    }

    /// <summary>
    /// Copies the snippet's command to the clipboard and surfaces a status
    /// message so the user gets feedback without a modal. Called from every
    /// dispatch path (Enter, Ctrl+Enter, double-click) when the selected
    /// item Id starts with <c>"snippet-"</c>.
    /// </summary>
    private void HandleSnippetSelection(ServerItemViewModel item)
    {
        var publicId = item.Id["snippet-".Length..];
        var action = _cachedSnippets.FirstOrDefault(
            a => string.Equals(a.PublicId.ToString("N"), publicId, StringComparison.OrdinalIgnoreCase));
        if (action is null) return;

        OpenSnippetDetail(action);
    }

    /// <summary>Closes the palette and clears any split mode state.</summary>
    [RelayCommand]
    private void Close()
    {
        _splitPaletteSession = null;
        CloseSnippetDetail();
        IsOpen = false;
    }

    // ── Selection dispatch ───────────────────────────────────────────

    /// <summary>
    /// Synchronous entry point for palette item selection (used by mouse
    /// double-click handler). Captures split state immediately to avoid
    /// race conditions with popup deactivation that would otherwise null
    /// out <c>_splitPaletteSession</c> before the async command runs.
    /// </summary>
    public void ExecuteSelection(ServerItemViewModel item)
    {
        // Snippets open an in-palette detail view - keep the palette open.
        if (item.Id.StartsWith("snippet-", StringComparison.Ordinal))
        {
            HandleSnippetSelection(item);
            return;
        }

        var splitSession = _splitPaletteSession;
        var splitOrientation = _splitPaletteOrientation;
        var splitPaneId = _splitPalettePaneId;
        _splitPaletteSession = null;
        _splitPalettePaneId = null;
        IsOpen = false;

        if (splitSession is not null)
        {
            // Check if this is an active session merge (prefix "session-")
            if (item.Id.StartsWith("session-", StringComparison.Ordinal))
            {
                var sourceSessionId = item.Id["session-".Length..];
                _main.MergeExistingSession(splitSession, sourceSessionId, splitOrientation, splitPaneId);
                return;
            }

            // Built-in tool selected in split mode - dock tool in split pane
            if (item.Id.StartsWith("tool-", StringComparison.Ordinal))
            {
                SplitSessionWithTool(splitSession, item.Id["tool-".Length..], splitOrientation, splitPaneId);
                return;
            }

            if (!item.IsTypedDestination)
            {
                _main.SplitSessionWithServerAsync(splitSession, item.Id, splitOrientation, splitPaneId)
                    .SafeFireAndForget();
                return;
            }
        }

        // Fall through to normal palette behavior
        ConnectInternalAsync(item).SafeFireAndForget();
    }

    /// <summary>
    /// Async command fired when the user presses Enter on the palette.
    /// Handles split mode dispatch first, then falls through to the normal
    /// connection / tool / ad-hoc routing.
    /// </summary>
    [RelayCommand]
    private async Task ConnectFromPaletteAsync(ServerItemViewModel? server)
    {
        if (server is null) return;

        // Snippets open an in-palette detail view - keep the palette open.
        if (server.Id.StartsWith("snippet-", StringComparison.Ordinal))
        {
            HandleSnippetSelection(server);
            return;
        }

        var splitSession = _splitPaletteSession;
        var splitOrientation = _splitPaletteOrientation;
        var splitPaneId = _splitPalettePaneId;
        _splitPaletteSession = null;
        _splitPalettePaneId = null;
        IsOpen = false;

        // If the palette was opened in split mode, route to split logic
        if (splitSession is not null && !server.IsTypedDestination)
        {
            // Check if this is an active session merge
            if (server.Id.StartsWith("session-", StringComparison.Ordinal))
            {
                var sourceSessionId = server.Id["session-".Length..];
                _main.MergeExistingSession(splitSession, sourceSessionId, splitOrientation, splitPaneId);
                return;
            }

            // Built-in tool selected in split mode
            if (server.Id.StartsWith("tool-", StringComparison.Ordinal))
            {
                SplitSessionWithTool(splitSession, server.Id["tool-".Length..], splitOrientation, splitPaneId);
                return;
            }

            await _main.SplitSessionWithServerAsync(splitSession, server.Id, splitOrientation, splitPaneId);
            return;
        }

        if (server.Id.StartsWith("tool-", StringComparison.Ordinal))
        {
            await OpenToolFromPaletteAsync(server);
        }
        else if (server.Id.StartsWith("ext-tool-", StringComparison.Ordinal))
        {
            LaunchExternalToolFromPalette(server);
        }
        else if (server.Id.StartsWith("snippet-", StringComparison.Ordinal))
        {
            HandleSnippetSelection(server);
        }
        else if (server.IsTypedDestination)
        {
            await ConnectAdHocAsync(server);
        }
        else
        {
            _main.ServerList.ConnectCommand.Execute(server);
        }
    }

    /// <summary>
    /// Normal (non-split) palette action - extracted so
    /// <see cref="ExecuteSelection"/> can call it after the split check
    /// without duplicating the routing logic.
    /// </summary>
    private async Task ConnectInternalAsync(ServerItemViewModel server)
    {
        if (server.Id.StartsWith("tool-", StringComparison.Ordinal))
        {
            await OpenToolFromPaletteAsync(server);
        }
        else if (server.Id.StartsWith("ext-tool-", StringComparison.Ordinal))
        {
            LaunchExternalToolFromPalette(server);
        }
        else if (server.Id.StartsWith("snippet-", StringComparison.Ordinal))
        {
            HandleSnippetSelection(server);
        }
        else if (server.IsTypedDestination)
        {
            await ConnectAdHocAsync(server);
        }
        else
        {
            _main.ServerList.ConnectCommand.Execute(server);
        }
    }

    /// <summary>
    /// Async command fired when the user presses Ctrl+Enter on the palette:
    /// always splits with the currently-active session (or falls back to a
    /// normal connection when no active session exists).
    /// </summary>
    [RelayCommand]
    private async Task ConnectSplitFromPaletteAsync(ServerItemViewModel? server)
    {
        if (server is null) return;

        // Snippets open an in-palette detail view - keep the palette open.
        if (server.Id.StartsWith("snippet-", StringComparison.Ordinal))
        {
            HandleSnippetSelection(server);
            return;
        }

        IsOpen = false;

        if (server.Id.StartsWith("tool-", StringComparison.Ordinal))
        {
            var activeForTool = _main.Connection.ActiveSession;
            if (activeForTool is not null)
            {
                SplitSessionWithTool(activeForTool, server.Id["tool-".Length..], SplitOrientation.Vertical);
                return;
            }

            await OpenToolFromPaletteAsync(server);
            return;
        }

        if (server.Id.StartsWith("ext-tool-", StringComparison.Ordinal))
        {
            LaunchExternalToolFromPalette(server);
            return;
        }

        var activeSession = _main.Connection.ActiveSession;
        if (activeSession is not null && !server.IsTypedDestination)
        {
            await _main.SplitSessionWithServerAsync(activeSession, server.Id, SplitOrientation.Vertical);
        }
        else if (server.IsTypedDestination)
        {
            await ConnectAdHocAsync(server);
        }
        else
        {
            _main.ServerList.ConnectCommand.Execute(server);
        }
    }

    // ── Connection helpers ───────────────────────────────────────────

    /// <summary>
    /// Connects a destination typed by hand (SSH or RDP) by building a temporary DTO
    /// from the palette item and calling the shared ConnectionService
    /// directly. Supports <c>user@host:port</c> style input for SSH.
    /// </summary>
    /// <remarks>
    /// Reached only through <see cref="ServerItemViewModel.IsTypedDestination"/>, never by
    /// the identifier's text: a saved profile whose identifier sits in the quick-connect
    /// namespace is dialled by the server list with its full profile, not rebuilt here as a
    /// bare one.
    /// </remarks>
    private async Task ConnectAdHocAsync(ServerItemViewModel server)
    {
        var connType = server.ConnectionType?.ToUpperInvariant() ?? "SSH";

        var dto = new ServerProfileDto
        {
            Id = server.Id,
            DisplayName = server.DisplayName,
            RemoteServer = server.RemoteServer ?? "",
            ConnectionType = connType,
        };

        if (connType == "SSH")
        {
            dto.SshPort = DefaultPorts.Ssh;
            dto.SshUsername = server.DisplayName.Contains('@')
                ? server.DisplayName.Split('@')[0]
                : "";

            // Parse port from display name if present (user@host:port)
            var parts = server.DisplayName.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var port))
            {
                dto.SshPort = port;
                dto.RemoteServer = parts[0].Contains('@')
                    ? parts[0].Split('@')[1]
                    : parts[0];
            }
        }
        else if (connType == "RDP")
        {
            dto.RemotePort = DefaultPorts.Rdp;
        }

        var settings = await _configManager.LoadSettingsAsync();
        ConnectionResult result;

        if (connType == "RDP")
        {
            result = await _main.ServerList.ConnectionService.ConnectRdpAsync(dto, settings);
        }
        else
        {
            result = await _main.ServerList.ConnectionService.ConnectSshAsync(dto, settings);
        }

        if (result.Success && result.Session is not null)
        {
            var tab = _main.Connection.AddSession(
                dto.Id,
                dto.DisplayName,
                connType,
                settings.MaxEmbeddedSessions);
            if (tab is null)
            {
                SessionCoordinator.SafeDisposeSessionResult(result.Session);
                return;
            }

            tab.MarkAsAdHoc(dto);

            tab.HostControl = _embeddedSessionManager.CreateHostControl(
                tab, dto.DisplayName, connType, result.Session, settings);
            if (tab.HostControl is EmbeddedRdpView rdpView)
            {
                rdpView.SetOwningPane(tab.PrimaryPane);
            }

            tab.Status = SessionStatusTokens.Connected;
            _main.StatusText = _localizer.Format("StatusConnected",
                !string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.DisplayName : dto.RemoteServer);
        }
        else if (result.Success)
        {
            // External mode: process launched; keep a lightweight tab so ad-hoc
            // connections can still be persisted from the tab context menu.
            var tab = _main.Connection.AddSession(dto.Id, dto.DisplayName, connType);
            tab.MarkAsAdHoc(dto);
            tab.Status = SessionStatusTokens.LaunchedExternalClient;

            _main.StatusText = _localizer["StatusLaunchedExternalClient"];
        }
        else
        {
            _main.StatusText = result.ErrorMessage ?? _localizer["ErrorConnectionFailed"];
        }
    }

    /// <summary>
    /// Opens a tool tab from a palette item whose Id starts with
    /// <c>"tool-"</c>. Extracts the tool ID and optional argument from the
    /// encoded Id, tracks the tool as recently used, and opens it via
    /// <see cref="MainViewModel.OpenToolTabAsync"/>.
    /// </summary>
    private async Task OpenToolFromPaletteAsync(ServerItemViewModel item)
    {
        // Id format: "tool-<toolid>|<argument>"
        var payload = item.Id["tool-".Length..];
        var pipeIndex = payload.IndexOf('|');
        var toolId = pipeIndex >= 0 ? payload[..pipeIndex] : payload;
        var argument = pipeIndex >= 0 ? payload[(pipeIndex + 1)..] : null;

        var context = !string.IsNullOrEmpty(argument) ? new ToolContext(Argument: argument) : null;
        _main.TrackRecentTool(toolId.ToUpperInvariant());
        await _main.OpenToolTabAsync(toolId, item.DisplayName, context);
    }

    /// <summary>
    /// Launches an external tool from a palette item whose Id starts with
    /// <c>"ext-tool-"</c>. Matches the tool name back to the configured
    /// <see cref="ExternalToolDefinition"/> and starts the process. When a
    /// server is selected in the tree, placeholders are resolved against it.
    /// </summary>
    private void LaunchExternalToolFromPalette(ServerItemViewModel item)
    {
        var toolName = item.Id["ext-tool-".Length..];
        var extTool = _main.CurrentSettings?.ExternalTools
            .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));

        if (extTool is null) return;

        _externalToolLaunchService.LaunchConfigured(extTool, _main.ServerList.SelectedServer, _main.Localize);
    }

    /// <summary>
    /// Splits the given session with a built-in tool pane and tracks the
    /// tool as recently used. Delegates the actual split to
    /// <see cref="SplitService.SplitSessionWithTool"/>.
    /// </summary>
    private void SplitSessionWithTool(
        SessionTabViewModel session,
        string paletteToolPayload,
        SplitOrientation orientation,
        string? paneId = null)
    {
        _main.Split.SplitSessionWithTool(session, paletteToolPayload, orientation, paneId);
        // Parse tool ID for recent tracking
        var pipeIndex = paletteToolPayload.IndexOf('|');
        var toolId = pipeIndex >= 0 ? paletteToolPayload[..pipeIndex] : paletteToolPayload;
        _main.TrackRecentTool(toolId.ToUpperInvariant());
    }

}
