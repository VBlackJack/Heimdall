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
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the SSH gateway add/edit dialog.
/// Supports chained gateways via parent gateway selection.
/// </summary>
public partial class GatewayDialogViewModel : ObservableValidator
{
    /// <summary>
    /// Localizer for translating validation error messages. Set by the dialog service.
    /// </summary>
    public LocalizationManager? Localizer { get; set; }

    // --- Dialog state ---

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private bool _isEditMode;

    // --- Gateway fields ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Gateway name is required.")]
    [MinLength(1, ErrorMessage = "Gateway name cannot be empty.")]
    private string _name = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Host address is required.")]
    [MinLength(1, ErrorMessage = "Host address cannot be empty.")]
    private string _host = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    private int _port = 22;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Username is required.")]
    [MinLength(1, ErrorMessage = "Username cannot be empty.")]
    private string _user = "";

    [ObservableProperty]
    private string _keyPath = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _keyPassphrase = "";

    // Existing encrypted SSH secrets (preserved on edit if user doesn't change them)
    public string? ExistingSshPasswordEncrypted { get; set; }
    public string? ExistingSshKeyPassphraseEncrypted { get; set; }

    /// <summary>Whether a password is stored for this gateway.</summary>
    public bool HasStoredPassword => !string.IsNullOrEmpty(ExistingSshPasswordEncrypted);

    /// <summary>Whether a key passphrase is stored for this gateway.</summary>
    public bool HasStoredKeyPassphrase => !string.IsNullOrEmpty(ExistingSshKeyPassphraseEncrypted);

    /// <summary>Forgets the stored password.</summary>
    /// <remarks>
    /// An empty box means "keep what is stored", while the hint invites the user to leave it
    /// blank for key-only sign-in: the one gesture that looks like a removal kept the secret, and
    /// a gateway moved to a key or an agent went on sending its old password. The server dialog
    /// has carried these two commands for the same reason.
    /// </remarks>
    [RelayCommand]
    private void ClearStoredPassword()
    {
        ExistingSshPasswordEncrypted = null;
        Password = "";
        OnPropertyChanged(nameof(HasStoredPassword));
        IsDirty = true;
    }

    /// <summary>Forgets the stored key passphrase.</summary>
    [RelayCommand]
    private void ClearStoredKeyPassphrase()
    {
        ExistingSshKeyPassphraseEncrypted = null;
        KeyPassphrase = "";
        OnPropertyChanged(nameof(HasStoredKeyPassphrase));
        IsDirty = true;
    }

    /// <summary>
    /// Gives the window its title when the caller did not, from whether a gateway is being edited.
    /// </summary>
    /// <remarks>
    /// The title is bound to <see cref="DialogTitle"/>, which no caller ever assigned, so the
    /// window, its taskbar entry and screen readers had no name for it.
    /// </remarks>
    public void EnsureDialogTitle()
    {
        if (string.IsNullOrEmpty(DialogTitle) && Localizer is not null)
        {
            DialogTitle = Localizer[IsEditMode ? "GatewayDialogTitleEdit" : "GatewayDialogTitleAdd"];
        }
    }

    /// <summary>
    /// Reports a port the box could not turn into a number.
    /// </summary>
    /// <remarks>
    /// The box converts its text to a number on the way to <see cref="Port"/>; text that does not
    /// convert never arrives, so validation saw the previous port and Save stored it.
    /// </remarks>
    public void ReportUnreadablePort()
    {
        PortError = Localizer?["ValidationGatewayPortRange"] ?? "Port must be between 1 and 65535.";
        RefreshValidationSummary();
    }

    // Whether the gateway loaded for edit relied on the legacy mapping, where the stored password
    // doubles as the key passphrase. The mapping is derived from the passphrase field being absent,
    // so writing an empty one on save switched it off and broke an encrypted key on a mere rename.
    private bool _sourceUsesLegacySshCredentialMapping;

    /// <summary>
    /// Returns the label for the SSH password field.
    /// </summary>
    public string GatewayPasswordLabel => Localizer?["GatewayDialogLabelPassword"] ?? "Password";

    /// <summary>Whether the key passphrase field should be shown.</summary>
    public bool HasKeyPath => !string.IsNullOrWhiteSpace(KeyPath);

    /// <summary>
    /// Returns the gateway password field hint.
    /// </summary>
    public string GatewayAuthHint => Localizer?["GatewayAuthHintPassword"] ?? "";

    /// <summary>Returns the gateway key passphrase field hint.</summary>
    public string GatewayKeyPassphraseHint => Localizer?["GatewayAuthHintKey"] ?? "";

