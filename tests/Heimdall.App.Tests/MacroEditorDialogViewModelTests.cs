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

using System.Text.Json;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class MacroEditorDialogViewModelTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Constructor_LoadsMacroIntoEditableRows()
    {
        var macro = CreateMacro();

        var vm = new MacroEditorDialogViewModel(macro);

        Assert.Equal("Macro A", vm.MacroName);
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal(@"echo READY\n", vm.Entries[0].InputText);
        Assert.Equal(25, vm.Entries[0].DelayMs);
        Assert.Equal("READY", vm.Entries[1].ExpectPattern);
        Assert.True(vm.Entries[1].ExpectIsRegex);
        Assert.Equal(1_500, vm.Entries[1].ExpectTimeoutMs);
        Assert.Equal(ExpectTimeoutAction.Continue, vm.Entries[1].ExpectOnTimeout);
    }

    [Fact]
    public void AddExpectStep_AddsPureExpectEntry()
    {
        var vm = new MacroEditorDialogViewModel(CreateMacro());

        vm.AddExpectStepCommand.Execute(null);

        var entry = vm.Entries[^1];
        Assert.Equal(string.Empty, entry.InputText);
        Assert.Equal(0, entry.DelayMs);
        Assert.Equal(string.Empty, entry.ExpectPattern);
        Assert.Equal(MacroEntry.DefaultExpectTimeoutMs, entry.ExpectTimeoutMs);
    }

    [Fact]
    public void ExpectPattern_EmptyAndNonEmpty_TogglesExpectControls()
    {
        var entry = new MacroEditorDialogViewModel(CreateMacro()).Entries[0];

        Assert.False(entry.HasExpectPattern);
        Assert.False(entry.ExpectControlsEnabled);

        entry.ExpectPattern = "READY";

        Assert.True(entry.HasExpectPattern);
        Assert.True(entry.ExpectControlsEnabled);

        entry.ExpectPattern = string.Empty;

        Assert.False(entry.HasExpectPattern);
        Assert.False(entry.ExpectControlsEnabled);
    }

    [Fact]
    public void RegexPattern_Invalid_IsFlagged()
    {
        var entry = new MacroEditorDialogViewModel(CreateMacro()).Entries[0];

        entry.ExpectPattern = "[";
        entry.ExpectIsRegex = true;

        Assert.NotNull(entry.RegexValidationMessage);
        Assert.Contains("Invalid regex", entry.RegexValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectTimeoutMs_SetterClampsBounds()
    {
        var entry = new MacroEditorDialogViewModel(CreateMacro()).Entries[0];

        entry.ExpectTimeoutMs = -1;
        Assert.Equal(MacroEntry.MinExpectTimeoutMs, entry.ExpectTimeoutMs);

        entry.ExpectTimeoutMs = int.MaxValue;
        Assert.Equal(MacroEntry.MaxExpectTimeoutMs, entry.ExpectTimeoutMs);
    }

    [Fact]
    public void TrySave_ProducesExpectedMacroAndSerializes()
    {
        var vm = new MacroEditorDialogViewModel(CreateMacro());
        vm.MacroName = "Edited";
        vm.Entries[0].InputText = @"echo DONE\n";
        vm.Entries[0].ExpectPattern = "READY";
        vm.Entries[0].ExpectTimeoutMs = 2_000;
        vm.Entries[0].ExpectOnTimeout = ExpectTimeoutAction.Abort;

        var saved = vm.TrySave();

        Assert.True(saved);
        Assert.NotNull(vm.Result);
        Assert.Equal(MacroEditorDialogAction.Save, vm.Result.Action);
        var macro = Assert.IsType<TerminalMacro>(vm.Result.Macro);
        Assert.Equal("macro-a", macro.Id);
        Assert.Equal("Edited", macro.Name);
        var first = macro.Entries[0];
        Assert.Equal("echo DONE\n", first.Input);
        Assert.Equal("READY", first.ExpectPattern);
        Assert.Equal(2_000, first.ExpectTimeoutMs);
        Assert.False(first.ExpectIsRegex);
        Assert.Equal(ExpectTimeoutAction.Abort, first.ExpectOnTimeout);

        var json = JsonSerializer.Serialize(macro, WriteOptions);
        var roundTripped = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal("READY", roundTripped.Entries[0].ExpectPattern);
    }

    [Fact]
    public void DeleteAndReorder_AreReflectedInSavedMacro()
    {
        var vm = new MacroEditorDialogViewModel(CreateMacro());

        vm.MoveDownCommand.Execute(vm.Entries[0]);
        vm.DeleteEntryCommand.Execute(vm.Entries[0]);

        Assert.True(vm.TrySave());
        var macro = Assert.IsType<TerminalMacro>(vm.Result?.Macro);
        var entry = Assert.Single(macro.Entries);
        Assert.Equal("echo READY\n", entry.Input);
    }

    [Fact]
    public void Cancel_DiscardedByKeepingSourceMacroUnchanged()
    {
        var macro = CreateMacro();
        var vm = new MacroEditorDialogViewModel(macro);

        vm.MacroName = "Dirty";
        vm.Entries[0].InputText = "changed";

        Assert.Equal("Macro A", macro.Name);
        Assert.Equal("echo READY\n", macro.Entries[0].Input);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void LegacyMacro_LoadsEditsAndSavesWithoutCorruptingEntries()
    {
        const string json = """
            {
              "id": "legacy",
              "name": "Legacy",
              "entries": [
                {
                  "input": "pwd",
                  "delayMs": 5
                }
              ]
            }
            """;
        var macro = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);
        Assert.NotNull(macro);
        var vm = new MacroEditorDialogViewModel(macro);

        vm.MacroName = "Legacy edited";
        vm.Entries[0].DelayMs = 10;

        Assert.True(vm.TrySave());
        var saved = Assert.IsType<TerminalMacro>(vm.Result?.Macro);
        var entry = Assert.Single(saved.Entries);
        Assert.Equal("pwd", entry.Input);
        Assert.Equal(10, entry.DelayMs);
        Assert.Null(entry.ExpectPattern);
        Assert.Null(entry.ExpectTimeoutMs);
        Assert.False(entry.ExpectIsRegex);
        Assert.Equal(ExpectTimeoutAction.Abort, entry.ExpectOnTimeout);
    }

    [Fact]
    public void MacroInputEscaper_RendersAndParsesVisibleEscapes()
    {
        const string input = "line1\r\n\t\u001B\\";

        var encoded = MacroInputEscaper.Encode(input);
        var parsed = MacroInputEscaper.TryDecode(encoded, out var decoded, out var error);

        Assert.Equal(@"line1\r\n\t\x1B\\", encoded);
        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(input, decoded);
    }

    private static TerminalMacro CreateMacro()
    {
        return new TerminalMacro
        {
            Id = "macro-a",
            Name = "Macro A",
            Description = "desc",
            CreatedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Entries =
            [
                new MacroEntry
                {
                    Input = "echo READY\n",
                    DelayMs = 25
                },
                new MacroEntry
                {
                    Input = "echo DONE\n",
                    DelayMs = 50,
                    ExpectPattern = "READY",
                    ExpectTimeoutMs = 1_500,
                    ExpectIsRegex = true,
                    ExpectOnTimeout = ExpectTimeoutAction.Continue
                }
            ]
        };
    }
}
