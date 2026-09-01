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
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.Services.PostConnect;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.ViewModels.Dialogs;

public partial class ServerDialogViewModel
{
    public Heimdall.App.Services.IDialogService? DialogService { get; set; }
    public IServiceScopeFactory? ServiceScopeFactory { get; set; }

    public ServerDialogViewModel()
        : this(new WinFormsMonitorEnumerator())
    {
    }

    internal ServerDialogViewModel(int screenCount)
        : this(new StaticMonitorEnumerator(CreateStaticMonitors(screenCount)))
    {
    }

    internal ServerDialogViewModel(IMonitorEnumerator monitorEnumerator)
    {
        _monitorEnumerator = monitorEnumerator ?? throw new ArgumentNullException(nameof(monitorEnumerator));
        RefreshAvailableMonitors(preferredSelection: []);
        PostConnectSteps.CollectionChanged += OnPostConnectStepsChanged;
    }

    public ObservableCollection<PostConnectStepItemViewModel> PostConnectSteps { get; } = [];

    public ObservableCollection<MonitorChoiceViewModel> AvailableMonitors { get; } = [];

    private static MonitorInfo[] CreateStaticMonitors(int screenCount)
        => Enumerable.Range(0, Math.Max(0, screenCount))
            .Select(index => new MonitorInfo(
                index,
                1920,
                1080,
                index == 0,
                $"DISPLAY{index + 1}"))
            .ToArray();

