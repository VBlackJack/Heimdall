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

using Heimdall.App.Localization;
using Heimdall.Core.Localization;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Composes the one status line a native OpenSSH launch shows: the capability limit, if any, and
/// the count of port-forward or environment entries ssh was not given.
/// </summary>
internal static class OpenSshLaunchStatusText
{
    /// <summary>Returns the combined text, or null when there is nothing to tell the user.</summary>
    internal static string? Compose(
        LocalizationManager localizer,
        SshCapabilityNotice? capabilityNotice,
        int droppedEntryCount)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        string? capabilityText = capabilityNotice is null
            ? null
            : localizer[capabilityNotice.StatusLocalizationKey];
        string? droppedText = droppedEntryCount > 0
            ? localizer.FormatCount(
                droppedEntryCount,
                SshLocalizationKeys.StatusOpenSshEntriesNotAppliedOne,
                SshLocalizationKeys.StatusOpenSshEntriesNotApplied,
                droppedEntryCount)
            : null;

        return (capabilityText, droppedText) switch
        {
            (null, null) => null,
            (not null, null) => capabilityText,
            (null, not null) => droppedText,
            _ => capabilityText + " " + droppedText,
        };
    }
}
