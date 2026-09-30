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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Builds WPF context menus for the session TreeView (server, folder, tool,
/// empty area). Extracted from <c>MainWindow.xaml.cs</c> to reduce code-behind
/// size and enable targeted unit testing of the menu-building logic.
/// </summary>
public sealed class ContextMenuFactory
{
    private readonly ExternalToolProviderService _externalToolProvider;

    /// <summary>
    /// Initialises a new <see cref="ContextMenuFactory"/>.
    /// </summary>
    /// <param name="externalToolProvider">
    /// Service that exposes the list of auto-detected third-party tools
    /// (Sysinternals, NirSoft, ...) used to build the "Detected Tools" submenu.
    /// </param>
    public ContextMenuFactory(ExternalToolProviderService externalToolProvider)
    {
        _externalToolProvider = externalToolProvider;
    }

    /// <summary>
    /// Builds the context menu for a TreeView node by branching on the target
    /// type. Returns an empty-area menu when <paramref name="target"/> is
    /// <c>null</c>.
    /// </summary>
    public ContextMenu CreateTreeContextMenu(
        object? target,
        MainViewModel vm,
        IContextMenuCallbacks callbacks)
    {
        return target switch
        {
            BulkSelectionContext bulk => CreateBulkSelectionContextMenu(vm, bulk),
            ServerItemViewModel server when ConnectionTypeCatalog.IsToolConnectionType(server.ConnectionType)
                => CreateToolContextMenu(vm, server, callbacks),
            ServerItemViewModel server => CreateServerContextMenu(vm, server, callbacks),
            FolderViewModel folder => CreateFolderContextMenu(vm, folder, callbacks),
            _ => CreateEmptyAreaContextMenu(vm, callbacks)
        };
    }

    /// <summary>
    /// Builds the right-click context menu for a server node.
    /// </summary>
    private ContextMenu CreateServerContextMenu(
        MainViewModel vm,
        ServerItemViewModel server,
        IContextMenuCallbacks callbacks)
    {
        var menu = CreateContextMenu();

        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxConnect"),
            vm.ServerList.ConnectCommand,
            server));
        if (IsRdpServer(server))
        {
            menu.Items.Add(CreateConnectWithMenu(vm, server));
        }
        menu.Items.Add(CreateConnectAsMenu(vm, server));
        menu.Items.Add(CreateOpenInSplitMenu(vm, server));
        var renameItem = new MenuItem
        {
            Header = vm.Localize("TreeCtxRename"),
            InputGestureText = vm.Localize("TreeCtxGestureRename")
        };
        renameItem.Click += (_, _) => callbacks.BeginInlineRename(server);
        menu.Items.Add(renameItem);
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxEdit"),
            vm.ServerList.EditServerCommand,
            server,
            inputGestureText: vm.Localize("TreeCtxGestureEdit")));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxDuplicate"),
            vm.ServerList.DuplicateServerCommand,
            server));
        menu.Items.Add(CreateFavoriteMenuItem(vm, [server]));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMoveToGroupMenu(vm, server));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxCopyHostname"),
            vm.ServerList.CopyHostnameCommand,
            server));
        bool hasUsername = !string.IsNullOrWhiteSpace(server.Username);
        MenuItem copyUsername = CreateMenuItem(
            vm.Localize("TreeCtxCopyUsername"),
            vm.ServerList.CopyUsernameCommand,
            server,
            hasUsername);
        if (!hasUsername)
        {
            ExplainDisabled(copyUsername, vm.Localize("TreeCtxCopyUsernameDisabledReason"));
        }

        menu.Items.Add(copyUsername);
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxCopyAddress"),
            vm.ServerList.CopyAddressCommand,
            server));
        if (string.Equals(server.ConnectionType, "SSH", StringComparison.OrdinalIgnoreCase))
        {
            menu.Items.Add(CreateMenuItem(
                vm.Localize("TreeCtxCopySshCommand"),
                vm.ServerList.CopySshCommandCommand,
                server));
        }
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxTestReachability"),
            vm.ServerList.TestReachabilityCommand,
            server));

        // Wake-on-LAN (only shown when MAC address is configured)
        if (Core.Security.WakeOnLan.IsValidMac(server.MacAddress))
        {
            var wolItem = new MenuItem { Header = vm.Localize("TreeCtxWakeOnLan") };
            wolItem.Click += async (_, _) =>
            {
                var sent = await Core.Security.WakeOnLan.SendAsync(server.MacAddress);
                vm.StatusText = sent
                    ? vm.Localize("WolSent")
                    : vm.Localize("WolFailed");
            };
            menu.Items.Add(wolItem);
        }

        // Notes submenu
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateNotesSubmenu(vm, server, callbacks));

        // External tools submenu
        var externalToolsMenu = CreateExternalToolsMenu(vm, server, callbacks);
        if (externalToolsMenu is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(externalToolsMenu);
        }

        // Detected third-party tools submenu (Sysinternals / NirSoft)
        var detectedToolsMenu = CreateDetectedToolsMenu(vm, server, callbacks);
        if (detectedToolsMenu is not null)
        {
            if (externalToolsMenu is null) menu.Items.Add(new Separator());
            menu.Items.Add(detectedToolsMenu);
        }

        menu.Items.Add(new Separator());
        var deleteItem = CreateMenuItem(
            vm.Localize("TreeCtxDelete"),
            vm.ServerList.DeleteServerCommand,
            server,
            inputGestureText: vm.Localize("TreeCtxGestureDelete"));
        ApplyDestructiveForeground(deleteItem);
        menu.Items.Add(deleteItem);

        return menu;
    }

    /// <summary>
    /// Builds the reduced bulk-actions context menu shown when right-clicking
    /// an item already participating in a multi-selection.
    /// </summary>
    private static ContextMenu CreateBulkSelectionContextMenu(
        MainViewModel vm,
        BulkSelectionContext bulkContext)
    {
        var menu = CreateContextMenu();
        var selectionCount = bulkContext.Items.Count;
        var connectableCount = vm.ServerList.GetBulkConnectTargetCount(bulkContext.Items);

        var headerItem = new MenuItem
        {
            Header = string.Format(vm.Localize("TreeCtxItemsSelected"), selectionCount),
            IsEnabled = false,
            FontStyle = FontStyles.Italic
        };
        menu.Items.Add(headerItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            string.Format(vm.Localize("TreeCtxConnectSelected"), connectableCount),
            vm.ServerList.ConnectSelectedCommand,
            isEnabled: connectableCount > 0));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxBulkDuplicate"),
            vm.ServerList.DuplicateSelectedCommand));
        menu.Items.Add(CreateBulkEditMenu(vm, bulkContext));
        menu.Items.Add(CreateFavoriteMenuItem(vm, bulkContext.Items));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateBulkMoveToGroupMenu(vm, bulkContext));
        menu.Items.Add(new Separator());

        var deleteItem = CreateMenuItem(
            string.Format(vm.Localize("TreeCtxDeleteSelected"), selectionCount),
            vm.ServerList.DeleteSelectedCommand,
            inputGestureText: vm.Localize("TreeCtxGestureDelete"));
        ApplyDestructiveForeground(deleteItem);
        menu.Items.Add(deleteItem);

        return menu;
    }

    /// <summary>
    /// Builds the Notes template submenu (blank, daily, incident, procedure)
    /// for the supplied server.
    /// </summary>
    private static MenuItem CreateNotesSubmenu(
        MainViewModel vm,
        ServerItemViewModel server,
        IContextMenuCallbacks callbacks)
    {
        var submenu = new MenuItem { Header = vm.Localize("TreeCtxNotes") };

        var blankItem = new MenuItem { Header = vm.Localize("ToolNotesBtnNew") };
        blankItem.Click += (_, _) => callbacks.OpenNotesForServer(server, NoteTemplateKind.Blank);
        submenu.Items.Add(blankItem);

        var dailyItem = new MenuItem { Header = vm.Localize("ToolNotesBtnDaily") };
        dailyItem.Click += (_, _) => callbacks.OpenNotesForServer(server, NoteTemplateKind.Daily);
        submenu.Items.Add(dailyItem);

        var incidentItem = new MenuItem { Header = vm.Localize("ToolNotesBtnIncident") };
        incidentItem.Click += (_, _) => callbacks.OpenNotesForServer(server, NoteTemplateKind.Incident);
        submenu.Items.Add(incidentItem);

        var procedureItem = new MenuItem { Header = vm.Localize("ToolNotesBtnProcedure") };
        procedureItem.Click += (_, _) => callbacks.OpenNotesForServer(server, NoteTemplateKind.Procedure);
        submenu.Items.Add(procedureItem);

        return submenu;
    }

    /// <summary>
    /// Builds the one-shot RDP mode override submenu.
    /// </summary>
    private static MenuItem CreateConnectWithMenu(MainViewModel vm, ServerItemViewModel server)
    {
        var submenu = new MenuItem
        {
            Header = vm.Localize("MenuItemConnectWith"),
            ToolTip = vm.Localize("MenuItemConnectWithTooltip")
        };

        submenu.Items.Add(CreateMenuItem(
            vm.Localize("MenuItemConnectEmbedded"),
            vm.ServerList.ConnectEmbeddedCommand,
            server));
        submenu.Items.Add(CreateMenuItem(
            vm.Localize("MenuItemConnectExternalMstsc"),
            vm.ServerList.ConnectExternalCommand,
            server));

        return submenu;
    }

    /// <summary>
    /// Interactive protocols offered by "Connect as...", paired with their display-label
    /// localization key. The server's own protocol is filtered out at build time.
    /// </summary>
    private static readonly (string Protocol, string LabelKey)[] ConnectAsProtocols =
    [
        ("SSH", "ConnectionTypeSsh"),
        ("RDP", "ConnectionTypeRdp"),
        ("SFTP", "ConnectionTypeSftp"),
        ("VNC", "ConnectionTypeVnc"),
        ("TELNET", "ConnectionTypeTelnet"),
    ];

    /// <summary>
    /// Builds the "Connect as..." submenu: connects the server's host with a chosen
    /// protocol as a transient ad-hoc session. Lists the interactive protocols except
    /// the server's own (case-insensitive).
    /// </summary>
    private static MenuItem CreateConnectAsMenu(MainViewModel vm, ServerItemViewModel server)
    {
        var submenu = new MenuItem { Header = vm.Localize("TreeCtxConnectAs") };

        foreach (var (protocol, labelKey) in ConnectAsProtocols)
        {
            if (string.Equals(server.ConnectionType, protocol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var captured = protocol;
            var item = new MenuItem { Header = vm.Localize(labelKey) };
            item.Click += (_, _) => _ = vm.ConnectServerAsProtocolAsync(server, captured);
            submenu.Items.Add(item);
        }

        return submenu;
    }

    /// <summary>
    /// Builds the "Open in split" submenu: opens the server in a new pane split
    /// off the currently active session. Disabled (parent and children) when there
    /// is no active session to split with.
    /// </summary>
    private static MenuItem CreateOpenInSplitMenu(MainViewModel vm, ServerItemViewModel server)
    {
        var hasActiveSession = vm.Connection.ActiveSession is not null;

        var submenu = new MenuItem
        {
            Header = vm.Localize("TreeCtxOpenInSplit"),
            IsEnabled = hasActiveSession
        };
        if (!hasActiveSession)
        {
            ExplainDisabled(submenu, vm.Localize("TreeCtxOpenInSplitDisabledReason"));
        }

        submenu.Items.Add(CreateOpenInSplitItem(
            vm, server, "OrientationHorizontal", Core.Models.SplitOrientation.Horizontal, hasActiveSession));
        submenu.Items.Add(CreateOpenInSplitItem(
            vm, server, "OrientationVertical", Core.Models.SplitOrientation.Vertical, hasActiveSession));

        return submenu;
    }

    private static MenuItem CreateOpenInSplitItem(
        MainViewModel vm,
        ServerItemViewModel server,
        string labelKey,
        Core.Models.SplitOrientation orientation,
        bool isEnabled)
    {
        var item = new MenuItem
        {
            Header = vm.Localize(labelKey),
            IsEnabled = isEnabled
        };
        item.Click += (_, _) =>
        {
            // Re-read the active session at click time; it may have changed since the
            // menu was built. Cannot split without one.
            var active = vm.Connection.ActiveSession;
            if (active is not null)
            {
                _ = vm.SplitSessionWithServerAsync(active, server.Id, orientation);
            }
        };
        return item;
    }

    private static bool IsRdpServer(ServerItemViewModel server)
    {
        return string.Equals(server.ConnectionType, "RDP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a context menu specific to tool entries (TOOL:*) in the TreeView.
    /// Excludes server-specific actions like Connect, Copy Hostname, Wake-on-LAN, External Tools.
    /// </summary>
    private ContextMenu CreateToolContextMenu(
        MainViewModel vm,
        ServerItemViewModel tool,
        IContextMenuCallbacks callbacks)
    {
        var menu = CreateContextMenu();

        // "Open in Tab" - the primary action for tools
        var openItem = new MenuItem { Header = vm.Localize("TreeCtxOpenToolInTab") };
        openItem.Click += (_, _) =>
        {
            var toolId = ConnectionTypeCatalog.StripToolPrefix(tool.ConnectionType!);
            vm.TrackRecentTool(toolId.ToUpperInvariant());
            var context = new Core.Models.ToolContext(
                TargetHost: tool.RemoteServer,
                TargetPort: tool.RemotePort > 0 ? tool.RemotePort : null,
                Argument: tool.RemoteServer);
            _ = vm.OpenToolTabAsync(toolId, tool.DisplayName, context);
        };
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        // Move to Project / Group (tools can be organized just like servers)
        menu.Items.Add(CreateMoveToGroupMenu(vm, tool));

        menu.Items.Add(new Separator());

        var renameItem = new MenuItem
        {
            Header = vm.Localize("TreeCtxRename"),
            InputGestureText = vm.Localize("TreeCtxGestureRename")
        };
        renameItem.Click += (_, _) => callbacks.BeginInlineRename(tool);
        menu.Items.Add(renameItem);

        // Remove from inventory
        var removeItem = CreateMenuItem(
            vm.Localize("TreeCtxRemoveTool"),
            vm.ServerList.DeleteServerCommand,
            tool);
        ApplyDestructiveForeground(removeItem);
        menu.Items.Add(removeItem);

        return menu;
    }

    /// <summary>
    /// Builds the "External Tools" submenu for a server context menu.
    /// Returns <c>null</c> if no external tools are configured.
    /// </summary>
    private static MenuItem? CreateExternalToolsMenu(
        MainViewModel vm,
        ServerItemViewModel server,
        IContextMenuCallbacks callbacks)
    {
        var tools = vm.CurrentSettings?.ExternalTools;
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var submenu = new MenuItem
        {
            Header = vm.Localize("TreeCtxExternalTools")
        };

        foreach (var tool in tools)
        {
            var toolItem = new MenuItem
            {
                Header = tool.Name
            };

            // Capture for closure
            var capturedTool = tool;
            toolItem.Click += (_, _) => callbacks.LaunchExternalTool(server, capturedTool);
            submenu.Items.Add(toolItem);
        }

        return submenu;
    }

    /// <summary>
    /// Creates a submenu for auto-detected third-party tools (Sysinternals, NirSoft),
    /// grouped by provider. Only shown if at least one tool is detected.
    /// </summary>
    private MenuItem? CreateDetectedToolsMenu(
        MainViewModel vm,
        ServerItemViewModel server,
        IContextMenuCallbacks callbacks)
    {
        var tools = _externalToolProvider.DetectedTools;
        if (tools is null || tools.Count == 0)
            return null;

        var submenu = new MenuItem
        {
            Header = vm.Localize("TreeCtxDetectedTools")
        };

        // Group by provider (Sysinternals, NirSoft, etc.)
        var groups = tools.GroupBy(t => t.ProviderName, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var providerMenu = new MenuItem { Header = group.Key, FontWeight = FontWeights.SemiBold };

            foreach (var tool in group.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var toolItem = new MenuItem { Header = tool.Name };

                if (tool.DescriptionKey is not null)
                    toolItem.ToolTip = vm.Localize(tool.DescriptionKey);

                var captured = tool;
                toolItem.Click += (_, _) => callbacks.LaunchDetectedTool(server, captured);
                providerMenu.Items.Add(toolItem);
            }

            submenu.Items.Add(providerMenu);
        }

        return submenu;
    }

    /// <summary>
    /// Builds the right-click context menu for a folder node. Supports bulk
    /// connect, adding servers / sub-folders / tools, rename, and delete.
    /// </summary>
    private ContextMenu CreateFolderContextMenu(
        MainViewModel vm,
        FolderViewModel folder,
        IContextMenuCallbacks callbacks)
    {
        var menu = CreateContextMenu();

        // Connect all servers in this folder (recursively)
        var allServers = GetAllServersRecursive(folder);
        var connectableCount = vm.ServerList.GetBulkConnectTargetCount(allServers);
        var connectAllItem = new MenuItem
        {
            Header = string.Format(vm.Localize("TreeCtxConnectAllCount"), connectableCount),
            IsEnabled = connectableCount > 0
        };
        connectAllItem.Click += async (_, _) =>
        {
            var plan = await vm.ServerList.PrepareBulkConnectPlanAsync(allServers, CancellationToken.None);
            if (plan.Refused)
            {
                // The gate reports its own outcome; naming a different reason here would mislead.
                return;
            }

            if (plan.ConnectableCount <= 0)
            {
                vm.StatusText = vm.ServerList.ComposeNothingToConnectStatus(plan.Skips);
                return;
            }

            var confirmed = await vm.DialogService.ShowConfirmAsync(
                vm.Localize("ConfirmConnectAllTitle"),
                string.Format(vm.Localize("ConfirmConnectAllMessage"), plan.ConnectableCount));

            if (!confirmed) return;

            await vm.ServerList.ConnectServersBulkCoreAsync(plan, CancellationToken.None);
        };
        menu.Items.Add(connectAllItem);

        menu.Items.Add(new Separator());

        // Add server to this folder
        var seed = new ServerDialogSeed(null, folder.FullPath);
        menu.Items.Add(CreateMenuItem(
            vm.Localize("DialogTitleAddServer"),
            vm.ServerList.AddServerCommand,
            seed));

        // Add sub-folder (via input dialog)
        var addSubItem = new MenuItem { Header = vm.Localize("TreeCtxNewGroup") };
        addSubItem.Click += async (_, _) => await CreateFolderFromMenuAsync(vm, folder.FullPath);
        menu.Items.Add(addSubItem);
        menu.Items.Add(CreateAddToolMenuItem(vm, callbacks, folder.FullPath));

        menu.Items.Add(new Separator());

        // Rename folder
        if (!string.IsNullOrEmpty(folder.FullPath))
        {
            var renameItem = new MenuItem
            {
                Header = vm.Localize("TreeCtxRename"),
                InputGestureText = vm.Localize("TreeCtxGestureRename")
            };
            renameItem.Click += (_, _) => callbacks.BeginInlineRename(folder);
            menu.Items.Add(renameItem);
            menu.Items.Add(CreateFolderColorMenu(vm, folder));
            menu.Items.Add(CreateMoveFolderMenu(vm, folder, callbacks));

            // Delete folder (move servers to root)
            var deleteItem = new MenuItem
            {
                Header = vm.Localize("TreeCtxDeleteGroup")
            };
            ApplyDestructiveForeground(deleteItem);
            deleteItem.Click += async (_, _) => await DeleteFolderFromMenuAsync(
                vm,
                folder,
                path => new FolderDeletionService(vm.ConfigManager).DeleteAsync(
                    path,
                    vm.ServerList.FlushExpandStateForCloseAsync));
            menu.Items.Add(deleteItem);
        }

        return menu;
    }

    /// <summary>
    /// Confirms and deletes a folder from its context menu, reporting a failure on the status
    /// line instead of letting it escape the click handler.
    /// </summary>
    /// <param name="vm">The shell view model.</param>
    /// <param name="folder">The folder to delete.</param>
    /// <param name="delete">Deletes the folder at a path and returns the reloaded inventory.</param>
    /// <remarks>
    /// The handler was an async lambda with nothing around it, so a failure - a locked settings
    /// file, a dialog that threw - left an unobserved exception on an async void and said nothing.
    /// A deletion is also not something the organization undo can reverse: it would put the
    /// sessions back but not the folder's own entry, colour and defaults, an undo that looks
    /// complete and is not. Rather than offer that, a completed deletion withdraws the undo bar,
    /// which would otherwise go on offering to undo the change made before it as if it were the
    /// last one.
    /// </remarks>
    internal static async Task DeleteFolderFromMenuAsync(
        MainViewModel vm,
        FolderViewModel folder,
        Func<string, Task<FolderDeletionResult>> delete)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(delete);

        try
        {
            int affectedEntryCount =
                vm.ServerList.GetCanonicalFolderEntryCount(folder.FullPath);
            bool confirmed = await vm.DialogService.ShowConfirmAsync(
                vm.Localize("TreeCtxDeleteGroup"),
                string.Format(
                    vm.Localize("TreeCtxDeleteGroupConfirm"),
                    folder.Name,
                    affectedEntryCount),
                "warning");

            if (!confirmed)
            {
                return;
            }

            FolderDeletionResult result = await delete(folder.FullPath);
            vm.ServerList.ClearOrganizationUndo();
            vm.ServerList.LoadServers(result.Servers, result.Settings);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Folder deletion from the tree menu failed", ex);
            vm.StatusText = string.Format(vm.Localize("StatusFolderDeleteFailed"), folder.Name);
        }
    }

    /// <summary>
    /// Every place a folder can be moved to, the top level first: the keyboard route to what a
    /// drag does with the pointer. The folder itself, its descendants and its current parent are
    /// left out or disabled, which is the same rule the drop applies.
    /// </summary>
    private static MenuItem CreateMoveFolderMenu(
        MainViewModel vm,
        FolderViewModel folder,
        IContextMenuCallbacks callbacks)
    {
        MenuItem item = new() { Header = vm.Localize("TreeCtxMoveFolderTo") };
        string currentParent = FolderPath.ParentOf(folder.FullPath);

        MenuItem topLevel = new()
        {
            Header = vm.Localize("TreeCtxMoveFolderToTopLevel"),
            IsEnabled = TreeInteractionState.IsFolderMoveTarget(folder.FullPath, null)
        };
        topLevel.Click += async (_, _) => await callbacks.MoveFolderAsync(folder, null);
        item.Items.Add(topLevel);

        AddFolderTargets(
            item.Items,
            vm,
            vm.ServerList.GetGroupTargets(includeNoGroup: false).Select(group => group.GroupName),
            path => !FolderPath.IsSelfOrDescendant(path, folder.FullPath),
            (path, header) =>
            {
                MenuItem child = new()
                {
                    Header = header,
                    IsEnabled = !string.Equals(path, currentParent, StringComparison.OrdinalIgnoreCase)
                };
                child.Click += async (_, _) => await callbacks.MoveFolderAsync(folder, path);
                return child;
            });

        return item;
    }

    /// <summary>
    /// Adds a folder picker to a menu as nested submenus that mirror the folder tree, rather than
    /// one flat list of full paths.
    /// </summary>
    /// <param name="items">The menu level to fill.</param>
    /// <param name="vm">The shell view model, for the "into this folder" wording.</param>
    /// <param name="paths">Every folder path a target may name.</param>
    /// <param name="include">Whether a folder is offered at all; an excluded folder takes its descendants with it.</param>
    /// <param name="createTarget">Builds the entry that performs the move into a path, given its header.</param>
    /// <remarks>
    /// A flat list of two hundred folders is a menu nobody can use, and it spelled every path out
    /// in full. Each folder is now one entry under its parent. A folder with children opens a
    /// submenu whose first entry moves into the folder itself: WPF gives a submenu header no click
    /// of its own, and the entry keeps the target reachable from the keyboard, Right to open and
    /// Enter to choose.
    /// </remarks>
    internal static void AddFolderTargets(
        ItemCollection items,
        MainViewModel vm,
        IEnumerable<string> paths,
        Func<string, bool> include,
        Func<string, string, MenuItem> createTarget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(createTarget);

        foreach (FolderTargetNode node in FolderTargetNode.BuildForest(paths))
        {
            AddFolderTarget(items, vm, node, include, createTarget);
        }
    }

    private static void AddFolderTarget(
        ItemCollection items,
        MainViewModel vm,
        FolderTargetNode node,
        Func<string, bool> include,
        Func<string, string, MenuItem> createTarget)
    {
        if (!include(node.FullPath))
        {
            return;
        }

        List<FolderTargetNode> children = node.Children.Where(child => include(child.FullPath)).ToList();
        if (children.Count == 0)
        {
            items.Add(createTarget(node.FullPath, node.Name));
            return;
        }

        MenuItem branch = new() { Header = node.Name };
        MenuItem here = createTarget(
            node.FullPath,
            string.Format(vm.Localize("TreeCtxMoveIntoFolder"), node.Name));
        branch.Items.Add(here);
        branch.Items.Add(new Separator());
        foreach (FolderTargetNode child in children)
        {
            AddFolderTarget(branch.Items, vm, child, include, createTarget);
        }

        branch.IsEnabled = branch.Items.OfType<MenuItem>().Any(entry => entry.IsEnabled);
        items.Add(branch);
    }

    /// <summary>One folder of the picker, with the folders directly under it.</summary>
    internal sealed class FolderTargetNode(string name, string fullPath)
    {
        public string Name { get; } = name;

        public string FullPath { get; } = fullPath;

        public List<FolderTargetNode> Children { get; } = [];

        /// <summary>
        /// Builds the folder forest from full paths, creating the intermediate folders a path
        /// implies and sorting every level the way the tree does.
        /// </summary>
        public static List<FolderTargetNode> BuildForest(IEnumerable<string> paths)
        {
            List<FolderTargetNode> roots = [];
            Dictionary<string, FolderTargetNode> byPath = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                string current = "";
                List<FolderTargetNode> level = roots;
                foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = current.Length == 0 ? segment : $"{current}/{segment}";
                    if (!byPath.TryGetValue(current, out FolderTargetNode? node))
                    {
                        node = new FolderTargetNode(segment, current);
                        byPath.Add(current, node);
                        level.Add(node);
                    }

                    level = node.Children;
                }
            }

            Sort(roots);
            return roots;
        }

        private static void Sort(List<FolderTargetNode> level)
        {
            level.Sort((left, right) => DisplayNameOrdering.Comparer.Compare(left.Name, right.Name));
            foreach (FolderTargetNode node in level)
            {
                Sort(node.Children);
            }
        }
    }

    /// <summary>Side of the colour swatch drawn beside a palette entry.</summary>
    private const double FolderColorSwatchSize = 12;

    /// <summary>Corner radius of that swatch.</summary>
    private const double FolderColorSwatchRadius = 2;

    /// <summary>
    /// The palette a folder icon can take, shared with project badges, plus a way back to the
    /// themed default.
    /// </summary>
    private static MenuItem CreateFolderColorMenu(MainViewModel vm, FolderViewModel folder)
    {
        MenuItem colorMenu = new() { Header = vm.Localize("TreeCtxFolderColor") };
        foreach ((string color, string labelKey) in BadgeColorPalette.Entries)
        {
            colorMenu.Items.Add(CreateFolderColorItem(vm, folder, color, vm.Localize(labelKey)));
        }

        colorMenu.Items.Add(new Separator());
        colorMenu.Items.Add(CreateFolderColorItem(
            vm,
            folder,
            color: null,
            vm.Localize("TreeCtxFolderColorNone")));
        return colorMenu;
    }

    private static MenuItem CreateFolderColorItem(
        MainViewModel vm,
        FolderViewModel folder,
        string? color,
        string header)
    {
        bool isCurrent = color is null
            ? !folder.HasColor
            : string.Equals(folder.Color, color, StringComparison.OrdinalIgnoreCase);
        MenuItem item = new()
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isCurrent,
            Tag = color
        };

        if (color is not null)
        {
            item.Icon = new System.Windows.Shapes.Rectangle
            {
                Width = FolderColorSwatchSize,
                Height = FolderColorSwatchSize,
                RadiusX = FolderColorSwatchRadius,
                RadiusY = FolderColorSwatchRadius,
                Fill = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color))
            };
        }

        item.Click += async (_, _) => await vm.ServerList.SetFolderColorAsync(folder.FullPath, color);
        return item;
    }

    /// <summary>
    /// Builds the context menu shown when right-clicking empty TreeView space
    /// (no server, no folder). Exposes add-server, add-gateway, new-folder,
    /// and add-tool entries.
    /// </summary>
    private static ContextMenu CreateEmptyAreaContextMenu(
        MainViewModel vm,
        IContextMenuCallbacks callbacks)
    {
        var menu = CreateContextMenu();

        menu.Items.Add(CreateMenuItem(
            vm.Localize("DialogTitleAddServer"),
            vm.ServerList.AddServerCommand,
            inputGestureText: vm.Localize("TreeCtxGestureAddServer")));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("BtnAddGateway"),
            vm.Settings.AddGatewayOutsidePanelCommand));

        // New root folder
        var newFolderItem = new MenuItem { Header = vm.Localize("TreeCtxNewGroup") };
        newFolderItem.Click += async (_, _) => await CreateFolderFromMenuAsync(vm, parentPath: null);
        menu.Items.Add(newFolderItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateAddToolMenuItem(vm, callbacks));

        return menu;
    }

    /// <summary>
    /// Creates a folder from a context menu, asking the same question and giving the same answers
    /// as the Add menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both call sites used to inline their own rule: an EmptyGroups membership test with no else
    /// branch, so a name that already existed produced a closed dialog and nothing else - and the
    /// test was narrow enough to miss a folder that exists only because sessions carry its path.
    /// The rule now lives once, in MainWindow.ResolveFolderCreation, and all three entry points
    /// share it. Three copies of one decision is how they came to disagree.
    /// </para>
    /// <para>
    /// A failure - a settings file that cannot be read or written - is caught and reported on the
    /// status line. The click handler that awaits this is async void, and an exception escaping it
    /// was reported nowhere.
    /// </para>
    /// </remarks>
    internal static async Task CreateFolderFromMenuAsync(MainViewModel vm, string? parentPath)
    {
        ArgumentNullException.ThrowIfNull(vm);

        try
        {
            string? name = await vm.DialogService.ShowInputAsync(
                vm.Localize("NewGroupDialogTitle"),
                vm.Localize("NewGroupFieldName"));

            string? requested = string.IsNullOrWhiteSpace(name)
                ? name
                : string.IsNullOrEmpty(parentPath)
                    ? name.Trim()
                    : $"{parentPath}/{name.Trim()}";

            AppSettings settings = await vm.ConfigManager.LoadSettingsAsync();
            List<ServerProfileDto> servers = await vm.ConfigManager.LoadServersAsync();
            List<string?> existingPaths =
                [.. settings.EmptyGroups, .. servers.Select(server => server.Group)];

            (MainWindow.FolderCreationOutcome outcome, string path) =
                MainWindow.ResolveFolderCreation(requested, existingPaths);

            switch (outcome)
            {
                case MainWindow.FolderCreationOutcome.Created:
                    settings = await MainWindow.CommitEmptyFolderAsync(vm.ConfigManager, path);
                    vm.ServerList.LoadServers(servers, settings);
                    vm.StatusText = string.Format(vm.Localize("StatusGroupCreated"), path);
                    break;

                case MainWindow.FolderCreationOutcome.Duplicate:
                    vm.DialogService.ShowWarning(
                        vm.Localize("NewGroupDialogTitle"),
                        vm.Localize("RenameGroupErrorSiblingCollision"));
                    break;

                case MainWindow.FolderCreationOutcome.Cancelled:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected folder creation outcome: {outcome}.");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Folder creation from the tree menu failed", ex);
            vm.StatusText = vm.Localize("StatusFolderCreateFailed");
        }
    }

    /// <summary>
    /// Creates the "Add Tool" menu item that invokes
    /// <see cref="IContextMenuCallbacks.AddToolFromMenu"/> with the supplied
    /// folder path as the target group.
    /// </summary>
    private static MenuItem CreateAddToolMenuItem(
        MainViewModel vm,
        IContextMenuCallbacks callbacks,
        string? group = null)
    {
        var item = new MenuItem { Header = vm.Localize("AddMenuTool"), Tag = group };
        item.Click += (_, _) => callbacks.AddToolFromMenu(group);
        return item;
    }

    /// <summary>
    /// Builds the "Move to Group" submenu listing every existing group within
    /// the server's current project (plus a "no group" entry).
    /// </summary>
    private static MenuItem CreateMoveToGroupMenu(MainViewModel vm, ServerItemViewModel server)
    {
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxMoveToGroup")
        };

        IReadOnlyList<GroupTarget> targets = vm.ServerList.GetGroupTargets(includeNoGroup: true);
        foreach (GroupTarget group in targets.Where(target => target.IsVirtualGroup))
        {
            item.Items.Add(CreateMenuItem(
                group.DisplayName,
                vm.ServerList.MoveToGroupCommand,
                new ServerMoveToGroupRequest(server, null),
                !string.IsNullOrWhiteSpace(server.Group)));
        }

        AddFolderTargets(
            item.Items,
            vm,
            targets.Where(target => !target.IsVirtualGroup).Select(target => target.GroupName),
            static _ => true,
            (path, header) => CreateMenuItem(
                header,
                vm.ServerList.MoveToGroupCommand,
                new ServerMoveToGroupRequest(server, path),
                !string.Equals(server.Group, path, StringComparison.OrdinalIgnoreCase)));

        return item;
    }

    /// <summary>
    /// Builds the bulk "Move to Group" submenu from the union of group targets
    /// across the currently selected projects.
    /// </summary>
    private static MenuItem CreateBulkMoveToGroupMenu(
        MainViewModel vm,
        BulkSelectionContext bulkContext)
    {
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxMoveToGroup")
        };

        AddBulkMoveTargets(item.Items, vm, bulkContext.Items);
        item.IsEnabled = item.Items.OfType<MenuItem>().Any(child => child.IsEnabled);
        return item;
    }

    /// <summary>
    /// Fills a menu level with every folder a multi-selection can move into, nested like the
    /// tree: the context menu's submenu and the bulk bar's Move button share it.
    /// </summary>
    /// <param name="items">The menu level to fill.</param>
    /// <param name="vm">The shell view model.</param>
    /// <param name="selection">The sessions to move.</param>
    internal static void AddBulkMoveTargets(
        ItemCollection items,
        MainViewModel vm,
        IReadOnlyList<ServerItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(selection);

        IReadOnlyList<GroupTarget> targets = vm.ServerList.GetBulkGroupTargets(selection, includeNoGroup: true);
        foreach (GroupTarget group in targets.Where(target => target.IsVirtualGroup))
        {
            items.Add(CreateMenuItem(
                group.DisplayName,
                vm.ServerList.MoveSelectedToGroupCommand,
                new BulkMoveToGroupRequest(null),
                vm.ServerList.IsBulkMoveTargetEnabled(selection, null)));
        }

        AddFolderTargets(
            items,
            vm,
            targets.Where(target => !target.IsVirtualGroup).Select(target => target.GroupName),
            static _ => true,
            (path, header) => CreateMenuItem(
                header,
                vm.ServerList.MoveSelectedToGroupCommand,
                new BulkMoveToGroupRequest(path),
                vm.ServerList.IsBulkMoveTargetEnabled(selection, path)));
    }

    /// <summary>
    /// Adds or removes sessions from the favorites, the same flag the server dialog sets and
    /// the Favorites filter reads.
    /// </summary>
    /// <remarks>
    /// The flag could only be set by opening the server dialog, one session at a time. The entry
    /// adds when any of the targets is not yet a favorite, and removes once they all are.
    /// </remarks>
    private static MenuItem CreateFavoriteMenuItem(
        MainViewModel vm,
        IReadOnlyList<ServerItemViewModel> servers)
    {
        bool allFavorite = servers.Count > 0 && servers.All(server => server.IsFavorite);
        return CreateMenuItem(
            vm.Localize(allFavorite ? "TreeCtxFavoriteRemove" : "TreeCtxFavoriteAdd"),
            vm.ServerList.SetFavoriteCommand,
            new FavoriteChangeRequest(servers, !allFavorite));
    }

    /// <summary>
    /// Says why an entry is disabled, on hover and to assistive technology, instead of leaving a
    /// greyed item to be puzzled over.
    /// </summary>
    private static void ExplainDisabled(MenuItem item, string reason)
    {
        item.ToolTip = reason;
        ToolTipService.SetShowOnDisabled(item, true);
        System.Windows.Automation.AutomationProperties.SetHelpText(item, reason);
    }

    private static MenuItem CreateBulkEditMenu(
        MainViewModel vm,
        BulkSelectionContext bulkContext)
    {
        var usernameTargetCount = vm.ServerList.GetBulkUsernameTargetCount(bulkContext.Items);
        var passwordTargetCount = vm.ServerList.GetBulkPasswordTargetCount(bulkContext.Items);
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxBulkEditMenu")
        };

        item.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxBulkEditPort"),
            vm.ServerList.BulkEditPortCommand,
            bulkContext.Items));

        item.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxBulkEditGateway"),
            vm.ServerList.BulkEditGatewayCommand,
            bulkContext.Items));

        item.Items.Add(CreateMenuItem(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                vm.Localize("TreeCtxBulkEditUsername"),
                usernameTargetCount),
            vm.ServerList.BulkEditUsernameCommand,
            bulkContext.Items,
            isEnabled: usernameTargetCount > 0));

        item.Items.Add(CreateMenuItem(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                vm.Localize("TreeCtxBulkEditPassword"),
                passwordTargetCount),
            vm.ServerList.BulkEditPasswordCommand,
            bulkContext.Items,
            isEnabled: passwordTargetCount > 0));

        return item;
    }

    /// <summary>
    /// Creates a bare, themed <see cref="ContextMenu"/> instance. Centralised
    /// so future styling changes touch a single location.
    /// </summary>
    private static ContextMenu CreateContextMenu()
    {
        return new ContextMenu();
    }

    /// <summary>
    /// Colors a destructive menu item with the themed error brush. When the
    /// theme bridge is not merged (theoretical swap window) or there is no
    /// running <see cref="Application"/> (designer/test contexts), the item
    /// keeps its inherited foreground instead of falling back to a hardcoded color.
    /// </summary>
    private static void ApplyDestructiveForeground(MenuItem item)
    {
        if (Application.Current?.TryFindResource("ErrorBrush") is Brush errorBrush)
        {
            item.Foreground = errorBrush;
        }
    }

    /// <summary>
    /// Creates a <see cref="MenuItem"/> bound to an <see cref="ICommand"/>,
    /// optionally carrying a parameter, enabled state and input-gesture hint.
    /// </summary>
    private static MenuItem CreateMenuItem(
        string header,
        ICommand command,
        object? parameter = null,
        bool isEnabled = true,
        string? inputGestureText = null)
    {
        return new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = parameter,
            IsEnabled = isEnabled,
            InputGestureText = inputGestureText ?? string.Empty
        };
    }

    /// <summary>
    /// Recursively collects all <see cref="ServerItemViewModel"/> instances
    /// from a folder and its sub-folders.
    /// </summary>
    private static List<ServerItemViewModel> GetAllServersRecursive(FolderViewModel folder)
    {
        var result = new List<ServerItemViewModel>(folder.Servers);
        foreach (var sub in folder.SubFolders)
        {
            result.AddRange(GetAllServersRecursive(sub));
        }
        return result;
    }
}
