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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>The bookkeeping behind hiding the floating windows while the workspace is locked.</summary>
public sealed class LockHiddenWindowsTests
{
    private sealed class FakeWindow(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void BeginLock_ReturnsTheVisibleWindowsOnce()
    {
        LockHiddenWindows<FakeWindow> held = new();
        FakeWindow first = new("first");
        FakeWindow second = new("second");

        IReadOnlyList<FakeWindow> hidden = held.BeginLock([first, second]);

        Assert.Equal([first, second], hidden);
        Assert.True(held.IsLocked);
        Assert.True(held.IsHeld(first));
        Assert.Empty(held.BeginLock([first, second]));
    }

    [Fact]
    public void EndLock_ReturnsTheHeldWindowsThatAreStillOpen()
    {
        LockHiddenWindows<FakeWindow> held = new();
        FakeWindow stays = new("stays");
        FakeWindow closed = new("closed");
        held.BeginLock([stays, closed]);

        IReadOnlyList<FakeWindow> toShow = held.EndLock([stays]);

        Assert.Equal([stays], toShow);
        Assert.False(held.IsLocked);
        Assert.False(held.IsHeld(stays));
        Assert.Empty(held.EndLock([stays]));
    }

    [Fact]
    public void AWindowOpenedWhileLocked_IsHeldAndRevealedByUnlock()
    {
        LockHiddenWindows<FakeWindow> held = new();
        FakeWindow late = new("late");
        held.BeginLock([]);

        Assert.True(held.RegisterNewWindow(late));
        Assert.True(held.IsHeld(late));
        Assert.False(held.RegisterNewWindow(late));

        Assert.Equal([late], held.EndLock([late]));
    }

    [Fact]
    public void AWindowOpenedWhileUnlocked_IsNotHeld()
    {
        LockHiddenWindows<FakeWindow> held = new();
        FakeWindow window = new("window");

        Assert.False(held.RegisterNewWindow(window));
        Assert.False(held.IsHeld(window));
    }

    [Fact]
    public void AForgottenWindow_IsNotShownAgain()
    {
        LockHiddenWindows<FakeWindow> held = new();
        FakeWindow window = new("window");
        held.BeginLock([window]);

        held.Forget(window);

        Assert.Empty(held.EndLock([window]));
    }
}
