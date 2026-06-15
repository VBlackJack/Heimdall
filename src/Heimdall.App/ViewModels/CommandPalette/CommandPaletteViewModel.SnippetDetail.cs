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
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;

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

    // ── Variant selection ────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the parameter editor and generated command whenever the selected
    /// variant changes. Template variants build editable parameters; literal
    /// example variants use the command verbatim.
    /// </summary>
    partial void OnSelectedSnippetVariantChanged(SnippetVariantDisplayItem? value)
    {
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
            SnippetValidationError = string.Empty;
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
        var variants = SnippetVariantResolver.GetVariants(action);
        if (variants.Count == 0)
        {
            CopySnippetPayload(ResolveSnippetCommand(action), action.Title);
            return;
        }

        CloseSnippetDetail();

        _snippetScope = _serviceProvider.CreateScope();
        _snippetGenerator = _snippetScope.ServiceProvider.GetRequiredService<ICommandGeneratorService>();

        SnippetDetailTitle = action.Title;
        foreach (var variant in variants)
        {
            SnippetVariants.Add(new SnippetVariantDisplayItem
            {
                DisplayLabel = BuildVariantLabel(variant),
                PreviewCommand = variant.PreviewCommand,
                Variant = variant
            });
        }

        IsSnippetDetailOpen = true;

        var defaultVariant = SnippetVariantResolver.GetDefault(action);
        var defaultItem = defaultVariant is null
            ? null
            : SnippetVariants.FirstOrDefault(v => ReferenceEquals(v.Variant, defaultVariant));
        SelectedSnippetVariant = defaultItem ?? SnippetVariants.FirstOrDefault();
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
        SnippetDetailTitle = string.Empty;
        SnippetGeneratedCommand = string.Empty;
        SnippetValidationError = string.Empty;
        IsSnippetCommandValid = false;
        IsSnippetDetailOpen = false;
        SelectedSnippetVariant = null;
        _snippetTemplate = null;
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
    private void SendSnippet()
    {
        if (string.IsNullOrEmpty(SnippetGeneratedCommand)) return;

        var active = _main.Connection.ActiveSession;
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

    /// <summary>Returns to the palette result list, keeping the palette open.</summary>
    [RelayCommand]
    private void BackFromSnippetDetail()
    {
        CloseSnippetDetail();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Copies the given payload to the clipboard and sets the "copied" status,
    /// swallowing and logging clipboard failures (no modal).
    /// </summary>
    private void CopySnippetPayload(string payload, string title)
    {
        try
        {
            System.Windows.Clipboard.SetText(payload);
            _main.StatusText = _localizer.Format("PaletteSnippetCopied", title);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Snippet clipboard copy failed: {ex.Message}");
        }
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
