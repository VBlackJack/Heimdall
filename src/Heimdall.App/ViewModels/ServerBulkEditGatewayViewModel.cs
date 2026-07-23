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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Collects an explicit routing choice for a bulk server update without exposing
/// gateway credentials to the dialog.
/// </summary>
public sealed class ServerBulkEditGatewayViewModel : ObservableObject
{
    private ServerBulkEditGatewayChoice? _selectedChoice;
    private GatewayOption? _selectedGateway;
    private bool _isApplyEnabled;
    private ServerBulkEditGatewayResult? _resolvedResult;

    public ServerBulkEditGatewayViewModel(
        LocalizationManager localizer,
        int count,
        IEnumerable<GatewayOption> availableGateways)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(availableGateways);

        Count = count;
        Header = localizer.Format("BulkEditGatewayHeader", count);
        AvailableGateways = new ObservableCollection<GatewayOption>(availableGateways);
        RefreshResolution();
    }

    public int Count { get; }

    public string Header { get; }

    /// <summary>
    /// Credential-free gateway choices. This collection must never contain
    /// <see cref="Heimdall.Core.Configuration.SshGatewayDto"/> instances.
    /// </summary>
    public ObservableCollection<GatewayOption> AvailableGateways { get; }

    public GatewayOption? SelectedGateway
    {
        get => _selectedGateway;
        set
        {
            if (SetProperty(ref _selectedGateway, value))
            {
                RefreshResolution();
            }
        }
    }

    public bool IsGatewayChoiceSelected
    {
        get => _selectedChoice == ServerBulkEditGatewayChoice.UseGateway;
        set
        {
            if (value)
            {
                SelectChoice(ServerBulkEditGatewayChoice.UseGateway);
            }
        }
    }

    public bool IsDirectChoiceSelected
    {
        get => _selectedChoice == ServerBulkEditGatewayChoice.DirectConnection;
        set
        {
            if (value)
            {
                SelectChoice(ServerBulkEditGatewayChoice.DirectConnection);
            }
        }
    }

    public bool IsInheritChoiceSelected
    {
        get => _selectedChoice == ServerBulkEditGatewayChoice.InheritFolderDefault;
        set
        {
            if (value)
            {
                SelectChoice(ServerBulkEditGatewayChoice.InheritFolderDefault);
            }
        }
    }

    public bool IsGatewayPickerEnabled => IsGatewayChoiceSelected;

    public bool IsApplyEnabled
    {
        get => _isApplyEnabled;
        private set => SetProperty(ref _isApplyEnabled, value);
    }

    public ServerBulkEditGatewayResult? ResolvedResult
    {
        get => _resolvedResult;
        private set => SetProperty(ref _resolvedResult, value);
    }

    private void SelectChoice(ServerBulkEditGatewayChoice choice)
    {
        if (_selectedChoice == choice)
        {
            return;
        }

        _selectedChoice = choice;
        OnPropertyChanged(nameof(IsGatewayChoiceSelected));
        OnPropertyChanged(nameof(IsDirectChoiceSelected));
        OnPropertyChanged(nameof(IsInheritChoiceSelected));
        OnPropertyChanged(nameof(IsGatewayPickerEnabled));
        RefreshResolution();
    }

    private void RefreshResolution()
    {
        ResolvedResult = _selectedChoice switch
        {
            ServerBulkEditGatewayChoice.UseGateway when SelectedGateway is not null =>
                new ServerBulkEditGatewayResult(
                    ServerBulkEditGatewayChoice.UseGateway,
                    SelectedGateway.Id),
            ServerBulkEditGatewayChoice.DirectConnection =>
                new ServerBulkEditGatewayResult(
                    ServerBulkEditGatewayChoice.DirectConnection,
                    GatewayId: null),
            ServerBulkEditGatewayChoice.InheritFolderDefault =>
                new ServerBulkEditGatewayResult(
                    ServerBulkEditGatewayChoice.InheritFolderDefault,
                    GatewayId: null),
            _ => null
        };
        IsApplyEnabled = ResolvedResult is not null;
    }
}

/// <summary>
/// Explicit bulk routing choice and its persisted server-field mapping.
/// </summary>
public enum ServerBulkEditGatewayChoice
{
    /// <summary>Sets <c>SshGatewayId</c> to the canonical gateway ID and <c>UseDirectConnection</c> to false.</summary>
    UseGateway,

    /// <summary>Sets <c>SshGatewayId</c> to null and <c>UseDirectConnection</c> to true.</summary>
    DirectConnection,

    /// <summary>Sets <c>SshGatewayId</c> to null and <c>UseDirectConnection</c> to false.</summary>
    InheritFolderDefault
}

/// <summary>
/// Validated result returned by the bulk gateway dialog.
/// </summary>
public sealed record ServerBulkEditGatewayResult(
    ServerBulkEditGatewayChoice Choice,
    string? GatewayId);
