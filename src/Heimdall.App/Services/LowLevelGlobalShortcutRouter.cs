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

namespace Heimdall.App.Services;

internal static class LowLevelGlobalShortcutRouter
{
    internal static bool ShouldRoute(Key key, ModifierKeys modifiers)
    {
        return (key == Key.Tab && modifiers == ModifierKeys.Control)
            || (key == Key.Tab && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            || (key == Key.O && modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
    }
}
