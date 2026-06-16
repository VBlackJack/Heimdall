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
using Heimdall.Core.Localization;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the command action add/edit dialog.
/// Supports dual-platform templates with parameterized patterns.
/// </summary>
public partial class CommandActionDialogViewModel : ObservableValidator
{
    public LocalizationManager? Localizer { get; set; }

    public List<string> AvailableCategories { get; set; } = [];

    // ── Dialog state ──────────────────────────────────────────────

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _isDirty;

    private bool _isInitializing;

    private string? _editActionId;
    private Guid? _editPublicId;
    private DateTime? _editCreatedAt;
    private string? _editWinTemplateId;
    private Guid? _editWinTemplatePublicId;
    private string? _editLinuxTemplateId;
    private Guid? _editLinuxTemplatePublicId;
    private List<CommandExample>? _editExamples;
    private List<CommandExample>? _editWindowsExamples;
    private List<CommandExample>? _editLinuxExamples;
    private List<ExternalLink>? _editLinks;

    // ── Action fields ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Title is required.")]
    [MaxLength(200, ErrorMessage = "Title must not exceed 200 characters.")]
    private string _title = "";

    [ObservableProperty]
    [MaxLength(2000, ErrorMessage = "Description must not exceed 2000 characters.")]
    private string _description = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Category is required.")]
    private string _category = "";

    [ObservableProperty]
    private Platform _platform = Platform.Both;

    public bool ShowWindowsSection => Platform is Platform.Windows or Platform.Both;

    public bool ShowLinuxSection => Platform is Platform.Linux or Platform.Both;

    [ObservableProperty]
    private CriticalityLevel _level = CriticalityLevel.Info;

    [ObservableProperty]
    private string _tags = "";

    [ObservableProperty]
    [MaxLength(5000, ErrorMessage = "Notes must not exceed 5000 characters.")]
    private string _notes = "";

    // ── Templates ─────────────────────────────────────────────────

    [ObservableProperty]
    private string _windowsPattern = "";

    [ObservableProperty]
    private string _windowsTemplateName = "";

    public ObservableCollection<ParameterEntryVm> WindowsParameters { get; } = [];

    [ObservableProperty]
    private string _linuxPattern = "";

    [ObservableProperty]
    private string _linuxTemplateName = "";

    public ObservableCollection<ParameterEntryVm> LinuxParameters { get; } = [];

    // ── Validation ────────────────────────────────────────────────

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private string? _titleError;

    [ObservableProperty]
    private string? _categoryError;

    [RelayCommand]
    private void Validate()
    {
        ValidateAllProperties();

        TitleError = GetLocalizedFieldError(nameof(Title));
        CategoryError = GetLocalizedFieldError(nameof(Category));

        // At least one template pattern is required
        if (string.IsNullOrWhiteSpace(WindowsPattern) && string.IsNullOrWhiteSpace(LinuxPattern))
        {
            ValidationError = Localizer?["ToolCmdLibValidationPatternRequired"]
                ?? "At least one command pattern is required.";
            return;
        }

        ValidationError = TitleError ?? CategoryError;
    }

    partial void OnTitleChanged(string value)
    {
        if (TitleError is not null)
        {
            ValidateProperty(value, nameof(Title));
            TitleError = GetLocalizedFieldError(nameof(Title));
            ValidationError = TitleError ?? CategoryError;
        }
    }

    partial void OnCategoryChanged(string value)
    {
        if (CategoryError is not null)
        {
            ValidateProperty(value, nameof(Category));
            CategoryError = GetLocalizedFieldError(nameof(Category));
            ValidationError = TitleError ?? CategoryError;
        }
    }

    partial void OnPlatformChanged(Platform value)
    {
        OnPropertyChanged(nameof(ShowWindowsSection));
        OnPropertyChanged(nameof(ShowLinuxSection));
    }

    // ── Parameter commands ────────────────────────────────────────

    [RelayCommand]
    private void AddWindowsParameter()
    {
        WindowsParameters.Add(new ParameterEntryVm());
    }

    [RelayCommand]
    private void RemoveWindowsParameter(ParameterEntryVm? param)
    {
        if (param is not null) WindowsParameters.Remove(param);
    }

    [RelayCommand]
    private void AddLinuxParameter()
    {
        LinuxParameters.Add(new ParameterEntryVm());
    }

    [RelayCommand]
    private void RemoveLinuxParameter(ParameterEntryVm? param)
    {
        if (param is not null) LinuxParameters.Remove(param);
    }

