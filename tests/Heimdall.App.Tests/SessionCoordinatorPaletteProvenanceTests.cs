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
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The palette routes a row by where it came from, not by the text of its identifier.
/// </summary>
/// <remarks>
/// <para>A saved profile and a destination typed by hand can carry the same identifier string:
/// the quick-connect namespace is reserved at the import doors, but profiles imported before the
/// reservation, hand-edited into the inventory, or brought in by the legacy import still hold
/// one. The palette used to send every such row down the typed-destination path, which rebuilds
/// a bare profile from the row and drops the saved gateway, ports, credentials and RDP settings.
/// The oracle here is the profile the protocol handler is asked to dial: the saved one, with its
/// saved port and username, or the transient one, with the protocol defaults.</para>
/// <para>Three entry points, because the routing is spelled out three times: Enter, Ctrl+Enter
/// and a click on a result. A mutant that restores the prefix test at one of them is caught by
/// that row alone.</para>
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string ReservedSshProfileId = "adhoc-ssh-demo.example.com";
    private const string TypedHost = "demo.example.com";
    private const int SavedSshPort = 2222;
    private const string SavedSshUsername = "admin";
    private const string Refusal = "refused by the test";

    /// <summary>
    /// The ways a row leaves the palette. The first three route through the plain palette; the
    /// last three reach the split-mode branches, which test the same mark a line earlier.
    /// </summary>
    public enum PaletteEntry
    {
        Enter,
        CtrlEnter,
        Selection,
        EnterInSplitMode,
        SelectionInSplitMode,
        CtrlEnterWithActiveSession
    }

    // The split-mode rows are the awaited ones only: a saved profile in split mode is split
    // into the existing session, and that pipeline offers nothing to wait on when it is fired
    // and forgotten by a click.
    [Theory]
    [InlineData(PaletteEntry.Enter)]
    [InlineData(PaletteEntry.CtrlEnter)]
    [InlineData(PaletteEntry.Selection)]
    [InlineData(PaletteEntry.EnterInSplitMode)]
    [InlineData(PaletteEntry.CtrlEnterWithActiveSession)]
    public async Task Palette_SavedProfileWithAReservedIdentifier_DialsTheSavedProfile(PaletteEntry entry)
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler handler = await ArrangeReservedProfileAsync(harness);

        ServerItemViewModel row = Assert.Single(
            harness.Main.ServerList.Servers,
            server => string.Equals(server.Id, ReservedSshProfileId, StringComparison.Ordinal));

        // The row came from the inventory: nothing on disk can mark it as typed.
        Assert.False(row.IsTypedDestination);

        await DriveAsync(harness, entry, row);
        await WaitUntilAsync(() => handler.LastServer is not null);

        // The saved port and username are the observation; the identifier is not, because the
        // server list adopts a session identity on the profile for the length of the pipeline.
        ServerProfileDto dialled = handler.LastServer!;
        Assert.Equal(SavedSshPort, dialled.SshPort);
        Assert.Equal(SavedSshUsername, dialled.SshUsername);

        // Let the pipeline the click fired finish before the harness is torn down.
        await WaitUntilAsync(() => !harness.Main.ServerList.ConnectCommand.IsRunning);
    }

    [Theory]
    [InlineData(PaletteEntry.Enter)]
    [InlineData(PaletteEntry.CtrlEnter)]
    [InlineData(PaletteEntry.Selection)]
    [InlineData(PaletteEntry.EnterInSplitMode)]
    [InlineData(PaletteEntry.SelectionInSplitMode)]
    [InlineData(PaletteEntry.CtrlEnterWithActiveSession)]
    public async Task Palette_TypedDestinationSharingTheIdentifier_DialsATransientProfile(PaletteEntry entry)
    {
        using TestHarness harness = TestHarness.Create();
        ControlledProtocolHandler handler = await ArrangeReservedProfileAsync(harness);

        // The collision itself: the same identifier string, minted for a host typed by hand,
        // while the saved profile above sits in the inventory.
        ServerItemViewModel typed = new()
        {
            Id = ReservedSshProfileId,
            DisplayName = TypedHost,
            RemoteServer = TypedHost,
            Endpoint = TypedHost,
            ConnectionType = "SSH",
            IsTypedDestination = true
        };

        await DriveAsync(harness, entry, typed);
        await WaitUntilAsync(() => handler.LastServer is not null);

        ServerProfileDto dialled = handler.LastServer!;
        Assert.Equal(ReservedSshProfileId, dialled.Id);
        Assert.Equal(DefaultPorts.Ssh, dialled.SshPort);
        Assert.NotEqual(SavedSshUsername, dialled.SshUsername);

        // The typed route reports the refusal on the status line once it is done.
        await WaitUntilAsync(() => string.Equals(harness.Main.StatusText, Refusal, StringComparison.Ordinal));
    }

    // The mint sites are the only place the mark may be set, so each of them has to set it: a
    // bare host offers SSH and RDP, and a user@host:port query offers one SSH row.
    [Fact]
    public void Palette_BareHostQuery_MarksBothOffersAsTypedDestinations()
    {
        using TestHarness harness = TestHarness.Create();
        CommandPaletteViewModel palette = harness.Main.CommandPalette;

        palette.SearchText = "10.0.0.9";

        Assert.Equal(2, palette.Results.Count);
        Assert.All(palette.Results, row => Assert.True(row.IsTypedDestination));
        Assert.All(palette.Results, row => Assert.True(AdHocProfileIds.IsAdHoc(row.Id)));
    }

    [Fact]
    public void Palette_UserAtHostQuery_MarksTheOfferAsATypedDestination()
    {
        using TestHarness harness = TestHarness.Create();
        CommandPaletteViewModel palette = harness.Main.CommandPalette;

        palette.SearchText = "root@10.0.0.9:2222";

        ServerItemViewModel row = Assert.Single(palette.Results);
        Assert.True(row.IsTypedDestination);
        Assert.True(AdHocProfileIds.IsAdHoc(row.Id));
    }

    // The control that keeps the two assertions above meaningful: a row built from the
    // inventory is not marked, so the mark measures the mint sites and not a default.
    [Fact]
    public async Task Palette_InventoryRow_IsNotATypedDestination()
    {
        using TestHarness harness = TestHarness.Create();
        _ = await ArrangeReservedProfileAsync(harness);
        CommandPaletteViewModel palette = harness.Main.CommandPalette;

        palette.SearchText = TypedHost;

        Assert.Contains(palette.Results, row => !row.IsTypedDestination
            && string.Equals(row.Id, ReservedSshProfileId, StringComparison.Ordinal));
    }

    private static async Task<ControlledProtocolHandler> ArrangeReservedProfileAsync(TestHarness harness)
    {
        ServerProfileDto saved = harness.CreateServer("SSH");
        saved.Id = ReservedSshProfileId;
        saved.RemoteServer = TypedHost;
        saved.SshPort = SavedSshPort;
        saved.SshUsername = SavedSshUsername;
        await harness.PersistServerAsync(saved);

        // A refusal, so neither route goes on to build a host control: the profile handed to
        // the handler is the whole observation.
        ControlledProtocolHandler handler = harness.GetHandler("SSH");
        handler.Result.SetResult(new ConnectionResult(false, Refusal, null));
        return handler;
    }

    private static Task DriveAsync(TestHarness harness, PaletteEntry entry, ServerItemViewModel item)
    {
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        switch (entry)
        {
            case PaletteEntry.Enter:
                return palette.ConnectFromPaletteCommand.ExecuteAsync(item);
            case PaletteEntry.CtrlEnter:
                return palette.ConnectSplitFromPaletteCommand.ExecuteAsync(item);
            case PaletteEntry.EnterInSplitMode:
                palette.OpenSplit(AddExistingSession(harness), SplitOrientation.Vertical);
                return palette.ConnectFromPaletteCommand.ExecuteAsync(item);
            case PaletteEntry.SelectionInSplitMode:
                palette.OpenSplit(AddExistingSession(harness), SplitOrientation.Vertical);
                palette.ExecuteSelection(item);
                return Task.CompletedTask;
            case PaletteEntry.CtrlEnterWithActiveSession:
                harness.Main.Connection.ActiveSession = AddExistingSession(harness);
                return palette.ConnectSplitFromPaletteCommand.ExecuteAsync(item);
            default:
                palette.ExecuteSelection(item);
                return Task.CompletedTask;
        }
    }

    // A session for the split-mode branches to split; it never connects, so its host control
    // stays null and the palette does not offer it as a merge candidate.
    private static SessionTabViewModel AddExistingSession(TestHarness harness)
    {
        SessionTabViewModel existing = harness.Main.Connection.AddSession(
            "existing-session",
            "Existing session",
            "SSH");
        existing.Status = "Disconnected";
        return existing;
    }
}
