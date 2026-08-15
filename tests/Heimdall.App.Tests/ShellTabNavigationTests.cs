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

using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// Exercises the four conditions of the tab-change rule one at a time.
/// </summary>
/// <remarks>
/// Inside the shell view model this rule was reachable only by constructing the whole shell, so
/// each condition had only ever been covered together with the other three. Every test here holds
/// three of them fixed and moves the fourth.
/// </remarks>
public sealed class ShellTabNavigationTests
{
    [Fact]
    public void LeavingDirtySettings_Prompts()
    {
        Assert.Equal(
            ShellTabNavigationDecision.PromptBeforeLeavingSettings,
            ShellTabNavigation.Decide(
                ShellTab.Settings,
                ShellTab.About,
                settingsHaveUnsavedChanges: true,
                guardSuppressed: false));
    }

    [Fact]
    public void LeavingCleanSettings_Applies()
    {
        Assert.Equal(
            ShellTabNavigationDecision.Apply,
            ShellTabNavigation.Decide(
                ShellTab.Settings,
                ShellTab.About,
                settingsHaveUnsavedChanges: false,
                guardSuppressed: false));
    }

    [Fact]
    public void LeavingAnyOtherTab_Applies_EvenWithUnsavedSettings()
    {
        // Dirtiness gates leaving Settings, nothing else. Without this condition the shell would
        // hold the operator on the Sessions tab over edits made somewhere they cannot see.
        Assert.Equal(
            ShellTabNavigationDecision.Apply,
            ShellTabNavigation.Decide(
                ShellTab.Sessions,
                ShellTab.Tunnels,
                settingsHaveUnsavedChanges: true,
                guardSuppressed: false));
    }

    [Fact]
    public void StayingOnSettings_Applies_EvenWithUnsavedChanges()
    {
        // Settings to Settings is not a departure. This is the case the prompt path itself
        // produces when it puts Settings back, so treating it as a departure would ask about the
        // unsaved changes a second time in answer to the first question.
        Assert.Equal(
            ShellTabNavigationDecision.Apply,
            ShellTabNavigation.Decide(
                ShellTab.Settings,
                ShellTab.Settings,
                settingsHaveUnsavedChanges: true,
                guardSuppressed: false));
    }

    [Fact]
    public void WhenTheShellIsDrivingTheChange_Applies()
    {
        // The suppression flag exists because the prompt path navigates twice, and both of those
        // re-enter this rule. Losing it makes the revert prompt about the prompt.
        Assert.Equal(
            ShellTabNavigationDecision.Apply,
            ShellTabNavigation.Decide(
                ShellTab.Settings,
                ShellTab.About,
                settingsHaveUnsavedChanges: true,
                guardSuppressed: true));
    }

    [Fact]
    public void TabIdentityIsOrdinal_SoADifferentlyCasedNameIsADifferentTab()
    {
        // These are internal identifiers, never user-facing text, and every comparison in the
        // shell is ordinal. A culture-sensitive comparison slipped in here would make the guard
        // fire for a tab that no other site in the shell recognises.
        Assert.Equal(
            ShellTabNavigationDecision.Apply,
            ShellTabNavigation.Decide(
                "settings",
                ShellTab.About,
                settingsHaveUnsavedChanges: true,
                guardSuppressed: false));
    }

    [Fact]
    public void EveryTabIdentifierIsDistinct_AndAllListsThemAll()
    {
        // Two identifiers sharing a value would make one tab unreachable while every comparison
        // still compiled.
        Assert.Equal(ShellTab.All.Count, ShellTab.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, ShellTab.All.Count);
        Assert.Contains(ShellTab.Settings, ShellTab.All);
        Assert.All(ShellTab.All, identifier => Assert.False(string.IsNullOrWhiteSpace(identifier)));
    }
}
