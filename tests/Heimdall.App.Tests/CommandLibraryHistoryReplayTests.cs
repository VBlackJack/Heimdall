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
/// Pins putting a history row back into the generator.
/// </summary>
/// <remarks>
/// The library can have moved on since a row was written, so each of the three ways it can
/// have moved is covered: the action deleted, its parameters renamed, and its templates
/// offering two platforms. A replay that silently produced a different command from the
/// one the row shows would be worse than no replay at all.
/// </remarks>
public sealed class CommandLibraryHistoryReplayTests
{
    [Fact]
    public async Task ReplayingSelectsTheActionAndRestoresTheValues()
    {
        var viewModel = await CreateViewModelAsync(
            CommandLibraryTestHelpers.CreateLinuxAction(
                "tail", "Tail a log", "tail -f {path}",
                CommandLibraryTestHelpers.RequiredParameter("path", "Path")));
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row("tail", Platform.Linux, ("path", "/var/log/syslog")));

        Assert.True(viewModel.IsGeneratorVisible);
        Assert.Equal("tail", viewModel.SelectedAction?.Id);
        Assert.Equal("/var/log/syslog", Parameter(viewModel, "path").Value);
        Assert.Contains("/var/log/syslog", viewModel.GeneratedCommand, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generated command is the thing the user acts on, so replay has to leave it
    /// showing what will actually run, not just fill the boxes.
    /// </summary>
    [Fact]
    public async Task ReplayingLeavesTheCommandReadyToSend()
    {
        var viewModel = await CreateViewModelAsync(
            CommandLibraryTestHelpers.CreateLinuxAction(
                "tail", "Tail a log", "tail -f {path}",
                CommandLibraryTestHelpers.RequiredParameter("path", "Path")));
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row("tail", Platform.Linux, ("path", "/var/log/syslog")));

        Assert.True(viewModel.IsCommandValid);
        Assert.Empty(viewModel.ValidationError);
    }

    [Fact]
    public async Task ReplayingClosesTheHistoryPanel()
    {
        var viewModel = await CreateViewModelAsync(
            CommandLibraryTestHelpers.CreateLinuxAction("tail", "Tail a log", "tail -f /tmp/a"));
        using var _ = viewModel;
        viewModel.IsHistoryVisible = true;

        viewModel.ReplayHistoryEntry(Row("tail", Platform.Linux));

        Assert.False(viewModel.IsHistoryVisible);
    }

    /// <summary>
    /// An action can have been deleted since the row was written. Saying so beats doing
    /// nothing, which reads as a broken button.
    /// </summary>
    [Fact]
    public async Task ReplayingADeletedActionSaysSo()
    {
        var dialog = new SilentDialogService();
        var viewModel = await CreateViewModelAsync(
            dialog, CommandLibraryTestHelpers.CreateLinuxAction("tail", "Tail a log", "tail -f /tmp/a"));
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row("deleted-action", Platform.Linux));

