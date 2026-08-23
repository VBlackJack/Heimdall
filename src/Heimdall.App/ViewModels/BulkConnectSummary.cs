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

namespace Heimdall.App.ViewModels;

/// <summary>
/// What became of every server a bulk connect was asked to handle.
/// </summary>
/// <param name="Selected">How many servers the user selected.</param>
/// <param name="Connected">How many opened a session.</param>
/// <param name="Failed">How many were attempted and did not.</param>
/// <param name="Skipped">How many were never attempted for a known reason.</param>
/// <param name="Cancelled">Whether the run stopped before reaching the end.</param>
/// <remarks>
/// <see cref="NotAttempted"/> is derived rather than counted, and that is the whole point:
/// a term computed from the selection cannot be forgotten by a branch that increments
/// nothing. The defect this type exists for was exactly that - cancelling a bulk left its
/// remaining servers in no counter at all, so three servers cancelled after the first were
/// reported as "connected 1, failed 0, skipped 0", and a green test asserted it.
/// </remarks>
internal readonly record struct BulkConnectTally(
    int Selected,
    int Connected,
    int Failed,
    int Skipped,
    bool Cancelled)
{
    /// <summary>
    /// Servers the run never reached. Zero unless the run was cut short.
    /// </summary>
    /// <remarks>
    /// Clamped at zero rather than allowed to go negative. A negative value would mean the
    /// counters disagree with the selection, which is a defect in the caller, not something
    /// to render to a user as a negative number of servers.
    /// </remarks>
    public int NotAttempted => Math.Max(0, Selected - Connected - Failed - Skipped);

    /// <summary>
    /// Whether the summary must say the run was cut short, rather than merely report counts.
    /// </summary>
    /// <remarks>
    /// A cancelled run with nothing left to attempt - the user cancelled on the last server
    /// - has no missing servers to explain, so the plain summary is already complete and
    /// truthful. Saying "cancelled, 0 not attempted" there would be noise.
    /// </remarks>
    public bool NeedsCancellationNotice => Cancelled && NotAttempted > 0;
}

/// <summary>
/// Builds the end-of-run tally for a bulk connect.
/// </summary>
internal static class BulkConnectSummary
{
    /// <summary>Tallies one run.</summary>
    /// <param name="selected">Servers the user selected, skipped ones included.</param>
    /// <param name="connected">Servers that opened a session.</param>
    /// <param name="failed">Servers attempted without success.</param>
    /// <param name="skipped">Servers never attempted, for a reason already known.</param>
    /// <param name="cancelled">Whether the run stopped early.</param>
    internal static BulkConnectTally Describe(
        int selected,
        int connected,
        int failed,
        int skipped,
        bool cancelled)
        => new(selected, connected, failed, skipped, cancelled);

    /// <summary>
    /// Which message a run that found nothing connectable should show.
    /// </summary>
    /// <param name="skippedCount">Selected servers that were skipped before any attempt.</param>
    /// <remarks>
    /// <b>Shared because three sites decide it.</b> Two in the bulk view model and one in
    /// the context menu each carried their own copy of "say nothing to connect", and each
    /// discarded the skip count. Telling a user who selected several servers that there was
    /// nothing to connect is misleading rather than terse - it names an empty selection when
    /// the truth is that every member was skipped for a reason the run already knows.
    /// </remarks>
    internal static string NothingToConnectKey(int skippedCount)
        => skippedCount > 0
            ? "StatusBulkConnectNothingToConnectSkipped"
            : "StatusBulkConnectNothingToConnect";
}
