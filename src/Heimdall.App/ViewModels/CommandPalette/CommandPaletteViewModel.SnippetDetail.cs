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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;
using CommandExample = TwinShell.Core.Models.CommandExample;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;
using ExternalLink = TwinShell.Core.Models.ExternalLink;

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Snippet drill-in (detail) surface of the Command Palette: lets the user pick
/// among an action's related command variants, fill template parameters inline,
/// then send the generated command to the active session (Enter), copy it
/// (Ctrl+Enter), or return to the result list (Esc).
/// </summary>
public sealed partial class CommandPaletteViewModel
{
    // ── Scoped TwinShell services (mirrors CommandLibraryViewModel) ───
    private IServiceScope? _snippetScope;
    private ICommandGeneratorService? _snippetGenerator;
    private CommandTemplate? _snippetTemplate;

    // Retains the action currently open in the detail surface so its criticality
    // can be read when a snippet is sent (the execute path is guarded).
    private ActionModel? _detailAction;

    // True when SnippetGeneratedCommand currently holds a literal example applied
    // via ApplySnippetExample rather than output produced by the generator. Example
    // text bypasses parameter validation and escaping, so a snippet-level Send must
    // always confirm before running it regardless of the action's level (mirrors
    // CommandLibraryViewModel._generatedFromExample).
    private bool _snippetGeneratedFromExample;

    /// <summary>
    /// Test seam: true when <see cref="SnippetGeneratedCommand"/> currently holds an
    /// applied example. Reset whenever the command is regenerated from parameters,
    /// the variant selection changes, or the detail surface closes.
    /// </summary>
    internal bool SnippetGeneratedFromExample => _snippetGeneratedFromExample;

    // ── Observable detail state ──────────────────────────────────────

    /// <summary>True while the snippet drill-in detail view is shown over the result list.</summary>
    [ObservableProperty]
    private bool _isSnippetDetailOpen;

    /// <summary>Title of the action being drilled into (used by status messages and the header).</summary>
    [ObservableProperty]
    private string _snippetDetailTitle = string.Empty;

    /// <summary>Selectable command variants of the current action.</summary>
    [ObservableProperty]
    private ObservableCollection<SnippetVariantDisplayItem> _snippetVariants = new();

    /// <summary>Currently selected variant (drives the parameter rebuild).</summary>
    [ObservableProperty]
    private SnippetVariantDisplayItem? _selectedSnippetVariant;

    /// <summary>Editable parameter entries for the active template variant (empty for examples).</summary>
    [ObservableProperty]
    private ObservableCollection<CommandLibraryParameterEntry> _snippetParameters = new();

    /// <summary>The command text to send/copy (generated from the template or a literal example).</summary>
    [ObservableProperty]
    private string _snippetGeneratedCommand = string.Empty;

    /// <summary>Multi-line validation error text (one bullet per error), empty when valid.</summary>
    [ObservableProperty]
    private string _snippetValidationError = string.Empty;

    /// <summary>True when the current parameter set produces a valid command.</summary>
    [ObservableProperty]
    private bool _isSnippetCommandValid;

    /// <summary>True when the active variant exposes editable template parameters.</summary>
    public bool SnippetHasParameters => SnippetParameters.Count > 0;

    // ── Action explanation (from CommandPresentationResolver) ─────────

    /// <summary>Localized description of the action being drilled into (empty when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnippetHasDescription))]
    private string _snippetDetailDescription = string.Empty;

    /// <summary>Additional notes for the action being drilled into (empty when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnippetHasNotes))]
    private string _snippetDetailNotes = string.Empty;

    /// <summary>Localized long-form risk label, shown as the risk badge tooltip.</summary>
    [ObservableProperty]
    private string _snippetDetailRiskLabel = string.Empty;

    /// <summary>Localized short risk badge text.</summary>
    [ObservableProperty]
    private string _snippetDetailRiskBadge = string.Empty;

    /// <summary>Theme resource key for the brush representing the risk level.</summary>
    [ObservableProperty]
    private string _snippetDetailRiskBrushKey = string.Empty;

    /// <summary>Localized platform label (Windows / Linux / Both).</summary>
    [ObservableProperty]
    private string _snippetDetailPlatformLabel = string.Empty;

    /// <summary>True when the drilled-in action exposes a description.</summary>
    public bool SnippetHasDescription => !string.IsNullOrEmpty(SnippetDetailDescription);

    /// <summary>True when the drilled-in action exposes notes.</summary>
    public bool SnippetHasNotes => !string.IsNullOrEmpty(SnippetDetailNotes);

    /// <summary>Command examples of the drilled-in action (copied on demand).</summary>
    public ObservableCollection<CommandExample> SnippetExamples { get; } = new();

    /// <summary>External documentation links of the drilled-in action.</summary>
    public ObservableCollection<ExternalLink> SnippetLinks { get; } = new();

