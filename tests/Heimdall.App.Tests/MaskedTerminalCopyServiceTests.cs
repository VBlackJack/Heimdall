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

using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using static Heimdall.App.Tests.MaskedCopyTestSupport;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers <c>MaskedTerminalCopyService</c>: the read, mask, review and copy flow, and the
/// registration of its keyboard shortcut.
/// </summary>
public sealed class MaskedTerminalCopyServiceTests
{
    private sealed class Rig
    {
        public required LocalizationManager Localizer { get; init; }

        public SessionCoordinatorPreMountTests.FakeDialogService Dialogs { get; } = new();

        public RecordingClipboard Clipboard { get; } = new();

        public List<string> Statuses { get; } = [];

        public MaskedTerminalCopyService Service { get; private set; } = null!;

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig { Localizer = await LocalizerAsync() };
            rig.Service = new MaskedTerminalCopyService(rig.Dialogs, rig.Localizer, rig.Clipboard, rig.Statuses.Add);
            return rig;
        }
    }

    [Fact]
    public async Task AReadyTerminal_OpensTheDialogWithMaskedText_AndGivesTheFocusBack()
    {
        var rig = await Rig.CreateAsync();
        var terminal = new FakeTerminal();

        await rig.Service.RunAsync(Tab(Leaf(terminal)));

        var viewModel = Assert.Single(rig.Dialogs.MaskedCopies);
        Assert.DoesNotContain(Secret, viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Contains("[masked:", viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Equal(1, terminal.FocusCalls);
        Assert.False(rig.Service.IsRunning);
    }

    [Fact]
    public async Task Copy_PlacesTheMaskedTextOnTheClipboard_AndReportsIt()
    {
        var rig = await Rig.CreateAsync();
        rig.Dialogs.MaskedCopyBehavior = viewModel =>
        {
            viewModel.CopyCommand.Execute(null);
            return Task.CompletedTask;
        };

        await rig.Service.RunAsync(Tab(Leaf(new FakeTerminal())));

        var viewModel = Assert.Single(rig.Dialogs.MaskedCopies);
        Assert.Equal([viewModel.MaskedText], rig.Clipboard.Texts);
        Assert.Equal([rig.Localizer["MaskedCopyCopied"]], rig.Statuses);
    }

    [Fact]
    public async Task Cancel_CopiesNothing_AndStillGivesTheFocusBack()
    {
        var rig = await Rig.CreateAsync();
        var terminal = new FakeTerminal();
        rig.Dialogs.MaskedCopyBehavior = viewModel =>
        {
            viewModel.CancelCommand.Execute(null);
            return Task.CompletedTask;
        };

        await rig.Service.RunAsync(Tab(Leaf(terminal)));

        Assert.Empty(rig.Clipboard.Texts);
        Assert.Empty(rig.Statuses);
        Assert.Equal(1, terminal.FocusCalls);
    }

    [Theory]
    [InlineData(TerminalTextReadStatus.NotReady, "MaskedCopyStatusNotReady")]
    [InlineData(TerminalTextReadStatus.Timeout, "MaskedCopyStatusTimeout")]
    [InlineData(TerminalTextReadStatus.Failed, "MaskedCopyStatusFailed")]
    public async Task ARead_ThatIsNotOk_ShowsAStatusMessage_AndNoDialog(TerminalTextReadStatus status, string key)
    {
        var rig = await Rig.CreateAsync();
        var terminal = new FakeTerminal { Result = TerminalTextReadResult.Empty(status) };

        await rig.Service.RunAsync(Tab(Leaf(terminal)));

        Assert.Empty(rig.Dialogs.MaskedCopies);
        Assert.Equal([rig.Localizer[key]], rig.Statuses);
        Assert.Empty(rig.Clipboard.Texts);
    }

    [Fact]
    public async Task ATabWithNoTerminal_ShowsAStatusMessage_AndNoDialog()
    {
        var rig = await Rig.CreateAsync();

        await rig.Service.RunAsync(Tab(Leaf(new object())));

        Assert.Empty(rig.Dialogs.MaskedCopies);
        Assert.Equal([rig.Localizer["MaskedCopyStatusNoTerminal"]], rig.Statuses);
    }

    [Fact]
    public async Task AReadThatThrows_IsCaught_ReportedAsAFailure_AndTheServiceIsFreeAgain()
    {
        var rig = await Rig.CreateAsync();
        var terminal = new FakeTerminal { ReadFailure = new InvalidOperationException("boom") };

        await rig.Service.RunAsync(Tab(Leaf(terminal)));

        Assert.Equal([rig.Localizer["MaskedCopyStatusFailed"]], rig.Statuses);
        Assert.False(rig.Service.IsRunning);
    }

    [Fact]
    public async Task ASecondInvocationWhileTheDialogIsOpen_DoesNothing()
    {
        var rig = await Rig.CreateAsync();
        var release = new TaskCompletionSource();
        rig.Dialogs.MaskedCopyBehavior = _ => release.Task;
        var tab = Tab(Leaf(new FakeTerminal()));

        Task first = rig.Service.RunAsync(tab);
        await WaitForAsync(() => rig.Dialogs.MaskedCopies.Count == 1);

        await rig.Service.RunAsync(tab);

        Assert.Single(rig.Dialogs.MaskedCopies);
        Assert.True(rig.Service.IsRunning);

        release.SetResult();
        await first;

        Assert.False(rig.Service.IsRunning);
    }

    // -- The keyboard shortcut ---------------------------------------

    [Fact]
    public async Task CtrlShiftM_OnATabWithoutATerminal_IsNotSwallowed()
    {
        var rig = await Rig.CreateAsync();
        var tab = Tab(Leaf(new object()));
        var keys = new KeyboardShortcutService();
        MainWindow.RegisterMaskedCopyShortcut(keys, () => rig.Service.CanRun(tab), () => rig.Service.Start(tab));

        bool handled = keys.TryHandle(Key.M, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.False(handled);
        Assert.Empty(rig.Dialogs.MaskedCopies);
        Assert.Empty(rig.Statuses);
    }

    [Fact]
    public async Task CtrlShiftM_OnATabWithAReadyTerminal_StartsTheCopy()
    {
        var rig = await Rig.CreateAsync();
        var tab = Tab(Leaf(new FakeTerminal()));
        var keys = new KeyboardShortcutService();
        MainWindow.RegisterMaskedCopyShortcut(keys, () => rig.Service.CanRun(tab), () => rig.Service.Start(tab));

        bool handled = keys.TryHandle(Key.M, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.True(handled);
        await WaitForAsync(() => rig.Dialogs.MaskedCopies.Count == 1);
    }

    [Fact]
    public async Task CtrlM_WithoutShift_IsNotTheMaskedCopyChord()
    {
        var rig = await Rig.CreateAsync();
        var tab = Tab(Leaf(new FakeTerminal()));
        var keys = new KeyboardShortcutService();
        MainWindow.RegisterMaskedCopyShortcut(keys, () => rig.Service.CanRun(tab), () => rig.Service.Start(tab));

        Assert.False(keys.TryHandle(Key.M, ModifierKeys.Control));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
