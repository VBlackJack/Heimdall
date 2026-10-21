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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// A command selected in a terminal becomes a library action in one gesture: the session manager
/// opens the library with the selection and the session's facts, and the library opens its
/// editor prefilled with it.
/// </summary>
public sealed class CommandLibraryCaptureTests
{
    private const string ProfileId = "srv-01-id";

    // ── Session manager ──────────────────────────────────────────

    [Fact]
    public async Task ACaptureOpensTheLibraryWithTheTextAndTheSessionFacts()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var manager = CreateManager(localizer);
        manager.ProfileResolver = id => id == ProfileId
            ? new ServerProfileDto
            {
                RemoteServer = "srv-01",
                ConnectionType = "SSH",
                SshUsername = "alice",
                RemoteOs = RemoteOs.Windows,
            }
            : null;

        string? openedToolId = null;
        string? openedTitle = null;
        ToolContext? openedContext = null;
        manager.OpenToolCallback = (toolId, title, context) =>
        {
            openedToolId = toolId;
            openedTitle = title;
            openedContext = context;
            return Task.CompletedTask;
        };

        await manager.OpenLibraryWithCaptureAsync(SshTab(), pane: null, "Get-Service\r\nGet-Process\r\n");

        Assert.Equal(ToolRegistry.CommandLibraryToolId, openedToolId);
        Assert.Equal(localizer["PaletteToolCmdLib"], openedTitle);
        Assert.NotNull(openedContext);
        Assert.Equal("Get-Service ; Get-Process", openedContext!.InitialCommandText);
        Assert.Equal("srv-01", openedContext.TargetHost);
        Assert.Equal("alice", openedContext.Username);
        Assert.Equal(RemoteOs.Windows, openedContext.RemoteOs);
        Assert.Equal("SSH", openedContext.ConnectionType);
    }

    [Fact]
    public async Task ACaptureWithoutAProfileIsJoinedForTheTabProtocol()
    {
        var manager = CreateManager(await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        ToolContext? openedContext = null;
        manager.OpenToolCallback = (_, _, context) =>
        {
            openedContext = context;
            return Task.CompletedTask;
        };

        await manager.OpenLibraryWithCaptureAsync(SshTab(), pane: null, "cd /tmp\nls");

        Assert.Equal("cd /tmp && ls", openedContext?.InitialCommandText);
        Assert.Equal("SSH", openedContext?.ConnectionType);
    }

    [Fact]
    public async Task ABlankCaptureOpensNothing()
    {
        var manager = CreateManager(await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        int opened = 0;
        manager.OpenToolCallback = (_, _, _) =>
        {
            opened++;
            return Task.CompletedTask;
        };

        await manager.OpenLibraryWithCaptureAsync(SshTab(), pane: null, " \r\n ");

        Assert.Equal(0, opened);
    }

    // ── Library ──────────────────────────────────────────────────

    [Fact]
    public async Task ALinuxCaptureOpensTheEditorPrefilledOnLinux()
    {
        using var viewModel = CreateViewModel(new FakeActionService([ExistingAction()]), await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        CommandActionDialogViewModel? shown = null;
        viewModel.ShowActionDialogAsync = vm =>
        {
            shown = vm;
            return Task.FromResult(false);
        };

        await viewModel.InitializeAsync(
            "srv-01",
            capturedCommand: new CapturedCommand("journalctl -u nginx", RemoteOs.Linux, "ssh"));

        Assert.NotNull(shown);
        Assert.Equal(Platform.Linux, shown!.Platform);
        Assert.Equal("journalctl -u nginx", shown.LinuxPattern);
        Assert.Equal(string.Empty, shown.WindowsPattern);
        Assert.Equal(string.Empty, shown.Title);
        Assert.Equal(string.Empty, shown.Category);
        Assert.Equal(CriticalityLevel.Run, shown.Level);
        Assert.Equal("ssh", shown.Tags);
        Assert.False(shown.IsEditMode);
    }

    [Fact]
    public async Task AWindowsCaptureOpensTheEditorPrefilledOnWindows()
    {
        using var viewModel = CreateViewModel(new FakeActionService([ExistingAction()]), await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        CommandActionDialogViewModel? shown = null;
        viewModel.ShowActionDialogAsync = vm =>
        {
            shown = vm;
            return Task.FromResult(false);
        };

        await viewModel.InitializeAsync(
            null,
            capturedCommand: new CapturedCommand("Get-Service ; Get-Process", RemoteOs.Windows, "winrm"));

        Assert.NotNull(shown);
        Assert.Equal(Platform.Windows, shown!.Platform);
        Assert.Equal("Get-Service ; Get-Process", shown.WindowsPattern);
        Assert.Equal(string.Empty, shown.LinuxPattern);
        Assert.Equal("winrm", shown.Tags);
    }

    [Fact]
    public async Task CancellingTheEditorLeavesTheLibraryAsItWas()
    {
        var actions = new FakeActionService([ExistingAction()]);
        using var viewModel = CreateViewModel(actions, await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        int shownCount = 0;
        viewModel.ShowActionDialogAsync = _ =>
        {
            shownCount++;
            return Task.FromResult(false);
        };

        await viewModel.InitializeAsync(null, capturedCommand: new CapturedCommand("uptime", RemoteOs.Linux, "ssh"));

        Assert.Equal(1, shownCount);
        Assert.Equal("existing", Assert.Single(viewModel.AllEntries).Source.Id);
        Assert.Single(await actions.GetAllActionsAsync());
        Assert.True(viewModel.IsReady);
    }

    [Fact]
    public async Task SavingACaptureSelectsTheNewActionAndItsCategoryIsTheNextCapturesCategory()
    {
        var actions = new FakeActionService([ExistingAction()]);
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var memory = new CommandLibrarySessionMemory();
        using var first = CreateViewModel(actions, localizer, memory);
        first.ShowActionDialogAsync = vm =>
        {
            vm.Title = "Nginx journal";
            vm.Category = "Logs";
            return Task.FromResult(true);
        };

        await first.InitializeAsync(null, capturedCommand: new CapturedCommand("journalctl -u nginx", RemoteOs.Linux, "ssh"));

        Assert.Equal(2, first.AllEntries.Count);
        Assert.NotNull(first.SelectedEntry);
        Assert.Equal("Nginx journal", first.SelectedEntry!.Source.Title);
        Assert.Equal("journalctl -u nginx", first.SelectedEntry.Source.LinuxCommandTemplate?.CommandPattern);

        // Another library tab of the same session starts the next capture in that category.
        using var second = CreateViewModel(actions, localizer, memory);
        CommandActionDialogViewModel? shown = null;
        second.ShowActionDialogAsync = vm =>
        {
            shown = vm;
            return Task.FromResult(false);
        };

        await second.InitializeAsync(null, capturedCommand: new CapturedCommand("nginx -t", RemoteOs.Linux, "ssh"));

        Assert.Equal("Logs", shown?.Category);
    }

    [Fact]
    public async Task WithoutACaptureTheLibraryOpensNoEditor()
    {
        using var viewModel = CreateViewModel(new FakeActionService([ExistingAction()]), await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        int shownCount = 0;
        viewModel.ShowActionDialogAsync = _ =>
        {
            shownCount++;
            return Task.FromResult(false);
        };

        await viewModel.InitializeAsync("srv-01");

        Assert.Equal(0, shownCount);
    }

    [Fact]
    public void AContextWithTextBecomesACaptureForTheResolvedPlatform()
    {
        CapturedCommand? capture = CommandCapture.FromContext(
            new ToolContext(ConnectionType: "SSH", RemoteOs: RemoteOs.Auto, InitialCommandText: " df -h "));

        Assert.Equal(new CapturedCommand("df -h", RemoteOs.Linux, "ssh"), capture);
        Assert.Null(CommandCapture.FromContext(new ToolContext(ConnectionType: "SSH")));
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static SessionTabViewModel SshTab()
    {
        var pane = new SessionPaneModel
        {
            PaneId = "pane-1",
            ServerId = "session-1",
            OriginalServerId = ProfileId,
            Title = "srv-01",
            ConnectionType = "SSH",
        };
        return new SessionTabViewModel { RootContent = pane };
    }

    private static EmbeddedSessionManager CreateManager(LocalizationManager localizer)
        => new(
            localizer,
            null!,
            null!,
            null!,
            new ToolRegistry(),
            null!,
            null!,
            null!,
            null!,
            null!);

    private static ActionModel ExistingAction()
        => CommandLibraryTestHelpers.CreateLinuxAction("existing", "Disk usage", "df -h");

    private static CommandLibraryViewModel CreateViewModel(
        FakeActionService actions,
        LocalizationManager localizer,
        CommandLibrarySessionMemory? memory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => actions);
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        if (memory is not null)
        {
            services.AddSingleton(memory);
        }

        return new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            localizer,
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);
    }
}
