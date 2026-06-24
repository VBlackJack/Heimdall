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
    /// (Sysinternals, NirSoft, …) used to build the "Detected Tools" submenu.
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
            ServerItemViewModel server when server.ConnectionType?.StartsWith(
                "TOOL:", StringComparison.OrdinalIgnoreCase) == true
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
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxEdit"),
            vm.ServerList.EditServerCommand,
            server,
            inputGestureText: "Ctrl+E"));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxDuplicate"),
            vm.ServerList.DuplicateServerCommand,
            server));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMoveToProjectMenu(vm, server));
        menu.Items.Add(CreateMoveToGroupMenu(vm, server));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxCopyHostname"),
            vm.ServerList.CopyHostnameCommand,
            server));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxCopyUsername"),
            vm.ServerList.CopyUsernameCommand,
            server,
            !string.IsNullOrWhiteSpace(server.Username)));
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
            inputGestureText: "Ctrl+Del");
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
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateBulkMoveToProjectMenu(vm, bulkContext));
        menu.Items.Add(CreateBulkMoveToGroupMenu(vm, bulkContext));
        menu.Items.Add(new Separator());

        var deleteItem = CreateMenuItem(
            string.Format(vm.Localize("TreeCtxDeleteSelected"), selectionCount),
            vm.ServerList.DeleteSelectedCommand,
            inputGestureText: "Del");
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
        _ = callbacks;

        var menu = CreateContextMenu();

        // "Open in Tab" — the primary action for tools
        var openItem = new MenuItem { Header = vm.Localize("TreeCtxOpenToolInTab") };
        openItem.Click += (_, _) =>
        {
            var toolId = tool.ConnectionType!["TOOL:".Length..];
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
        menu.Items.Add(CreateMoveToProjectMenu(vm, tool));
        menu.Items.Add(CreateMoveToGroupMenu(vm, tool));

        menu.Items.Add(new Separator());

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
            if (plan.ConnectableCount <= 0)
            {
                vm.StatusText = vm.Localize("StatusBulkConnectNothingToConnect");
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
        addSubItem.Click += async (_, _) =>
        {
            var name = await vm.DialogService.ShowInputAsync(
                vm.Localize("TreeCtxNewGroup"),
                vm.Localize("ServerFieldGroup"));

            if (!string.IsNullOrWhiteSpace(name))
            {
                string newPath = string.IsNullOrEmpty(folder.FullPath)
                    ? name.Trim()
                    : $"{folder.FullPath}/{name.Trim()}";

                var settings = await vm.ConfigManager.LoadSettingsAsync();
                if (!settings.EmptyGroups.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    settings.EmptyGroups.Add(newPath);
                    await vm.ConfigManager.SaveSettingsAsync(settings);
                    var servers = await vm.ConfigManager.LoadServersAsync();
                    vm.ServerList.LoadServers(servers, settings);
                }
            }
        };
        menu.Items.Add(addSubItem);
        menu.Items.Add(CreateAddToolMenuItem(vm, callbacks, folder.FullPath));

        menu.Items.Add(new Separator());

        // Rename folder
        if (!string.IsNullOrEmpty(folder.FullPath))
        {
            var renameItem = new MenuItem { Header = vm.Localize("TreeCtxRenameGroup") };
            renameItem.Click += async (_, _) =>
            {
                var newName = await vm.DialogService.ShowInputAsync(
                    vm.Localize("TreeCtxRenameGroup"),
                    vm.Localize("ServerFieldGroup"),
                    folder.Name);

                if (!string.IsNullOrWhiteSpace(newName) &&
                    !string.Equals(newName.Trim(), folder.Name, StringComparison.Ordinal))
                {
                    var servers = await vm.ConfigManager.LoadServersAsync();
                    string oldPath = folder.FullPath;
                    string parentPath = oldPath.Contains('/')
                        ? oldPath[..oldPath.LastIndexOf('/')]
                        : "";
                    string newPath = string.IsNullOrEmpty(parentPath)
                        ? newName.Trim()
                        : $"{parentPath}/{newName.Trim()}";

                    // Rename in server Group paths
                    foreach (var dto in servers)
                    {
                        if (dto.Group is not null &&
                            (dto.Group.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                             dto.Group.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)))
                        {
                            dto.Group = newPath + dto.Group[oldPath.Length..];
                        }
                    }

                    // Rename in EmptyGroups
                    var settings = await vm.ConfigManager.LoadSettingsAsync();
                    for (int i = 0; i < settings.EmptyGroups.Count; i++)
                    {
                        var eg = settings.EmptyGroups[i];
                        if (eg.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                            eg.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.EmptyGroups[i] = newPath + eg[oldPath.Length..];
                        }
                    }

                    await vm.ConfigManager.SaveSettingsAsync(settings);
                    await vm.ConfigManager.SaveServersAsync(servers);
                    vm.ServerList.LoadServers(servers, settings);
                }
            };
            menu.Items.Add(renameItem);

            // Delete folder (move servers to root)
            var deleteItem = new MenuItem
            {
                Header = vm.Localize("TreeCtxDeleteGroup")
            };
            ApplyDestructiveForeground(deleteItem);
            deleteItem.Click += async (_, _) =>
            {
                var confirmed = await vm.DialogService.ShowConfirmAsync(
                    vm.Localize("TreeCtxDeleteGroup"),
                    string.Format(vm.Localize("TreeCtxDeleteGroupConfirm"), folder.Name),
                    "warning");

                if (!confirmed) return;

                var servers = await vm.ConfigManager.LoadServersAsync();
                foreach (var dto in servers)
                {
                    if (dto.Group is not null &&
                        (dto.Group.Equals(folder.FullPath, StringComparison.OrdinalIgnoreCase) ||
                         dto.Group.StartsWith(folder.FullPath + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        dto.Group = null;
                    }
                }

                var settings = await vm.ConfigManager.LoadSettingsAsync();
                settings.EmptyGroups.RemoveAll(p =>
                    p.Equals(folder.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith(folder.FullPath + "/", StringComparison.OrdinalIgnoreCase));
                await vm.ConfigManager.SaveSettingsAsync(settings);
                await vm.ConfigManager.SaveServersAsync(servers);
                vm.ServerList.LoadServers(servers, settings);
            };
            menu.Items.Add(deleteItem);
        }

        return menu;
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
            inputGestureText: "Ctrl+N"));
        menu.Items.Add(CreateMenuItem(
            vm.Localize("BtnAddGateway"),
            vm.Settings.AddGatewayCommand));

        // New root folder
        var newFolderItem = new MenuItem { Header = vm.Localize("TreeCtxNewGroup") };
        newFolderItem.Click += async (_, _) =>
        {
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
        };
        menu.Items.Add(newFolderItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateAddToolMenuItem(vm, callbacks));

        return menu;
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
    /// Builds the "Move to Project" submenu listing every registered project
    /// (plus a "no project" entry) as a move target.
    /// </summary>
    private static MenuItem CreateMoveToProjectMenu(MainViewModel vm, ServerItemViewModel server)
    {
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxMoveToProject")
        };

        foreach (var project in vm.ServerList.GetProjectTargets(includeNoProject: true))
        {
            var targetProjectId = string.IsNullOrWhiteSpace(project.Id) ? null : project.Id;
            var child = CreateMenuItem(
                project.Name,
                vm.ServerList.MoveToProjectCommand,
                new ServerMoveToProjectRequest(server, targetProjectId),
                !string.Equals(server.ProjectId, project.Id, StringComparison.Ordinal));

            item.Items.Add(child);
        }

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

        foreach (var group in vm.ServerList.GetGroupTargets(server.ProjectId, includeNoGroup: true))
        {
            var targetGroupName = string.IsNullOrWhiteSpace(group.GroupName) ? null : group.GroupName;
            var child = CreateMenuItem(
                group.DisplayName,
                vm.ServerList.MoveToGroupCommand,
                new ServerMoveToGroupRequest(server, targetGroupName),
                !string.Equals(server.Group, group.GroupName, StringComparison.OrdinalIgnoreCase));

            item.Items.Add(child);
        }

        return item;
    }

    /// <summary>
    /// Builds the bulk "Move to Project" submenu from the full list of project
    /// targets, disabling entries only when every selected item is already in the
    /// requested project.
    /// </summary>
    private static MenuItem CreateBulkMoveToProjectMenu(
        MainViewModel vm,
        BulkSelectionContext bulkContext)
    {
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxBulkMoveToProject")
        };

        var enabledChildren = 0;
        foreach (var project in vm.ServerList.GetProjectTargets(includeNoProject: true))
        {
            var targetProjectId = string.IsNullOrWhiteSpace(project.Id) ? null : project.Id;
            var isEnabled = vm.ServerList.IsBulkMoveProjectTargetEnabled(bulkContext.Items, targetProjectId);
            if (isEnabled)
            {
                enabledChildren++;
            }

            var child = CreateMenuItem(
                project.Name,
                vm.ServerList.MoveSelectedToProjectCommand,
                new BulkMoveToProjectRequest(targetProjectId),
                isEnabled);

            item.Items.Add(child);
        }

        item.IsEnabled = enabledChildren > 0;
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

        var enabledChildren = 0;
        foreach (var group in vm.ServerList.GetBulkGroupTargets(bulkContext.Items, includeNoGroup: true))
        {
            var targetGroupName = string.IsNullOrWhiteSpace(group.GroupName) ? null : group.GroupName;
            var isEnabled = vm.ServerList.IsBulkMoveTargetEnabled(bulkContext.Items, targetGroupName);
            if (isEnabled)
            {
                enabledChildren++;
            }

            var child = CreateMenuItem(
                group.DisplayName,
                vm.ServerList.MoveSelectedToGroupCommand,
                new BulkMoveToGroupRequest(targetGroupName),
                isEnabled);

            item.Items.Add(child);
        }

        item.IsEnabled = enabledChildren > 0;
        return item;
    }

    private static MenuItem CreateBulkEditMenu(
        MainViewModel vm,
        BulkSelectionContext bulkContext)
    {
        var item = new MenuItem
        {
            Header = vm.Localize("TreeCtxBulkEditMenu")
        };

        item.Items.Add(CreateMenuItem(
            vm.Localize("TreeCtxBulkEditPort"),
            vm.ServerList.BulkEditPortCommand,
            bulkContext.Items));

        item.Items.Add(CreateMenuItem(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                vm.Localize("TreeCtxBulkEditUsername"),
                bulkContext.Items.Count),
            vm.ServerList.BulkEditUsernameCommand,
            bulkContext.Items));

        item.Items.Add(CreateMenuItem(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                vm.Localize("TreeCtxBulkEditPassword"),
                bulkContext.Items.Count),
            vm.ServerList.BulkEditPasswordCommand,
            bulkContext.Items));

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
