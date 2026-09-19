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
using Heimdall.Core.Models;
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the guards the broadcast path was missing while the single-target Send had them.
/// </summary>
/// <remarks>
/// Every one of these was a gate that existed on the path reaching <b>one</b> terminal and was
/// absent from the path reaching <b>many</b>. That is the wrong way round, and it is the shape to
/// look for whenever a wider path is added beside a narrow one.
/// </remarks>
public sealed class CommandLibraryBroadcastGuardTests
{
    // -- The validity gate ------------------------------------------

    /// <summary>
    /// When validation fails the generator does not clear the command, it replaces it with the
    /// raw pattern, braces and all. Send greys out on that; the broadcast did not.
    /// </summary>
    [Fact]
    public async Task AnInvalidCommandCannotBeBroadcast()
    {
        var (viewModel, _) = await LoadedAsync();
        using var _1 = viewModel;

        viewModel.GeneratedCommand = "systemctl restart {service}";
        viewModel.IsCommandValid = false;
        viewModel.BroadcastTargets.Single(t => t.Id == "t1").IsSelected = true;

        Assert.False(viewModel.BroadcastCommand.CanExecute(null));
    }

    /// <summary>
    /// Control: the same state with a valid command must be runnable, or the test above passes
    /// against a command that can never run at all.
    /// </summary>
    [Fact]
    public async Task AValidCommandStillCanBeBroadcast()
    {
        var (viewModel, _) = await LoadedAsync();
        using var _1 = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.IsCommandValid = true;
        viewModel.BroadcastTargets.Single(t => t.Id == "t1").IsSelected = true;

        Assert.True(viewModel.BroadcastCommand.CanExecute(null));
    }

    // -- The empty state --------------------------------------------

    [Fact]
    public async Task WithNoTerminalThePanelSaysSo()
    {
        var (viewModel, broadcaster) = await LoadedAsync();
        using var _1 = viewModel;

        Assert.False(viewModel.HasNoBroadcastTargets);

        broadcaster.Targets.Clear();
        viewModel.RefreshBroadcastTargets();

        Assert.True(viewModel.HasNoBroadcastTargets);
        Assert.False(viewModel.HasBroadcastTargets);
    }

    // -- The criticality guard --------------------------------------

    /// <summary>
    /// Send asks the dangerous-command question for a Dangerous action. The broadcast, which
    /// reaches more machines, did not ask it at all - only its own count question, which never
    /// says the action is dangerous.
    /// </summary>
    [Fact]
    public async Task ADangerousActionIsConfirmedAsDangerousBeforeTheCount()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var dialog = new SilentDialogService { ConfirmResult = true };
        var viewModel = await CommandLibraryBroadcastHarness.CreateAsync(
            broadcaster, dialog, CriticalityLevel.Dangerous);
        using var _1 = viewModel;

        Stage(viewModel, "rm -rf /var/log/app");

        await viewModel.BroadcastAsync();

