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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The session host is collapsed while a WPF overlay needs the space its child windows would
/// otherwise paint over.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void SessionHost_IsVisibleOnlyWhenNothingCoversIt(bool locked, bool onboarding, bool dragging, bool expected)
    {
        using TestHarness harness = TestHarness.Create();
        harness.Main.IsWorkspaceLocked = locked;
        harness.Main.IsOnboardingActive = onboarding;
        harness.Main.IsRdpImportDragActive = dragging;

        Assert.Equal(expected, harness.Main.IsSessionHostVisible);
    }

    [Theory]
    [InlineData(nameof(MainViewModel.IsWorkspaceLocked))]
    [InlineData(nameof(MainViewModel.IsOnboardingActive))]
    [InlineData(nameof(MainViewModel.IsRdpImportDragActive))]
    public void SessionHost_RaisesItsChangeWhenAnyCoveringFlagChanges(string flag)
    {
        using TestHarness harness = TestHarness.Create();
        List<string?> raised = [];
        harness.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        switch (flag)
        {
            case nameof(MainViewModel.IsWorkspaceLocked):
                harness.Main.IsWorkspaceLocked = true;
                break;
            case nameof(MainViewModel.IsOnboardingActive):
                harness.Main.IsOnboardingActive = true;
                break;
            default:
                harness.Main.IsRdpImportDragActive = true;
                break;
        }

        Assert.Contains(nameof(MainViewModel.IsSessionHostVisible), raised);
    }
}
