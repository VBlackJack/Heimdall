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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// What a bulk connect says it did, and whether that adds up.
/// </summary>
/// <remarks>
/// The defect this pins was a summary that reported "Connected 1, failed 0, skipped 0."
/// for a selection of three cancelled after the first. Two servers appeared in no counter,
/// so the sentence was false rather than terse, and a green test asserted it.
/// </remarks>
public sealed class BulkConnectSummaryTests
{
    [Theory]
    [InlineData(3, 1, 0, 0, 2)]
    [InlineData(50, 3, 0, 0, 47)]
    [InlineData(5, 1, 1, 1, 2)]
    public void EverySelectedServer_IsAccountedFor(
        int selected,
        int connected,
        int failed,
        int skipped,
        int expectedNotAttempted)
    {
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected, connected, failed, skipped, cancelled: true);

        // The property, stated twice on purpose: the missing term has the value it should,
        // and the four terms add back up to the selection. A counter that a branch forgets
        // to increment cannot satisfy the second one.
        Assert.Equal(expectedNotAttempted, tally.NotAttempted);
        Assert.Equal(
            selected,
            tally.Connected + tally.Failed + tally.Skipped + tally.NotAttempted);
    }

    [Fact]
    public void RunThatFinished_HasNothingLeftUnattempted()
    {
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected: 3, connected: 2, failed: 1, skipped: 0, cancelled: false);

        Assert.Equal(0, tally.NotAttempted);
        Assert.False(tally.NeedsCancellationNotice);
    }

    [Fact]
    public void CancelledOnTheLastServer_SaysNothingExtra()
    {
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected: 2, connected: 2, failed: 0, skipped: 0, cancelled: true);

        // Cancelling once everything has been handled leaves no missing servers to explain.
        // "Cancelled, 0 not attempted" would be noise, so the notice is suppressed - but the
        // arithmetic still closes, which is the part that must never depend on the wording.
        Assert.Equal(0, tally.NotAttempted);
        Assert.False(tally.NeedsCancellationNotice);
    }

    [Fact]
    public void CancelledWithServersLeft_SaysSo()
    {
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected: 4, connected: 1, failed: 0, skipped: 0, cancelled: true);

        Assert.True(tally.NeedsCancellationNotice);
        Assert.Equal(3, tally.NotAttempted);
    }

    [Fact]
    public void RunThatWasNotCancelled_NeverAsksForTheNotice()
    {
        // Counters that do not add up are a defect in the caller. Whatever the cause, a run
        // nobody cancelled must not claim it was.
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected: 9, connected: 1, failed: 0, skipped: 0, cancelled: false);

        Assert.False(tally.NeedsCancellationNotice);
    }

    [Fact]
    public void CountersExceedingTheSelection_DoNotRenderNegativeServers()
    {
        // Defensive, and deliberately not an exception: a miscount is a bug to fix, but
        // showing the user "-2 not attempted" would turn it into a visible absurdity.
        BulkConnectTally tally = BulkConnectSummary.Describe(
            selected: 2, connected: 3, failed: 1, skipped: 0, cancelled: true);

        Assert.Equal(0, tally.NotAttempted);
    }

    [Fact]
    public void NothingConnectable_NamesTheSkipsWhenThereWereAny()
    {
        // The other half of the defect: a path that discarded the skip count told a user who
        // had selected several servers that there was nothing to connect.
        Assert.Equal(
            "StatusBulkConnectNothingToConnectSkipped",
            BulkConnectSummary.NothingToConnectKey(3));
    }

    [Fact]
    public void NothingConnectableAndNothingSkipped_StaysPlain()
        => Assert.Equal(
            "StatusBulkConnectNothingToConnect",
            BulkConnectSummary.NothingToConnectKey(0));
}
