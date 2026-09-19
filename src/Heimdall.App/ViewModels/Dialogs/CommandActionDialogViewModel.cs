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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the command action add/edit dialog.
/// Supports dual-platform templates with parameterized patterns.
/// </summary>
public partial class CommandActionDialogViewModel : ObservableValidator
{
    /// <summary>
    /// Watches the two parameter lists so the consistency warning stays live while the
    /// action is being written.
    /// </summary>
    /// <remarks>
    /// It has to be live rather than computed on save: the warning does not block, so a
    /// dialog that only produced it on Save would close in the same gesture and the
    /// operator would never read it.
    /// </remarks>
    public CommandActionDialogViewModel()
    {
        WindowsParameters.CollectionChanged += OnParameterListChanged;
        LinuxParameters.CollectionChanged += OnParameterListChanged;
    }

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

    // ── Action fields ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Title is required.")]
    [MaxLength(200, ErrorMessage = "Title must not exceed 200 characters.")]
    private string _title = "";

    [ObservableProperty]
    [NotifyDataErrorInfo]
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
    [NotifyDataErrorInfo]
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

    // ── Examples & links ──────────────────────────────────────────

    /// <summary>
    /// Single editable example list; each row carries its own target platform.
    /// Routed into the action's three example buckets on save.
    /// </summary>
    public ObservableCollection<ExampleEntryVm> Examples { get; } = [];

    public ObservableCollection<LinkEntryVm> Links { get; } = [];

    private IReadOnlyList<string>? _platformOptions;

    /// <summary>
    /// Localized platform names, index-aligned to the <see cref="Platform"/> enum
    /// (0 = Windows, 1 = Linux, 2 = Both), so a row's enum value maps to the combo
    /// <c>SelectedIndex</c>. Falls back to plain names when no localizer is set.
    /// </summary>
    public IReadOnlyList<string> PlatformOptions => _platformOptions ??=
    [
        Localizer?["ToolCmdLibPlatformWindows"] ?? "Windows",
        Localizer?["ToolCmdLibPlatformLinux"] ?? "Linux",
        Localizer?["ToolCmdLibPlatformBoth"] ?? "Both",
    ];

    // ── Validation ────────────────────────────────────────────────

    [ObservableProperty]
    private string? _validationError;

    /// <summary>
    /// What the command pattern and the declared parameters disagree about, or null when
    /// they agree.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ValidationError"/>, which blocks the save.
    /// Half of what this reports is a reading of intent rather than a fact - braces are
    /// ordinary shell punctuation - and a guess must not refuse somebody's work. The other
    /// half is certain but harmless. Both are worth saying; neither is worth stopping for.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsistencyWarning))]
    private string? _consistencyWarning;

    /// <summary>True when there is something to say about the pattern.</summary>
    public bool HasConsistencyWarning => !string.IsNullOrEmpty(ConsistencyWarning);

    /// <summary>
    /// How many times the warning has been recomputed. Exposed for tests only.
    /// </summary>
    /// <remarks>
    /// Unsubscribing a removed parameter changes nothing a caller can see: the recomputed
    /// text is identical either way, because the entry is gone from the list the check
    /// reads. Measured - a test written against the text alone passes with the
    /// unsubscription deleted. The count is the only place the difference shows, so the
    /// rule is stated against the count.
    /// </remarks>
    internal int ConsistencyRecomputeCount { get; private set; }

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

        // Description and Notes have their own length limits. They carry no inline error
        // label of their own, so their message is surfaced through the shared line;
        // without this the limits were decorative and an over-long value saved silently.
        ValidationError = TitleError
            ?? CategoryError
            ?? GetLocalizedFieldError(nameof(Description))
            ?? GetLocalizedFieldError(nameof(Notes));
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

    partial void OnWindowsPatternChanged(string value) => RefreshConsistencyWarning();

    partial void OnLinuxPatternChanged(string value) => RefreshConsistencyWarning();