        // Two questions: the danger, then the count. One of them alone is not consent.
        Assert.Equal(2, dialog.Shown.Count);
        Assert.Equal(2, broadcaster.Sent.Count);
    }

    /// <summary>
    /// Control: an Info action must still ask exactly once, or the test above is satisfied by
    /// anything that asks twice for everything.
    /// </summary>
    [Fact]
    public async Task AnInfoActionIsConfirmedOnce()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var dialog = new SilentDialogService { ConfirmResult = true };
        var viewModel = await CommandLibraryBroadcastHarness.CreateAsync(
            broadcaster, dialog, CriticalityLevel.Info);
        using var _1 = viewModel;

        Stage(viewModel, "uptime");

        await viewModel.BroadcastAsync();

        Assert.Single(dialog.Shown);
        Assert.Equal(2, broadcaster.Sent.Count);
    }

    /// <summary>
    /// Refusing the danger question must stop the broadcast before anything is sent, or the
    /// question is decoration.
    /// </summary>
    [Fact]
    public async Task RefusingTheDangerQuestionSendsNothing()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var dialog = new SilentDialogService { ConfirmResult = false };
        var viewModel = await CommandLibraryBroadcastHarness.CreateAsync(
            broadcaster, dialog, CriticalityLevel.Dangerous);
        using var _1 = viewModel;

        Stage(viewModel, "rm -rf /var/log/app");

        await viewModel.BroadcastAsync();

        Assert.Empty(broadcaster.Sent);

        // It stopped at the FIRST question rather than asking the count of a batch it was
        // never going to send.
        Assert.Single(dialog.Shown);
    }

    // -- What a partial send leaves behind --------------------------

    /// <summary>
    /// The obvious gesture after a partial send is to wait for the one that failed and press the
    /// button again. Leaving every target ticked made that re-run the command on the ones that
    /// had already succeeded.
    /// </summary>
    [Fact]
    public async Task AfterAPartialSendOnlyTheUnreachedStayTicked()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false), ("t3", "web-03", false));
        broadcaster.Refuse.Add("t2");
        var viewModel = await CommandLibraryBroadcastHarness.CreateAsync(broadcaster, confirm: true);
        using var _1 = viewModel;

        viewModel.GeneratedCommand = "useradd deploy";
        viewModel.IsCommandValid = true;
        foreach (var entry in viewModel.BroadcastTargets)
        {
            entry.IsSelected = true;
        }

        await viewModel.BroadcastAsync();

        Assert.Equal(["t2"], viewModel.BroadcastTargets.Where(t => t.IsSelected).Select(t => t.Id));

        // The retry reaches the one that failed, and nothing else.
        broadcaster.Refuse.Clear();
        broadcaster.Sent.Clear();
        await viewModel.BroadcastAsync();

        Assert.Equal(["t2"], broadcaster.Sent.Select(entry => entry.TargetId));
    }

    /// <summary>
    /// Control: a send that reached everything must not leave a selection either, or the test
    /// above could pass by unticking unconditionally.
    /// </summary>
    [Fact]
    public async Task AfterAWholeSendNothingStaysTicked()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        var viewModel = await CommandLibraryBroadcastHarness.CreateAsync(broadcaster, confirm: true);
        using var _1 = viewModel;

        viewModel.GeneratedCommand = "uptime";
        viewModel.IsCommandValid = true;
        foreach (var entry in viewModel.BroadcastTargets)
        {
            entry.IsSelected = true;
        }

        await viewModel.BroadcastAsync();

        Assert.DoesNotContain(viewModel.BroadcastTargets, t => t.IsSelected);
    }

    // -- Harness ----------------------------------------------------

    /// <summary>
    /// Puts the view model in the state a successful generation leaves it in, with every
    /// target ticked.
    /// </summary>
    private static void Stage(CommandLibraryViewModel viewModel, string command)
    {
        // Selecting the action is what gives the broadcast a criticality level to read; without
        // it the level falls back to Info and the danger guard has nothing to fire on.
        viewModel.SelectedEntry = viewModel.AllEntries.Single();
        viewModel.GeneratedCommand = command;
        viewModel.IsCommandValid = true;
        foreach (var entry in viewModel.BroadcastTargets)
        {
            entry.IsSelected = true;
        }
    }

    private static CommandLibraryBroadcastHarness.FakeBroadcaster Broadcaster(
        params (string Id, string Name, bool IsOrigin)[] targets)
        => CommandLibraryBroadcastHarness.Broadcaster(targets);

    private static async Task<(CommandLibraryViewModel ViewModel,
        CommandLibraryBroadcastHarness.FakeBroadcaster Broadcaster)> LoadedAsync()
    {
        var broadcaster = Broadcaster(("t1", "web-01", true), ("t2", "web-02", false));
        return (await CommandLibraryBroadcastHarness.CreateAsync(broadcaster, confirm: true),
            broadcaster);
    }
}
