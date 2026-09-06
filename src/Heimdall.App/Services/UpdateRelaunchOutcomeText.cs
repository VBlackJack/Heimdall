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

using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Pure mapper from an <see cref="UpdateRelaunchOutcome"/> to its locale key, on the
/// same shape as <see cref="UpdateInstallOutcomeText"/>.
/// </summary>
public static class UpdateRelaunchOutcomeText
{
    /// <summary>
    /// Returns the locale key to show, or null when the outcome is not worth a word.
    /// </summary>
    /// <remarks>
    /// The wording it selects is a statement, never an accusation. An update can fail to
    /// apply because the installer failed, because the user declined the elevation
    /// prompt, or because the relauncher was killed - and from here those are
    /// indistinguishable. Saying what is true, and pointing at the log, is the most that
    /// can honestly be said.
    /// </remarks>
    public static string? StatusKey(UpdateRelaunchOutcome outcome) => outcome switch
    {
        UpdateRelaunchOutcome.None => null,
        UpdateRelaunchOutcome.Succeeded => null,
        UpdateRelaunchOutcome.NotApplied => "UpdateBannerOutcomeNotApplied",
        UpdateRelaunchOutcome.CancelledByUser => "UpdateBannerOutcomeCancelled",
        UpdateRelaunchOutcome.InstallerFailed => "UpdateBannerOutcomeInstallerFailed",
        UpdateRelaunchOutcome.IntegrityRejected => "UpdateBannerOutcomeIntegrityRejected",
        UpdateRelaunchOutcome.ApplicationStillRunning => "UpdateBannerOutcomeAppStillRunning",
        _ => null, // total, and silent: an unknown outcome must not invent a message.
    };
}
