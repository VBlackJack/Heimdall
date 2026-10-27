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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins when the generator shows its validation message.
/// </summary>
/// <remarks>
/// Selecting an action whose required parameter starts empty showed "Key is required." in red
/// before the operator had typed anything: an error for a field nobody had touched yet. The
/// command stays invalid (Copy and Send stay disabled, and Copy's tooltip says why); only the
/// message waits for the first edit. Any other error, such as a shipped default that fails its
/// own type, still shows at once, because waiting would hide a fault the operator did not cause.
/// </remarks>
public sealed class CommandLibraryValidationDisplayTests
{
    [Fact]
    public async Task AnUntouchedRequiredParameterKeepsTheCommandInvalidButSaysNothingYet()
    {
        var viewModel = await CreateViewModelAsync(RequiredPathAction(defaultValue: null));
        using var _ = viewModel;

        Select(viewModel);

        Assert.False(viewModel.IsCommandValid);
        Assert.False(viewModel.HasValidationError);
    }

    [Fact]
    public async Task ARequiredParameterEmptiedAfterAnEditSaysSo()
    {
        var viewModel = await CreateViewModelAsync(RequiredPathAction(defaultValue: null));
        using var _ = viewModel;
        Select(viewModel);
        var path = Assert.Single(viewModel.Parameters);

        path.Value = "/var/log/syslog";
        path.Value = string.Empty;

        Assert.False(viewModel.IsCommandValid);
        Assert.True(viewModel.HasValidationError);
    }

    /// <summary>
    /// Positive control for the first test: an error that is not a missing value shows before any
    /// edit, so the rule hides only "required", not everything.
    /// </summary>
    [Fact]
    public async Task AWrongDefaultShowsAtOnce()
    {
        var viewModel = await CreateViewModelAsync(IntegerAction(defaultValue: "not-a-number"));
        using var _ = viewModel;

        Select(viewModel);

        Assert.False(viewModel.IsCommandValid);
        Assert.True(viewModel.HasValidationError);
    }

    private static void Select(CommandLibraryViewModel viewModel)
        => viewModel.SelectedEntry = Assert.Single(viewModel.ActionsView!.Cast<CommandLibraryActionEntry>());

    private static ActionModel RequiredPathAction(string? defaultValue)
        => Action("tail -f {path}", new TemplateParameter
        {
            Name = "path",
            Label = "Path",
            Type = "string",
            Required = true,
            DefaultValue = defaultValue
        });

    private static ActionModel IntegerAction(string defaultValue)
        => Action("head -n {count} /tmp/a", new TemplateParameter
        {
            Name = "count",
            Label = "Count",
            Type = "int",
            Required = true,
            DefaultValue = defaultValue
        });

    private static ActionModel Action(string pattern, TemplateParameter parameter)
        => new()
        {
            Id = "a",
            Title = "An action",
            Category = "Ops",
            Platform = Platform.Linux,
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = "a-linux",
                Name = "a",
                Platform = Platform.Linux,
                CommandPattern = pattern,
                Parameters = [parameter]
            }
        };

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }
}