    // ── Dirty tracking ────────────────────────────────────────────

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_isInitializing) return;

        if (e.PropertyName is nameof(Title) or nameof(Description)
            or nameof(Category) or nameof(Platform) or nameof(Level)
            or nameof(Tags) or nameof(Notes)
            or nameof(WindowsPattern) or nameof(WindowsTemplateName)
            or nameof(LinuxPattern) or nameof(LinuxTemplateName))
        {
            IsDirty = true;
        }
    }

    // ── Conversion ────────────────────────────────────────────────

    public ActionModel ToAction()
    {
        var action = new ActionModel
        {
            Id = _editActionId ?? Guid.NewGuid().ToString(),
            PublicId = _editPublicId ?? Guid.NewGuid(),
            Title = Title.Trim(),
            Description = Description.Trim(),
            Category = Category.Trim(),
            Platform = Platform,
            Level = Level,
            Tags = string.IsNullOrWhiteSpace(Tags)
                ? [] : Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            Examples = _editExamples is null ? new() : new List<CommandExample>(_editExamples),
            WindowsExamples = _editWindowsExamples is null ? new() : new List<CommandExample>(_editWindowsExamples),
            LinuxExamples = _editLinuxExamples is null ? new() : new List<CommandExample>(_editLinuxExamples),
            Links = _editLinks is null ? new() : new List<ExternalLink>(_editLinks),
            IsUserCreated = true,
            UpdatedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(WindowsPattern))
        {
            var templateId = _editWinTemplateId ?? $"{action.Id}-win";
            action.WindowsCommandTemplateId = templateId;
            action.WindowsCommandTemplate = new CommandTemplate
            {
                Id = templateId,
                PublicId = _editWinTemplatePublicId ?? Guid.NewGuid(),
                Platform = TwinShell.Core.Enums.Platform.Windows,
                Name = string.IsNullOrWhiteSpace(WindowsTemplateName) ? Title.Trim() : WindowsTemplateName.Trim(),
                CommandPattern = WindowsPattern.Trim(),
                Parameters = WindowsParameters.Select(p => p.ToModel()).ToList()
            };
        }

        if (!string.IsNullOrWhiteSpace(LinuxPattern))
        {
            var templateId = _editLinuxTemplateId ?? $"{action.Id}-linux";
            action.LinuxCommandTemplateId = templateId;
            action.LinuxCommandTemplate = new CommandTemplate
            {
                Id = templateId,
                PublicId = _editLinuxTemplatePublicId ?? Guid.NewGuid(),
                Platform = TwinShell.Core.Enums.Platform.Linux,
                Name = string.IsNullOrWhiteSpace(LinuxTemplateName) ? Title.Trim() : LinuxTemplateName.Trim(),
                CommandPattern = LinuxPattern.Trim(),
                Parameters = LinuxParameters.Select(p => p.ToModel()).ToList()
            };
        }

        action.CreatedAt = _editCreatedAt ?? DateTime.UtcNow;

        return action;
    }

    public static CommandActionDialogViewModel FromAction(ActionModel action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var vm = new CommandActionDialogViewModel { _isInitializing = true };
        vm.IsEditMode = true;
        vm._editActionId = action.Id;
        vm._editPublicId = action.PublicId;
        vm._editCreatedAt = action.CreatedAt;
        vm.Title = action.Title;
        vm.Description = action.Description ?? "";
        vm.Category = action.Category;
        vm.Platform = action.Platform;
        vm.Level = action.Level;
        vm.Tags = action.Tags is { Count: > 0 } ? string.Join(", ", action.Tags) : "";
        vm.Notes = action.Notes ?? "";
        vm._editExamples = new List<CommandExample>(action.Examples);
        vm._editWindowsExamples = new List<CommandExample>(action.WindowsExamples);
        vm._editLinuxExamples = new List<CommandExample>(action.LinuxExamples);
        vm._editLinks = new List<ExternalLink>(action.Links);

        if (action.WindowsCommandTemplate is { } winTemplate)
        {
            vm._editWinTemplateId = winTemplate.Id;
            vm._editWinTemplatePublicId = winTemplate.PublicId;
            vm.WindowsTemplateName = winTemplate.Name;
            vm.WindowsPattern = winTemplate.CommandPattern;
            foreach (var p in winTemplate.Parameters)
            {
                vm.WindowsParameters.Add(ParameterEntryVm.FromModel(p));
            }
        }

        if (action.LinuxCommandTemplate is { } linuxTemplate)
        {
            vm._editLinuxTemplateId = linuxTemplate.Id;
            vm._editLinuxTemplatePublicId = linuxTemplate.PublicId;
            vm.LinuxTemplateName = linuxTemplate.Name;
            vm.LinuxPattern = linuxTemplate.CommandPattern;
            foreach (var p in linuxTemplate.Parameters)
            {
                vm.LinuxParameters.Add(ParameterEntryVm.FromModel(p));
            }
        }

        vm._isInitializing = false;
        return vm;
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ValidationKeyMap = new(StringComparer.Ordinal)
    {
        ["Title is required."] = "ToolCmdLibValidationTitleRequired",
        ["Title must not exceed 200 characters."] = "ToolCmdLibValidationTitleMaxLength",
        ["Category is required."] = "ToolCmdLibValidationCategoryRequired",
        ["Description must not exceed 2000 characters."] = "ToolCmdLibValidationDescMaxLength",
        ["Notes must not exceed 5000 characters."] = "ToolCmdLibValidationNotesMaxLength",
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
/// Editable parameter entry for the template parameter list.
/// </summary>
public partial class ParameterEntryVm : ObservableObject
{
    public static readonly string[] AvailableTypes =
        ["string", "int", "bool", "hostname", "ipaddress", "path"];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _type = "string";
    [ObservableProperty] private string _defaultValue = "";
    [ObservableProperty] private bool _required;
    [ObservableProperty] private string _description = "";

    public TemplateParameter ToModel() => new()
    {
        Name = Name.Trim(),
        Label = Label.Trim(),
        Type = Type,
        DefaultValue = string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue.Trim(),
        Required = Required,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim()
    };

    public static ParameterEntryVm FromModel(TemplateParameter p) => new()
    {
        Name = p.Name,
        Label = p.Label,
        Type = p.Type ?? "string",
        DefaultValue = p.DefaultValue ?? "",
        Required = p.Required,
        Description = p.Description ?? ""
    };
}
