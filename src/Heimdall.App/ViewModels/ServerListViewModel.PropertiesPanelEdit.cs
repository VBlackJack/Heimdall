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

using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Editing the five basic fields of the selected session in the properties panel, without the
/// full session dialog.
/// </summary>
/// <remarks>
/// The panel can stay in edit mode for minutes while a drag, a nudge or a folder move changes the
/// same profile, so a save never replaces the stored profile with an object built when the panel
/// opened. It validates the typed values against the profile as it is stored at that moment, and
/// inside the inventory lock re-finds the profile by id and assigns only the fields the user
/// changed. The edits belong to the session they were typed for: when the selection moves on, or
/// the panel is hidden, they are detached from the panel first and then saved or discarded for
/// that session, never for the newly selected one.
/// </remarks>
public partial class ServerListViewModel
{
    private SessionPropertiesEditViewModel? _propertiesEdit;

    /// <summary>The edit in progress in the panel; null when the panel is read-only.</summary>
    public SessionPropertiesEditViewModel? PropertiesEdit => _propertiesEdit;

    /// <summary>The last question or save the panel started for edits it had to settle; completes when it is done.</summary>
    internal Task PropertiesEditResolution { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the panel shows the edit form of the selected session.</summary>
    public bool PropertiesPanelShowsEditor =>
        _propertiesEdit is { } edit
        && PropertiesPanelShowsSession
        && string.Equals(SelectedServer?.Id, edit.ServerId, StringComparison.Ordinal);

    /// <summary>Whether the panel shows Connect, Edit and Edit here rather than the edit form.</summary>
    public bool PropertiesPanelShowsActions => !PropertiesPanelShowsEditor;

    /// <summary>
    /// Whether "Edit here" is offered: one local session, not a tool, not a shared row, not a
    /// folder summary or a multi-selection, and no edit already open.
    /// </summary>
    public bool PropertiesPanelCanEditHere =>
        _propertiesEdit is null
        && _propertiesSummary is null
        && !HasMultiSelection
        && SelectedServer is { IsShared: false } selected
        && !ConnectionTypeCatalog.IsToolConnectionType(selected.ConnectionType);

    [RelayCommand(CanExecute = nameof(PropertiesPanelCanEditHere))]
    private async Task BeginPropertiesEditAsync()
    {
        ServerItemViewModel? server = SelectedServer;
        if (server is null || !PropertiesPanelCanEditHere)
        {
            return;
        }

        // A fresh read: the row may be older than the file.
        List<ServerProfileDto> inventory = await _configManager.LoadServersAsync();
        ServerProfileDto? stored = inventory.FirstOrDefault(
            candidate => string.Equals(candidate.Id, server.Id, StringComparison.Ordinal));
        if (stored is null)
        {
            StatusMessageRequested?.Invoke(_localizer["ErrorServerNotFound"]);
            return;
        }

        AppSettings settings = _currentSettings ?? await _configManager.LoadSettingsAsync();

        // The selection may have moved while the file was read.
        if (!ReferenceEquals(SelectedServer, server) || !PropertiesPanelCanEditHere)
        {
            return;
        }

        string serverId = server.Id;
        SessionPropertiesEditViewModel edit = new(
            serverId,
            server.DisplayName,
            new SessionBasicFieldsValidator(stored, _localizer, settings),
            _localizer,
            BuildFolderChoices(settings),
            () => _allServers.FirstOrDefault(
                candidate => string.Equals(candidate.Id, serverId, StringComparison.Ordinal))?.IsActiveSession == true,
            SavePropertiesEditAsync,
            CancelPropertiesEdit);
        SetPropertiesEdit(edit);
    }

    /// <summary>The folder paths the folder box offers: every local one, parents of nested ones included.</summary>
    private List<string> BuildFolderChoices(AppSettings settings)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string group in _allServers
            .Where(server => !server.IsShared)
            .Select(server => server.Group)
            .Concat(settings.EmptyGroups))
        {
            string path = group;
            while (!string.IsNullOrWhiteSpace(path) && paths.Add(path))
            {
                path = FolderPath.ParentOf(path);
            }
        }

        List<string> sorted = [.. paths];
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    private void CancelPropertiesEdit(SessionPropertiesEditViewModel edit) => EndPropertiesEdit(edit);

    internal Task SavePropertiesEditAsync(SessionPropertiesEditViewModel edit) =>
        SavePropertiesEditCoreAsync(edit);

    /// <summary>Whether the application is closing; tests replace it, there is no WPF application there.</summary>
    internal Func<bool> ApplicationIsShuttingDown { get; set; } = IsApplicationClosing;

