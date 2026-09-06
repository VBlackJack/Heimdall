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

namespace Heimdall.App.Services;

/// <summary>
/// Pure mapper from an <see cref="UpdateInstallOutcome"/> to its status locale key, shared by the
/// settings screen and the update banner.
/// </summary>
public static class UpdateInstallOutcomeText
{
    /// <summary>
    /// Returns the locale key for the status message of a non-success outcome, or null for
    /// <see cref="UpdateInstallOutcome.Started"/> (the app is shutting down, no message needed).
    /// </summary>
    public static string? StatusKey(UpdateInstallOutcome outcome) => outcome switch
    {
        UpdateInstallOutcome.Started => null,
        UpdateInstallOutcome.InstallLaunchFailed => "SettingsUpdateStatusInstallFailed",
        UpdateInstallOutcome.Cancelled => "SettingsUpdateStatusCancelled",
        UpdateInstallOutcome.VerificationFailed => "SettingsUpdateStatusVerificationFailed",
        UpdateInstallOutcome.DownloadFailed => "SettingsUpdateStatusDownloadFailed",

        // Total, and silent: an unknown outcome must not invent a cause. The default arm
        // used to say "Download failed." for any member added later, on both surfaces.
        _ => null,
    };
}
