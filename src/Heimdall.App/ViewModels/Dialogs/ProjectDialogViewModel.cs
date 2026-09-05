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

using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the project add/edit dialog.
/// Projects group servers with shared defaults and visual identifiers.
/// </summary>
public partial class ProjectDialogViewModel : ObservableValidator
{
    /// <summary>
    /// Predefined color palette for project badges.
    /// </summary>
    public static readonly string[] AvailableColors = BadgeColorPalette.Colors;

    /// <summary>
    /// Localizer for translating validation error messages. Set by the dialog service.
    /// </summary>
    public LocalizationManager? Localizer { get; set; }

    // --- Dialog state ---

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private bool _isEditMode;

    // --- Project fields ---

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Project name is required.")]
    [MinLength(1, ErrorMessage = "Project name cannot be empty.")]
    [MaxLength(50, ErrorMessage = "Project name must not exceed 50 characters.")]
    private string _name = "";

    [ObservableProperty]
    [MaxLength(200, ErrorMessage = "Description must not exceed 200 characters.")]
    private string _description = "";

    [ObservableProperty]
    private string _color = "#3B82F6";

    [ObservableProperty]
    private string _icon = "";

    [ObservableProperty]
    private string _defaultSshUsername = "";

    [ObservableProperty]
    private string _defaultSshKeyPath = "";

    [ObservableProperty]
    private string _defaultGatewayId = "";

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
        if (e.PropertyName is nameof(Name) or nameof(Description)
            or nameof(Color) or nameof(Icon)
            or nameof(DefaultSshUsername) or nameof(DefaultSshKeyPath)
            or nameof(DefaultGatewayId))
        {
            IsDirty = true;
        }
    }

    // --- Validation ---

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private string? _descriptionError;

    /// <summary>
    /// Triggers full validation of all annotated properties.
    /// Populates per-field inline errors and an aggregate summary.
    /// </summary>
    [RelayCommand]
    private void Validate()
    {
        ValidateAllProperties();

        NameError = GetLocalizedFieldError(nameof(Name));
        DescriptionError = GetLocalizedFieldError(nameof(Description));

        RefreshValidationSummary();
    }

    private void RefreshValidationSummary()
    {
        ValidationError = NameError ?? DescriptionError;
    }

    // --- Live re-validation (only when errors are already showing) ---

    partial void OnNameChanged(string value)
    {
        if (NameError is not null)
        {
            ValidateProperty(value, nameof(Name));
            NameError = GetLocalizedFieldError(nameof(Name));
            RefreshValidationSummary();
        }
    }

    partial void OnDescriptionChanged(string value)
    {
        if (DescriptionError is not null)
        {
            ValidateProperty(value, nameof(Description));
            DescriptionError = GetLocalizedFieldError(nameof(Description));
            RefreshValidationSummary();
        }
    }

    /// <summary>
    /// Maps the current ViewModel state to a flat DTO for persistence.
    /// </summary>
    public ProjectDto ToDto()
    {
        return new ProjectDto
        {
            Name = Name,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            Color = Color,
            DefaultSshUsername = string.IsNullOrWhiteSpace(DefaultSshUsername) ? null : DefaultSshUsername,
            DefaultSshKeyPath = string.IsNullOrWhiteSpace(DefaultSshKeyPath) ? null : DefaultSshKeyPath,
            DefaultGatewayId = string.IsNullOrWhiteSpace(DefaultGatewayId) ? null : DefaultGatewayId
        };
    }

    /// <summary>
    /// Creates a ViewModel pre-populated from an existing DTO (for edit mode).
    /// </summary>
    /// <param name="dto">The project DTO to load values from.</param>
    /// <returns>A populated ProjectDialogViewModel in edit mode.</returns>
    public static ProjectDialogViewModel FromDto(ProjectDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var vm = new ProjectDialogViewModel { _isInitializing = true };
        vm.IsEditMode = true;
        vm.Name = dto.Name;
        vm.Description = dto.Description ?? "";
        vm.Color = dto.Color ?? "#3B82F6";
        vm.DefaultSshUsername = dto.DefaultSshUsername ?? "";
        vm.DefaultSshKeyPath = dto.DefaultSshKeyPath ?? "";
        vm.DefaultGatewayId = dto.DefaultGatewayId ?? "";
        vm._isInitializing = false;
        return vm;
    }

    private static readonly Dictionary<string, string> ValidationKeyMap = new(StringComparer.Ordinal)
    {
        ["Project name is required."] = "ValidationProjectNameRequired",
        ["Project name cannot be empty."] = "ValidationProjectNameEmpty",
        ["Project name must not exceed 50 characters."] = "ValidationProjectNameMaxLength",
        ["Description must not exceed 200 characters."] = "ValidationProjectDescMaxLength",
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
/// Immutable result returned by the project dialog on close.
/// </summary>
/// <param name="Project">The project DTO with user-entered values.</param>
/// <param name="Saved">True if the user clicked Save, false if cancelled.</param>
public sealed record ProjectDialogResult(ProjectDto Project, bool Saved);
