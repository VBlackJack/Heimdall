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

using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// Makes sure a launched product window is in front before a test sends global keyboard input.
/// </summary>
/// <remarks>
/// Keys sent through the keyboard go to whatever window Windows has in front, and Windows may
/// refuse the launched app the foreground (a run of the whole suite on a developer desktop does).
/// A test that typed anyway would send its keys, an Enter included, to another window, possibly
/// the developer's own shell. The guard fails the test instead.
/// </remarks>
public static class ForegroundGuard
{
    private const string ForegroundDescription = "the product window to be the foreground window (keys are not sent otherwise)";
    private const string User32Library = "user32.dll";

    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Brings <paramref name="window"/> to the front and fails when it does not get there.</summary>
    public static void Ensure(Window window)
    {
        window.SetForeground();
        var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
        Assert.True(handle != 0, "The product window has no native handle.");
        WaitHelpers.WaitUntil(
            () => GetForegroundWindow().ToInt64() == handle,
            ForegroundDescription,
            ForegroundTimeout);
    }

    [DllImport(User32Library)]
    private static extern IntPtr GetForegroundWindow();
}
