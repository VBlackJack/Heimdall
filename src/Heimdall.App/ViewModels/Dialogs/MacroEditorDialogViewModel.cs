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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;
using Heimdall.Core.Matching;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Dialogs;

public enum MacroEditorDialogAction
{
    Save,
    Delete
}

public sealed record MacroEditorDialogResult(
    MacroEditorDialogAction Action,
    TerminalMacro? Macro);

public sealed record MacroTimeoutActionOption(
    ExpectTimeoutAction Action,
    string Label);

public partial class MacroEditorDialogViewModel : ObservableObject
{
    private readonly TerminalMacro _source;
    private readonly LocalizationManager? _localizer;

    [ObservableProperty]
    private string _macroName;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private string? _validationError;

    public MacroEditorDialogViewModel(TerminalMacro macro, LocalizationManager? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(macro);

        _source = macro;
        _localizer = localizer;
        _macroName = macro.Name;

        TimeoutActionOptions =
        [
            new MacroTimeoutActionOption(ExpectTimeoutAction.Abort, T("MacroEditorTimeoutAbort", "Abort")),
            new MacroTimeoutActionOption(ExpectTimeoutAction.Continue, T("MacroEditorTimeoutContinue", "Continue"))
        ];

        foreach (var entry in macro.Entries)
        {
            Entries.Add(new MacroEntryEditorViewModel(entry, _localizer));
        }
    }

    public ObservableCollection<MacroEntryEditorViewModel> Entries { get; } = [];

    public IReadOnlyList<MacroTimeoutActionOption> TimeoutActionOptions { get; }

    public MacroEditorDialogResult? Result { get; private set; }

    public string DialogTitle => T("MacroEditorTitle", "Edit macro");

    public string TimeoutRangeText => string.Format(
        CultureInfo.CurrentCulture,
        T("MacroEditorTimeoutRange", "Range: {0}-{1} ms"),
        MacroEntry.MinExpectTimeoutMs,
        MacroEntry.MaxExpectTimeoutMs);

    [RelayCommand]
    private void AddExpectStep()
    {
        Entries.Add(new MacroEntryEditorViewModel(
            new MacroEntry
            {
                Input = string.Empty,
                DelayMs = 0,
                ExpectPattern = string.Empty,
                ExpectTimeoutMs = MacroEntry.DefaultExpectTimeoutMs
            },
            _localizer));
    }

    [RelayCommand]
    private void AddSendStep()
    {
        Entries.Add(new MacroEntryEditorViewModel(
            new MacroEntry
            {
                Input = string.Empty,
                DelayMs = 0
            },
            _localizer));
    }

    [RelayCommand]
    private void DeleteEntry(MacroEntryEditorViewModel? entry)
    {
        if (entry is not null)
        {
            Entries.Remove(entry);
        }
    }