    private static bool IsApplicationClosing() =>
        Application.Current?.Dispatcher is { } dispatcher
        && (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished);

    private enum PropertiesSaveOutcome
    {
        Saved,
        Missing,
        Invalid,
        ProtocolChanged,
    }

    private sealed record PropertiesSaveResult(
        PropertiesSaveOutcome Outcome,
        ServerProfileDto? Profile,
        SessionBasicFieldErrors? Errors);

    /// <summary>
    /// Writes an edit to the session it was typed for.
    /// </summary>
    /// <param name="edit">The edit; it may already be detached from the panel.</param>
    /// <returns>True when the profile was written.</returns>
    private async Task<bool> SavePropertiesEditCoreAsync(SessionPropertiesEditViewModel edit)
    {
        if (edit.IsSaving || edit.IsSettled)
        {
            return false;
        }

        ServerItemViewModel? row = _allServers.FirstOrDefault(
            candidate => string.Equals(candidate.Id, edit.ServerId, StringComparison.Ordinal));
        bool shared = SharedSessionIds.IsShared(edit.ServerId) || row is { IsShared: true };
        if (row is null || shared)
        {
            EndPropertiesEdit(edit);
            StatusMessageRequested?.Invoke(_localizer[shared ? "StatusSharedSessionReadOnly" : "ErrorServerNotFound"]);
            return false;
        }

        edit.IsSaving = true;
        try
        {
            AppSettings settings = _currentSettings ?? await _configManager.LoadSettingsAsync();
            SessionBasicFieldValues original = edit.Original;
            SessionBasicFieldValues values = edit.Values;

            // The whole decision runs under the inventory lock, on the profile as it is stored now:
            // it is re-found by id, judged by the dialog's rules, and only the fields the user
            // changed are assigned. Whatever else changed since the panel opened is kept.
            PropertiesSaveResult result = await _configManager.MutateServersAsync(inventory =>
            {
                ServerProfileDto? stored = inventory.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, edit.ServerId, StringComparison.Ordinal));
                if (stored is null)
                {
                    return new PropertiesSaveResult(PropertiesSaveOutcome.Missing, null, null);
                }

                SessionBasicFieldsValidator validator = new(stored, _localizer, settings);

                // A port or a login typed for one protocol must never land in another's fields.
                if (!string.Equals(validator.ConnectionType, edit.ConnectionType, StringComparison.Ordinal))
                {
                    return new PropertiesSaveResult(PropertiesSaveOutcome.ProtocolChanged, null, null);
                }

                // Fields left alone are judged by what is stored now, not by what was read at open.
                SessionBasicFieldErrors errors = validator.Validate(
                    SessionBasicFields.Merge(validator.Current, original, values));
                if (!errors.IsValid)
                {
                    return new PropertiesSaveResult(PropertiesSaveOutcome.Invalid, null, errors);
                }

                SessionBasicFields.Apply(stored, original, values);
                return new PropertiesSaveResult(PropertiesSaveOutcome.Saved, stored.CloneFaithfully(), null);
            });

            switch (result.Outcome)
            {
                case PropertiesSaveOutcome.Missing:
                    EndPropertiesEdit(edit);
                    StatusMessageRequested?.Invoke(_localizer["ErrorServerNotFound"]);
                    return false;
                case PropertiesSaveOutcome.ProtocolChanged:
                    EndPropertiesEdit(edit);
                    StatusMessageRequested?.Invoke(_localizer.Format("PropertiesEditProtocolChanged", edit.SessionName));
                    return false;
                case PropertiesSaveOutcome.Invalid:
                    edit.ShowErrors(result.Errors!);
                    if (!ReferenceEquals(edit, _propertiesEdit))
                    {
                        // Nothing on screen shows the errors of an edit the panel let go of.
                        StatusMessageRequested?.Invoke(
                            _localizer.Format("PropertiesEditDiscardedInvalid", edit.SessionName));
                    }

                    return false;
            }

            // Ended before the tree is rebuilt, so the selection settling again is not read as the
            // user moving away from an edit that is already saved.
            EndPropertiesEdit(edit);

