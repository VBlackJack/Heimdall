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

/// <summary>The keyboard shortcuts of the embedded editor.</summary>
public enum EditorShortcutAction
{
    None,
    Save,
    Close,
}

/// <summary>Classifies a key press in the embedded editor.</summary>
/// <remarks>
/// Ctrl+S and Ctrl+W, the two an editor is expected to answer; the editor had a Save and a Close
/// button and no key for either.
/// </remarks>
public static class EditorShortcut
{
    public static EditorShortcutAction Classify(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control)
        {
            return EditorShortcutAction.None;
        }

        return key switch
        {
            Key.S => EditorShortcutAction.Save,
            Key.W => EditorShortcutAction.Close,
            _ => EditorShortcutAction.None,
        };
    }
}
