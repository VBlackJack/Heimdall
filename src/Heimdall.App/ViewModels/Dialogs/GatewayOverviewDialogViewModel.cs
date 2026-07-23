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
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

public sealed record GatewayOverviewMutationRequest(
    IReadOnlyList<string> ServerIds,
    string? TargetGatewayId);

public sealed partial class GatewayOverviewDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly IReadOnlyList<GatewayOption> _availableGateways;
    private readonly Func<GatewayOverviewMutationRequest, CancellationToken, Task<int>>? _updateGatewayReferencesAsync;
    private readonly Func<CancellationToken, Task<GatewayOverview>>? _reloadOverviewAsync;

    public GatewayOverviewDialogViewModel(GatewayOverview overview, LocalizationManager localizer)
        : this(overview, localizer, [], null, null, null)
    {
    }

    public GatewayOverviewDialogViewModel(
        GatewayOverview overview,
        LocalizationManager localizer,
        IEnumerable<GatewayOption>? availableGateways,
        Func<GatewayOverviewMutationRequest, CancellationToken, Task<int>>? updateGatewayReferencesAsync,
        Func<CancellationToken, Task<GatewayOverview>>? reloadOverviewAsync,
        string? warningMessage = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        _availableGateways = (availableGateways ?? [])
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Id))
            .ToList();
        _updateGatewayReferencesAsync = updateGatewayReferencesAsync;
        _reloadOverviewAsync = reloadOverviewAsync;

        DialogTitle = localizer["GatewayOverviewTitle"];
        Description = localizer["GatewayOverviewDescription"];
        CloseLabel = localizer["BtnClose"];
        EmptyGatewaysText = localizer["GatewayOverviewEmpty"];
        MissingReferencesDescription = localizer["GatewayOverviewMissingDescription"];
        WarningMessage = warningMessage ?? "";

        ApplyOverview(overview);
    }

    public string DialogTitle { get; }

    public string Description { get; }

    public string CloseLabel { get; }

    [ObservableProperty]
    private string _gatewaySummary = "";

    [ObservableProperty]
    private string _routedSessionSummary = "";

    [ObservableProperty]
    private string _missingReferenceSummary = "";

    public string EmptyGatewaysText { get; }

    public string MissingReferencesDescription { get; }

    public ObservableCollection<GatewayOverviewGatewayItemViewModel> Gateways { get; } = [];

    public ObservableCollection<GatewayOverviewMissingReferenceItemViewModel> MissingReferences { get; } = [];

    public string WarningMessage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasGateways => Gateways.Count > 0;

    public bool HasMissingReferences => MissingReferences.Count > 0;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasWarningMessage => !string.IsNullOrWhiteSpace(WarningMessage);

    private bool CanMutate => _updateGatewayReferencesAsync is not null;

    internal async Task ResolveMissingReferenceAsync(
        GatewayOverviewMissingReferenceItemViewModel missingReference,
        string? targetGatewayId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(missingReference);
        if (_updateGatewayReferencesAsync is null)
        {
            return;
        }

        IsBusy = true;
        missingReference.IsBusy = true;
        RefreshMissingReferenceCommands();
        StatusMessage = "";

        try
        {
            var request = new GatewayOverviewMutationRequest(
                missingReference.SessionIds,
                string.IsNullOrWhiteSpace(targetGatewayId) ? null : targetGatewayId);
            int updatedCount = await _updateGatewayReferencesAsync(request, cancellationToken);

            if (_reloadOverviewAsync is not null)
            {
                GatewayOverview refreshed = await _reloadOverviewAsync(cancellationToken);
                ApplyOverview(refreshed);
            }
            else if (updatedCount > 0)
            {
                MissingReferences.Remove(missingReference);
                OnPropertyChanged(nameof(HasMissingReferences));
            }

            StatusMessage = updatedCount <= 0
                ? _localizer["GatewayOverviewActionNoOp"]
                : request.TargetGatewayId is null
                    ? _localizer.Format("GatewayOverviewClearSuccess", updatedCount)
                    : _localizer.Format("GatewayOverviewReassignSuccess", updatedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer.Format("GatewayOverviewActionFailed", ex.Message);
        }
        finally
        {
            missingReference.IsBusy = false;
            IsBusy = false;
            RefreshMissingReferenceCommands();
        }
    }

    private void ApplyOverview(GatewayOverview overview)
    {
        GatewaySummary = _localizer.Format("GatewayOverviewSummaryGateways", overview.GatewayCount);
        RoutedSessionSummary = _localizer.Format("GatewayOverviewSummaryRoutedSessions", overview.RoutedSessionCount);
        MissingReferenceSummary = _localizer.Format(
            "GatewayOverviewSummaryMissingReferences",
            overview.MissingReferenceCount);

        Gateways.Clear();
        foreach (GatewayOverviewGatewayGroup gateway in overview.Gateways)
        {
            Gateways.Add(new GatewayOverviewGatewayItemViewModel(gateway, _localizer));
        }

        MissingReferences.Clear();
        foreach (GatewayOverviewMissingReferenceGroup reference in overview.MissingReferences)
        {
            MissingReferences.Add(new GatewayOverviewMissingReferenceItemViewModel(
                reference,
                _localizer,
                _availableGateways,
                CanMutate,
                () => IsBusy,
                ResolveMissingReferenceAsync));
        }

        OnPropertyChanged(nameof(HasGateways));
        OnPropertyChanged(nameof(HasMissingReferences));
    }

    private void RefreshMissingReferenceCommands()
    {
        foreach (GatewayOverviewMissingReferenceItemViewModel reference in MissingReferences)
        {
            reference.RefreshCanExecute();
        }
    }
}

