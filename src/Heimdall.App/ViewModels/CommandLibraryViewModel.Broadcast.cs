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
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Models;
using TwinShell.Core.Enums;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Broadcast partial of <see cref="CommandLibraryViewModel"/>: sending one generated command to
/// several open terminals at once.
/// </summary>
/// <remarks>
/// <para>
/// Four decisions shape what this does, and each of them is a decision rather than an
/// implementation detail.
/// </para>
/// <para>
/// <b>It reaches open terminals only.</b> Connecting on the operator's behalf in order to run a
/// command is a different feature, with credentials, host keys and a guard that can refuse. It is
/// not folded in behind a checkbox here.
/// </para>
/// <para>
/// <b>Every target receives the same text.</b> Substituting a parameter per target would generate
/// a different command per machine from one visible preview, which is a worse thing to get wrong
/// than it is useful to have.
/// </para>
/// <para>
/// <b>One confirmation, naming the count.</b> Not one per target: a dialog that appears twenty
/// times is a dialog people learn to dismiss. The confirmation fires for every broadcast whatever
/// the action's declared level, because reaching several machines at once is itself the thing
/// being confirmed. The single-target Send asks only when the action is dangerous, and that
/// difference is deliberate.
/// </para>
/// <para>
/// <b>A refusal stops the batch; a failure does not.</b> If a target no longer takes the command,
/// the remaining targets still receive it and the summary names how many did not. Stopping half
/// way would leave the operator with a set of machines they would have to work out by hand.
/// </para>
/// </remarks>
public sealed partial class CommandLibraryViewModel
{
    /// <summary>
    /// Supplies the terminals a broadcast can reach. Wired by the view from
    /// <see cref="ToolContext.CommandBroadcaster"/>; null when the library was opened outside a
    /// session, and the whole panel then stays hidden.
    /// </summary>
    public ICommandBroadcaster? CommandBroadcaster { get; set; }

    /// <summary>The open terminals, each with the checkbox that decides whether it receives.</summary>
    public ObservableCollection<CommandBroadcastTargetEntry> BroadcastTargets { get; } = [];

    [ObservableProperty]
    private bool _isBroadcastPanelOpen;

    [ObservableProperty]
    private string _broadcastStatus = string.Empty;

    /// <summary>
    /// True once a broadcast has reported, so the status line is not given room before it has
    /// anything to say.
    /// </summary>
    public bool HasBroadcastStatus => !string.IsNullOrEmpty(BroadcastStatus);

    /// <summary>True when at least one open terminal can receive.</summary>
    public bool HasBroadcastTargets => BroadcastTargets.Count > 0;

    /// <summary>
    /// Drives the panel's empty state. The panel is offered whenever the library sits in a
    /// session, so it can legitimately open with nothing to send to, and an empty list that says
    /// nothing reads as a panel that failed to load.
    /// </summary>
    public bool HasNoBroadcastTargets => BroadcastTargets.Count == 0;

    /// <summary>
    /// How many terminals the next broadcast would reach. Drives the button label, so the count
    /// the operator confirms is the count they were shown.
    /// </summary>
    public int SelectedBroadcastCount => BroadcastTargets.Count(entry => entry.IsSelected);

    /// <summary>
    /// The broadcast button's label, carrying the count. The number the operator reads on the
    /// button is the number the confirmation then names.
    /// </summary>
    public string BroadcastButtonText =>
        _localizer.Format("ToolCmdLibBroadcastSendToCount", SelectedBroadcastCount);

    /// <summary>
    /// The panel is offered whenever the library sits in a session, which is whenever there is
    /// anything at all to send to.
    /// </summary>
    /// <remarks>
    /// It deliberately does not depend on how many terminals are open <b>now</b>. Gating the
    /// toggle on a count read when the library was opened hides it for the ordinary case of
    /// opening a second session afterwards, and a panel whose own opening gesture is invisible
    /// cannot be opened at all. The cost is that with one terminal open the panel repeats what
    /// Send already does, which is a smaller thing to be wrong about.
    /// </remarks>
    public bool CanOfferBroadcast => CommandBroadcaster is not null;

    /// <summary>
    /// Re-reads the open terminals, keeping the checkboxes the operator has already set.
    /// </summary>
    /// <remarks>
    /// A tab opened or closed while the panel is up would otherwise leave the list describing a
    /// state that is gone, and a checked row pointing at nothing.
    /// </remarks>
    public void RefreshBroadcastTargets()
    {
        if (CommandBroadcaster is null)
        {
            ClearBroadcastEntries();
            NotifyBroadcastState();
            return;
        }

        var previouslySelected = BroadcastTargets
            .Where(entry => entry.IsSelected)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        // A first read has no previous selection to carry, so the originating terminal starts
        // checked: it is the one the Send button would have reached.
        bool firstRead = BroadcastTargets.Count == 0;

        ClearBroadcastEntries();

        foreach (var target in CommandBroadcaster.GetTargets())
        {
            bool selected = firstRead
                ? target.IsOrigin
                : previouslySelected.Contains(target.Id);

            var entry = new CommandBroadcastTargetEntry(target, selected);
            entry.PropertyChanged += OnBroadcastTargetChanged;
            BroadcastTargets.Add(entry);
        }

        NotifyBroadcastState();
    }

