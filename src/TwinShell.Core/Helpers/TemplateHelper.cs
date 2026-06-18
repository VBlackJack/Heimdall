/*
 * Copyright 2025 Julien Bombled
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

using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Helper class for command template operations.
/// </summary>
public static class TemplateHelper
{
    /// <summary>
    /// Gets the active command template for an action based on the current platform.
    /// Returns the template matching the current platform, with fallback logic.
    /// </summary>
    /// <param name="action">The action to get the template from</param>
    /// <returns>The active template, or null if no template is available</returns>
    public static CommandTemplate? GetActiveTemplate(ActionModel action)
    {
        if (action == null)
            return null;

        // BUGFIX: Use PlatformHelper to get the current platform instead of hardcoding preference
        var currentPlatform = PlatformHelper.GetCurrentPlatform();

        // Return the template for the current platform, with fallback to the other platform
        if (currentPlatform == Platform.Windows)
        {
            return action.WindowsCommandTemplate ?? action.LinuxCommandTemplate;
        }
        else
        {
            return action.LinuxCommandTemplate ?? action.WindowsCommandTemplate;
        }
    }

    /// <summary>
    /// Gets the platform for a given command template relative to an action.
    /// </summary>
    /// <param name="action">The action containing the templates</param>
    /// <param name="template">The template to check</param>
    /// <returns>The platform (Windows or Linux)</returns>
    public static Platform GetPlatformForTemplate(ActionModel action, CommandTemplate template)
    {
        // BUGFIX: Specify parameter name in ArgumentNullException
        if (action == null)
            throw new ArgumentNullException(nameof(action));
        if (template == null)
            throw new ArgumentNullException(nameof(template));

        return template == action.WindowsCommandTemplate ? Platform.Windows : Platform.Linux;
    }

    /// <summary>
    /// Determines if a template is valid (not null and has parameters or base command).
    /// </summary>
    /// <param name="template">The template to validate</param>
    /// <returns>True if the template is valid</returns>
    public static bool IsValidTemplate(CommandTemplate? template)
    {
        return template != null && !string.IsNullOrWhiteSpace(template.CommandPattern);
    }

    /// <summary>
    /// Determines if an action is cross-platform (supports both Windows and Linux with valid templates).
    /// </summary>
    /// <param name="action">The action to check</param>
    /// <returns>True if the action has valid templates for both platforms</returns>
    public static bool IsCrossPlatform(ActionModel? action)
    {
        return action != null &&
               action.Platform == Platform.Both &&
               action.WindowsCommandTemplate != null &&
               action.LinuxCommandTemplate != null;
    }

    /// <summary>
    /// Gets the appropriate template for the specified platform selection.
    /// For cross-platform actions, returns the template matching the selected platform.
    /// For single-platform actions, uses the default platform logic.
    /// </summary>
    /// <param name="action">The action containing templates</param>
    /// <param name="selectedPlatform">The platform selected by the user</param>
    /// <param name="isCrossPlatform">Whether the action is cross-platform</param>
    /// <returns>The appropriate command template</returns>
    public static CommandTemplate? GetTemplateForPlatform(
        ActionModel? action,
        Platform selectedPlatform,
        bool isCrossPlatform)
    {
        if (action == null)
            return null;

        if (isCrossPlatform)
        {
            return selectedPlatform == Platform.Windows
                ? action.WindowsCommandTemplate
                : action.LinuxCommandTemplate;
        }

        return GetActiveTemplate(action);
    }
}
