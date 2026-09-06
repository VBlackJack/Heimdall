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

/// <summary>How far the relauncher got before it gave up.</summary>
/// <remarks>
/// A closed vocabulary of tokens, defined once and read by both sides: the generated
/// script writes one of these, the application maps it to a sentence. Two copies of a
/// vocabulary is how the writer and the reader drift into disagreeing.
/// </remarks>
public static class UpdateOutcomeStage
{
    /// <summary>Failed before the installer was reached at all.</summary>
    public const string Preparation = "Preparation";

    /// <summary>The installer was rejected by the signature or hash check.</summary>
    public const string IntegrityRejected = "IntegrityRejected";

    /// <summary>The installer was launched; its outcome was not yet known.</summary>
    public const string InstallerLaunch = "InstallerLaunch";

    /// <summary>The installer ran and reported a non-zero exit code.</summary>
    public const string InstallerExit = "InstallerExit";

    /// <summary>
    /// The application never exited within the wait, so the installer was not run: it
    /// would have force-closed a live session instead of updating it.
    /// </summary>
    public const string ApplicationStillRunning = "ApplicationStillRunning";

    /// <summary>
    /// The user declined the elevation prompt. The installer never started, so no
    /// exit code exists to say so; the launch itself reported the refusal.
    /// </summary>
    public const string ElevationDeclined = "ElevationDeclined";
}

/// <summary>
/// What the relauncher recorded about a failure, written by the generated script.
/// </summary>
/// <remarks>
/// Every field is produced without escaping: a token from a closed vocabulary and two
/// integers. That is deliberate. The script builds this JSON by string concatenation in
/// PowerShell, and a field that could contain arbitrary text - an exception message, a
/// path - is a field that can produce malformed JSON on the one run that matters. The
/// exception detail belongs in the transcript, which is now in the directory the About
/// panel names.
/// </remarks>
/// <param name="SchemaVersion">
/// Guards against reading a record written by a future version with different meaning.
/// </param>
/// <param name="Stage">One of <see cref="UpdateOutcomeStage"/>.</param>
/// <param name="InstallerExitCode">
/// The installer's exit code. Meaningless unless <paramref name="InstallerExitCodeKnown"/>
/// is 1.
/// </param>
/// <param name="InstallerExitCodeKnown">
/// 1 when the exit code was actually read, 0 otherwise. Two integers rather than a
/// nullable field, so the emitted JSON is always well formed and no PowerShell null ever
/// concatenates into it - and so an unreadable code is representable without overloading
/// a sentinel that a genuinely negative crash code could collide with.
/// </param>
public sealed record UpdateFailureRecord(
    int SchemaVersion,
    string Stage,
    int InstallerExitCode,
    int InstallerExitCodeKnown)
{
    /// <summary>The schema this build writes and is willing to read.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>True when the exit code carries meaning.</summary>
    public bool HasExitCode => InstallerExitCodeKnown == 1;
}
