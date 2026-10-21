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
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what a declared far-end operating system changes: the library's platform filter, the
/// template a post-connect step expands to, the template the post-connect picker previews, and
/// the session facts a library docked beside a terminal receives.
/// </summary>
public sealed class RemoteOsSessionContextTests
{
    private const string WindowsPattern = "Get-Service";
    private const string LinuxPattern = "systemctl list-units";

    [Fact]
    public async Task ExplicitWindows_OverSsh_SelectsTheWindowsFilter()
    {
        using var viewModel = await CreateLibraryAsync();

        viewModel.AutoSelectPlatform("SSH", RemoteOs.Windows);

        Assert.Equal(CommandLibraryViewModel.PlatformFilterWindows, viewModel.PlatformFilterIndex);
    }

    [Fact]
    public async Task Auto_OverSsh_StillSelectsTheLinuxFilter()
    {
        using var viewModel = await CreateLibraryAsync();

        viewModel.AutoSelectPlatform("SSH", RemoteOs.Auto);

        Assert.Equal(CommandLibraryViewModel.PlatformFilterLinux, viewModel.PlatformFilterIndex);
    }

    [Fact]
    public async Task ExplicitWindows_PostConnectStepExpandsTheWindowsTemplate()
    {
        var resolver = CreateStepResolver(DualAction());

        var result = await new RemoteOsBoundStepResolver(resolver, RemoteOs.Windows)
            .ResolveAsync(new PostConnectStep { CommandLibraryId = "dual" }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.Resolved, result.Status);
        Assert.Equal(WindowsPattern, result.ResolvedInput);
    }

    // The counterweight: an undeclared far end keeps the Linux preference of the resolver.
    [Fact]
    public async Task Auto_PostConnectStepStillExpandsTheLinuxTemplate()
    {
        var resolver = CreateStepResolver(DualAction());

        var result = await new RemoteOsBoundStepResolver(resolver, RemoteOs.Auto)
            .ResolveAsync(new PostConnectStep { CommandLibraryId = "dual" }, CancellationToken.None);

        Assert.Equal(LinuxPattern, result.ResolvedInput);
    }

    [Fact]
    public async Task ExplicitWindows_ThePickerPreviewsTheWindowsTemplate()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(DualAction());
        var picker = new CommandLibraryPickerDialogViewModel(localizer, provider.GetRequiredService<IServiceScopeFactory>())
        {
            RemoteOs = RemoteOs.Windows
        };
        await picker.InitializeAsync();

        picker.SelectedAction = picker.Actions.Single();

        Assert.True(picker.CanConfirm);
        Assert.Equal(WindowsPattern, picker.GeneratedCommand);
    }

    [Fact]
    public void ASessionProfile_FillsTheFactsTheContextLacks()
    {
        var profile = new ServerProfileDto
        {
            RemoteServer = " srv-01 ",
            ConnectionType = "SSH",
            SshUsername = "alice",
            RemoteOs = RemoteOs.Windows
        };

        var context = EmbeddedSessionManager.WithSessionProfile(new ToolContext(), profile)!;

        Assert.Equal("srv-01", context.TargetHost);
        Assert.Equal("alice", context.Username);
        Assert.Equal("SSH", context.ConnectionType);
        Assert.Equal(RemoteOs.Windows, context.RemoteOs);
    }

    [Fact]
    public void WhatTheCallerPutInTheContext_WinsOverTheSessionProfile()
    {
        var profile = new ServerProfileDto
        {
            RemoteServer = "srv-01",
            ConnectionType = "SSH",
            RemoteOs = RemoteOs.Windows
        };

        var context = EmbeddedSessionManager.WithSessionProfile(
            new ToolContext(TargetHost: "other", RemoteOs: RemoteOs.Linux),
            profile)!;

        Assert.Equal("other", context.TargetHost);
        Assert.Equal(RemoteOs.Linux, context.RemoteOs);
    }

    private static ActionModel DualAction() => new()
    {
        Id = "dual",
        Title = "List services",
        Category = "Ops",
        Platform = Platform.Both,
        WindowsCommandTemplate = new CommandTemplate
        {
            Id = "dual-win",
            Name = "Windows",
            Platform = Platform.Windows,
            CommandPattern = WindowsPattern
        },
        LinuxCommandTemplate = new CommandTemplate
        {
            Id = "dual-linux",
            Name = "Linux",
            Platform = Platform.Linux,
            CommandPattern = LinuxPattern
        }
    };

    private static CommandLibraryStepResolver CreateStepResolver(params ActionModel[] actions)
        => new(CommandLibraryTestHelpers.CreateResolverServiceProvider(actions)
            .GetRequiredService<IServiceScopeFactory>());

    private static async Task<CommandLibraryViewModel> CreateLibraryAsync()
        => new(
            CommandLibraryTestHelpers.CreateResolverServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);
}
