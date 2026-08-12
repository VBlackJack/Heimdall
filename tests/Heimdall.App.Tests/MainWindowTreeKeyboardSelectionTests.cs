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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class MainWindowTreeKeyboardSelectionTests
{
    [Fact]
    public void CtrlSpace_TogglesFocusedServer()
    {
        ServerItemViewModel focused = CreateServer("focused");

        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            Key.Space,
            ModifierKeys.Control,
            focused,
            [focused]);

        Assert.True(handled);
        Assert.True(toggle);
        Assert.Same(focused, target);
    }

    [Theory]
    [InlineData(Key.Down, 2)]
    [InlineData(Key.Up, 0)]
    public void ShiftArrow_ExtendsInRequestedDirection(Key key, int expectedIndex)
    {
        ServerItemViewModel first = CreateServer("first");
        ServerItemViewModel focused = CreateServer("focused");
        ServerItemViewModel last = CreateServer("last");
        ServerItemViewModel[] visibleServers = [first, focused, last];

        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            key,
            ModifierKeys.Shift,
            focused,
            visibleServers);

        Assert.True(handled);
        Assert.False(toggle);
        Assert.Same(visibleServers[expectedIndex], target);
    }

    [Theory]
    [InlineData(Key.Up, 0)]
    [InlineData(Key.Down, 2)]
    public void ShiftArrow_AtBoundary_IsHandledWithoutMutation(Key key, int focusedIndex)
    {
        ServerItemViewModel[] visibleServers =
        [
            CreateServer("first"),
            CreateServer("middle"),
            CreateServer("last")
        ];

        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            key,
            ModifierKeys.Shift,
            visibleServers[focusedIndex],
            visibleServers);

        Assert.True(handled);
        Assert.False(toggle);
        Assert.Null(target);
    }

    [Theory]
    [InlineData(Key.Space, ModifierKeys.None)]
    [InlineData(Key.Space, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.Down, ModifierKeys.None)]
    [InlineData(Key.Down, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.Up, ModifierKeys.Control)]
    public void OtherModifierCombinations_AreNotHandled(Key key, ModifierKeys modifiers)
    {
        ServerItemViewModel focused = CreateServer("focused");

        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            key,
            modifiers,
            focused,
            [focused]);

        Assert.False(handled);
        Assert.False(toggle);
        Assert.Null(target);
    }

    [Fact]
    public void ShiftArrow_NonVisibleFocusedServer_IsNotHandled()
    {
        ServerItemViewModel focused = CreateServer("focused");

        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            Key.Down,
            ModifierKeys.Shift,
            focused,
            [CreateServer("visible")]);

        Assert.False(handled);
        Assert.False(toggle);
        Assert.Null(target);
    }

    [Fact]
    public void CtrlSpace_WithoutFocusedServer_IsNotHandled()
    {
        (bool handled, bool toggle, ServerItemViewModel? target) = Resolve(
            Key.Space,
            ModifierKeys.Control,
            null,
            []);

        Assert.False(handled);
        Assert.False(toggle);
        Assert.Null(target);
    }

    [Fact]
    public void HandledTargetWithoutContainer_IsConsumedWithoutMutation()
    {
        ServerItemViewModel target = CreateServer("target");
        bool toggled = false;
        bool extended = false;
        bool synchronized = false;
        bool consumed = MainWindow.ApplyTreeKeyboardSelection(
            handled: true,
            toggle: true,
            target,
            targetContainer: null,
            _ => toggled = true,
            _ => extended = true,
            _ => synchronized = true);

        Assert.True(consumed);
        Assert.False(toggled);
        Assert.False(extended);
        Assert.False(synchronized);
    }

    private static (bool Handled, bool Toggle, ServerItemViewModel? Target) Resolve(
        Key key,
        ModifierKeys modifiers,
        ServerItemViewModel? focusedServer,
        IReadOnlyList<ServerItemViewModel> visibleServers)
    {
        return MainWindow.ResolveTreeKeyboardSelection(
            key,
            modifiers,
            focusedServer,
            visibleServers);
    }

    private static ServerItemViewModel CreateServer(string id)
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH"
        });
    }
}
