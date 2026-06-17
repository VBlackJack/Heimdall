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

using System;
using System.Threading.Tasks;
using TwinShell.Core.Enums;

namespace Heimdall.App.Services;

/// <summary>
/// Shared decision rule for confirming the execution of commands flagged as
/// dangerous. Centralizing it here lets every call site that <i>executes</i> a
/// command (Command Library Send, Command Palette snippet send) apply the same
/// confirmation policy without duplicating the threshold logic.
/// </summary>
public static class DangerousCommandGuard
{
    /// <summary>
    /// Decides whether a command at the given criticality level may proceed.
    /// <see cref="CriticalityLevel.Info"/> and <see cref="CriticalityLevel.Run"/>
    /// proceed with zero friction; <see cref="CriticalityLevel.Dangerous"/> asks
    /// the user to confirm first.
    /// </summary>
    /// <param name="level">The criticality of the command about to run.</param>
    /// <param name="dialog">The dialog service used to prompt for confirmation.</param>
    /// <param name="localize">Resolves an i18n key to its localized string.</param>
    /// <param name="topmost">
    /// When <c>true</c>, the confirmation renders above a topmost popup (e.g. the
    /// Ctrl+K command palette); otherwise it uses the default window stacking.
    /// </param>
    /// <returns>
    /// <c>true</c> when the command may proceed (below the dangerous threshold,
    /// or confirmed by the user); <c>false</c> when the user declined.
    /// </returns>
    /// <remarks>
    /// The threshold is evaluated with <c>&gt;=</c> rather than <c>==</c> so that
    /// any unexpected value above <see cref="CriticalityLevel.Dangerous"/> also
    /// prompts instead of silently failing open. This keeps the rule total and
    /// fail-safe over the whole <see cref="CriticalityLevel"/> domain.
    /// </remarks>
    public static async Task<bool> ConfirmIfDangerousAsync(
        CriticalityLevel level,
        IDialogService dialog,
        Func<string, string> localize,
        bool topmost = false)
    {
        if (level < CriticalityLevel.Dangerous)
        {
            return true;
        }

        return await dialog.ShowConfirmAsync(
            localize("ConfirmDangerousSendTitle"),
            localize("ConfirmDangerousSendMessage"),
            "warning",
            topmost);
    }
}