    /// <summary>
    /// Opening the panel re-reads the terminals, so what it lists is what is open now.
    /// </summary>
    /// <remarks>
    /// This hangs off the property rather than off a command because the toggle in the view binds
    /// <c>IsChecked</c> two-way and nothing else sets it. A command beside a one-way IsChecked lets
    /// a UIA client flip the button through the Toggle pattern without running the command, and the
    /// button then reads "on" over a closed panel.
    /// </remarks>
    partial void OnIsBroadcastPanelOpenChanged(bool value)
    {
        if (value)
        {
            RefreshBroadcastTargets();
        }
    }

    [RelayCommand]
    private void SelectAllBroadcastTargets()
    {
        foreach (var entry in BroadcastTargets)
        {
            entry.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearBroadcastSelection()
    {
        foreach (var entry in BroadcastTargets)
        {
            entry.IsSelected = false;
        }
    }

    /// <summary>
    /// Sends the generated command to every checked terminal, after one confirmation that names
    /// how many will receive it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBroadcast))]
    public async Task BroadcastAsync()
    {
        if (!CanBroadcast())
        {
            return;
        }

        // The ids are taken before the confirmation and re-resolved by the broadcaster after it,
        // so a tab closed while the dialog was up reports as unreached instead of sending to
        // whichever tab has since taken its place.
        var chosen = BroadcastTargets
            .Where(entry => entry.IsSelected)
            .Select(entry => (entry.Id, entry.DisplayName))
            .ToList();

        // The level guard the single-target Send applies. An example bypasses parameter validation
        // and escaping, so example-originated text is treated as dangerous whatever the action
        // says - the same rule as SendAsync, and it has to hold on the path that reaches more
        // machines rather than only on the one that reaches one.
        var level = _generatedFromExample
            ? CriticalityLevel.Dangerous
            : _selectedAction?.Level ?? CriticalityLevel.Info;
        if (!await DangerousCommandGuard.ConfirmIfDangerousAsync(level, _dialogService, LocalizeKey))
        {
            return;
        }

        bool confirmed = await _dialogService.ShowConfirmAsync(
            LocalizeKey("ToolCmdLibBroadcastConfirmTitle"),
            _localizer.Format(
                "ToolCmdLibBroadcastConfirmMessage",
                chosen.Count,
                GeneratedCommand),
            "warning");

        if (!confirmed)
        {
            return;
        }

        var unreached = new List<string>();
        var unreachedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, displayName) in chosen)
        {
            if (!CommandBroadcaster!.Send(id, GeneratedCommand))
            {
                unreached.Add(displayName);
                unreachedIds.Add(id);
            }
        }

        int delivered = chosen.Count - unreached.Count;

        BroadcastStatus = unreached.Count == 0
            ? _localizer.Format("ToolCmdLibBroadcastSent", delivered)
            : _localizer.Format(
                "ToolCmdLibBroadcastSentPartial",
                delivered,
                chosen.Count,
                string.Join(", ", unreached));

        OnPropertyChanged(nameof(HasBroadcastStatus));

        // Only what did NOT receive stays ticked. Leaving everything ticked made the obvious
        // gesture after a partial send - wait for the one that failed, press the button again -
        // run the command a second time on every target that had already succeeded.
        // Keyed on the id, not the display name: two panes can carry the same name, and
        // unticking the wrong one is the failure this is here to prevent.
        foreach (var entry in BroadcastTargets)
        {
            entry.IsSelected = entry.IsSelected && unreachedIds.Contains(entry.Id);
        }

        // One history entry for one command, whatever it reached. The history replays into the
        // session the operator is in, so a row per target would all replay to the same place.
        if (delivered > 0)
        {
            RecordHistory();
        }

        RefreshBroadcastTargets();
    }

    /// <summary>
    /// The same validity gate the single-target Send is under, plus a target to send to.
    /// </summary>
    /// <remarks>
    /// <see cref="IsCommandValid"/> matters here and is easy to miss: when validation fails the
    /// generator does not clear <see cref="GeneratedCommand"/>, it replaces it with the raw
    /// pattern, braces and all. Send greys out on that and Broadcast did not, so the wider path
    /// was the unguarded one.
    /// </remarks>
    private bool CanBroadcast() =>
        CommandBroadcaster is not null
        && IsCommandValid
        && !string.IsNullOrEmpty(GeneratedCommand)
        && SelectedBroadcastCount > 0;

    private void ClearBroadcastEntries()
    {
        foreach (var entry in BroadcastTargets)
        {
            entry.PropertyChanged -= OnBroadcastTargetChanged;
        }

        BroadcastTargets.Clear();
    }

    private void OnBroadcastTargetChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(
            e.PropertyName,
            nameof(CommandBroadcastTargetEntry.IsSelected),
            StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedBroadcastCount));
            OnPropertyChanged(nameof(BroadcastButtonText));
            BroadcastCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyBroadcastState()
    {
        OnPropertyChanged(nameof(HasBroadcastTargets));
        OnPropertyChanged(nameof(HasNoBroadcastTargets));
        OnPropertyChanged(nameof(CanOfferBroadcast));
        OnPropertyChanged(nameof(SelectedBroadcastCount));
        OnPropertyChanged(nameof(BroadcastButtonText));
        BroadcastCommand.NotifyCanExecuteChanged();
    }
}