    private void OnParameterListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var entry in e.OldItems?.OfType<ParameterEntryVm>() ?? [])
        {
            entry.PropertyChanged -= OnParameterEntryChanged;
        }

        foreach (var entry in e.NewItems?.OfType<ParameterEntryVm>() ?? [])
        {
            entry.PropertyChanged += OnParameterEntryChanged;
        }

        RefreshConsistencyWarning();
    }

    private void OnParameterEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the name decides whether a parameter matches the pattern.
        if (e.PropertyName == nameof(ParameterEntryVm.Name))
        {
            RefreshConsistencyWarning();
        }
    }

    /// <summary>
    /// Recomputes what the patterns and their declared parameters disagree about.
    /// </summary>
    /// <remarks>
    /// Each template is reported on its own, and named when the action carries both, since
    /// a parameter declared for Linux says nothing about the Windows pattern.
    /// </remarks>
    private void RefreshConsistencyWarning()
    {
        ConsistencyRecomputeCount++;

        var hasBoth = !string.IsNullOrWhiteSpace(WindowsPattern)
            && !string.IsNullOrWhiteSpace(LinuxPattern);

        var lines = new List<string>();
        lines.AddRange(DescribeDrift(
            WindowsPattern, WindowsParameters, hasBoth ? Localize("ToolCmdLibPlatformWindows") : null));
        lines.AddRange(DescribeDrift(
            LinuxPattern, LinuxParameters, hasBoth ? Localize("ToolCmdLibPlatformLinux") : null));

        ConsistencyWarning = lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private IEnumerable<string> DescribeDrift(
        string pattern, IEnumerable<ParameterEntryVm> parameters, string? platformLabel)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            yield break;
        }

        var names = parameters.Select(parameter => parameter.Name).ToList();

        var undeclared = CommandPatternQuoting.FindUndeclaredPlaceholders(pattern, names);
        if (undeclared.Count > 0)
        {
            yield return Prefix(platformLabel, string.Format(
                Localize("ToolCmdLibWarnUndeclaredPlaceholders"), string.Join(", ", undeclared)));
        }

        var unused = CommandPatternQuoting.FindUnusedParameters(pattern, names)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (unused.Count > 0)
        {
            yield return Prefix(platformLabel, string.Format(
                Localize("ToolCmdLibWarnUnusedParameters"), string.Join(", ", unused)));
        }
    }

    private static string Prefix(string? platformLabel, string message) =>
        platformLabel is null ? message : $"{platformLabel}: {message}";

    private string Localize(string key) => Localizer?[key] ?? key;

    partial void OnPlatformChanged(Platform value)
    {
        RefreshConsistencyWarning();
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

    // ── Example & link commands ───────────────────────────────────

    [RelayCommand]
    private void AddExample()
    {
        Examples.Add(new ExampleEntryVm());
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveExample(ExampleEntryVm? example)
    {
        if (example is not null && Examples.Remove(example)) IsDirty = true;
    }

    [RelayCommand]
    private void AddLink()
    {
        Links.Add(new LinkEntryVm());
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveLink(LinkEntryVm? link)
    {
        if (link is not null && Links.Remove(link)) IsDirty = true;
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
            Examples = Examples.Where(e => e.Platform == TwinShell.Core.Enums.Platform.Both)
                .Select(e => e.ToModel()).ToList(),
            WindowsExamples = Examples.Where(e => e.Platform == TwinShell.Core.Enums.Platform.Windows)
                .Select(e => e.ToModel()).ToList(),
            LinuxExamples = Examples.Where(e => e.Platform == TwinShell.Core.Enums.Platform.Linux)
                .Select(e => e.ToModel()).ToList(),
            Links = Links.Select(l => l.ToModel()).ToList(),
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

    /// <summary>
    /// Builds a dialog for a new action that starts as a copy of <paramref name="action"/>,
    /// carrying its content and none of its identity.
    /// </summary>
    /// <param name="action">The action to copy. It is not modified.</param>
    /// <param name="title">Title for the copy, already localized by the caller.</param>
    /// <remarks>
    /// <para>
    /// The point is the actions the library ships: they cannot be edited, so the only way
    /// to get a variant of one was to retype it. A copy is a new action in every respect -
    /// it mints its own identifiers when saved, and saving it leaves the original alone.
    /// </para>
    /// <para>
    /// Everything that identifies the source is deliberately dropped: the action id, its
    /// public id, its creation date, and the identifiers of both templates. Carrying any
    /// one of them would make the save an update of the original rather than a new row,
    /// which for a shipped action means silently editing something that is supposed to be
    /// read-only.
    /// </para>
    /// </remarks>
    public static CommandActionDialogViewModel AsCopyOf(ActionModel action, string title)
    {
        ArgumentNullException.ThrowIfNull(action);

        var vm = FromAction(action);

        vm._isInitializing = true;
        vm.IsEditMode = false;
        vm.Title = title;

        vm._editActionId = null;
        vm._editPublicId = null;
        vm._editCreatedAt = null;
        vm._editWinTemplateId = null;
        vm._editWinTemplatePublicId = null;
        vm._editLinuxTemplateId = null;
        vm._editLinuxTemplatePublicId = null;
        vm._isInitializing = false;

        // A copy is unsaved work from the moment it exists, unlike an edit of something
        // already stored.
        vm.IsDirty = true;

        return vm;
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
        foreach (var e in action.Examples)
        {
            vm.Examples.Add(ExampleEntryVm.FromModel(e, TwinShell.Core.Enums.Platform.Both));
        }
        foreach (var e in action.WindowsExamples)
        {
            vm.Examples.Add(ExampleEntryVm.FromModel(e, TwinShell.Core.Enums.Platform.Windows));
        }
        foreach (var e in action.LinuxExamples)
        {
            vm.Examples.Add(ExampleEntryVm.FromModel(e, TwinShell.Core.Enums.Platform.Linux));
        }
        foreach (var l in action.Links)
        {
            vm.Links.Add(LinkEntryVm.FromModel(l));
        }

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

/// <summary>
/// Editable example entry: a command plus description targeting one platform.
/// The platform routes the example into the action's matching example bucket.
/// </summary>
public partial class ExampleEntryVm : ObservableObject
{
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Platform _platform = Platform.Both;

    public CommandExample ToModel() => new()
    {
        Command = Command.Trim(),
        Description = Description.Trim(),
        Platform = Platform
    };

    public static ExampleEntryVm FromModel(CommandExample e, Platform platform) => new()
    {
        Command = e.Command,
        Description = e.Description,
        Platform = platform
    };
}

/// <summary>
/// Editable documentation link entry (title + URL).
/// </summary>
public partial class LinkEntryVm : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _url = "";

    public ExternalLink ToModel() => new()
    {
        Title = Title.Trim(),
        Url = Url.Trim()
    };

    public static LinkEntryVm FromModel(ExternalLink l) => new()
    {
        Title = l.Title,
        Url = l.Url
    };
}
