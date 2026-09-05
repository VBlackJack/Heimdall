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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    /// <summary>
    /// Applies a persisted rename to the existing server ViewModel without
    /// rebuilding the filtered tree or replacing the current selection.
    /// </summary>
    internal void ApplyInlineServerRename(
        ServerItemViewModel server,
        ServerProfileDto persistedServer)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(persistedServer);

        AppSettings settings = _currentSettings ?? new AppSettings();
        Dictionary<string, ProjectDto> projectMap = BuildProjectMap(settings);
        Dictionary<string, SshGatewayDto> gatewayMap = BuildGatewayMap(settings);

        server.UpdateFromDto(
            persistedServer,
            ResolveProject(projectMap, persistedServer.ProjectId),
            gatewayMap,
            _localizer);

        ResortStableTreeProjection();
        ApplyFilter(server.Id);
    }

    /// <summary>
    /// Applies a persisted folder rename to the existing folder and server
    /// ViewModels so keyboard focus and object identity survive the operation.
    /// </summary>
    internal void ApplyInlineFolderRename(
        FolderViewModel folder,
        string oldPath,
        FolderRenameResult result)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status != FolderRenameStatus.Renamed
            || string.IsNullOrWhiteSpace(result.NewPath)
            || result.Servers is null
            || result.Settings is null)
        {
            throw new ArgumentException(
                "A successful folder rename result is required.",
                nameof(result));
        }

        _currentSettings = result.Settings;
        Dictionary<string, ProjectDto> projectMap = BuildProjectMap(result.Settings);
        Dictionary<string, SshGatewayDto> gatewayMap = BuildGatewayMap(result.Settings);
        Dictionary<string, ServerProfileDto> persistedById = result.Servers.ToDictionary(
            server => server.Id,
            StringComparer.Ordinal);

        foreach (ServerItemViewModel server in _allServers)
        {
            if (persistedById.TryGetValue(server.Id, out ServerProfileDto? persistedServer))
            {
                server.UpdateFromDto(
                    persistedServer,
                    ResolveProject(projectMap, persistedServer.ProjectId),
                    gatewayMap,
                    _localizer);
            }
        }

        FolderRenamePlan plan = new(oldPath, result.NewPath);
        RewriteFolderSubtree(folder, plan, isRenamedRoot: true);

        _expandedNodes.Clear();
        foreach (string expandedPath in result.Settings.TreeExpandedNodes)
        {
            _expandedNodes.Add(expandedPath);
        }

        RefreshLookupCollections(result.Settings);
        RefreshFolderColors();
        ResortStableTreeProjection();
        ApplyFilter();
    }

    private static void RewriteFolderSubtree(
        FolderViewModel folder,
        FolderRenamePlan plan,
        bool isRenamedRoot)
    {
        folder.FullPath = plan.Rewrite(folder.FullPath);
        if (isRenamedRoot)
        {
            int separatorIndex = folder.FullPath.LastIndexOf('/');
            folder.Name = separatorIndex >= 0
                ? folder.FullPath[(separatorIndex + 1)..]
                : folder.FullPath;
        }

        foreach (FolderViewModel child in folder.SubFolders)
        {
            RewriteFolderSubtree(child, plan, isRenamedRoot: false);
        }
    }
}
