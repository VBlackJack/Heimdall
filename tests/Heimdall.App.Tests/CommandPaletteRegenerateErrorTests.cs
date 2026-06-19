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
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

public sealed class CommandPaletteRegenerateErrorTests
{
    [Fact]
    public async Task RegenerateSnippetCommand_GenerationFailureSurfacesLocalizedErrorAndSuccessClearsIt()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var generator = new CommandGeneratorService(new FakeTwinShellLocalizationService());
        var viewModel = new CommandPaletteViewModel(
            null!,
            localizer,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var failingTemplate = new CommandTemplate
        {
            Name = "Too long",
            Platform = Platform.Windows,
            CommandPattern = new string('x', ValidationConstants.MaxCommandLength + 1)
        };

        SetPrivateField(viewModel, "_snippetTemplate", failingTemplate);
        SetPrivateField<ICommandGeneratorService?>(viewModel, "_snippetGenerator", generator);

        InvokeRegenerateSnippetCommand(viewModel);

        var expectedError = string.Format(
            localizer["ToolCmdLibGenerateError"],
            $"Generated command exceeds maximum length of {ValidationConstants.MaxCommandLength} characters (was {ValidationConstants.MaxCommandLength + 1})");
        viewModel.SnippetGeneratedCommand.Should().Be(failingTemplate.CommandPattern);
        viewModel.SnippetValidationError.Should().Be(expectedError);
        viewModel.IsSnippetCommandValid.Should().BeFalse();

        var validTemplate = new CommandTemplate
        {
            Name = "List processes",
            Platform = Platform.Windows,
            CommandPattern = "Get-Process"
        };

        SetPrivateField(viewModel, "_snippetTemplate", validTemplate);

        InvokeRegenerateSnippetCommand(viewModel);

        viewModel.SnippetGeneratedCommand.Should().Be("Get-Process");
        viewModel.SnippetValidationError.Should().BeEmpty();
        viewModel.IsSnippetCommandValid.Should().BeTrue();
    }

    private static void SetPrivateField<T>(CommandPaletteViewModel viewModel, string fieldName, T value)
    {
        var field = typeof(CommandPaletteViewModel).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(viewModel, value);
    }

    private static void InvokeRegenerateSnippetCommand(CommandPaletteViewModel viewModel)
    {
        var method = typeof(CommandPaletteViewModel).GetMethod(
            "RegenerateSnippetCommand",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(viewModel, null);
    }
}
