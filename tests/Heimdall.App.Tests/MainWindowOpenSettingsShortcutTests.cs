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
/// Freezes the gesture that opens the Settings tab: Ctrl+comma, and only that.
/// </summary>
public sealed class MainWindowOpenSettingsShortcutTests
{
    [Fact]
    public void CtrlComma_OpensTheSettingsTab()
    {
        KeyboardShortcutService service = new();
        int opened = 0;
        MainWindow.RegisterOpenSettingsShortcut(service, () => opened++);

        bool handled = service.TryHandle(Key.OemComma, ModifierKeys.Control);

        Assert.True(handled);
        Assert.Equal(1, opened);
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    public void OtherCommaGestures_StayWithTheFocusedElement(ModifierKeys modifiers)
    {
        KeyboardShortcutService service = new();
        int opened = 0;
        MainWindow.RegisterOpenSettingsShortcut(service, () => opened++);

        bool handled = service.TryHandle(Key.OemComma, modifiers);

        Assert.False(handled);
        Assert.Equal(0, opened);
    }
}
