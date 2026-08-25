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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Creates an SSH gateway from any surface that is not the settings panel.
/// </summary>
public interface IGatewayCreationService
{
    /// <summary>
    /// Prompts for a new gateway and persists it.
    /// </summary>
    /// <returns>
    /// The gateway as persisted, identifier included, or <see langword="null"/> when the
    /// user cancelled the dialog.
    /// </returns>
    Task<SshGatewayDto?> CreateAsync();
}

/// <summary>
/// The single owner of what "create a gateway outside the settings panel" means.
/// </summary>
/// <remarks>
/// Two surfaces need it - the Add menu with the tree context menu, and the network tab of
/// the server dialog - and neither owns a Save button. Both used to be candidates for their
/// own copy of the sequence: prompt, assign an identifier, write. A second copy is how the
/// two would have drifted, which is the defect this whole item exists to undo. The write
/// goes through <see cref="IConfigManager.MergeSettingAsync"/> so it reloads from disk under
/// the write lock rather than overwriting whatever another surface has stored meanwhile.
/// </remarks>
public sealed class GatewayCreationService : IGatewayCreationService
{
    private readonly IConfigManager _configManager;
    private readonly IDialogService _dialogService;

    public GatewayCreationService(IConfigManager configManager, IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(dialogService);

        _configManager = configManager;
        _dialogService = dialogService;
    }

    /// <inheritdoc/>
    public async Task<SshGatewayDto?> CreateAsync()
    {
        AppSettings persisted = await _configManager.LoadSettingsAsync();

        GatewayDialogViewModel dialogViewModel = new()
        {
            AvailableParents = new ObservableCollection<GatewayOption>(
                persisted.SshGateways.Select(
                    gateway => new GatewayOption(gateway.Id, $"{gateway.Name} ({gateway.Host})")))
        };

        GatewayDialogResult? result = await _dialogService.ShowGatewayDialogAsync(dialogViewModel);
        if (result?.Saved != true)
        {
            return null;
        }

        SshGatewayDto created = result.Gateway;
        created.Id = Guid.NewGuid().ToString();

        await _configManager.MergeSettingAsync(
            settings => settings.SshGateways.Add(created.CloneFaithfully()));

        return created;
    }
}
