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
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins sending one generated command to several open terminals at once.
/// </summary>
/// <remarks>
/// This is the one Command Library gesture that reaches machines the operator is not looking at,
/// so the tests here are about what it refuses and what it admits to, more than about what it
/// sends.
/// </remarks>
public sealed class CommandLibraryBroadcastTests
{
    // -- What the panel offers --------------------------------------

    /// <summary>
    /// Opened outside a session there is nothing to send to, and the toggle is the only thing
    /// that would reveal the panel, so it stays away.
    /// </summary>
    [Fact]
    public async Task OutsideASessionThereIsNoPanel()
    {
        var viewModel = await CreateAsync(broadcaster: null);
        using var _ = viewModel;

        Assert.False(viewModel.CanOfferBroadcast);
        Assert.Empty(viewModel.BroadcastTargets);
    }

    [Fact]
    public async Task InsideASessionThePanelIsOffered()
    {
        var viewModel = await CreateAsync(Broadcaster(("t1", "web-01", true)));
        using var _ = viewModel;

        Assert.True(viewModel.CanOfferBroadcast);
    }

    /// <summary>
    /// Opening a second session after the Command Library is the ordinary way to end up with
    /// something to broadcast to.
    /// </summary>
    /// <remarks>
    /// Gating the toggle on a count read at open time passes every other test in this file and
    /// still leaves the feature unreachable in exactly this case, because the gesture that shows
    /// the panel is the thing that stays hidden. This asserts the toggle is up <b>before</b> the
    /// second terminal exists, and that opening the panel then finds it.
    /// </remarks>
    [Fact]
    public async Task ATerminalOpenedAfterTheLibraryIsStillReachable()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true));
        var viewModel = await CreateAsync(broadcaster);
        using var _ = viewModel;

        Assert.True(viewModel.CanOfferBroadcast);

        broadcaster.Targets.Add(new CommandBroadcastTarget("t2", "web-02", "SSH", false));

        // Setting the property IS the gesture: the toggle binds IsChecked two-way and nothing
        // else opens the panel.
        viewModel.IsBroadcastPanelOpen = true;

        Assert.Equal(["t1", "t2"], viewModel.BroadcastTargets.Select(t => t.Id));
    }

    /// <summary>
    /// The originating terminal is what the Send button would have reached, so a batch that
    /// started without it would silently drop the one target the operator was looking at.
    /// </summary>
    [Fact]
    public async Task TheOriginatingTerminalStartsChecked()
    {
        var viewModel = await CreateAsync(Broadcaster(
            ("t1", "web-01", false), ("t2", "web-02", true), ("t3", "web-03", false)));
        using var _ = viewModel;

        Assert.Equal(1, viewModel.SelectedBroadcastCount);
        Assert.Equal("web-02", viewModel.BroadcastTargets.Single(t => t.IsSelected).DisplayName);
    }

    /// <summary>
    /// A tab opened or closed while the panel is up must not throw away what the operator has
    /// ticked, and must not re-tick the origin over the top of their choice either.
    /// </summary>
    [Fact]
    public async Task RefreshingKeepsWhatTheOperatorTicked()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CreateAsync(broadcaster);
        using var _ = viewModel;

        viewModel.BroadcastTargets.Single(t => t.Id == "t1").IsSelected = false;
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        broadcaster.Targets.Add(new CommandBroadcastTarget("t3", "web-03", "SSH", false));
        viewModel.RefreshBroadcastTargets();

        Assert.False(viewModel.BroadcastTargets.Single(t => t.Id == "t1").IsSelected);
        Assert.True(viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected);
        Assert.False(viewModel.BroadcastTargets.Single(t => t.Id == "t3").IsSelected);
    }

    [Fact]
    public async Task ATerminalThatHasClosedLeavesTheList()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CreateAsync(broadcaster);
        using var _ = viewModel;

        broadcaster.Targets.RemoveAll(target => target.Id == "t2");
        viewModel.RefreshBroadcastTargets();

        Assert.Equal(["t1"], viewModel.BroadcastTargets.Select(t => t.Id));
    }

    // -- What it sends ----------------------------------------------

    [Fact]
    public async Task ItSendsToEveryTickedTerminalAndNoOthers()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false), ("t3", "web-03", false));
        var viewModel = await CreateAsync(broadcaster, confirm: true);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t3").IsSelected = true;

        await viewModel.BroadcastAsync();

        Assert.Equal([("t1", "uptime"), ("t3", "uptime")], broadcaster.Sent);
    }

    /// <summary>
    /// Reaching several machines at once is itself what needs confirming, so the prompt does not
    /// depend on the action's declared level the way the single-target Send does.
    /// </summary>
    [Fact]
    public async Task ItAsksOnce_WhateverTheActionsLevel()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var dialog = new SilentDialogService { ConfirmResult = true };
        var viewModel = await CreateAsync(broadcaster, dialog);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        await viewModel.BroadcastAsync();

        // Two targets, one prompt. A prompt that appears per target is a prompt people learn
        // to dismiss.
        Assert.Single(dialog.ConfirmMessages);
        Assert.Equal(2, broadcaster.Sent.Count);
    }

    /// <summary>
    /// The count on the button is what the operator decided from, so it is the count the prompt
    /// has to name.
    /// </summary>
    [Fact]
    public async Task ThePromptNamesTheCountTheButtonShowed()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false), ("t3", "web-03", false));
        var dialog = new SilentDialogService { ConfirmResult = true };
        var viewModel = await CreateAsync(broadcaster, dialog);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        Assert.Equal(2, viewModel.SelectedBroadcastCount);
        Assert.Contains("2", viewModel.BroadcastButtonText, StringComparison.Ordinal);

        await viewModel.BroadcastAsync();

        Assert.Contains("2", dialog.ConfirmMessages.Single(), StringComparison.Ordinal);
        Assert.Contains("uptime", dialog.ConfirmMessages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusingThePromptSendsNothing()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CreateAsync(broadcaster, confirm: false);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "rm -rf /tmp/x";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        await viewModel.BroadcastAsync();

        Assert.Empty(broadcaster.Sent);
        Assert.Empty(viewModel.BroadcastStatus);
    }

    [Fact]
    public async Task WithNothingTickedTheCommandCannotRun()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CreateAsync(broadcaster, confirm: true);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t1").IsSelected = false;

        Assert.False(viewModel.BroadcastCommand.CanExecute(null));

        await viewModel.BroadcastAsync();

        Assert.Empty(broadcaster.Sent);
    }

    // -- What it admits to ------------------------------------------

    /// <summary>
    /// Stopping at the first target that will not take the command leaves the operator with a
    /// set of machines they would have to work out by hand.
    /// </summary>
    [Fact]
    public async Task OneTerminalRefusingDoesNotStopTheRest()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false), ("t3", "web-03", false));
        broadcaster.Refuse.Add("t2");
        var viewModel = await CreateAsync(broadcaster, confirm: true);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;
        viewModel.BroadcastTargets.Single(t => t.Id == "t3").IsSelected = true;

        await viewModel.BroadcastAsync();

        Assert.Equal(["t1", "t3"], broadcaster.Sent.Select(entry => entry.TargetId));
    }

    [Fact]
    public async Task ThePartialSummaryNamesWhatItDidNotReach()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        broadcaster.Refuse.Add("t2");
        var viewModel = await CreateAsync(broadcaster, confirm: true);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        await viewModel.BroadcastAsync();

        Assert.True(viewModel.HasBroadcastStatus);
        Assert.Contains("web-02", viewModel.BroadcastStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// Control for the test above: with nothing refusing, the summary must not name a terminal
    /// as unreached, or the partial case cannot be told from the whole one.
    /// </summary>
    [Fact]
    public async Task TheWholeSummaryNamesNoTerminal()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CreateAsync(broadcaster, confirm: true);
        using var _ = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.BroadcastTargets.Single(t => t.Id == "t2").IsSelected = true;

        await viewModel.BroadcastAsync();

        Assert.True(viewModel.HasBroadcastStatus);
        Assert.DoesNotContain("web-02", viewModel.BroadcastStatus, StringComparison.Ordinal);
    }

    // -- Harness ----------------------------------------------------

    private static FakeBroadcaster Broadcaster(params (string Id, string Name, bool IsOrigin)[] targets)
    {
        var fake = new FakeBroadcaster();
        foreach (var (id, name, isOrigin) in targets)
        {
            fake.Targets.Add(new CommandBroadcastTarget(id, name, "SSH", isOrigin));
        }

        return fake;
    }

    private static Task<CommandLibraryViewModel> CreateAsync(
        FakeBroadcaster? broadcaster, bool confirm = true)
        => CreateAsync(broadcaster, new SilentDialogService { ConfirmResult = confirm });

    private static async Task<CommandLibraryViewModel> CreateAsync(
        FakeBroadcaster? broadcaster, SilentDialogService dialog)
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

    /// <summary>
    /// Stands in for the session manager's broadcaster. It records what it was asked to send in
    /// order, which is what separates "reached three terminals" from "reached one, three times".
    /// </summary>
    private sealed class FakeBroadcaster : ICommandBroadcaster
    {
        public List<CommandBroadcastTarget> Targets { get; } = [];

        /// <summary>Target ids that report the command was not delivered.</summary>
        public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

        public List<(string TargetId, string Command)> Sent { get; } = [];

        public IReadOnlyList<CommandBroadcastTarget> GetTargets() => [.. Targets];

        public bool Send(string targetId, string command)
        {
            if (Refuse.Contains(targetId) || !Targets.Any(t => t.Id == targetId))
            {
                return false;
            }

            Sent.Add((targetId, command));
            return true;
        }
    }
}
