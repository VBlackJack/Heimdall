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

namespace Heimdall.App.Views;

/// <summary>
/// Whether a file browser may act on a keyboard shortcut right now.
/// </summary>
/// <remarks>
/// Two ways the browser used to steal keys. Under the inline editor overlay its shortcuts stayed
/// live: F2 opened the rename of a hidden row, F5 refreshed, Ctrl+F swallowed the keystroke. And
/// with a text box focused, Delete, F2, Enter, Ctrl+C and Ctrl+V went to the row rather than to
/// the text: Delete in the filter box opened the deletion of the selected file.
/// </remarks>
public static class FileBrowserShortcutPolicy
{
    public static bool ShouldHandleShortcut(bool inlineEditorOpen, bool focusInTextInput)
        => !inlineEditorOpen && !focusInTextInput;
}