        Assert.False(viewModel.IsGeneratorVisible);
        Assert.Contains(dialog.Shown, shown => shown.StartsWith("warning:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A row that cannot be opened has no values to restore, and one written before the
    /// history recorded an action id has nothing to select.
    /// </summary>
    [Theory]
    [InlineData(false, "tail")]
    [InlineData(true, "")]
    public async Task ARowThatCannotBeReplayedIsLeftAlone(bool readable, string actionId)
    {
        var dialog = new SilentDialogService();
        var viewModel = await CreateViewModelAsync(
            dialog, CommandLibraryTestHelpers.CreateLinuxAction("tail", "Tail a log", "tail -f /tmp/a"));
        using var _ = viewModel;

        var row = Row(actionId, Platform.Linux);
        viewModel.ReplayHistoryEntry(new CommandLibraryHistoryEntry
        {
            ActionId = row.ActionId,
            Platform = row.Platform,
            Parameters = row.Parameters,
            IsReadable = readable
        });

        Assert.False(viewModel.IsGeneratorVisible);
        Assert.Empty(dialog.Shown);
    }

    /// <summary>
    /// A value whose parameter was renamed away has nowhere to go, and must not stop the
    /// rest of the row from being restored.
    /// </summary>
    [Fact]
    public async Task AValueWhoseParameterIsGoneIsDropped()
    {
        var viewModel = await CreateViewModelAsync(
            CommandLibraryTestHelpers.CreateLinuxAction(
                "tail", "Tail a log", "tail -n {lines} {path}",
                CommandLibraryTestHelpers.RequiredParameter("path", "Path"),
                CommandLibraryTestHelpers.OptionalParameter("lines", "Lines", "10")));
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row(
            "tail", Platform.Linux,
            ("path", "/var/log/syslog"),
            ("removed", "whatever it used to be")));

        Assert.Equal("/var/log/syslog", Parameter(viewModel, "path").Value);
        Assert.DoesNotContain(
            viewModel.Parameters,
            parameter => string.Equals(parameter.Name, "removed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A parameter the row does not mention keeps its template default rather than being
    /// blanked: the row says what was set, not what was left alone.
    /// </summary>
    [Fact]
    public async Task AParameterTheRowDoesNotMentionKeepsItsDefault()
    {
        var viewModel = await CreateViewModelAsync(
            CommandLibraryTestHelpers.CreateLinuxAction(
                "tail", "Tail a log", "tail -n {lines} {path}",
                CommandLibraryTestHelpers.RequiredParameter("path", "Path"),
                CommandLibraryTestHelpers.OptionalParameter("lines", "Lines", "10")));
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row("tail", Platform.Linux, ("path", "/var/log/syslog")));

        Assert.Equal("10", Parameter(viewModel, "lines").Value);
    }

    /// <summary>
    /// An action carrying both templates must be replayed on the platform it ran on, or
    /// the same history line quietly yields a different command.
    /// </summary>
    [Theory]
    [InlineData(Platform.Windows, true)]
    [InlineData(Platform.Linux, false)]
    public async Task ReplayingPicksTheTemplateOfThePlatformItRanOn(
        Platform recorded, bool expectsWindows)
    {
        var viewModel = await CreateViewModelAsync(DualPlatformAction());
        using var _ = viewModel;

        viewModel.ReplayHistoryEntry(Row("dual", recorded, ("path", "/tmp/a")));

        Assert.True(viewModel.HasMultipleTemplates);
        Assert.Equal(expectsWindows, viewModel.UseWindowsTemplate);
    }

    private static ActionModel DualPlatformAction()
    {
        var parameter = CommandLibraryTestHelpers.RequiredParameter("path", "Path");
        return new ActionModel
        {
            Id = "dual",
            Title = "Show a file",
            Category = "Ops",
            Platform = Platform.Both,
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = "dual-win",
                Name = "Windows",
                Platform = Platform.Windows,
                CommandPattern = "Get-Content {path}",
                Parameters = [parameter]
            },
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = "dual-linux",
                Name = "Linux",
                Platform = Platform.Linux,
                CommandPattern = "cat {path}",
                Parameters = [parameter]
            }
        };
    }

    private static CommandLibraryHistoryEntry Row(
        string actionId, Platform platform, params (string Name, string Value)[] values)
        => new()
        {
            ActionId = actionId,
            Platform = platform,
            IsReadable = true,
            Parameters = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal)
        };

    private static CommandLibraryParameterEntry Parameter(
        CommandLibraryViewModel viewModel, string name)
        => viewModel.Parameters.Single(
            parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));

    private static Task<CommandLibraryViewModel> CreateViewModelAsync(params ActionModel[] actions)
        => CreateViewModelAsync(new SilentDialogService(), actions);

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(
        SilentDialogService dialog, params ActionModel[] actions)
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
            dialog,
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }
}
