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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The state of the properties panel while it edits the five basic fields of one session: the
/// values typed, whether they differ from what was read, their inline errors, and whether Save is
/// allowed.
/// </summary>
/// <remarks>
/// It belongs to the session it was opened for (<see cref="ServerId"/>), not to whatever row is
/// selected later, and it holds no stored profile: saving is the owner's job, which re-reads the
/// profile under the inventory lock. Kept apart from the window so every transition can be tested
/// without one.
/// </remarks>
public sealed partial class SessionPropertiesEditViewModel : ObservableObject
{
    private readonly SessionBasicFieldsValidator _validator;
    private readonly LocalizationManager _localizer;
    private readonly Func<bool> _isSessionOpen;
    private readonly Func<SessionPropertiesEditViewModel, Task> _save;
    private readonly Action<SessionPropertiesEditViewModel> _cancel;
    private readonly SessionBasicFieldValues _original;
    private bool _initialized;

    internal SessionPropertiesEditViewModel(
        string serverId,
        string sessionName,
        SessionBasicFieldsValidator validator,
        LocalizationManager localizer,
        IReadOnlyList<string> folderChoices,
        Func<bool> isSessionOpen,
        Func<SessionPropertiesEditViewModel, Task> save,
        Action<SessionPropertiesEditViewModel> cancel)
    {
        ServerId = serverId;
        SessionName = sessionName;
        _validator = validator;
        _localizer = localizer;
        FolderChoices = folderChoices;
        _isSessionOpen = isSessionOpen;
        _save = save;
        _cancel = cancel;
        _original = validator.Current;

        _displayName = _original.DisplayName;
        _host = _original.Host;
        _portText = _original.Port.ToString(CultureInfo.InvariantCulture);
        _username = _original.Username;
        _folder = _original.Folder;
        ConnectionType = validator.ConnectionType;
        _initialized = true;
        Revalidate();
        _localizer.LocaleChanged += OnLocaleChanged;
    }

    /// <summary>The connection type the edit began with; a save is refused if the stored one differs.</summary>
    public string ConnectionType { get; }

    /// <summary>Whether a save of this edit is in flight.</summary>
    internal bool IsSaving { get; set; }

    /// <summary>Whether the edit is over: saved, cancelled, or handed to a question already.</summary>
    internal bool IsSettled { get; private set; }

    /// <summary>Ends the edit for good and stops it following the language; false if it already was over.</summary>
    internal bool TrySettle()
    {
        if (IsSettled)
        {
            return false;
        }

        IsSettled = true;
        _localizer.LocaleChanged -= OnLocaleChanged;
        return true;
    }

    private void OnLocaleChanged(string locale)
    {
        // The messages and the port caption are read from the catalogue when asked for.
        OnPropertyChanged(nameof(PortLabel));
        Revalidate();
    }

    /// <summary>The id of the session the edits belong to.</summary>
    public string ServerId { get; }

    /// <summary>The name of the session when the edit began, for the questions asked about it.</summary>
    public string SessionName { get; }

    /// <summary>Whether the protocol has a server address.</summary>
    public bool HasHost => _validator.Capabilities.HasHost;

    /// <summary>Whether the protocol has a port.</summary>
    public bool HasPort => _validator.Capabilities.HasPort;

    /// <summary>Whether the protocol has a login name.</summary>
    public bool HasUsername => _validator.Capabilities.HasUsername;

    /// <summary>The caption of the port field, named for the protocol.</summary>
    public string PortLabel => _validator.PortLabel;

    /// <summary>The folder paths already in the tree, for the folder box to offer.</summary>
    public IReadOnlyList<string> FolderChoices { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private string _portText;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private string? _displayNameError;

    [ObservableProperty]
    private string? _hostError;

    [ObservableProperty]
    private string? _portError;

    [ObservableProperty]
    private string? _usernameError;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string _connectionNote = "";

    /// <summary>Whether Save is allowed: something changed and everything is valid.</summary>
    public bool CanSave => IsDirty && IsValid;

    /// <summary>Whether the note about an open session is shown.</summary>
    public bool HasConnectionNote => ConnectionNote.Length > 0;

    /// <summary>The values as typed now; a port that is no number reads as 0, which the port rule refuses.</summary>
    internal SessionBasicFieldValues Values => new(
        DisplayName,
        Host,
        ParsePort(PortText),
        string.IsNullOrWhiteSpace(Username) ? "" : Username,
        string.IsNullOrWhiteSpace(Folder) ? "" : Folder);

    /// <summary>The values the panel read when the edit began.</summary>
    internal SessionBasicFieldValues Original => _original;

    /// <summary>
    /// Saves through the owner; allowed only when <see cref="CanSave"/>. The command itself
    /// refuses too, because a relay command's Execute does not consult its CanExecute.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => CanSave ? _save(this) : Task.CompletedTask;

    /// <summary>Gives the edits up and returns the panel to its read-only view.</summary>
    [RelayCommand]
    private void Cancel() => _cancel(this);

    /// <summary>Shows the errors a save found on the stored profile, which can differ from the ones seen while typing.</summary>
    internal void ShowErrors(SessionBasicFieldErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        DisplayNameError = errors.DisplayName;
        HostError = errors.Host;
        PortError = errors.Port;
        UsernameError = errors.Username;
        IsValid = errors.IsValid;
        NotifySaveChanged();
    }

    /// <summary>Re-reads whether the session is open; connecting or closing changes the note.</summary>
    internal void RefreshOpenState() => UpdateConnectionNote();

    partial void OnDisplayNameChanged(string value) => Revalidate();

    partial void OnHostChanged(string value) => Revalidate();

    partial void OnPortTextChanged(string value) => Revalidate();

    partial void OnUsernameChanged(string value) => Revalidate();

    partial void OnFolderChanged(string value) => Revalidate();

    partial void OnConnectionNoteChanged(string value) => OnPropertyChanged(nameof(HasConnectionNote));

    private void Revalidate()
    {
        if (!_initialized)
        {
            return;
        }

        SessionBasicFieldValues values = Values;
        SessionBasicFieldErrors errors = _validator.Validate(values);
        DisplayNameError = errors.DisplayName;
        HostError = errors.Host;
        PortError = errors.Port;
        UsernameError = errors.Username;
        IsValid = errors.IsValid;
        IsDirty = values != _original;
        UpdateConnectionNote();
        NotifySaveChanged();
    }

    private void UpdateConnectionNote()
    {
        SessionBasicFieldValues values = Values;
        bool connectionChanged = values.Host != _original.Host
            || values.Port != _original.Port
            || values.Username != _original.Username;
        ConnectionNote = connectionChanged && _isSessionOpen()
            ? _localizer["PropertiesEditConnectedNote"]
            : "";
    }

    private void NotifySaveChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static int ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
}