    /// <summary>True when the drilled-in action exposes at least one example.</summary>
    public bool SnippetHasExamples => SnippetExamples.Count > 0;

    /// <summary>True when the drilled-in action exposes at least one documentation link.</summary>
    public bool SnippetHasLinks => SnippetLinks.Count > 0;

    /// <summary>True when the drilled-in action exposes template (generator) variants.</summary>
    [ObservableProperty]
    private bool _snippetHasVariants;

    // ── Variant selection ────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the parameter editor and generated command whenever the selected
    /// variant changes. Template variants build editable parameters; literal
    /// example variants use the command verbatim.
    /// </summary>
    partial void OnSelectedSnippetVariantChanged(SnippetVariantDisplayItem? value)
    {
        // A variant change produces a fresh generated command, so any previously
        // applied example is no longer in effect.
        _snippetGeneratedFromExample = false;

        UnsubscribeSnippetParameters();
        SnippetParameters.Clear();

        if (value is null)
        {
            _snippetTemplate = null;
            SnippetGeneratedCommand = string.Empty;
            SnippetValidationError = string.Empty;
            IsSnippetCommandValid = false;
            OnPropertyChanged(nameof(SnippetHasParameters));
            return;
        }

        if (value.Variant.Template is not null)
        {
            _snippetTemplate = value.Variant.Template;
            var entries = SnippetParameterFactory.Build(_snippetTemplate, ResolveActiveSessionHost());
            foreach (var entry in entries)
            {
                entry.PropertyChanged += OnSnippetParameterValueChanged;
                SnippetParameters.Add(entry);
            }
            RegenerateSnippetCommand();
        }
        else
        {
            _snippetTemplate = null;
            SnippetGeneratedCommand = value.Variant.LiteralCommand ?? string.Empty;
            SnippetValidationError = string.Empty;
            IsSnippetCommandValid = true;
        }

        OnPropertyChanged(nameof(SnippetHasParameters));
    }

