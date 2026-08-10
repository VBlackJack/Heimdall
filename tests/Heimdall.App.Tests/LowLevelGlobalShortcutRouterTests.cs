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

public sealed class LowLevelGlobalShortcutRouterTests
{
    [Theory]
    [InlineData(Key.Tab, ModifierKeys.Control)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.O, ModifierKeys.Control | ModifierKeys.Shift)]
    public void ShouldRoute_ApprovedShortcut_ReturnsTrue(
        Key key,
        ModifierKeys modifiers)
    {
        bool shouldRoute = LowLevelGlobalShortcutRouter.ShouldRoute(key, modifiers);

        Assert.True(shouldRoute);
    }

    [Theory]
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.Tab, ModifierKeys.Shift)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)]
    [InlineData(Key.O, ModifierKeys.Control)]
    [InlineData(Key.O, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)]
    [InlineData(Key.K, ModifierKeys.Control)]
    [InlineData(Key.L, ModifierKeys.Control)]
    [InlineData(Key.F11, ModifierKeys.None)]
    public void ShouldRoute_UnapprovedShortcut_ReturnsFalse(
        Key key,
        ModifierKeys modifiers)
    {
        bool shouldRoute = LowLevelGlobalShortcutRouter.ShouldRoute(key, modifiers);

        Assert.False(shouldRoute);
    }

    [Fact]
    public void TryHandle_ExplicitApprovedTuple_UsesExistingRegistration()
    {
        KeyboardShortcutService service = new();
        int invocationCount = 0;
        service.Register(
            Key.Tab,
            ModifierKeys.Control,
            () => invocationCount++);

        bool handled = service.TryHandle(Key.Tab, ModifierKeys.Control);

        Assert.True(handled);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void TryHandle_ExplicitTupleWithExtraModifier_DoesNotDispatch()
    {
        KeyboardShortcutService service = new();
        int invocationCount = 0;
        service.Register(
            Key.Tab,
            ModifierKeys.Control,
            () => invocationCount++);

        bool handled = service.TryHandle(
            Key.Tab,
            ModifierKeys.Control | ModifierKeys.Alt);

        Assert.False(handled);
        Assert.Equal(0, invocationCount);
    }
}
