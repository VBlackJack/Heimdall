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
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Models;
using static Heimdall.App.Tests.MaskedCopyTestSupport;

namespace Heimdall.App.Tests;

/// <summary>
/// The "Copy output, secrets masked" entry of the Ctrl+K palette: when it is listed and what
/// each way of choosing it does.
/// </summary>
/// <remarks>
/// Producers: <c>CommandPaletteViewModel.OnSearchTextChanged</c> (the listing, gated by
/// <c>MaskedTerminalCopyService.CanRun</c> and the split-mode check) and
/// <c>CommandPaletteViewModel.RunPaletteCommand</c>, reached from
/// <c>ConnectFromPaletteCommand</c> (Enter), <c>ConnectSplitFromPaletteCommand</c> (Ctrl+Enter)
/// and <c>ExecuteSelection</c> (double-click).
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string MaskedCopyQuery = "masked";

    private static readonly TimeSpan MaskedCopyWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MaskedCopyPoll = TimeSpan.FromMilliseconds(10);

    // The active tab of the harness, with the given host control as its only pane.
    private static SessionTabViewModel ActivateTab(TestHarness harness, object? hostControl)
    {
        SessionTabViewModel tab = harness.Main.Connection.AddSession("masked-copy-tab", "Masked copy tab", "SSH");
        tab.RootContent = Leaf(hostControl);
        harness.Main.Connection.ActiveSession = tab;
        return tab;
    }

    private static bool PaletteListsMaskedCopy(CommandPaletteViewModel palette)
    {
        palette.SearchText = string.Empty;
        palette.SearchText = MaskedCopyQuery;
        return palette.Results.Any(row => row.Id == CommandPaletteViewModel.MaskedCopyCommandId);
    }

    private static ServerItemViewModel MaskedCopyRow(CommandPaletteViewModel palette)
    {
        Assert.True(PaletteListsMaskedCopy(palette));
        return palette.Results.Single(row => row.Id == CommandPaletteViewModel.MaskedCopyCommandId);
    }

    private static async Task WaitForMaskedCopyDialogAsync(TestHarness harness)
    {
        using CancellationTokenSource timeout = new(MaskedCopyWait);
        while (harness.DialogService.MaskedCopies.Count == 0)
        {
            await Task.Delay(MaskedCopyPoll, timeout.Token);
        }
    }

    private static void AssertMaskedCopyDialogOpened(TestHarness harness)
    {
        MaskedCopyDialogViewModel dialog = Assert.Single(harness.DialogService.MaskedCopies);
        Assert.DoesNotContain(Secret, dialog.MaskedText, StringComparison.Ordinal);
        Assert.Contains("[masked:", dialog.MaskedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Palette_ListsMaskedCopy_ForAReadyTerminalTab()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new FakeTerminal());

        Assert.True(PaletteListsMaskedCopy(harness.Main.CommandPalette));
    }

    // The control that keeps the absence assertions below meaningful: the same query on a ready
    // terminal tab finds the row, so a missing row measures the gate and not the query.
    [Fact]
    public void Palette_DoesNotListMaskedCopy_ForATabWithoutATerminal()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new object());

        Assert.False(PaletteListsMaskedCopy(harness.Main.CommandPalette));
    }

    [Fact]
    public void Palette_DoesNotListMaskedCopy_ForATerminalThatIsNotReady()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new FakeTerminal { CanReadText = false });

        Assert.False(PaletteListsMaskedCopy(harness.Main.CommandPalette));
    }

    [Fact]
    public void Palette_DoesNotListMaskedCopy_WhenThereIsNoActiveTab()
    {
        using TestHarness harness = TestHarness.Create();

        Assert.False(PaletteListsMaskedCopy(harness.Main.CommandPalette));
    }

    [Fact]
    public void Palette_DoesNotListMaskedCopy_InSplitMode()
    {
        using TestHarness harness = TestHarness.Create();
        SessionTabViewModel tab = ActivateTab(harness, new FakeTerminal());
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        Assert.True(PaletteListsMaskedCopy(palette));

        palette.OpenSplit(tab, SplitOrientation.Vertical);

        Assert.False(PaletteListsMaskedCopy(palette));
    }

    [Fact]
    public async Task Palette_Enter_OnMaskedCopy_OpensTheMaskedCopyDialog()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new FakeTerminal());
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        ServerItemViewModel row = MaskedCopyRow(palette);
        palette.IsOpen = true;

        await palette.ConnectFromPaletteCommand.ExecuteAsync(row);
        await WaitForMaskedCopyDialogAsync(harness);

        AssertMaskedCopyDialogOpened(harness);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public async Task Palette_CtrlEnter_OnMaskedCopy_OpensTheMaskedCopyDialog()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new FakeTerminal());
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        ServerItemViewModel row = MaskedCopyRow(palette);
        palette.IsOpen = true;

        await palette.ConnectSplitFromPaletteCommand.ExecuteAsync(row);
        await WaitForMaskedCopyDialogAsync(harness);

        AssertMaskedCopyDialogOpened(harness);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public async Task Palette_DoubleClick_OnMaskedCopy_OpensTheMaskedCopyDialog()
    {
        using TestHarness harness = TestHarness.Create();
        ActivateTab(harness, new FakeTerminal());
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        ServerItemViewModel row = MaskedCopyRow(palette);
        palette.IsOpen = true;

        palette.ExecuteSelection(row);
        await WaitForMaskedCopyDialogAsync(harness);

        AssertMaskedCopyDialogOpened(harness);
        Assert.False(palette.IsOpen);
    }
}