    /// <summary>
    /// Shows the resulting chain topology when a parent gateway is selected.
    /// </summary>
    public string GatewayChainSummary => string.IsNullOrWhiteSpace(SelectedParentGatewayId)
        ? ""
        : Localizer?["GatewayChainLabel"] is string label
            ? $"{label}: {AvailableParents.FirstOrDefault(p => string.Equals(p.Id, SelectedParentGatewayId, StringComparison.OrdinalIgnoreCase))?.DisplayText ?? SelectedParentGatewayId} \u2192 {Name}"
            : "";

    /// <summary>
    /// Optional parent gateway for chained SSH tunnels.
    /// Empty string means no parent (direct connection).
    /// </summary>
    [ObservableProperty]
    private string _selectedParentGatewayId = "";

    /// <summary>
    /// Read-only display of the SSH host key fingerprint (populated after first connection).
    /// </summary>
    [ObservableProperty]
    private string _hostKeyFingerprint = "";

    /// <summary>
    /// Available parent gateways for chaining. The caller builds it with
    /// <see cref="Services.GatewayParentEligibility"/>, which leaves out every parent that
    /// would close a loop or make a chain longer than a connection follows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParentChoices))]
    private ObservableCollection<GatewayOption> _availableParents = [];

    /// <summary>
    /// What the parent picker lists: no parent first, then <see cref="AvailableParents"/>.
    /// </summary>
    /// <remarks>
    /// The picker had no way back to a direct connection, so a parent once chosen could only
    /// be swapped for another. A loop already on disk could not be broken at all when every
    /// other gateway sat inside it.
    /// </remarks>
    public IReadOnlyList<GatewayOption> ParentChoices =>
        [new GatewayOption(string.Empty, Localizer?["GatewayDialogNoParent"] ?? "None"), .. AvailableParents];

