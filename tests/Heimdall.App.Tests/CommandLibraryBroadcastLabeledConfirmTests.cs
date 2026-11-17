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
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The broadcast question names its answers after the acts, declines by default, and shows the
/// whole command with its hidden characters made visible while sending it untouched.
/// </summary>
public sealed class CommandLibraryBroadcastLabeledConfirmTests
{
    private static readonly char RightToLeftOverride = (char)0x202E;

    private static readonly string DisguisedCommand = "echo safe" + RightToLeftOverride + "; id";

    [Fact]
    public async Task TheQuestionUsesTheLabeledOverloadWithTheSendLabelAndADeclineLabel()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = true };
        var broadcaster = CommandLibraryBroadcastHarness.Broadcaster(("t1", "web-01", true));
        using CommandLibraryViewModel viewModel = await CreateAsync(broadcaster, dialog);
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        viewModel.GeneratedCommand = "uptime";
        viewModel.IsCommandValid = true;

        await viewModel.BroadcastAsync();

        Assert.Equal(0, dialog.PlainCallCount);
        LabeledConfirmCall call = Assert.Single(dialog.LabeledCalls);
        Assert.Equal("warning", call.Severity);
        Assert.Equal(localizer.Format("ToolCmdLibBroadcastSendToCount", 1), call.ConfirmLabel);
        Assert.Equal(localizer["ToolCmdLibBroadcastConfirmDecline"], call.DeclineLabel);
        Assert.NotEqual("ToolCmdLibBroadcastConfirmDecline", call.DeclineLabel);
        Assert.Single(broadcaster.Sent);
    }

    [Fact]
    public async Task ADeclineSendsNothing()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = false };
        var broadcaster = CommandLibraryBroadcastHarness.Broadcaster(("t1", "web-01", true));
        using CommandLibraryViewModel viewModel = await CreateAsync(broadcaster, dialog);
        viewModel.GeneratedCommand = "uptime";
        viewModel.IsCommandValid = true;

        await viewModel.BroadcastAsync();

        Assert.Single(dialog.LabeledCalls);
        Assert.Empty(broadcaster.Sent);
    }

    [Fact]
    public async Task ABidiOverrideInTheCommandIsShownEscapedAndSentUnchanged()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = true };
        var broadcaster = CommandLibraryBroadcastHarness.Broadcaster(("t1", "web-01", true));
        using CommandLibraryViewModel viewModel = await CreateAsync(broadcaster, dialog);
        viewModel.GeneratedCommand = DisguisedCommand;
        viewModel.IsCommandValid = true;

        await viewModel.BroadcastAsync();

        string shown = Assert.Single(dialog.LabeledCalls).Message;
        Assert.DoesNotContain(RightToLeftOverride, shown);
        Assert.Contains(
            UntrustedText.MakeVisible(DisguisedCommand, UntrustedText.NoLengthLimit),
            shown,
            StringComparison.Ordinal);
        Assert.Equal(DisguisedCommand, Assert.Single(broadcaster.Sent).Command);
    }

    [Fact]
    public async Task WhatIsSentIsWhatWasShown_EvenIfTheCommandChangesWhileTheQuestionIsOpen()
    {
        FiveArgConfirmDialogService dialog = new() { Answer = true };
        var broadcaster = CommandLibraryBroadcastHarness.Broadcaster(("t1", "web-01", true));
        using CommandLibraryViewModel viewModel = await CreateAsync(broadcaster, dialog);
        viewModel.GeneratedCommand = "uptime";
        viewModel.IsCommandValid = true;
        dialog.WhileOpen = () => viewModel.GeneratedCommand = "rm -rf /";

        await viewModel.BroadcastAsync();

        Assert.Contains("uptime", Assert.Single(dialog.LabeledCalls).Message, StringComparison.Ordinal);
        Assert.Equal("uptime", Assert.Single(broadcaster.Sent).Command);
    }

    private static async Task<CommandLibraryViewModel> CreateAsync(
        CommandLibraryBroadcastHarness.FakeBroadcaster broadcaster,
        Heimdall.App.Services.IDialogService dialog)
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "uptime");

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService([action]));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            dialog,
            gitSyncService: null!,
            transferService: null!)
        {
            CommandBroadcaster = broadcaster
        };

        await viewModel.InitializeAsync(targetHost: null);
        viewModel.RefreshBroadcastTargets();
        return viewModel;
    }
}
