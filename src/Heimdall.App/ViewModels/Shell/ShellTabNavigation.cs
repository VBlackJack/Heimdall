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

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// What the shell should do about a requested tab change.
/// </summary>
public enum ShellTabNavigationDecision
{
    /// <summary>Navigate now.</summary>
    Apply,

    /// <summary>
    /// Stay on Settings and ask what to do with the unsaved changes. Navigation to the requested
    /// tab happens only once that question has an answer.
    /// </summary>
    PromptBeforeLeavingSettings,
}

/// <summary>
/// Decides whether a requested tab change may proceed.
/// </summary>
/// <remarks>
/// Extracted from the shell view model so the rule can be exercised on its own. Inside the view
/// model it was reachable only by constructing the whole shell - a view model, eleven child view
/// models and about twenty collaborators - which is why the four conditions that make it up had
/// never been tested apart from one another.
/// </remarks>
public static class ShellTabNavigation
{
    /// <summary>
    /// Returns whether the shell may navigate, or must first ask about unsaved settings.
    /// </summary>
    /// <param name="previousTab">The tab being left.</param>
    /// <param name="requestedTab">The tab being requested.</param>
    /// <param name="settingsHaveUnsavedChanges">Whether the settings editor is dirty.</param>
    /// <param name="guardSuppressed">
    /// Whether the shell is itself driving this change. The prompt path navigates twice - once to
    /// put Settings back, once to reach the requested tab - and both of those would re-enter this
    /// rule. Without the suppression the revert would prompt about the prompt.
    /// </param>
    public static ShellTabNavigationDecision Decide(
        string previousTab,
        string requestedTab,
        bool settingsHaveUnsavedChanges,
        bool guardSuppressed)
    {
        if (guardSuppressed)
        {
            return ShellTabNavigationDecision.Apply;
        }

        // Only leaving Settings is guarded. Dirty settings do not block navigation between other
        // tabs, and a change from Settings to Settings is not a departure.
        if (!string.Equals(previousTab, ShellTab.Settings, StringComparison.Ordinal)
            || string.Equals(requestedTab, ShellTab.Settings, StringComparison.Ordinal))
        {
            return ShellTabNavigationDecision.Apply;
        }

        return settingsHaveUnsavedChanges
            ? ShellTabNavigationDecision.PromptBeforeLeavingSettings
            : ShellTabNavigationDecision.Apply;
    }
}