    // --- Dirty state tracking ---

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// Suppresses dirty tracking during initialization (e.g., FromDto).
    /// </summary>
    private bool _isInitializing;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isInitializing) return;

        // Mark dirty when any user-editable property changes
        if (e.PropertyName is nameof(Name) or nameof(Host) or nameof(Port)
            or nameof(User) or nameof(KeyPath) or nameof(Password) or nameof(KeyPassphrase)
            or nameof(SelectedParentGatewayId))
        {
            IsDirty = true;
            InvalidateDiagnosticResult();
        }
    }

    // --- Validation ---

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private string? _hostError;

    [ObservableProperty]
    private string? _portError;

    [ObservableProperty]
    private string? _userError;

    /// <summary>
    /// Triggers full validation of all annotated properties.
    /// Populates per-field inline errors and an aggregate summary.
    /// </summary>
    [RelayCommand]
    private void Validate()
    {
        ValidateAllProperties();

        NameError = GetLocalizedFieldError(nameof(Name));
        HostError = GetLocalizedFieldError(nameof(Host));
        PortError = GetLocalizedFieldError(nameof(Port));
        UserError = GetLocalizedFieldError(nameof(User));

        RefreshValidationSummary();
    }

    private void RefreshValidationSummary()
    {
        ValidationError = NameError ?? HostError ?? PortError ?? UserError;
    }

    // --- Live re-validation (only when errors are already showing) ---

    partial void OnKeyPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasKeyPath));
        if (string.IsNullOrWhiteSpace(value))
        {
            KeyPassphrase = "";
            ExistingSshKeyPassphraseEncrypted = null;
            OnPropertyChanged(nameof(HasStoredKeyPassphrase));
        }
    }

    partial void OnSelectedParentGatewayIdChanged(string value)
    {
        OnPropertyChanged(nameof(GatewayChainSummary));
    }

    partial void OnAvailableParentsChanged(ObservableCollection<GatewayOption> value)
    {
        // Ids match without regard to case everywhere but in the picker, which compares them
        // exactly: a parent stored as "A" for the option "a" read as no selection, and the
        // picker cleared it. Adopting the option's spelling is not an edit.
        GatewayOption? match = value.FirstOrDefault(option => string.Equals(
            option.Id, SelectedParentGatewayId, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !string.Equals(match.Id, SelectedParentGatewayId, StringComparison.Ordinal))
        {
            bool wasInitializing = _isInitializing;
            _isInitializing = true;
            SelectedParentGatewayId = match.Id;
            _isInitializing = wasInitializing;
        }

        OnPropertyChanged(nameof(GatewayChainSummary));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(GatewayChainSummary));

        if (NameError is not null)
        {
            ValidateProperty(value, nameof(Name));
            NameError = GetLocalizedFieldError(nameof(Name));
            RefreshValidationSummary();
        }
    }

    partial void OnHostChanged(string value)
    {
        if (HostError is not null)
        {
            ValidateProperty(value, nameof(Host));
            HostError = GetLocalizedFieldError(nameof(Host));
            RefreshValidationSummary();
        }
    }

    partial void OnPortChanged(int value)
    {
        if (PortError is not null)
        {
            ValidateProperty(value, nameof(Port));
            PortError = GetLocalizedFieldError(nameof(Port));
            RefreshValidationSummary();
        }
    }

    partial void OnUserChanged(string value)
    {
        if (UserError is not null)
        {
            ValidateProperty(value, nameof(User));
            UserError = GetLocalizedFieldError(nameof(User));
            RefreshValidationSummary();
        }
    }

    /// <summary>
    /// Opens a file picker for SSH key selection.
    /// The View layer handles the actual file dialog; this command signals intent.
    /// </summary>
    [RelayCommand]
    private void BrowseKey()
    {
        // Intentionally empty: View binds via interaction trigger to open file dialog.
    }

    /// <summary>
    /// Maps the current ViewModel state to a flat DTO for persistence.
    /// </summary>
    public SshGatewayDto ToDto()
    {
        var keyPath = string.IsNullOrWhiteSpace(KeyPath) ? null : KeyPath;

        var dto = new SshGatewayDto
        {
            Name = Name,
            Host = Host,
            Port = Port,
            User = User,
            KeyPath = keyPath,
            SshPasswordEncrypted = string.IsNullOrEmpty(Password)
                ? ExistingSshPasswordEncrypted
                : Heimdall.Core.Security.CredentialProtector.Protect(Password),
            ParentGatewayId = string.IsNullOrWhiteSpace(SelectedParentGatewayId)
                ? null
                : SelectedParentGatewayId,
            HostKeyFingerprint = string.IsNullOrWhiteSpace(HostKeyFingerprint)
                ? null
                : HostKeyFingerprint
        };

        // The passphrase setter declares the field even when it assigns null, so it is only
        // called when the field has to be declared.
        if (!KeepsLegacySshCredentialMapping(keyPath))
        {
            dto.SshKeyPassphraseEncrypted = keyPath is null
                ? null
                : string.IsNullOrEmpty(KeyPassphrase)
                    ? ExistingSshKeyPassphraseEncrypted ?? string.Empty
                    : Heimdall.Core.Security.CredentialProtector.Protect(KeyPassphrase);
        }

        return dto;
    }

    private bool KeepsLegacySshCredentialMapping(string? keyPath) =>
        _sourceUsesLegacySshCredentialMapping
        && keyPath is not null
        && string.IsNullOrEmpty(KeyPassphrase)
        && ExistingSshKeyPassphraseEncrypted is null;

    /// <summary>
    /// Creates a ViewModel pre-populated from an existing DTO (for edit mode).
    /// </summary>
    /// <param name="dto">The gateway DTO to load values from.</param>
    /// <returns>A populated GatewayDialogViewModel in edit mode.</returns>
    public static GatewayDialogViewModel FromDto(SshGatewayDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var vm = new GatewayDialogViewModel { _isInitializing = true };
        vm.IsEditMode = true;
        vm._diagnosticGatewayId = dto.Id;
        vm.Name = dto.Name;
        vm.Host = dto.Host;
        vm.Port = dto.Port;
        vm.User = dto.User;
        vm.KeyPath = dto.KeyPath ?? "";
        vm.SelectedParentGatewayId = dto.ParentGatewayId ?? "";
        vm.HostKeyFingerprint = dto.HostKeyFingerprint ?? "";
        vm.ExistingSshPasswordEncrypted = dto.SshPasswordEncrypted;
        vm.ExistingSshKeyPassphraseEncrypted = dto.SshKeyPassphraseEncrypted;
        vm._sourceUsesLegacySshCredentialMapping = dto.UsesLegacySshCredentialMapping;
        vm._isInitializing = false;
        return vm;
    }

    private static readonly Dictionary<string, string> ValidationKeyMap = new(StringComparer.Ordinal)
    {
        ["Gateway name is required."] = "ValidationGatewayNameRequired",
        ["Gateway name cannot be empty."] = "ValidationGatewayNameEmpty",
        ["Host address is required."] = "ValidationGatewayHostRequired",
        ["Host address cannot be empty."] = "ValidationGatewayHostEmpty",
        ["Port must be between 1 and 65535."] = "ValidationGatewayPortRange",
        ["Username is required."] = "ValidationUsernameRequired",
        ["Username cannot be empty."] = "ValidationUsernameEmpty",
    };

    private string? GetLocalizedFieldError(string propertyName)
    {
        var error = GetErrors(propertyName)
            .OfType<System.ComponentModel.DataAnnotations.ValidationResult>()
            .FirstOrDefault();

        var message = error?.ErrorMessage;
        if (message is not null && Localizer is not null
            && ValidationKeyMap.TryGetValue(message, out var key))
        {
            return Localizer[key];
        }

        return message;
    }
}

/// <summary>
/// Immutable result returned by the gateway dialog on close.
/// </summary>
/// <param name="Gateway">The gateway DTO with user-entered values.</param>
/// <param name="Saved">True if the user clicked Save, false if cancelled.</param>
public sealed record GatewayDialogResult(SshGatewayDto Gateway, bool Saved);
