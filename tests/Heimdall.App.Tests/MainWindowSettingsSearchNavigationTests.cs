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

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes how the settings search box answers the keyboard: which gesture does what, and
/// where a step lands.
/// </summary>
/// <remarks>
/// The walk used to run forward only - Shift+Enter stepped forward like Enter, because only
/// the key was read - so overshooting a match meant walking the whole cycle again. Escape did
/// nothing at all, leaving the mouse as the only way to empty the box.
/// </remarks>
public sealed class MainWindowSettingsSearchNavigationTests
{
    [Fact]
    public void ShiftEnter_WalksBackwards()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.StepBack,
            MainWindow.ResolveSettingsSearchGesture(Key.Enter, ModifierKeys.Shift, 3, hasQuery: true));
    }

    [Fact]
    public void Enter_WalksForwards()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.StepForward,
            MainWindow.ResolveSettingsSearchGesture(Key.Enter, ModifierKeys.None, 3, hasQuery: true));
    }

    [Fact]
    public void Escape_EmptiesABoxThatHoldsText()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.Clear,
            MainWindow.ResolveSettingsSearchGesture(Key.Escape, ModifierKeys.None, 0, hasQuery: true));
    }

    // An empty box has nothing to dismiss. Claiming Escape there would take it from the
    // fullscreen binding, which is the only other thing that key does in the shell.
    [Fact]
    public void Escape_OnAnEmptyBox_IsLeftToTheShell()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.None,
            MainWindow.ResolveSettingsSearchGesture(Key.Escape, ModifierKeys.None, 0, hasQuery: false));
    }

    [Fact]
    public void Enter_WithoutMatches_DoesNothing()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.None,
            MainWindow.ResolveSettingsSearchGesture(Key.Enter, ModifierKeys.None, 0, hasQuery: true));
    }

    [Fact]
    public void TypingKeys_AreNotGestures()
    {
        Assert.Equal(
            MainWindow.SettingsSearchGesture.None,
            MainWindow.ResolveSettingsSearchGesture(Key.A, ModifierKeys.None, 3, hasQuery: true));
    }

    [Fact]
    public void ForwardStep_MovesToTheNextMatch()
    {
        Assert.Equal(1, MainWindow.ResolveSettingsSearchMatchIndex(0, 3, stepBack: false));
    }

    [Fact]
    public void ForwardStep_WrapsPastTheLastMatch()
    {
        Assert.Equal(0, MainWindow.ResolveSettingsSearchMatchIndex(2, 3, stepBack: false));
    }

    [Fact]
    public void ForwardStep_BeforeAnyJump_LandsOnTheFirstMatch()
    {
        Assert.Equal(0, MainWindow.ResolveSettingsSearchMatchIndex(-1, 3, stepBack: false));
    }

    [Fact]
    public void BackwardStep_MovesToThePreviousMatch()
    {
        Assert.Equal(1, MainWindow.ResolveSettingsSearchMatchIndex(2, 3, stepBack: true));
    }

    [Fact]
    public void BackwardStep_WrapsPastTheFirstMatch()
    {
        Assert.Equal(2, MainWindow.ResolveSettingsSearchMatchIndex(0, 3, stepBack: true));
    }

    // -1 means the walk has not started. Stepping back from there belongs on the last match,
    // where plain modular arithmetic would land two positions before the start instead.
    [Fact]
    public void BackwardStep_BeforeAnyJump_LandsOnTheLastMatch()
    {
        Assert.Equal(2, MainWindow.ResolveSettingsSearchMatchIndex(-1, 3, stepBack: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StepWithoutMatches_LeavesTheWalkUnpositioned(bool stepBack)
    {
        Assert.Equal(-1, MainWindow.ResolveSettingsSearchMatchIndex(-1, 0, stepBack));
    }
}
