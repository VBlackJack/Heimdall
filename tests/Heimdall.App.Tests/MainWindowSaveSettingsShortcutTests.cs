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

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes which keystrokes Ctrl+S on the settings panel is allowed to claim.
/// </summary>
/// <remarks>
/// The shortcut service marks a key handled as soon as a matching binding's canExecute
/// passes, so every term of the gate is a decision about who owns the keystroke: the
/// settings panel, the session terminal the user is typing in, or whatever else is
/// focused on another tab. A binding that claims more than the settings panel's Save
/// takes the key away from those without giving anything back.
/// </remarks>
public sealed class MainWindowSaveSettingsShortcutTests
{
    [Fact]
    public void OnSettingsTab_CtrlS_RunsSave()
    {
        KeyboardShortcutService service = new();
        int saves = 0;
        MainWindow.RegisterSaveSettingsShortcut(
            service,
            isTerminalFocused: () => false,
            isSettingsTabSelected: () => true,
            save: () => saves++);

        bool handled = service.TryHandle(Key.S, ModifierKeys.Control);

        Assert.True(handled);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void AwayFromTheSettingsTab_LeavesCtrlSAlone()
    {
        KeyboardShortcutService service = new();
        int saves = 0;
        MainWindow.RegisterSaveSettingsShortcut(
            service,
            isTerminalFocused: () => false,
            isSettingsTabSelected: () => false,
            save: () => saves++);

        bool handled = service.TryHandle(Key.S, ModifierKeys.Control);

        Assert.False(handled);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void InsideASessionTerminal_KeepsCtrlSInTheSession()
    {
        KeyboardShortcutService service = new();
        int saves = 0;
        MainWindow.RegisterSaveSettingsShortcut(
            service,
            isTerminalFocused: () => true,
            isSettingsTabSelected: () => true,
            save: () => saves++);

        bool handled = service.TryHandle(Key.S, ModifierKeys.Control);

        Assert.False(handled);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void CtrlShiftS_StaysWithTheScreenshotShortcut()
    {
        KeyboardShortcutService service = new();
        int saves = 0;
        MainWindow.RegisterSaveSettingsShortcut(
            service,
            isTerminalFocused: () => false,
            isSettingsTabSelected: () => true,
            save: () => saves++);

        bool handled = service.TryHandle(Key.S, ModifierKeys.Control | ModifierKeys.Shift);

        Assert.False(handled);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void UnmodifiedS_StaysWithTheFocusedField()
    {
        KeyboardShortcutService service = new();
        int saves = 0;
        MainWindow.RegisterSaveSettingsShortcut(
            service,
            isTerminalFocused: () => false,
            isSettingsTabSelected: () => true,
            save: () => saves++);

        bool handled = service.TryHandle(Key.S, ModifierKeys.None);

        Assert.False(handled);
        Assert.Equal(0, saves);
    }
}