    private sealed class StaticMonitorEnumerator(IReadOnlyList<MonitorInfo> monitors) : IMonitorEnumerator
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
    }

    [ObservableProperty]
    private PostConnectStepItemViewModel? _selectedPostConnectStep;

    public IReadOnlyList<PostConnectFailureOption> PostConnectFailureOptions =>
    [
        new(PostConnectFailurePolicy.Continue, L("ServerDialogPostConnectFailureContinue")),
        new(PostConnectFailurePolicy.Stop, L("ServerDialogPostConnectFailureStop"))
    ];

    /// <summary>
    /// Drives the sentence shown over the empty list. An empty post-connect sequence is the
    /// normal case, so the blank card is what most users see first and it has to say something.
    /// </summary>
    public bool HasNoPostConnectSteps => PostConnectSteps.Count == 0;

    public bool HasLegacyPostConnectCommand => !string.IsNullOrWhiteSpace(PostConnectCommand);

    public string LegacyPostConnectCommandText => PostConnectCommand;

    public string LegacyPostConnectDelayText => PostConnectDelayMs > 0
        ? PostConnectDelayMs.ToString(CultureInfo.InvariantCulture)
        : "0";

    public bool CanRemoveSelectedPostConnectStep => SelectedPostConnectStep is not null;

    public bool CanMoveSelectedPostConnectStepUp =>
        SelectedPostConnectStep is not null && PostConnectSteps.IndexOf(SelectedPostConnectStep) > 0;

    public bool CanMoveSelectedPostConnectStepDown =>
        SelectedPostConnectStep is not null
        && PostConnectSteps.IndexOf(SelectedPostConnectStep) >= 0
        && PostConnectSteps.IndexOf(SelectedPostConnectStep) < PostConnectSteps.Count - 1;

    partial void OnSelectedPostConnectStepChanged(PostConnectStepItemViewModel? value)
    {
        RemovePostConnectStepCommand.NotifyCanExecuteChanged();
        MovePostConnectStepUpCommand.NotifyCanExecuteChanged();
        MovePostConnectStepDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddPostConnectStep()
    {
        var step = new PostConnectStepItemViewModel();
        AttachPostConnectStep(step);
        PostConnectSteps.Add(step);
        SelectedPostConnectStep = step;
        IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedPostConnectStep))]
    private void RemovePostConnectStep()
    {
        if (SelectedPostConnectStep is null)
        {
            return;
        }

        var toRemove = SelectedPostConnectStep;
        DetachPostConnectStep(toRemove);
        var index = PostConnectSteps.IndexOf(toRemove);
        PostConnectSteps.Remove(toRemove);
        if (PostConnectSteps.Count == 0)
        {
            SelectedPostConnectStep = null;
        }
        else
        {
            SelectedPostConnectStep = PostConnectSteps[Math.Clamp(index, 0, PostConnectSteps.Count - 1)];
        }

        IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedPostConnectStepUp))]
    private void MovePostConnectStepUp()
    {
        if (SelectedPostConnectStep is null)
        {
            return;
        }

        var index = PostConnectSteps.IndexOf(SelectedPostConnectStep);
        if (index <= 0)
        {
            return;
        }

        PostConnectSteps.Move(index, index - 1);
        IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelectedPostConnectStepDown))]
    private void MovePostConnectStepDown()
    {
        if (SelectedPostConnectStep is null)
        {
            return;
        }

        var index = PostConnectSteps.IndexOf(SelectedPostConnectStep);
        if (index < 0 || index >= PostConnectSteps.Count - 1)
        {
            return;
        }

        PostConnectSteps.Move(index, index + 1);
        IsDirty = true;
    }

    [RelayCommand]
    private async Task LinkCommandLibraryAsync(PostConnectStepItemViewModel? item)
    {
        if (item is null || DialogService is null || Localizer is null || ServiceScopeFactory is null)
        {
            return;
        }

        var pickerVm = new CommandLibraryPickerDialogViewModel(Localizer, ServiceScopeFactory);
        var result = await DialogService.ShowCommandLibraryPickerAsync(
            pickerVm,
            BuildAutoPrefillContext());
        if (result is null)
        {
            return;
        }

        item.CommandLibraryId = result.ActionId;
        item.CommandLibraryParams = new Dictionary<string, string>(result.Parameters, StringComparer.Ordinal);
        item.LinkedActionTitle = result.ActionTitle;
        item.IsBroken = false;
        IsDirty = true;
    }

    [RelayCommand]
    private async Task ChangeCommandLibraryAsync(PostConnectStepItemViewModel? item)
    {
        if (item is null
            || !item.IsLinked
            || string.IsNullOrWhiteSpace(item.CommandLibraryId)
            || DialogService is null
            || Localizer is null
            || ServiceScopeFactory is null)
        {
            return;
        }

        var pickerVm = new CommandLibraryPickerDialogViewModel(Localizer, ServiceScopeFactory);
        var result = await DialogService.ShowCommandLibraryPickerAsync(
            pickerVm,
            BuildAutoPrefillContext(),
            item.CommandLibraryId,
            item.CommandLibraryParams ?? new Dictionary<string, string>(StringComparer.Ordinal));
        if (result is null)
        {
            return;
        }

        item.CommandLibraryId = result.ActionId;
        item.CommandLibraryParams = new Dictionary<string, string>(result.Parameters, StringComparer.Ordinal);
        item.LinkedActionTitle = result.ActionTitle;
        item.IsBroken = false;
        IsDirty = true;
    }

    [RelayCommand]
    private void UnlinkCommandLibrary(PostConnectStepItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.CommandLibraryId = null;
        item.CommandLibraryParams = null;
        item.LinkedActionTitle = null;
        item.IsBroken = false;
        IsDirty = true;
    }

    public void LoadPostConnectSteps(IEnumerable<PostConnectStep> steps)
    {
        var previousInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            foreach (var existing in PostConnectSteps.ToArray())
            {
                DetachPostConnectStep(existing);
            }

            PostConnectSteps.Clear();
            foreach (var step in steps.Select(PostConnectStepItemViewModel.FromModel))
            {
                AttachPostConnectStep(step);
                PostConnectSteps.Add(step);
            }

            SelectedPostConnectStep = PostConnectSteps.FirstOrDefault();
            OnPropertyChanged(nameof(HasLegacyPostConnectCommand));
            OnPropertyChanged(nameof(LegacyPostConnectCommandText));
            OnPropertyChanged(nameof(LegacyPostConnectDelayText));
        }
        finally
        {
            _isInitializing = previousInitializing;
        }
    }

    private void OnPostConnectStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Announced before either guard below. The empty-state sentence is a property of what the
        // list holds, not of who changed it, and opening a saved session is both the only path
        // that fills the list with no user gesture and the one both guards drop: it runs under
        // _isInitializing, and it empties the list through Clear, which reports Reset.
        OnPropertyChanged(nameof(HasNoPostConnectSteps));

        if (_isInitializing)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        IsDirty = true;
        RemovePostConnectStepCommand.NotifyCanExecuteChanged();
        MovePostConnectStepUpCommand.NotifyCanExecuteChanged();
        MovePostConnectStepDownCommand.NotifyCanExecuteChanged();
    }

    private void OnPostConnectStepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        IsDirty = true;
    }

    private void AttachPostConnectStep(PostConnectStepItemViewModel step)
    {
        step.PropertyChanged += OnPostConnectStepPropertyChanged;
    }

    private void DetachPostConnectStep(PostConnectStepItemViewModel step)
    {
        step.PropertyChanged -= OnPostConnectStepPropertyChanged;
    }

    private AutoPrefillContext BuildAutoPrefillContext()
    {
        string? username = null;
        int? port = null;
        if (IsSshFamilyConnection)
        {
            username = SshUsername;
            port = SshPort;
        }
        else if (IsRdpConnection)
        {
            username = RdpUsername;
            port = RemotePort;
        }
        else if (IsFtpConnection)
        {
            username = FtpUsername;
            port = FtpPort;
        }
        else if (IsTelnetConnection)
        {
            port = RemotePort;
        }

        return new AutoPrefillContext(
            RemoteServer,
            port,
            username,
            ConnectionType);
    }

    public async Task InitializePostConnectLinksAsync(CancellationToken cancellationToken = default)
    {
        if (ServiceScopeFactory is null)
        {
            return;
        }

        var linkedIds = PostConnectSteps
            .Where(step => step.IsLinked)
            .Select(step => step.CommandLibraryId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (linkedIds.Count == 0)
        {
            return;
        }

        using var scope = ServiceScopeFactory.CreateScope();
        var actionService = scope.ServiceProvider.GetRequiredService<IActionService>();
        var actions = (await actionService.GetAllActionsAsync().ConfigureAwait(false))
            .Where(action => linkedIds.Contains(action.Id, StringComparer.Ordinal))
            .ToDictionary(action => action.Id, action => action.Title, StringComparer.Ordinal);

        // Resolving the titles is the dialog reading the command library, not the user editing the
        // session: neither LinkedActionTitle nor IsBroken is written by ToModel, and both are
        // assigned on every open. Tracked, they reached OnPostConnectStepPropertyChanged and armed
        // the unsaved-changes guard before the window was shown, on every session with a linked
        // step - the same defect the reset in FromDto was meant to end, through the one open-path
        // call that runs after it.
        RunWithoutDirtyTracking(() =>
        {
            foreach (var step in PostConnectSteps.Where(step => step.IsLinked))
            {
                if (step.CommandLibraryId is null)
                {
                    continue;
                }

                if (actions.TryGetValue(step.CommandLibraryId, out var title))
                {
                    step.LinkedActionTitle = title;
                    step.IsBroken = false;
                }
                else
                {
                    step.LinkedActionTitle = null;
                    step.IsBroken = true;
                }
            }
        });
    }
}

public sealed record PostConnectFailureOption(PostConnectFailurePolicy Value, string Label);
