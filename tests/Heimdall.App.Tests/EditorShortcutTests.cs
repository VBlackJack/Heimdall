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

public sealed class EditorShortcutTests
{
    [Theory]
    [InlineData(Key.S, ModifierKeys.Control, EditorShortcutAction.Save)]
    [InlineData(Key.W, ModifierKeys.Control, EditorShortcutAction.Close)]
    [InlineData(Key.S, ModifierKeys.None, EditorShortcutAction.None)]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, EditorShortcutAction.None)]
    [InlineData(Key.Escape, ModifierKeys.None, EditorShortcutAction.None)]
    [InlineData(Key.F5, ModifierKeys.Control, EditorShortcutAction.None)]
    public void Classify_AnswersCtrlSAndCtrlWOnly(Key key, ModifierKeys modifiers, EditorShortcutAction expected)
    {
        Assert.Equal(expected, EditorShortcut.Classify(key, modifiers));
    }
}
