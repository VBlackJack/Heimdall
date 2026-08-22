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

namespace Heimdall.Core.Updates;

/// <summary>
/// Decides what a pending attempt record means, given the version now running.
/// </summary>
/// <remarks>
/// One predicate, shared by whatever reports the outcome and by the tests that pin it,
/// rather than a copy on each side. Two surfaces holding independent copies of one rule
/// is this codebase's recurring defect shape.
/// </remarks>
public static class UpdateOutcomeClassifier
{
    /// <summary>
    /// Classifies a pending attempt.
    /// </summary>
    /// <remarks>
    /// The fail-safe direction is fixed and deliberate: whenever the running version is
    /// the one that was attempted, the answer is <see cref="UpdateRelaunchOutcome.Succeeded"/>
    /// and nothing is said. Telling someone their update failed while they are looking at
    /// the new version would destroy confidence in the feature faster than the silence
    /// this fixes, so a false report has to survive the version check first.
    /// </remarks>
    /// <param name="attempt">The pending record, or null when none was found.</param>
    /// <param name="runningVersion">The version actually running now.</param>
    public static UpdateRelaunchOutcome Classify(
        UpdateAttemptRecord? attempt,
        HeimdallVersion? runningVersion)
    {
        if (attempt is null)
        {
            return UpdateRelaunchOutcome.None;
        }

        // An attempt that never named a version cannot be compared against anything, so
        // it is discarded rather than reported. Silence beats a claim we cannot support.
        if (string.IsNullOrWhiteSpace(attempt.AttemptedVersion))
        {
            return UpdateRelaunchOutcome.None;
        }

        string? running = runningVersion?.ToString();
        if (!string.IsNullOrWhiteSpace(running)
            && string.Equals(running, attempt.AttemptedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateRelaunchOutcome.Succeeded;
        }

        return UpdateRelaunchOutcome.NotApplied;
    }
}