public sealed class GatewayOverviewGatewayItemViewModel
{
    public GatewayOverviewGatewayItemViewModel(GatewayOverviewGatewayGroup group, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(localizer);

        GatewayName = group.GatewayName;
        Endpoint = group.Endpoint;
        ParentText = string.IsNullOrWhiteSpace(group.ParentGatewayName)
            ? ""
            : localizer.Format("GatewayOverviewParentFormat", group.ParentGatewayName);
        SessionCountText = localizer.Format("GatewayOverviewSessionCount", group.Sessions.Count);
        EmptySessionsText = localizer["GatewayOverviewNoSessions"];
        Sessions = new ObservableCollection<GatewayOverviewSessionItemViewModel>(
            group.Sessions.Select(session => new GatewayOverviewSessionItemViewModel(session)));
    }

    public string GatewayName { get; }

    public string Endpoint { get; }

    public string ParentText { get; }

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentText);

    public string SessionCountText { get; }

    public string EmptySessionsText { get; }

    public ObservableCollection<GatewayOverviewSessionItemViewModel> Sessions { get; }

    public bool HasSessions => Sessions.Count > 0;
}

public sealed partial class GatewayOverviewMissingReferenceItemViewModel : ObservableObject
{
    private readonly Func<bool> _isParentBusy;
    private readonly Func<GatewayOverviewMissingReferenceItemViewModel, string?, CancellationToken, Task> _resolveAsync;

    public GatewayOverviewMissingReferenceItemViewModel(
        GatewayOverviewMissingReferenceGroup group,
        LocalizationManager localizer)
        : this(group, localizer, [], false, static () => false, static (_, _, _) => Task.CompletedTask)
    {
    }

    public GatewayOverviewMissingReferenceItemViewModel(
        GatewayOverviewMissingReferenceGroup group,
        LocalizationManager localizer,
        IReadOnlyList<GatewayOption> availableGateways,
        bool isActionEnabled,
        Func<bool> isParentBusy,
        Func<GatewayOverviewMissingReferenceItemViewModel, string?, CancellationToken, Task> resolveAsync)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(availableGateways);
        ArgumentNullException.ThrowIfNull(isParentBusy);
        ArgumentNullException.ThrowIfNull(resolveAsync);