    [RelayCommand]
    private void MoveUp(MacroEntryEditorViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        var index = Entries.IndexOf(entry);
        if (index > 0)
        {
            Entries.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(MacroEntryEditorViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        var index = Entries.IndexOf(entry);
        if (index >= 0 && index < Entries.Count - 1)
        {
            Entries.Move(index, index + 1);
        }
    }

    public bool TrySave()
    {
        ValidationError = null;
        NameError = string.IsNullOrWhiteSpace(MacroName)
            ? T("MacroEditorNameRequired", "Macro name is required.")
            : null;

        if (NameError is not null)
        {
            ValidationError = NameError;
            return false;
        }

        var entries = new List<MacroEntry>(Entries.Count);
        for (var index = 0; index < Entries.Count; index++)
        {
            var editorEntry = Entries[index];
            if (!editorEntry.TryBuildEntry(out var entry, out var error))
            {
                ValidationError = string.Format(
                    CultureInfo.CurrentCulture,
                    T("MacroEditorEntryInvalid", "Entry {0}: {1}"),
                    index + 1,
                    error);
                return false;
            }

            entries.Add(entry);
        }

        Result = new MacroEditorDialogResult(
            MacroEditorDialogAction.Save,
            new TerminalMacro
            {
                Id = _source.Id,
                Name = MacroName.Trim(),
                Description = _source.Description,
                Entries = entries,
                CreatedAt = _source.CreatedAt
            });
        return true;
    }

    public void RequestDelete()
    {
        Result = new MacroEditorDialogResult(MacroEditorDialogAction.Delete, null);
    }

    private string T(string key, string fallback)
    {
        return _localizer?[key] ?? fallback;
    }
}

public partial class MacroEntryEditorViewModel : ObservableObject
{
    private readonly LocalizationManager? _localizer;

    [ObservableProperty]
    private string _inputText;

    [ObservableProperty]
    private int _delayMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExpectPattern))]
    [NotifyPropertyChangedFor(nameof(ExpectControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(RegexValidationMessage))]
    private string _expectPattern;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegexValidationMessage))]
    private bool _expectIsRegex;

    [ObservableProperty]
    private int _expectTimeoutMs;

    [ObservableProperty]
    private ExpectTimeoutAction _expectOnTimeout;

    [ObservableProperty]
    private string? _inputValidationMessage;

    public MacroEntryEditorViewModel(MacroEntry entry, LocalizationManager? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _localizer = localizer;
        _inputText = MacroInputEscaper.Encode(entry.Input);
        _delayMs = Math.Max(0, entry.DelayMs);
        _expectPattern = entry.ExpectPattern ?? string.Empty;
        _expectIsRegex = entry.ExpectIsRegex;
        _expectTimeoutMs = entry.GetEffectiveExpectTimeoutMs();
        _expectOnTimeout = entry.ExpectOnTimeout;
    }

    public bool HasExpectPattern => ExpectPattern.Length > 0;

    public bool ExpectControlsEnabled => HasExpectPattern;

    public string? RegexValidationMessage
    {
        get
        {
            if (!HasExpectPattern || !ExpectIsRegex)
            {
                return null;
            }

            var result = RegexEngine.Test(ExpectPattern, string.Empty, RegexOptions.None);
            return result.Status == RegexTestStatus.InvalidPattern
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    T("MacroEditorRegexInvalid", "Invalid regex: {0}"),
                    result.ErrorMessage)
                : null;
        }
    }

    public bool TryBuildEntry(out MacroEntry entry, out string? error)
    {
        if (!MacroInputEscaper.TryDecode(InputText, out var input, out error))
        {
            InputValidationMessage = error;
            entry = new MacroEntry();
            return false;
        }

        InputValidationMessage = null;

        if (RegexValidationMessage is { } regexError)
        {
            error = regexError;
            entry = new MacroEntry();
            return false;
        }

        entry = new MacroEntry
        {
            Input = input,
            DelayMs = Math.Max(0, DelayMs),
            ExpectPattern = HasExpectPattern ? ExpectPattern : null,
            ExpectTimeoutMs = HasExpectPattern ? ExpectTimeoutMs : null,
            ExpectIsRegex = HasExpectPattern && ExpectIsRegex,
            ExpectOnTimeout = ExpectOnTimeout
        };
        return true;
    }

    partial void OnExpectIsRegexChanged(bool value)
    {
        OnPropertyChanged(nameof(RegexValidationMessage));
    }

    partial void OnExpectTimeoutMsChanged(int value)
    {
        var clamped = Math.Clamp(value, MacroEntry.MinExpectTimeoutMs, MacroEntry.MaxExpectTimeoutMs);
        if (clamped != value)
        {
            ExpectTimeoutMs = clamped;
        }
    }

    partial void OnInputTextChanged(string value)
    {
        if (InputValidationMessage is not null)
        {
            InputValidationMessage = null;
        }
    }

    private string T(string key, string fallback)
    {
        return _localizer?[key] ?? fallback;
    }
}

public static class MacroInputEscaper
{
    public static string Encode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(ch) || ch == '\u007F')
                    {
                        builder.Append(@"\x");
                        builder.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    public static bool TryDecode(string text, out string decoded, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            if (index == text.Length - 1)
            {
                decoded = string.Empty;
                error = "Trailing escape marker.";
                return false;
            }

            var next = text[++index];
            switch (next)
            {
                case '\\':
                    builder.Append('\\');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'x':
                    if (index + 2 >= text.Length)
                    {
                        decoded = string.Empty;
                        error = @"\x escape requires two hexadecimal digits.";
                        return false;
                    }

                    var hex = text.Substring(index + 1, 2);
                    if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    {
                        decoded = string.Empty;
                        error = @"\x escape requires two hexadecimal digits.";
                        return false;
                    }

                    builder.Append((char)value);
                    index += 2;
                    break;
                default:
                    decoded = string.Empty;
                    error = $"Unsupported escape sequence: \\{next}.";
                    return false;
            }
        }

        decoded = builder.ToString();
        error = null;
        return true;
    }
}
