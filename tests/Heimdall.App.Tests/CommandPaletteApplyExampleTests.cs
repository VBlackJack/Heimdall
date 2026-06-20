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

using System.Reflection;
using FluentAssertions;
using Heimdall.App.ViewModels.CommandPalette;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Locks the Command Palette snippet-detail "apply example" contract, mirroring the
/// Command Library examples list: clicking an example row loads it into the read-only
/// preview, marks the command valid, and flags it as example-sourced; that flag is
/// cleared when the command is regenerated from parameters or the variant changes.
/// </summary>
public sealed class CommandPaletteApplyExampleTests
{
    [Fact]
    public async Task ApplySnippetExample_DistinctNonFirstValues_UpdatePreviewAndMarkValid()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.ApplySnippetExampleCommand.Execute("netdom query fsmo -WhatIf");
        viewModel.SnippetGeneratedCommand.Should().Be("netdom query fsmo -WhatIf");
        viewModel.IsSnippetCommandValid.Should().BeTrue();
        viewModel.SnippetGeneratedFromExample.Should().BeTrue();

        viewModel.ApplySnippetExampleCommand.Execute("netdom query fsmo -Confirm:$false");
        viewModel.SnippetGeneratedCommand.Should().Be("netdom query fsmo -Confirm:$false");
        viewModel.IsSnippetCommandValid.Should().BeTrue();
        viewModel.SnippetGeneratedFromExample.Should().BeTrue();
    }

    [Fact]
    public async Task ApplySnippetExample_NullOrEmpty_IsNoOp()
    {
        var viewModel = await CreateViewModelAsync();
        viewModel.ApplySnippetExampleCommand.Execute("netdom query fsmo");
        var before = viewModel.SnippetGeneratedCommand;

        viewModel.ApplySnippetExampleCommand.Execute(null);
        viewModel.SnippetGeneratedCommand.Should().Be(before);

        viewModel.ApplySnippetExampleCommand.Execute(string.Empty);
        viewModel.SnippetGeneratedCommand.Should().Be(before);
    }

    [Fact]
    public async Task RegenerateSnippetCommand_ClearsFromExampleFlag()
    {
        var viewModel = await CreateViewModelAsync();
        viewModel.ApplySnippetExampleCommand.Execute("literal example");
        viewModel.SnippetGeneratedFromExample.Should().BeTrue();

        var template = new CommandTemplate
        {
            Name = "List processes",
            Platform = Platform.Windows,
            CommandPattern = "Get-Process"
        };
        SetPrivateField(viewModel, "_snippetTemplate", template);
        SetPrivateField<ICommandGeneratorService?>(
            viewModel, "_snippetGenerator", new CommandGeneratorService(new FakeTwinShellLocalizationService()));

        InvokePrivate(viewModel, "RegenerateSnippetCommand");

        viewModel.SnippetGeneratedFromExample.Should().BeFalse();
        viewModel.SnippetGeneratedCommand.Should().Be("Get-Process");
    }

    [Fact]
    public async Task VariantSelectionChange_ClearsFromExampleFlag()
    {
        var viewModel = await CreateViewModelAsync();
        viewModel.ApplySnippetExampleCommand.Execute("literal example");
        viewModel.SnippetGeneratedFromExample.Should().BeTrue();

        // A variant selection change produces a fresh command; the null-selection
        // path exercises the reset without touching session-dependent collaborators.
        InvokeOnSelectedSnippetVariantChanged(viewModel, null);

        viewModel.SnippetGeneratedFromExample.Should().BeFalse();
    }

    private static async Task<CommandPaletteViewModel> CreateViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        return new CommandPaletteViewModel(
            null!, localizer, null!, null!, null!, null!, null!, null!, null!);
    }

    private static void SetPrivateField<T>(CommandPaletteViewModel viewModel, string fieldName, T value)
    {
        var field = typeof(CommandPaletteViewModel).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(viewModel, value);
    }

    private static void InvokePrivate(CommandPaletteViewModel viewModel, string methodName)
    {
        var method = typeof(CommandPaletteViewModel).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(viewModel, null);
    }

    private static void InvokeOnSelectedSnippetVariantChanged(CommandPaletteViewModel viewModel, object? value)
    {
        var method = typeof(CommandPaletteViewModel).GetMethod(
            "OnSelectedSnippetVariantChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(viewModel, new[] { value });
    }
}
