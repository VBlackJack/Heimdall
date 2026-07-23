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

using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    public bool ShouldOpenBulkContextMenu(ServerItemViewModel? target)
    {
        return target is not null
            && SelectionCount > 1
            && SelectedItems.Contains(target);
    }

    public BulkSelectionContext? CreateBulkSelectionContext()
    {
        if (SelectionCount <= 1)
        {
            return null;
        }

        var snapshot = SelectedItems.ToList();
        return snapshot.Count <= 1
            ? null
            : new BulkSelectionContext(snapshot, SelectedServer);
    }

    public IReadOnlyList<GroupTarget> GetBulkGroupTargets(
        IReadOnlyList<ServerItemViewModel> selectedItems,
        bool includeNoGroup)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);

        var targets = new List<GroupTarget>();
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (includeNoGroup)
        {
            targets.Add(new GroupTarget(
                string.Empty,
                _localizer["TreeNodeNoGroup"],
                IsVirtualGroup: true));
            seenGroups.Add(string.Empty);
        }

        foreach (var projectId in selectedItems
                     .Select(item => item.ProjectId ?? string.Empty)
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var group in GetGroupTargets(projectId, includeNoGroup: false))
            {
                var normalizedGroup = NormalizeGroupForPersistence(group.GroupName) ?? string.Empty;
                if (!seenGroups.Add(normalizedGroup))
                {
                    continue;
                }

                targets.Add(group);
            }
        }

        return targets;
    }

    public bool IsBulkMoveTargetEnabled(
        IReadOnlyList<ServerItemViewModel> selectedItems,
        string? targetGroup)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);

        var normalizedTarget = NormalizeGroupForPersistence(targetGroup);
        return selectedItems.Any(item => !string.Equals(
            NormalizeGroupForPersistence(item.Group),
            normalizedTarget,
            StringComparison.OrdinalIgnoreCase));
    }

    public bool IsBulkMoveProjectTargetEnabled(
        IReadOnlyList<ServerItemViewModel> selectedItems,
        string? targetProjectId)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);

        var normalizedTarget = NormalizeProjectForSelection(targetProjectId);
        return selectedItems.Any(item => !string.Equals(
            NormalizeProjectForSelection(item.ProjectId),
            normalizedTarget,
            StringComparison.Ordinal));
    }

    public int GetBulkConnectTargetCount(IReadOnlyList<ServerItemViewModel> selectedItems)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);

        return NormalizeSelection(selectedItems)
            .Count(item => !IsToolEntry(item) && !_connectingServerIds.Contains(item.Id));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var message = BuildBulkDeleteConfirmationMessage(selectedItems);
        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["DialogTitleDeleteSelectedItems"],
            message,
            "danger");

        if (!confirmed)
        {
            return;
        }

        await DeleteServersCoreAsync(selectedItems, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedToGroup))]
    private async Task MoveSelectedToGroupAsync(BulkMoveToGroupRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count <= 1)
        {
            return;
        }

        await MoveServersToGroupCoreAsync(selectedItems, request.GroupName, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedToProject))]
    private async Task MoveSelectedToProjectAsync(BulkMoveToProjectRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var movedCount = await MoveServersToProjectCoreAsync(selectedItems, request.ProjectId, cancellationToken);
        if (movedCount <= 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            StatusMessageRequested?.Invoke(
                _localizer.Format("StatusBulkMovedToNoProject", movedCount));
            return;
        }

        var targetProjectName = _projectTargets
            .FirstOrDefault(project => string.Equals(project.Id, request.ProjectId, StringComparison.Ordinal))
            ?.Name ?? request.ProjectId;

        StatusMessageRequested?.Invoke(
            _localizer.Format("StatusBulkMovedToProject", movedCount, targetProjectName));
    }

    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    private async Task DuplicateSelectedAsync(CancellationToken cancellationToken)
    {
        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var clones = await DuplicateServersCoreAsync(selectedItems, cancellationToken);
        if (clones.Count == 0)
        {
            return;
        }

        StatusMessageRequested?.Invoke(_localizer.Format("StatusBulkDuplicated", clones.Count));
    }

    [RelayCommand(CanExecute = nameof(CanBulkEditPort))]
    private async Task BulkEditPortAsync(
        IReadOnlyList<ServerItemViewModel>? selection,
        CancellationToken cancellationToken)
    {
        var selectedItems = NormalizeSelection(selection ?? SelectedItems.ToList());
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var distinctPorts = selectedItems
            .Select(GetEditablePort)
            .Distinct()
            .Take(2)
            .ToList();
        int? initialPort = distinctPorts.Count == 1 ? distinctPorts[0] : null;

        var result = await _dialogService.ShowBulkEditPortAsync(
            selectedItems.Count,
            initialPort,
            cancellationToken);

        if (result is null)
        {
            return;
        }

        await EditPortServersCoreAsync(selectedItems, result.Value, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanBulkEditUsername))]
    private async Task BulkEditUsernameAsync(
        IReadOnlyList<ServerItemViewModel>? selection,
        CancellationToken cancellationToken)
    {
        var selectedItems = NormalizeSelection(selection ?? SelectedItems.ToList());
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var distinctUsernames = selectedItems
            .Select(server => server.Username ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        var initialUsername = distinctUsernames.Count == 1 ? distinctUsernames[0] : null;

        var result = await _dialogService.ShowBulkEditUsernameAsync(
            selectedItems.Count,
            initialUsername,
            cancellationToken);

        if (result is null)
        {
            return;
        }

        await EditUsernameServersCoreAsync(selectedItems, result, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanBulkEditPassword))]
    private async Task BulkEditPasswordAsync(
        IReadOnlyList<ServerItemViewModel>? selection,
        CancellationToken cancellationToken)
    {
        var selectedItems = NormalizeSelection(selection ?? SelectedItems.ToList());
        if (selectedItems.Count <= 1)
        {
            return;
        }

        var result = await _dialogService.ShowBulkEditPasswordAsync(
            selectedItems.Count,
            cancellationToken);

        if (result is null)
        {
            return;
        }

        await EditPasswordServersCoreAsync(selectedItems, result, cancellationToken);
    }

    [RelayCommand]
    private async Task ConnectSelectedAsync(CancellationToken cancellationToken)
    {
        var selectedItems = SelectedItems.ToList();
        if (selectedItems.Count <= 1)
        {
            return;
        }

        // Windows Hello gate: verify once for the whole batch, before the plan is
        // prepared (plan preparation resolves stored credentials). Abort on failure.
        var settings = await _configManager.LoadSettingsAsync();
        if (!await EnsureWindowsHelloAsync(settings, cancellationToken))
        {
            return;
        }

        var plan = await PrepareBulkConnectPlanAsync(selectedItems, cancellationToken);
        if (plan.ConnectableCount <= 0)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusBulkConnectNothingToConnect"]);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmConnectAllTitle"],
            _localizer.Format("ConfirmConnectAllMessage", plan.ConnectableCount));
        if (!confirmed)
        {
            return;
        }

        await ConnectServersBulkCoreAsync(plan, cancellationToken);
    }

    private bool CanDeleteSelected() => SelectionCount > 1;

    private bool CanMoveSelectedToGroup() => SelectionCount > 1;

    private bool CanMoveSelectedToProject() => SelectionCount > 1;

    private bool CanDuplicateSelected() => SelectionCount > 1;

    private bool CanBulkEditPort() => SelectionCount > 1;

    private bool CanBulkEditUsername() => SelectionCount > 1;

    private bool CanBulkEditPassword() => SelectionCount > 1;

    public async Task ConnectServersBulkCoreAsync(
        IReadOnlyList<ServerItemViewModel> serversToConnect,
        CancellationToken cancellationToken = default)
    {
        // Windows Hello gate for any caller of this entry point, before credentials are
        // resolved in plan preparation. The shared grace window prevents a second prompt
        // if a recent verification (single-connect or another batch) already succeeded.
        var settings = await _configManager.LoadSettingsAsync();
        if (!await EnsureWindowsHelloAsync(settings, cancellationToken))
        {
            return;
        }

        var plan = await PrepareBulkConnectPlanAsync(serversToConnect, cancellationToken);
        await ConnectServersBulkCoreAsync(plan, cancellationToken);
    }

    private async Task<IReadOnlyList<ServerItemViewModel>> DuplicateServersCoreAsync(
        IReadOnlyList<ServerItemViewModel> serversToDuplicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serversToDuplicate);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServers = NormalizeSelection(serversToDuplicate);
        if (selectedServers.Count == 0)
        {
            return [];
        }

        var ids = selectedServers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var cloneIds = new List<string>(selectedServers.Count);

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (cloneIds.Count == 0)
        {
            return [];
        }

        var cloneViewModels = cloneIds
            .Select(id => _allServers.FirstOrDefault(server => string.Equals(server.Id, id, StringComparison.Ordinal)))
            .OfType<ServerItemViewModel>()
            .ToList();

        Core.Logging.FileLogger.Info(
            $"DuplicateServersCoreAsync duplicated {cloneViewModels.Count} item(s) in a single transaction.");

        return cloneViewModels;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"DuplicateServersCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            var existingNames = _allServers
                .Select(server => server.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var clones = new List<ServerProfileDto>(selectedServers.Count);
            foreach (var server in selectedServers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceDto = dtoMap[server.Id];
                var json = System.Text.Json.JsonSerializer.Serialize(sourceDto);
                var clone = System.Text.Json.JsonSerializer.Deserialize<ServerProfileDto>(json);
                if (clone is null)
                {
                    Core.Logging.FileLogger.Warn(
                        $"DuplicateServersCoreAsync skipped '{server.DisplayName}' because the DTO clone could not be created.");
                    continue;
                }

                clone.Id = Guid.NewGuid().ToString();
                clone.DisplayName = GenerateUniqueDisplayName(sourceDto.DisplayName, existingNames);
                clone.RdpPasswordEncrypted = null;
                clones.Add(clone);
                cloneIds.Add(clone.Id);
            }

            if (clones.Count == 0)
            {
                return null;
            }

            serverDtos.AddRange(clones);

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                Array.Empty<(ServerItemViewModel OldVm, ServerProfileDto NewDto)>(),
                clones,
                cloneIds.ToArray(),
                cloneIds[^1],
                cloneIds[^1],
                null,
                null);
        }
    }

    private async Task EditPortServersCoreAsync(
        IReadOnlyList<ServerItemViewModel> sources,
        int newPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServers = NormalizeSelection(sources);
        if (selectedServers.Count == 0)
        {
            return;
        }

        var dirtyServers = selectedServers
            .Where(server => GetEditablePort(server) != newPort)
            .ToList();

        if (dirtyServers.Count == 0)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusBulkPortNoOp"]);
            return;
        }

        var selectedIds = selectedServers
            .Select(server => server.Id)
            .ToArray();
        var primarySelectionId = SelectedServer is not null
            && selectedServers.Any(server => string.Equals(server.Id, SelectedServer.Id, StringComparison.Ordinal))
                ? SelectedServer.Id
                : selectedServers[^1].Id;
        var ids = dirtyServers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var updatedCount = 0;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (updatedCount > 0)
        {
            Core.Logging.FileLogger.Info(
                $"EditPortServersCoreAsync updated port to {newPort} for {updatedCount} item(s) in a single transaction.");
        }

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"EditPortServersCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            foreach (var server in dirtyServers)
            {
                SetEditablePort(dtoMap[server.Id], newPort);
            }

            updatedCount = dirtyServers.Count;

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                dirtyServers
                    .Select(server => (OldVm: server, NewDto: dtoMap[server.Id]))
                    .ToArray(),
                Array.Empty<ServerProfileDto>(),
                selectedIds,
                primarySelectionId,
                null,
                "StatusBulkPortUpdated",
                [updatedCount]);
        }
    }

    private async Task EditUsernameServersCoreAsync(
        IReadOnlyList<ServerItemViewModel> sources,
        string newUsername,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(newUsername);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServers = NormalizeSelection(sources);
        if (selectedServers.Count == 0)
        {
            return;
        }

        var dirtyServers = selectedServers
            .Where(server => !string.Equals(server.Username, newUsername, StringComparison.Ordinal))
            .ToList();

        if (dirtyServers.Count == 0)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusBulkUsernameNoOp"]);
            return;
        }

        var selectedIds = selectedServers
            .Select(server => server.Id)
            .ToArray();
        var primarySelectionId = SelectedServer is not null
            && selectedServers.Any(server => string.Equals(server.Id, SelectedServer.Id, StringComparison.Ordinal))
                ? SelectedServer.Id
                : selectedServers[^1].Id;
        var ids = dirtyServers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var updatedCount = 0;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (updatedCount > 0)
        {
            Core.Logging.FileLogger.Info(
                $"EditUsernameServersCoreAsync updated username for {updatedCount} item(s) in a single transaction.");
        }

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"EditUsernameServersCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            foreach (var server in dirtyServers)
            {
                SetEditableUsername(dtoMap[server.Id], newUsername);
            }

            updatedCount = dirtyServers.Count;

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                dirtyServers
                    .Select(server => (OldVm: server, NewDto: dtoMap[server.Id]))
                    .ToArray(),
                Array.Empty<ServerProfileDto>(),
                selectedIds,
                primarySelectionId,
                null,
                "StatusBulkUsernameUpdated",
                [updatedCount]);
        }
    }

    private async Task EditPasswordServersCoreAsync(
        IReadOnlyList<ServerItemViewModel> sources,
        string newPlaintextPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(newPlaintextPassword);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServers = NormalizeSelection(sources);
        if (selectedServers.Count == 0)
        {
            return;
        }

        var encryptedPassword = Core.Security.CredentialProtector.Protect(newPlaintextPassword);

        var selectedIds = selectedServers
            .Select(server => server.Id)
            .ToArray();
        var primarySelectionId = SelectedServer is not null
            && selectedServers.Any(server => string.Equals(server.Id, SelectedServer.Id, StringComparison.Ordinal))
                ? SelectedServer.Id
                : selectedServers[^1].Id;
        var ids = selectedServers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var updatedCount = 0;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (updatedCount > 0)
        {
            Core.Logging.FileLogger.Info(
                $"EditPasswordServersCoreAsync updated password for {updatedCount} item(s) in a single transaction.");
        }

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"EditPasswordServersCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            foreach (var server in selectedServers)
            {
                SetEditablePassword(dtoMap[server.Id], encryptedPassword);
            }

            updatedCount = selectedServers.Count;

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                selectedServers
                    .Select(server => (OldVm: server, NewDto: dtoMap[server.Id]))
                    .ToArray(),
                Array.Empty<ServerProfileDto>(),
                selectedIds,
                primarySelectionId,
                null,
                "StatusBulkPasswordUpdated",
                [updatedCount]);
        }
    }

    private string BuildBulkDeleteConfirmationMessage(IReadOnlyList<ServerItemViewModel> selectedItems)
    {
        if (selectedItems.Count > 10)
        {
            return _localizer.Format("ConfirmDeleteSelectedItems", selectedItems.Count);
        }

        var bulletList = string.Join(
            Environment.NewLine,
            selectedItems
                .Select(item => item.DisplayName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => $"- {name}"));

        return _localizer.Format(
            "ConfirmDeleteSelectedItemsWithList",
            selectedItems.Count,
            bulletList);
    }

    public async Task<int> UpdateGatewayReferencesAsync(
        IReadOnlyCollection<string> serverIds,
        string? targetGatewayId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        cancellationToken.ThrowIfCancellationRequested();

        string[] ids = serverIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var vmMap = _allServers
            .Where(server => ids.Contains(server.Id, StringComparer.Ordinal))
            .ToDictionary(server => server.Id, StringComparer.Ordinal);
        if (vmMap.Count != ids.Length)
        {
            Core.Logging.FileLogger.Warn(
                $"UpdateGatewayReferencesAsync aborted because {ids.Length - vmMap.Count} selected VM(s) were missing.");
            return 0;
        }

        string? canonicalTargetGatewayId = null;
        if (!string.IsNullOrWhiteSpace(targetGatewayId))
        {
            AppSettings settings = await _configManager.LoadSettingsAsync();
            SshGatewayDto? targetGateway = settings.SshGateways.LastOrDefault(
                gateway => !string.IsNullOrWhiteSpace(gateway.Id)
                           && string.Equals(gateway.Id, targetGatewayId, StringComparison.OrdinalIgnoreCase));
            if (targetGateway is null)
            {
                StatusMessageRequested?.Invoke(
                    _localizer.Format("StatusBulkGatewayMissingTarget", ids.Length, targetGatewayId));
                Core.Logging.FileLogger.Warn(
                    $"UpdateGatewayReferencesAsync skipped {ids.Length} item(s) because gateway '{targetGatewayId}' no longer exists.");
                return 0;
            }

            canonicalTargetGatewayId = targetGateway.Id;
        }

        string[] finalSelectionIds = SelectedItems
            .Select(server => server.Id)
            .ToArray();
        string? primarySelectionId = SelectedServer?.Id;
        var updatedCount = 0;
        var unsupportedCount = 0;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (unsupportedCount > 0)
        {
            StatusMessageRequested?.Invoke(
                _localizer.Format("StatusBulkGatewayUnsupportedSkipped", unsupportedCount));
        }

        if (updatedCount > 0)
        {
            string targetLabel = canonicalTargetGatewayId ?? "<direct>";
            Core.Logging.FileLogger.Info(
                $"UpdateGatewayReferencesAsync updated gateway reference to '{targetLabel}' for {updatedCount} item(s) in a single transaction.");
        }

        return updatedCount;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id, StringComparer.Ordinal))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Length)
            {
                Core.Logging.FileLogger.Warn(
                    $"UpdateGatewayReferencesAsync aborted because {ids.Length - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            var updates = new List<(ServerItemViewModel OldVm, ServerProfileDto NewDto)>(ids.Length);
            foreach (string id in ids)
            {
                ServerProfileDto dto = dtoMap[id];
                bool changed;
                if (canonicalTargetGatewayId is null)
                {
                    changed = dto.SshGatewayId is not null || !dto.UseDirectConnection;
                    dto.SshGatewayId = null;
                    dto.UseDirectConnection = true;
                }
                else if (!ProtocolCapabilities.SupportsSshGateway(dto.ConnectionType))
                {
                    unsupportedCount++;
                    continue;
                }
                else
                {
                    changed = !string.Equals(dto.SshGatewayId, canonicalTargetGatewayId, StringComparison.Ordinal)
                              || dto.UseDirectConnection;
                    dto.SshGatewayId = canonicalTargetGatewayId;
                    dto.UseDirectConnection = false;
                }

                if (changed)
                {
                    updates.Add((vmMap[id], dto));
                }
            }

            updatedCount = updates.Count;
            if (updatedCount == 0)
            {
                return null;
            }

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                updates,
                Array.Empty<ServerProfileDto>(),
                finalSelectionIds,
                primarySelectionId,
                null,
                null,
                null);
        }
    }

    private async Task<bool> DeleteServersCoreAsync(
        IReadOnlyList<ServerItemViewModel> serversToDelete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serversToDelete);
        cancellationToken.ThrowIfCancellationRequested();

        var servers = NormalizeSelection(serversToDelete);
        if (servers.Count == 0)
        {
            return false;
        }

        var ids = servers
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var deleted = false;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (deleted)
        {
            Core.Logging.FileLogger.Info(
                $"DeleteServersCoreAsync deleted {ids.Count} item(s) in a single transaction.");
        }

        return deleted;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMatches = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToList();

            if (dtoMatches.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"DeleteServersCoreAsync aborted because {ids.Count - dtoMatches.Count} selected DTO(s) were missing.");
                return null;
            }

            serverDtos.RemoveAll(dto => ids.Contains(dto.Id));
            deleted = true;

            return new BulkMutationPlan(
                servers,
                Array.Empty<(ServerItemViewModel OldVm, ServerProfileDto NewDto)>(),
                Array.Empty<ServerProfileDto>(),
                Array.Empty<string>(),
                null,
                null,
                null,
                null);
        }
    }

    private async Task<int> MoveServersToProjectCoreAsync(
        IReadOnlyList<ServerItemViewModel> serversToMove,
        string? targetProjectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serversToMove);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTarget = NormalizeProjectForPersistence(targetProjectId);
        var selectedServers = NormalizeSelection(serversToMove);
        if (selectedServers.Count == 0)
        {
            return 0;
        }

        var moveCandidates = selectedServers
            .Where(server => !string.Equals(
                NormalizeProjectForSelection(server.ProjectId),
                NormalizeProjectForSelection(normalizedTarget),
                StringComparison.Ordinal))
            .ToList();

        if (moveCandidates.Count == 0)
        {
            return 0;
        }

        var selectedIds = selectedServers
            .Select(server => server.Id)
            .ToArray();
        var primarySelectionId = SelectedServer is not null && selectedServers.Contains(SelectedServer)
            ? SelectedServer.Id
            : selectedServers[^1].Id;
        var ids = moveCandidates
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var movedCount = 0;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (movedCount > 0)
        {
            Core.Logging.FileLogger.Info(
                $"MoveServersToProjectCoreAsync moved {moveCandidates.Count} item(s) to '{NormalizeProjectForSelection(normalizedTarget)}' in a single transaction.");
        }

        return movedCount;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"MoveServersToProjectCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            foreach (var server in moveCandidates)
            {
                dtoMap[server.Id].ProjectId = normalizedTarget;
            }

            movedCount = moveCandidates.Count;

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                moveCandidates
                    .Select(server => (OldVm: server, NewDto: dtoMap[server.Id]))
                    .ToArray(),
                Array.Empty<ServerProfileDto>(),
                selectedIds,
                primarySelectionId,
                null,
                null,
                null);
        }
    }

    internal async Task<BulkConnectPlan> PrepareBulkConnectPlanAsync(
        IReadOnlyList<ServerItemViewModel> serversToConnect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serversToConnect);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedServers = NormalizeSelection(serversToConnect);
        var settings = await _configManager.LoadSettingsAsync();
        if (selectedServers.Count == 0)
        {
            return new BulkConnectPlan(settings, [], 0);
        }

        var serverDtos = await _configManager.LoadServersAsync();
        var dtoMap = serverDtos.ToDictionary(dto => dto.Id, StringComparer.Ordinal);
        var candidates = new List<BulkConnectCandidate>(selectedServers.Count);
        var skippedCount = 0;

        foreach (var server in selectedServers)
        {
            if (IsToolEntry(server))
            {
                skippedCount++;
                Core.Logging.FileLogger.Info(
                    $"ConnectServersBulkCoreAsync skipped tool entry '{server.DisplayName}'.");
                continue;
            }

            if (_connectingServerIds.Contains(server.Id))
            {
                skippedCount++;
                Core.Logging.FileLogger.Info(
                    $"ConnectServersBulkCoreAsync skipped already-connecting item '{server.DisplayName}'.");
                continue;
            }

            if (!dtoMap.TryGetValue(server.Id, out var serverDto))
            {
                skippedCount++;
                Core.Logging.FileLogger.Warn(
                    $"ConnectServersBulkCoreAsync skipped missing DTO for id={server.Id}.");
                continue;
            }

            if (settings.GroupDefaults.Count > 0 && !string.IsNullOrEmpty(serverDto.Group))
            {
                var groupDefaults = Core.Configuration.GroupDefaultsDto.Resolve(
                    serverDto.Group,
                    settings.GroupDefaults);
                groupDefaults.ApplyTo(serverDto);
            }

            var skippedForCredentials = await TryResolveExternalCredentialsAsync(
                serverDto,
                settings,
                cancellationToken,
                skipOnFailure: true);
            if (skippedForCredentials)
            {
                skippedCount++;
                Core.Logging.FileLogger.Info(
                    $"ConnectServersBulkCoreAsync skipped '{server.DisplayName}' because credentials could not be resolved silently.");
                continue;
            }

            candidates.Add(new BulkConnectCandidate(server, serverDto));
        }

        return new BulkConnectPlan(settings, candidates, skippedCount);
    }

    internal async Task ConnectServersBulkCoreAsync(
        BulkConnectPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ConnectableCount <= 0)
        {
            StatusMessageRequested?.Invoke(_localizer["StatusBulkConnectNothingToConnect"]);
            return;
        }

        var connectedCount = 0;
        var failedCount = 0;
        var skippedCount = plan.SkippedCount;
        var cancelled = false;

        for (var i = 0; i < plan.Candidates.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var candidate = plan.Candidates[i];
            StatusMessageRequested?.Invoke(
                _localizer.Format(
                    "StatusConnectAllProgress",
                    i + 1,
                    plan.ConnectableCount,
                    candidate.Server.DisplayName));

            if (!_connectingServerIds.Add(candidate.Server.Id))
            {
                skippedCount++;
                Core.Logging.FileLogger.Info(
                    $"ConnectServersBulkCoreAsync skipped already-connecting item '{candidate.Server.DisplayName}' at execution time.");
                continue;
            }

            try
            {
                if (IsToolConnectionType(candidate.ServerDto.ConnectionType))
                {
                    skippedCount++;
                    continue;
                }

                string sessionId = SessionIdCodec.Create(candidate.Server.Id);
                Core.Logging.FileLogger.Info(
                    $"ConnectServersBulkCoreAsync: {candidate.Server.DisplayName} type={candidate.ServerDto.ConnectionType} gateway={candidate.ServerDto.SshGatewayId} sessionId={sessionId}");

                var outcome = await RunConnectionPipelineAsync(
                    candidate.ServerDto,
                    plan.Settings,
                    sessionId,
                    candidate.Server.Id,
                    candidate.Server,
                    cancellationToken);

                switch (outcome.Status)
                {
                    case BulkConnectOutcomeStatus.Success:
                        connectedCount++;
                        break;

                    case BulkConnectOutcomeStatus.PreflightFailed:
                    case BulkConnectOutcomeStatus.ConnectionFailed:
                    case BulkConnectOutcomeStatus.UnsupportedType:
                        failedCount++;
                        Core.Logging.FileLogger.Warn(
                            $"ConnectServersBulkCoreAsync failed for '{candidate.Server.DisplayName}': {outcome.ErrorMessage}");
                        break;

                    case BulkConnectOutcomeStatus.Skipped:
                        skippedCount++;
                        break;

                    case BulkConnectOutcomeStatus.Cancelled:
                        cancelled = true;
                        break;
                }
            }
            finally
            {
                _connectingServerIds.Remove(candidate.Server.Id);
            }

            if (cancelled)
            {
                break;
            }

            if (i < plan.Candidates.Count - 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
            }
        }

        StatusMessageRequested?.Invoke(
            _localizer.Format(
                "StatusBulkConnectSummary",
                connectedCount,
                failedCount,
                skippedCount));
    }

    private async Task<bool> MoveServersToGroupCoreAsync(
        IReadOnlyList<ServerItemViewModel> serversToMove,
        string? targetGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serversToMove);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTarget = NormalizeGroupForPersistence(targetGroup);
        var selectedServers = NormalizeSelection(serversToMove);
        if (selectedServers.Count == 0)
        {
            return false;
        }

        var moveCandidates = selectedServers
            .Where(server => !string.Equals(
                NormalizeGroupForPersistence(server.Group),
                normalizedTarget,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (moveCandidates.Count == 0)
        {
            return false;
        }

        var selectedIds = selectedServers
            .Select(server => server.Id)
            .ToArray();
        var primarySelectionId = SelectedServer is not null && selectedServers.Contains(SelectedServer)
            ? SelectedServer.Id
            : selectedServers[^1].Id;
        var ids = moveCandidates
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
        var moved = false;

        await ExecutePersistedBulkMutationAsync(BuildPlan, cancellationToken);

        if (moved)
        {
            Core.Logging.FileLogger.Info(
                $"MoveServersToGroupCoreAsync moved {moveCandidates.Count} item(s) to '{normalizedTarget ?? "<root>"}' in a single transaction.");
        }

        return moved;

        BulkMutationPlan? BuildPlan(List<ServerProfileDto> serverDtos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtoMap = serverDtos
                .Where(dto => ids.Contains(dto.Id))
                .ToDictionary(dto => dto.Id, StringComparer.Ordinal);

            if (dtoMap.Count != ids.Count)
            {
                Core.Logging.FileLogger.Warn(
                    $"MoveServersToGroupCoreAsync aborted because {ids.Count - dtoMap.Count} selected DTO(s) were missing.");
                return null;
            }

            foreach (var server in moveCandidates)
            {
                dtoMap[server.Id].Group = normalizedTarget;
            }

            moved = true;

            return new BulkMutationPlan(
                Array.Empty<ServerItemViewModel>(),
                moveCandidates
                    .Select(server => (OldVm: server, NewDto: dtoMap[server.Id]))
                    .ToArray(),
                Array.Empty<ServerProfileDto>(),
                selectedIds,
                primarySelectionId,
                null,
                null,
                null);
        }
    }

    private async Task ExecutePersistedBulkMutationAsync(
        Func<List<ServerProfileDto>, BulkMutationPlan?> buildPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildPlan);

        BulkMutationPlan? plan = await _configManager.MutateServersAsync(dtos =>
            buildPlan(dtos));
        if (plan is null || plan.IsNoOp)
        {
            return;
        }

        var settings = await _configManager.LoadSettingsAsync();
        _currentSettings = settings;
        var projectMap = BuildProjectMap(settings);
        var gatewayMap = BuildGatewayMap(settings);

        foreach (var vm in plan.ToRemove)
        {
            _allServers.Remove(vm);
            _connectionSm.Remove(vm.Id);
        }

        foreach (var (oldVm, newDto) in plan.ToUpdate)
        {
            if (_allServers.IndexOf(oldVm) < 0)
            {
                continue;
            }

            oldVm.UpdateFromDto(
                newDto,
                ResolveProject(projectMap, newDto.ProjectId),
                gatewayMap,
                _localizer);
        }

        foreach (var newDto in plan.ToAppend)
        {
            var newVm = ServerItemViewModel.FromDto(
                newDto,
                ResolveProject(projectMap, newDto.ProjectId),
                _connectionSm.GetState(newDto.Id).ToString(),
                gatewayMap,
                _localizer);
            _allServers.Add(newVm);
        }

        RefreshLookupCollections(settings);

        var anchorId = plan.FocusId ?? _selectionAnchor?.Id;
        ApplyFilter(plan.FocusId);

        if (plan.FinalSelectionIds is not null)
        {
            var currentById = _allServers.ToDictionary(vm => vm.Id, StringComparer.Ordinal);
            var finalSelection = new List<ServerItemViewModel>(plan.FinalSelectionIds.Count);
            foreach (var id in plan.FinalSelectionIds)
            {
                if (currentById.TryGetValue(id, out var vm))
                {
                    finalSelection.Add(vm);
                }
            }

            ServerItemViewModel? primary = null;
            if (plan.PrimarySelectionId is not null)
            {
                currentById.TryGetValue(plan.PrimarySelectionId, out primary);
            }

            ServerItemViewModel? anchor = null;
            if (anchorId is not null)
            {
                anchor = finalSelection.FirstOrDefault(vm => string.Equals(vm.Id, anchorId, StringComparison.Ordinal));
            }

            anchor ??= primary;
            ApplySelection(finalSelection, primary, anchor, updateSelectedServer: true);
        }

        if (plan.ToRemove.Count > 0 || plan.ToAppend.Count > 0)
        {
            OnPropertyChanged(nameof(Servers));
            OnPropertyChanged(nameof(IsEmpty));
        }

        if (plan.StatusKey is null)
        {
            return;
        }

        var message = plan.StatusArgs is { Length: > 0 }
            ? _localizer.Format(plan.StatusKey, plan.StatusArgs)
            : _localizer[plan.StatusKey];
        StatusMessageRequested?.Invoke(message);
    }

    private static int GetEditablePort(ServerItemViewModel server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.EffectivePort;
    }

    private static void SetEditablePort(ServerProfileDto dto, int port)
    {
        ArgumentNullException.ThrowIfNull(dto);

        switch (dto.ConnectionType?.ToUpperInvariant())
        {
            case "SSH":
            case "SFTP":
                dto.SshPort = port;
                dto.RemotePort = port;
                break;

            case "FTP":
                dto.FtpPort = port;
                dto.RemotePort = port;
                break;

            case "WINRM":
                dto.WinRmPort = port;
                break;

            case "VNC":
                dto.VncPort = port;
                dto.RemotePort = port;
                break;

            case "TELNET":
                dto.TelnetPort = port;
                dto.RemotePort = port;
                break;

            default:
                dto.RemotePort = port;
                break;
        }
    }

    private static void SetEditableUsername(ServerProfileDto dto, string username)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrEmpty(username);

        switch (dto.ConnectionType?.ToUpperInvariant())
        {
            case "SSH":
            case "SFTP":
                dto.SshUsername = username;
                break;

            case "FTP":
                dto.FtpUsername = username;
                break;

            case "WINRM":
                dto.WinRmUsername = username;
                dto.WinRmIdentityMode = Core.Configuration.WinRmIdentityMode.Credential;
                break;

            case "TELNET":
                dto.TelnetUsername = username;
                break;

            default:
                dto.RdpUsername = username;
                break;
        }
    }

    private static void SetEditablePassword(ServerProfileDto dto, string encryptedPassword)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrEmpty(encryptedPassword);

        switch (dto.ConnectionType?.ToUpperInvariant())
        {
            case "SSH":
            case "SFTP":
                dto.SshPasswordEncrypted = encryptedPassword;
                break;

            case "FTP":
                dto.FtpPasswordEncrypted = encryptedPassword;
                break;

            case "WINRM":
                dto.WinRmPasswordEncrypted = encryptedPassword;
                dto.WinRmIdentityMode = Core.Configuration.WinRmIdentityMode.Credential;
                break;

            case "TELNET":
                dto.TelnetPasswordEncrypted = encryptedPassword;
                break;

            case "VNC":
                dto.VncPassword = encryptedPassword;
                break;

            default:
                dto.RdpPasswordEncrypted = encryptedPassword;
                break;
        }
    }

    private static string? NormalizeProjectForPersistence(string? projectId)
    {
        return string.IsNullOrWhiteSpace(projectId) ? null : projectId;
    }

    private static string NormalizeProjectForSelection(string? projectId)
    {
        return NormalizeProjectForPersistence(projectId) ?? string.Empty;
    }

    private string GenerateUniqueDisplayName(string sourceDisplayName, HashSet<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);

        var baseName = sourceDisplayName + _localizer["ServerDuplicateSuffix"];
        if (existingNames.Add(baseName))
        {
            return baseName;
        }

        for (var duplicateNumber = 2; ; duplicateNumber++)
        {
            var candidate = $"{baseName} {duplicateNumber}";
            if (existingNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsToolEntry(ServerItemViewModel server)
    {
        return IsToolConnectionType(server.ConnectionType);
    }

    private static bool IsToolConnectionType(string? connectionType)
    {
        return connectionType?.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed record BulkMutationPlan(
        IReadOnlyList<ServerItemViewModel> ToRemove,
        IReadOnlyList<(ServerItemViewModel OldVm, ServerProfileDto NewDto)> ToUpdate,
        IReadOnlyList<ServerProfileDto> ToAppend,
        IReadOnlyList<string>? FinalSelectionIds,
        string? PrimarySelectionId,
        string? FocusId,
        string? StatusKey,
        object[]? StatusArgs)
    {
        public bool IsNoOp => ToRemove.Count == 0
                           && ToUpdate.Count == 0
                           && ToAppend.Count == 0;
    }

    internal sealed record BulkConnectPlan(
        AppSettings Settings,
        IReadOnlyList<BulkConnectCandidate> Candidates,
        int SkippedCount)
    {
        public int ConnectableCount => Candidates.Count;
    }

    internal sealed record BulkConnectCandidate(
        ServerItemViewModel Server,
        ServerProfileDto ServerDto);
}

public sealed record BulkSelectionContext(
    IReadOnlyList<ServerItemViewModel> Items,
    ServerItemViewModel? Primary);

public sealed record BulkMoveToProjectRequest(string? ProjectId);

public sealed record BulkMoveToGroupRequest(string? GroupName);
