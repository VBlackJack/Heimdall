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

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes when Ctrl+F may claim the keystroke.
/// </summary>
/// <remarks>
/// The filter box lives inside the sessions-tab subtree, so it is collapsed on every other
/// tab and cannot take keyboard focus there. The shortcut service marks the event handled for
/// any binding whose canExecute passes, so the gate is the only thing standing between the
/// user and a key that is swallowed for a guaranteed no-op.
/// </remarks>
public sealed class MainWindowFilterShortcutTests
{
    [Fact]
    public void FilterOffScreen_LeavesTheKeystrokeAlone()
    {
        Assert.False(MainWindow.CanFocusServerFilter(terminalFocused: false, filterVisible: false));
    }

    [Fact]
    public void FilterOnScreen_TakesTheKeystroke()
    {
        Assert.True(MainWindow.CanFocusServerFilter(terminalFocused: false, filterVisible: true));
    }

    [Fact]
    public void TerminalFocus_KeepsTheKeystrokeInTheSession()
    {
        Assert.False(MainWindow.CanFocusServerFilter(terminalFocused: true, filterVisible: true));
    }
}