        _isParentBusy = isParentBusy;
        _resolveAsync = resolveAsync;
        GatewayId = group.GatewayId;
        HeaderText = localizer.Format("GatewayOverviewMissingHeader", group.GatewayId);
        SessionCountText = localizer.Format("GatewayOverviewSessionCount", group.Sessions.Count);
        ConfigurationReferenceCountText = localizer.Format(
            "GatewayOverviewConfigurationReferenceCount",
            group.ConfigurationReferences.Count);
        SessionIds = group.Sessions.Select(session => session.Id).ToArray();
        Sessions = new ObservableCollection<GatewayOverviewSessionItemViewModel>(
            group.Sessions.Select(session => new GatewayOverviewSessionItemViewModel(session)));
        ConfigurationReferences = new ObservableCollection<GatewayOverviewConfigurationReferenceItemViewModel>(
            group.ConfigurationReferences.Select(reference =>
                new GatewayOverviewConfigurationReferenceItemViewModel(reference, localizer)));
        AvailableGateways = new ObservableCollection<GatewayOption>(availableGateways);
        IsActionEnabled = isActionEnabled;
        SelectedGatewayId = AvailableGateways.FirstOrDefault()?.Id ?? "";
    }

    public string GatewayId { get; }

    public string HeaderText { get; }

    public string SessionCountText { get; }

    public string ConfigurationReferenceCountText { get; }

    public IReadOnlyList<string> SessionIds { get; }

    public ObservableCollection<GatewayOverviewSessionItemViewModel> Sessions { get; }

    public ObservableCollection<GatewayOverviewConfigurationReferenceItemViewModel> ConfigurationReferences { get; }

    public ObservableCollection<GatewayOption> AvailableGateways { get; }

    public bool HasSessions => Sessions.Count > 0;

    public bool HasConfigurationReferences => ConfigurationReferences.Count > 0;

    public bool HasAvailableGateways => AvailableGateways.Count > 0;

    public bool IsActionEnabled { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReassignCommand))]
    private string _selectedGatewayId = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReassignCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private bool _isBusy;

    [RelayCommand(CanExecute = nameof(CanReassign))]
    private async Task ReassignAsync(CancellationToken cancellationToken)
    {
        await _resolveAsync(this, SelectedGatewayId, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _resolveAsync(this, null, cancellationToken);
    }

    private bool CanReassign() =>
        IsActionEnabled
        && !_isParentBusy()
        && !IsBusy
        && HasAvailableGateways
        && !string.IsNullOrWhiteSpace(SelectedGatewayId)
        && SessionIds.Count > 0;

    private bool CanClear() =>
        IsActionEnabled
        && !_isParentBusy()
        && !IsBusy
        && SessionIds.Count > 0;

    internal void RefreshCanExecute()
    {
        ReassignCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }
}

public sealed class GatewayOverviewConfigurationReferenceItemViewModel
{
    public GatewayOverviewConfigurationReferenceItemViewModel(
        GatewayOverviewConfigurationReference reference,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(localizer);

        Id = reference.Id;
        DisplayName = reference.DisplayName;
        ReferenceType = reference.Kind switch
        {
            GatewayOverviewConfigurationReferenceKind.GroupDefault =>
                localizer["GatewayOverviewGroupDefaultReferenceType"],
            GatewayOverviewConfigurationReferenceKind.ChildGatewayParent =>
                localizer["GatewayOverviewChildGatewayReferenceType"],
            _ => ""
        };
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string ReferenceType { get; }
}

public sealed class GatewayOverviewSessionItemViewModel
{
    public GatewayOverviewSessionItemViewModel(GatewayOverviewSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Id = session.Id;
        DisplayName = session.DisplayName;
        Metadata = string.Join(
            " | ",
            new[]
            {
                session.ConnectionType,
                session.Endpoint,
                session.GroupPath
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Metadata { get; }

    public bool HasMetadata => !string.IsNullOrWhiteSpace(Metadata);
}
