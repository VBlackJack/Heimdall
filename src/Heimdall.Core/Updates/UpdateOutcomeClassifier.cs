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
    /// <param name="failure">What the relauncher recorded, when it recorded anything.</param>
    /// <param name="runningVersion">The version actually running now.</param>
    public static UpdateRelaunchOutcome Classify(
        UpdateAttemptRecord? attempt,
        UpdateFailureRecord? failure,
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

        // The version check comes FIRST, before any failure record is consulted. That
        // ordering is the fail-safe, and it is what makes a false alarm require two
        // independent things to go wrong: an installer that reports an error after the
        // files were in fact replaced is a documented possibility, and the user looking
        // at the new version must not be told it failed.
        if (IsRunningTheAttemptedVersion(attempt.AttemptedVersion, runningVersion))
        {
            return UpdateRelaunchOutcome.Succeeded;
        }

        if (failure is null)
        {
            return UpdateRelaunchOutcome.NotApplied;
        }

        if (string.Equals(failure.Stage, UpdateOutcomeStage.IntegrityRejected, StringComparison.Ordinal))
        {
            return UpdateRelaunchOutcome.IntegrityRejected;
        }

        if (string.Equals(failure.Stage, UpdateOutcomeStage.ElevationDeclined, StringComparison.Ordinal))
        {
            return UpdateRelaunchOutcome.CancelledByUser;
        }

        if (string.Equals(failure.Stage, UpdateOutcomeStage.ApplicationStillRunning, StringComparison.Ordinal))
        {
            return UpdateRelaunchOutcome.ApplicationStillRunning;
        }

        if (failure.HasExitCode)
        {
            return InnoSetupExitCode.IsUserCancellation(failure.InstallerExitCode)
                ? UpdateRelaunchOutcome.CancelledByUser
                : UpdateRelaunchOutcome.InstallerFailed;
        }

        // A stage token this build does not recognize, or an exit code that could not be
        // read, falls back to the statement that is still true. An unknown token must
        // never throw and must never invent a cause.
        return UpdateRelaunchOutcome.NotApplied;
    }

    /// <summary>
    /// Whether the version now running is the one that was attempted.
    /// </summary>
    /// <remarks>
    /// Compared as versions when both sides parse, and only as text when they do not.
    /// Both sides originate from <see cref="HeimdallVersion"/>, whose equality is
    /// numeric; a comparison of their spellings would turn a successful update into a
    /// false "did not apply" the day one side gains a leading 'v' or a trailing
    /// component - which is the outcome the type remarks name as the one that must
    /// never occur.
    /// </remarks>
    private static bool IsRunningTheAttemptedVersion(string attemptedVersion, HeimdallVersion? runningVersion)
    {
        if (runningVersion is null)
        {
            return false;
        }

        if (HeimdallVersion.TryParse(attemptedVersion, out HeimdallVersion attempted))
        {
            return runningVersion.Value == attempted;
        }

        string running = runningVersion.Value.ToString();
        return !string.IsNullOrWhiteSpace(running)
            && string.Equals(running, attemptedVersion, StringComparison.OrdinalIgnoreCase);
    }
}
