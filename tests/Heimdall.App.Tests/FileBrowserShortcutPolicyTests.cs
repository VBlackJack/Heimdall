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
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// Under the inline editor overlay the browser's shortcuts stayed live (F2 renamed a hidden row,
/// F5 refreshed, Ctrl+F swallowed the keystroke), with a text box focused Delete, F2, Enter,
/// Ctrl+C and Ctrl+V went to the row instead of the text, and with the focus on a check box or a
/// toolbar button the keys that act on the selection still reached it.
/// </summary>
public sealed class FileBrowserShortcutPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ShouldHandleShortcut_OnlyWhenNoOverlayAndNoTextInputHasFocus(
        bool inlineEditorOpen,
        bool focusInTextInput,
        bool expected)
    {
        Assert.Equal(expected, FileBrowserShortcutPolicy.ShouldHandleShortcut(inlineEditorOpen, focusInTextInput));
    }

    [Theory]
    [InlineData(Key.F2, ModifierKeys.None, FileBrowserShortcut.Rename)]
    [InlineData(Key.Delete, ModifierKeys.None, FileBrowserShortcut.Delete)]
    [InlineData(Key.Enter, ModifierKeys.None, FileBrowserShortcut.Open)]
    [InlineData(Key.Back, ModifierKeys.None, FileBrowserShortcut.ParentFolder)]
    [InlineData(Key.X, ModifierKeys.Control, FileBrowserShortcut.Cut)]
    [InlineData(Key.C, ModifierKeys.Control, FileBrowserShortcut.Copy)]
    [InlineData(Key.V, ModifierKeys.Control, FileBrowserShortcut.Paste)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, FileBrowserShortcut.CopyPath)]
    public void Resolve_SelectionKeysAreHonouredOnlyWhileTheFocusIsInTheList(
        Key key,
        ModifierKeys modifiers,
        FileBrowserShortcut expected)
    {
        Assert.Equal(expected, FileBrowserShortcutPolicy.Resolve(key, modifiers, false, false, focusInList: true));

        // Focus on a check box or a toolbar button: the key belongs to that control, not to the row.
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(key, modifiers, false, false, focusInList: false));
    }

    [Theory]
    [InlineData(Key.F5, ModifierKeys.None, FileBrowserShortcut.Refresh)]
    [InlineData(Key.F, ModifierKeys.Control, FileBrowserShortcut.FocusFilter)]
    [InlineData(Key.F7, ModifierKeys.None, FileBrowserShortcut.NewFolder)]
    [InlineData(Key.D, ModifierKeys.Control | ModifierKeys.Shift, FileBrowserShortcut.Download)]
    [InlineData(Key.U, ModifierKeys.Control | ModifierKeys.Shift, FileBrowserShortcut.Upload)]
    [InlineData(Key.Left, ModifierKeys.Alt, FileBrowserShortcut.NavigateBack)]
    [InlineData(Key.Up, ModifierKeys.Alt, FileBrowserShortcut.ParentFolder)]
    [InlineData(Key.Escape, ModifierKeys.None, FileBrowserShortcut.CancelLoad)]
    public void Resolve_BrowserWideKeysWorkFromAnywhereOutsideATextBox(
        Key key,
        ModifierKeys modifiers,
        FileBrowserShortcut expected)
    {
        Assert.Equal(expected, FileBrowserShortcutPolicy.Resolve(key, modifiers, false, false, focusInList: false));
        Assert.Equal(expected, FileBrowserShortcutPolicy.Resolve(key, modifiers, false, false, focusInList: true));
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(key, modifiers, false, focusInTextInput: true, focusInList: false));
    }

    [Theory]
    [InlineData(Key.D, ModifierKeys.Alt)]
    [InlineData(Key.F4, ModifierKeys.None)]
    public void Resolve_PathBarShortcutsLeaveATextBoxButNotTheEditorOverlay(Key key, ModifierKeys modifiers)
    {
        Assert.Equal(
            FileBrowserShortcut.FocusPath,
            FileBrowserShortcutPolicy.Resolve(key, modifiers, false, focusInTextInput: true, focusInList: false));
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(key, modifiers, inlineEditorOpen: true, false, false));
    }

    [Fact]
    public void Resolve_NothingIsHandledWhileTheInlineEditorIsOpen()
    {
        foreach (Key key in new[] { Key.F5, Key.F2, Key.Delete, Key.Enter, Key.Back, Key.F7 })
        {
            Assert.Equal(
                FileBrowserShortcut.None,
                FileBrowserShortcutPolicy.Resolve(key, ModifierKeys.None, inlineEditorOpen: true, false, true));
        }
    }

    [Fact]
    public void Resolve_TextBoxKeepsDeleteAndCopyForItsOwnText()
    {
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(Key.Delete, ModifierKeys.None, false, focusInTextInput: true, focusInList: false));
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(Key.C, ModifierKeys.Control, false, focusInTextInput: true, focusInList: false));
    }

    [Fact]
    public void Resolve_ControlLIsLeftToTheApplicationWhichLocksTheWorkspaceWithIt()
    {
        Assert.Equal(
            FileBrowserShortcut.None,
            FileBrowserShortcutPolicy.Resolve(Key.L, ModifierKeys.Control, false, false, focusInList: true));
    }
}
