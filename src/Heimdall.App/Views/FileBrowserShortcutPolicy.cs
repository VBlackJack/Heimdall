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

namespace Heimdall.App.Views;

/// <summary>An action a file-browser keyboard shortcut stands for.</summary>
public enum FileBrowserShortcut
{
    /// <summary>The key is not a shortcut here, or the focus does not allow it.</summary>
    None,

    /// <summary>Reload the current folder.</summary>
    Refresh,

    /// <summary>Move the focus to the filter box.</summary>
    FocusFilter,

    /// <summary>Move the focus to the path bar.</summary>
    FocusPath,

    /// <summary>Create a folder in the current one.</summary>
    NewFolder,

    /// <summary>Download the selection.</summary>
    Download,

    /// <summary>Upload local files into the current folder.</summary>
    Upload,

    /// <summary>Go back in the navigation history.</summary>
    NavigateBack,

    /// <summary>Stop a folder listing that is still running.</summary>
    CancelLoad,

    /// <summary>Rename the selected entry.</summary>
    Rename,

    /// <summary>Delete the selected entries.</summary>
    Delete,

    /// <summary>Open the selected entry.</summary>
    Open,

    /// <summary>Go to the parent folder.</summary>
    ParentFolder,

    /// <summary>Cut the selected entries.</summary>
    Cut,

    /// <summary>Copy the selected entries.</summary>
    Copy,

    /// <summary>Paste the clipboard into the current folder.</summary>
    Paste,

    /// <summary>Copy the path of the selected entry as text.</summary>
    CopyPath,
}

/// <summary>
/// Whether a file browser may act on a keyboard shortcut right now, and which one a key is.
/// </summary>
/// <remarks>
/// <para>
/// Three ways the browser used to steal keys. Under the inline editor overlay its shortcuts stayed
/// live: F2 opened the rename of a hidden row, F5 refreshed, Ctrl+F swallowed the keystroke. With a
/// text box focused, Delete, F2, Enter, Ctrl+C and Ctrl+V went to the row rather than to the text:
/// Delete in the filter box opened the deletion of the selected file. And the keys that act on the
/// selection (Delete, F2, Enter, Backspace, cut, copy, paste) were also live when the focus sat on
/// a check box or a toolbar button, so Tab to the "hidden files" box and Delete opened the deletion
/// of the selected file.
/// </para>
/// <para>
/// The keys that act on the selection are therefore honoured only while the focus is in the list
/// itself. Ctrl+L is absent on purpose: the application owns it (lock the workspace), so the path
/// bar takes F4 and Alt+D, the Explorer pair.
/// </para>
/// </remarks>
public static class FileBrowserShortcutPolicy
{
    /// <summary>Whether the browser may act on a shortcut, ignoring where the focus sits within it.</summary>
    public static bool ShouldHandleShortcut(bool inlineEditorOpen, bool focusInTextInput)
        => !inlineEditorOpen && !focusInTextInput;

    /// <summary>Resolves a key press to the shortcut it stands for in the current focus context.</summary>
    /// <param name="key">The pressed key, with a system key already replaced by its real key.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <param name="inlineEditorOpen">Whether the inline editor overlay covers the browser.</param>
    /// <param name="focusInTextInput">Whether a text box holds the focus.</param>
    /// <param name="focusInList">Whether the focus is inside the file list.</param>
    public static FileBrowserShortcut Resolve(
        Key key,
        ModifierKeys modifiers,
        bool inlineEditorOpen,
        bool focusInTextInput,
        bool focusInList)
    {
        if (inlineEditorOpen)
        {
            return FileBrowserShortcut.None;
        }

        // Alt+D and F4 jump to the path bar from anywhere, a text box included: it is the one
        // shortcut whose whole point is to leave wherever the focus is.
        if ((key == Key.D && modifiers == ModifierKeys.Alt)
            || (key == Key.F4 && modifiers == ModifierKeys.None))
        {
            return FileBrowserShortcut.FocusPath;
        }

        if (focusInTextInput)
        {
            return FileBrowserShortcut.None;
        }

        FileBrowserShortcut anywhere = key switch
        {
            Key.F5 when modifiers == ModifierKeys.None => FileBrowserShortcut.Refresh,
            Key.F when modifiers == ModifierKeys.Control => FileBrowserShortcut.FocusFilter,
            Key.F7 when modifiers == ModifierKeys.None => FileBrowserShortcut.NewFolder,
            Key.D when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => FileBrowserShortcut.Download,
            Key.U when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => FileBrowserShortcut.Upload,
            Key.Left when modifiers == ModifierKeys.Alt => FileBrowserShortcut.NavigateBack,
            Key.Up when modifiers == ModifierKeys.Alt => FileBrowserShortcut.ParentFolder,
            Key.Escape when modifiers == ModifierKeys.None => FileBrowserShortcut.CancelLoad,
            _ => FileBrowserShortcut.None,
        };
        if (anywhere != FileBrowserShortcut.None || !focusInList)
        {
            return anywhere;
        }

        return key switch
        {
            Key.F2 when modifiers == ModifierKeys.None => FileBrowserShortcut.Rename,
            Key.Delete when modifiers == ModifierKeys.None => FileBrowserShortcut.Delete,
            Key.Enter when modifiers == ModifierKeys.None => FileBrowserShortcut.Open,
            Key.Back when modifiers == ModifierKeys.None => FileBrowserShortcut.ParentFolder,
            Key.X when modifiers == ModifierKeys.Control => FileBrowserShortcut.Cut,
            Key.C when modifiers == ModifierKeys.Control => FileBrowserShortcut.Copy,
            Key.V when modifiers == ModifierKeys.Control => FileBrowserShortcut.Paste,
            Key.C when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => FileBrowserShortcut.CopyPath,
            _ => FileBrowserShortcut.None,
        };
    }
}
