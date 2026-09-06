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

using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// Under the inline editor overlay the browser's shortcuts stayed live (F2 renamed a hidden row,
/// F5 refreshed, Ctrl+F swallowed the keystroke), and with a text box focused Delete, F2, Enter,
/// Ctrl+C and Ctrl+V went to the row instead of the text.
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
}
