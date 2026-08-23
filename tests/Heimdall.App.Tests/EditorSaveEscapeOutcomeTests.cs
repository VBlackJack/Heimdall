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

using Heimdall.App.Services.CloseGuard;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// What the editor overlay's Close button means, press by press, while a save will not end.
/// </summary>
public sealed class EditorSaveEscapeOutcomeTests
{
    [Fact]
    public void Decide_NothingInFlight_LetsTheOrdinaryCloseThrough()
    {
        Assert.Equal(
            EditorSaveEscapeOutcome.Response.AllowClose,
            EditorSaveEscapeOutcome.Decide(saveInProgress: false, escapeAttempted: false));
    }

    [Fact]
    public void Decide_FirstPressDuringASave_OffersTheEscape()
    {
        Assert.Equal(
            EditorSaveEscapeOutcome.Response.OfferEscape,
            EditorSaveEscapeOutcome.Decide(saveInProgress: true, escapeAttempted: false));
    }

    [Fact]
    public void Decide_SecondPressAfterAnEscape_ReportsInsteadOfOfferingAgain()
    {
        // Re-offering a drop on an already-dropped connection would raise the same
        // question about an act that has already happened, and consenting again would do
        // nothing while looking like it did something.
        Assert.Equal(
            EditorSaveEscapeOutcome.Response.ReportOutcome,
            EditorSaveEscapeOutcome.Decide(saveInProgress: true, escapeAttempted: true));
    }

    [Fact]
    public void Decide_SaveEndedAfterAnEscape_LetsTheCloseThrough()
    {
        // The escape worked. Nothing is in flight, so the guard has nothing left to hold.
        Assert.Equal(
            EditorSaveEscapeOutcome.Response.AllowClose,
            EditorSaveEscapeOutcome.Decide(saveInProgress: false, escapeAttempted: true));
    }

    [Fact]
    public void ReportKey_SaveStopped_SaysTheDropWorked()
    {
        Assert.Equal(
            SftpCloseGuardLocaleKeys.EditorSaveEscapeDropped,
            EditorSaveEscapeOutcome.ReportKey(saveInProgress: false));
    }

    [Fact]
    public void ReportKey_SaveStillRunning_SaysSoRatherThanClaimingSuccess()
    {
        // Dropping the connection can itself block on the lock the write holds. The
        // honest report is that it did not work, not silence and not a success.
        Assert.Equal(
            SftpCloseGuardLocaleKeys.EditorSaveEscapeStuck,
            EditorSaveEscapeOutcome.ReportKey(saveInProgress: true));
    }
}