    private void OnSnippetParameterValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandLibraryParameterEntry.Value))
        {
            RegenerateSnippetCommand();
        }
    }

    /// <summary>
    /// Recomputes <see cref="SnippetGeneratedCommand"/> from the current
    /// parameter values via the scoped command generator. Mirrors
    /// <c>CommandLibraryViewModel.RegenerateCommand</c>.
    /// </summary>
    private void RegenerateSnippetCommand()
    {
        if (_snippetTemplate is null || _snippetGenerator is null) return;

        _snippetGeneratedFromExample = false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in SnippetParameters)
        {
            values[p.Name] = p.Value;
        }

        try
        {
            var valid = _snippetGenerator.ValidateParameters(_snippetTemplate, values, out var errors);
            if (valid && errors.Count == 0)
            {
                SnippetGeneratedCommand = _snippetGenerator.GenerateCommand(_snippetTemplate, values);
                SnippetValidationError = string.Empty;
                IsSnippetCommandValid = true;
            }
            else
            {
                SnippetGeneratedCommand = _snippetTemplate.CommandPattern;
                SnippetValidationError = string.Join(
                    Environment.NewLine,
                    errors.Select(static error => $"- {error}"));
                IsSnippetCommandValid = false;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CommandPalette] Snippet generate failed: {ex.Message}");
            SnippetGeneratedCommand = _snippetTemplate.CommandPattern;
            SnippetValidationError = string.Format(_localizer["ToolCmdLibGenerateError"], ex.Message);
            IsSnippetCommandValid = false;
        }
    }

    // ── Open / close detail ──────────────────────────────────────────

    /// <summary>
    /// Opens the in-palette snippet detail view for the given action. When the
    /// action has no structured variants, falls back to the legacy
    /// copy-to-clipboard behavior and does not open the detail surface.
    /// </summary>
    public void OpenSnippetDetail(ActionModel action)
    {
        var presentation = CommandPresentationResolver.Resolve(action, key => _localizer[key]);
        var templateVariants = SnippetVariantResolver.GetVariants(action)
            .Where(v => v.Kind != SnippetVariantKind.Example)
            .ToList();

        // Open when there is at least one template generator or any metadata
        // (notes / examples / links) worth showing; otherwise keep the legacy
        // copy-to-clipboard fallback.
        if (templateVariants.Count == 0 && !presentation.HasMetadata)
        {
            CopySnippetPayload(ResolveSnippetCommand(action), action.Title);
            return;
        }

        CloseSnippetDetail();

        _snippetScope = _scopeFactory.CreateScope();
        _snippetGenerator = _snippetScope.ServiceProvider.GetRequiredService<ICommandGeneratorService>();

        _detailAction = action;
        SnippetDetailTitle = action.Title;

        SnippetDetailDescription = presentation.Description;
        SnippetDetailNotes = presentation.Notes;
        SnippetDetailRiskLabel = presentation.RiskLabel;
        SnippetDetailRiskBadge = presentation.RiskBadge;
        SnippetDetailRiskBrushKey = presentation.RiskBrushKey;
        SnippetDetailPlatformLabel = presentation.PlatformLabel;

        SnippetExamples.Clear();
        foreach (var example in presentation.Examples)
        {
            SnippetExamples.Add(example);
        }
        SnippetLinks.Clear();
        foreach (var link in presentation.Links)
        {
            SnippetLinks.Add(link);
        }
        OnPropertyChanged(nameof(SnippetHasExamples));
        OnPropertyChanged(nameof(SnippetHasLinks));

        // Variants list holds template (generator) variants only; examples live
        // exclusively in the Examples section.
        foreach (var variant in templateVariants)
        {
            SnippetVariants.Add(new SnippetVariantDisplayItem
            {
                DisplayLabel = BuildVariantLabel(variant),
                PreviewCommand = variant.PreviewCommand,
                Variant = variant
            });
        }
        SnippetHasVariants = templateVariants.Count > 0;

        IsSnippetDetailOpen = true;

        SelectedSnippetVariant = SnippetVariants.FirstOrDefault();
    }

    /// <summary>
    /// Resets all snippet-detail state, disposes the scoped generator, and hides
    /// the detail surface. Safe to call when no detail is open.
    /// </summary>
    private void CloseSnippetDetail()
    {
        UnsubscribeSnippetParameters();
        SnippetParameters.Clear();
        SnippetVariants.Clear();
        SnippetHasVariants = false;
        SnippetDetailTitle = string.Empty;
        SnippetDetailDescription = string.Empty;
        SnippetDetailNotes = string.Empty;
        SnippetDetailRiskLabel = string.Empty;
        SnippetDetailRiskBadge = string.Empty;
        SnippetDetailRiskBrushKey = string.Empty;
        SnippetDetailPlatformLabel = string.Empty;
        SnippetExamples.Clear();
        SnippetLinks.Clear();
        OnPropertyChanged(nameof(SnippetHasExamples));
        OnPropertyChanged(nameof(SnippetHasLinks));
        SnippetGeneratedCommand = string.Empty;
        SnippetValidationError = string.Empty;
        IsSnippetCommandValid = false;
        _snippetGeneratedFromExample = false;
        IsSnippetDetailOpen = false;
        SelectedSnippetVariant = null;
        _snippetTemplate = null;
        _detailAction = null;
        _snippetGenerator = null;
        _snippetScope?.Dispose();
        _snippetScope = null;
        OnPropertyChanged(nameof(SnippetHasParameters));
    }

    private void UnsubscribeSnippetParameters()
    {
        foreach (var p in SnippetParameters)
        {
            p.PropertyChanged -= OnSnippetParameterValueChanged;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends the generated command to the active session's terminal, falling
    /// back to the clipboard when no session can accept it (e.g. RDP).
    /// </summary>
    [RelayCommand]
    private async Task SendSnippetAsync()
    {
        if (string.IsNullOrEmpty(SnippetGeneratedCommand)) return;

        var active = _main.Connection.ActiveSession;
        if (active is not null)
        {
            // A command applied from an example bypasses parameter validation and
            // escaping, so force confirmation regardless of the action's level
            // (mirrors CommandLibraryViewModel.SendAsync).
            var level = _snippetGeneratedFromExample
                ? CriticalityLevel.Dangerous
                : _detailAction?.Level ?? CriticalityLevel.Info;
            if (!await DangerousCommandGuard.ConfirmIfDangerousAsync(
                    level, _dialogService, key => _localizer[key], topmost: true))
            {
                return;
            }
        }

        if (active is not null
            && _embeddedSessionManager.TrySendCommandToSession(active, SnippetGeneratedCommand))
        {
            _main.StatusText = _localizer.Format("PaletteSnippetSent", SnippetDetailTitle);
        }
        else
        {
            CopySnippetPayload(SnippetGeneratedCommand, SnippetDetailTitle);
        }

        CloseSnippetDetail();
        IsOpen = false;
    }

    /// <summary>Copies the generated command to the clipboard and closes the palette.</summary>
    [RelayCommand]
    private void CopySnippet()
    {
        if (string.IsNullOrEmpty(SnippetGeneratedCommand)) return;

        CopySnippetPayload(SnippetGeneratedCommand, SnippetDetailTitle);
        CloseSnippetDetail();
        IsOpen = false;
    }

    /// <summary>
    /// Copies a single example command to the clipboard without closing the
    /// palette (a convenience, unlike <see cref="CopySnippet"/>).
    /// </summary>
    [RelayCommand]
    private void CopySnippetExample(CommandExample? example)
    {
        if (example is null || string.IsNullOrEmpty(example.Command)) return;
        CopySnippetPayload(example.Command, SnippetDetailTitle);
    }

    /// <summary>
    /// Loads an example command into the <see cref="SnippetGeneratedCommand"/> preview
    /// so the user can review it before Copy/Send, mirroring
    /// <c>CommandLibraryViewModel.ApplyExample</c>. Marks the command valid and flags it
    /// as example-sourced (so a later Send forces confirmation). No-op when empty.
    /// </summary>
    [RelayCommand]
    private void ApplySnippetExample(string? command)
    {
        if (string.IsNullOrEmpty(command)) return;

        SnippetGeneratedCommand = command;
        SnippetValidationError = string.Empty;
        IsSnippetCommandValid = true;
        _snippetGeneratedFromExample = true;
    }

    /// <summary>
    /// Sends a single example command to the active session (or copies it when no
    /// session can accept it) and closes the palette, mirroring <see cref="SendSnippet"/>.
    /// </summary>
    [RelayCommand]
    private async Task SendSnippetExampleAsync(CommandExample? example)
    {
        if (example is null || string.IsNullOrEmpty(example.Command)) return;

        var active = _main.Connection.ActiveSession;
        if (active is not null)
        {
            var level = _detailAction?.Level ?? CriticalityLevel.Info;
            if (!await DangerousCommandGuard.ConfirmIfDangerousAsync(
                    level, _dialogService, key => _localizer[key], topmost: true))
            {
                return;
            }
        }

        if (active is not null
            && _embeddedSessionManager.TrySendCommandToSession(active, example.Command))
        {
            _main.StatusText = _localizer.Format("PaletteSnippetSent", SnippetDetailTitle);
        }
        else
        {
            CopySnippetPayload(example.Command, SnippetDetailTitle);
        }

        CloseSnippetDetail();
        IsOpen = false;
    }

    /// <summary>Returns to the palette result list, keeping the palette open.</summary>
    [RelayCommand]
    private void BackFromSnippetDetail()
    {
        CloseSnippetDetail();
    }

    /// <summary>
    /// Bridges the palette (discovery) to the Command Library (execution): opens
    /// (or focuses, if already open) the CMDLIB tool tab preselected on the action
    /// currently drilled into, then closes the palette. The action id is carried
    /// via <see cref="ToolContext.InitialActionId"/>; CMDLIB resolves it against
    /// the same <c>IActionService</c> the palette uses. No direct command injection
    /// happens here: the user sends or copies from CMDLIB. No-op when no detail is open.
    /// </summary>
    [RelayCommand]
    private async Task OpenSnippetInLibraryAsync()
    {
        var action = _detailAction;
        if (action is null) return;

        var context = new ToolContext(InitialActionId: action.Id);
        var title = _localizer["PaletteToolCmdLib"];

        _main.TrackRecentTool(ToolRegistry.CommandLibraryToolId);

        CloseSnippetDetail();
        IsOpen = false;

        await _main.OpenToolTabAsync(ToolRegistry.CommandLibraryToolId, title, context);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Copies the given payload to the clipboard and sets the "copied" status,
    /// swallowing and logging clipboard failures (no modal).
    /// </summary>
    private void CopySnippetPayload(string payload, string title)
    {
        if (SetClipboardText?.Invoke(payload) == true)
        {
            _main.StatusText = _localizer.Format("PaletteSnippetCopied", title);
            return;
        }

        FileLogger.Warn("Snippet clipboard copy failed.");
    }

    /// <summary>
    /// Best-effort host string of the active session: looks the session up in the
    /// server inventory by <c>OriginalServerId</c> and returns its remote host,
    /// or null when no session / no match / no host.
    /// </summary>
    private string? ResolveActiveSessionHost()
    {
        var active = _main.Connection.ActiveSession;
        if (active is null) return null;

        var originalId = active.OriginalServerId;
        if (string.IsNullOrWhiteSpace(originalId)) return null;

        var match = _main.ServerList.Servers
            .FirstOrDefault(s => string.Equals(s.Id, originalId, StringComparison.Ordinal));
        var host = match?.RemoteServer;
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    /// <summary>Builds the localized label for a variant from its kind and platform.</summary>
    private string BuildVariantLabel(SnippetVariant variant)
    {
        switch (variant.Kind)
        {
            case SnippetVariantKind.WindowsTemplate:
                return _localizer["PaletteVariantWindowsTemplate"];
            case SnippetVariantKind.LinuxTemplate:
                return _localizer["PaletteVariantLinuxTemplate"];
            default:
                var number = variant.ExampleIndex + 1;
                return variant.Platform switch
                {
                    Platform.Windows => _localizer.Format("PaletteVariantExampleWindows", number),
                    Platform.Linux => _localizer.Format("PaletteVariantExampleLinux", number),
                    _ => _localizer.Format("PaletteVariantExample", number)
                };
        }
    }
}