            // Decided now, from the selection as it is: a click on another row while the file was
            // written must not be pulled back to this session.
            bool stillSelected = !HasMultiSelection
                && string.Equals(SelectedServer?.Id, edit.ServerId, StringComparison.Ordinal);
            ApplyEditedProfile(row, result.Profile!, settings, stillSelected ? row.Id : null);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Saving the properties panel edits failed", ex);
            StatusMessageRequested?.Invoke(_localizer["PropertiesEditSaveFailed"]);
            return false;
        }
        finally
        {
            edit.IsSaving = false;
        }
    }

    private void SetPropertiesEdit(SessionPropertiesEditViewModel? edit)
    {
        _propertiesEdit = edit;
        OnPropertyChanged(nameof(PropertiesEdit));
        NotifyPropertiesPanelMode();
    }

    private void EndPropertiesEdit(SessionPropertiesEditViewModel edit)
    {
        if (ReferenceEquals(_propertiesEdit, edit))
        {
            SetPropertiesEdit(null);
        }

        edit.TrySettle();
    }

    /// <summary>Takes the edit out of the panel and returns it; null when none was open.</summary>
    private SessionPropertiesEditViewModel? DetachPropertiesEdit()
    {
        SessionPropertiesEditViewModel? edit = _propertiesEdit;
        if (edit is not null)
        {
            SetPropertiesEdit(null);
        }

        return edit;
    }

    /// <summary>
    /// Settles the edits of a panel that has just been hidden: a valid change is asked about, an
    /// invalid one is dropped with a word on the status line.
    /// </summary>
    internal void ResolvePropertiesEditOnPanelHidden()
    {
        if (DetachPropertiesEdit() is { } edit)
        {
            PropertiesEditResolution = ResolveDetachedPropertiesEditAsync(edit);
        }
    }

    /// <summary>
    /// The selection moved: edits typed for another session are settled for that session.
    /// </summary>
    private void ResolvePropertiesEditOnSelectionChange()
    {
        if (_propertiesEdit is not { } edit)
        {
            return;
        }

        ServerItemViewModel? selected = SelectedServer;
        bool sameSession = !HasMultiSelection
            && selected is not null
            && string.Equals(selected.Id, edit.ServerId, StringComparison.Ordinal);
        if (sameSession)
        {
            return;
        }

        // A search or a collapse hiding the row is the view, not the user moving on: the edit
        // waits and the form comes back with the row.
        if (_synchronizingSelection && selected is null)
        {
            return;
        }

        PropertiesEditResolution = ResolveDetachedPropertiesEditAsync(DetachPropertiesEdit()!);
    }

    /// <summary>
    /// Asks whether to save the edits of a session the panel has let go of, and saves them or
    /// drops them for that session.
    /// </summary>
    /// <remarks>
    /// The question is asked after yielding to the dispatcher: this is reached from the selection
    /// setter, possibly in the middle of a mouse gesture, and a modal dialog must not open there.
    /// The two answers are named after their acts. The confirm answer of the overload is the one
    /// that performs the act and the decline answer takes Enter and Escape, so Discard is the
    /// confirm answer and Save the decline one: the answer that loses nothing is the one Enter
    /// picks. While the application closes the edits are dropped without asking, because that
    /// overload answers "decline" there, which would mean Save.
    /// </remarks>
    private async Task ResolveDetachedPropertiesEditAsync(SessionPropertiesEditViewModel edit)
    {
        try
        {
            if (!edit.IsDirty || ApplicationIsShuttingDown())
            {
                return;
            }

            await _uiDispatcher.InvokeAsync(static () => { });

            // A save or a cancel that got in first has already dealt with these edits.
            if (edit.IsSaving || edit.IsSettled || ApplicationIsShuttingDown())
            {
                return;
            }

            if (!edit.IsValid)
            {
                StatusMessageRequested?.Invoke(_localizer.Format("PropertiesEditDiscardedInvalid", edit.SessionName));
                return;
            }

            ServerItemViewModel? row = _allServers.FirstOrDefault(
                candidate => string.Equals(candidate.Id, edit.ServerId, StringComparison.Ordinal));
            if (row is null
                || row.IsShared
                || ConnectionTypeCatalog.IsToolConnectionType(row.ConnectionType))
            {
                StatusMessageRequested?.Invoke(_localizer[row is { IsShared: true }
                    ? "StatusSharedSessionReadOnly"
                    : "ErrorServerNotFound"]);
                return;
            }

            bool discard = await _dialogService.ShowConfirmAsync(
                _localizer["PropertiesEditUnsavedTitle"],
                _localizer.Format("PropertiesEditUnsavedMessage", edit.SessionName),
                "warning",
                _localizer["BtnDiscard"],
                _localizer["BtnSave"]);
            if (discard || ApplicationIsShuttingDown())
            {
                return;
            }

            await SavePropertiesEditCoreAsync(edit);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Settling the unsaved properties panel edits failed", ex);
        }
        finally
        {
            edit.TrySettle();
        }
    }
}
