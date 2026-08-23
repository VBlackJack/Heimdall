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

using System.IO;
using System.Text;
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

/// <summary>
/// What a pending update attempt means, and what survives being read.
/// </summary>
public sealed class UpdateOutcomeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-bl0080-store",
        Guid.NewGuid().ToString("N"));

    private static UpdateAttemptRecord Attempt(string version, DateTimeOffset when) =>
        new(UpdateAttemptRecord.CurrentSchemaVersion, version, when);

    [Fact]
    public void Classify_NoAttempt_SaysNothing()
    {
        Assert.Equal(
            UpdateRelaunchOutcome.None,
            UpdateOutcomeClassifier.Classify(null, null, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Classify_RunningTheAttemptedVersion_ReportsSucceeded()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);

        // The fail-safe direction, and the most important assertion here. Telling someone
        // their update failed while they are looking at the new version would cost more
        // confidence than the silence this whole change replaces.
        Assert.Equal(
            UpdateRelaunchOutcome.Succeeded,
            UpdateOutcomeClassifier.Classify(attempt, null, HeimdallVersion.Parse("2026.082202")));
    }

    [Fact]
    public void Classify_VersionDidNotMove_ReportsNotApplied()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);

        Assert.Equal(
            UpdateRelaunchOutcome.NotApplied,
            UpdateOutcomeClassifier.Classify(attempt, null, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Classify_AttemptWithoutAVersion_SaysNothing()
    {
        UpdateAttemptRecord attempt = Attempt(string.Empty, DateTimeOffset.UtcNow);

        // Nothing can be compared, so nothing is claimed. The version type renders an
        // unparsed value as an empty string, so this is reachable rather than theoretical.
        Assert.Equal(
            UpdateRelaunchOutcome.None,
            UpdateOutcomeClassifier.Classify(attempt, null, HeimdallVersion.Parse("2026.082201")));
    }

    private static UpdateFailureRecord Failure(string stage, int exitCode, int known) =>
        new(UpdateFailureRecord.CurrentSchemaVersion, stage, exitCode, known);

    [Fact]
    public void Classify_RunningTheAttemptedVersion_WinsOverAnyFailureRecord()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);
        UpdateFailureRecord failure = Failure(UpdateOutcomeStage.InstallerExit, 3, known: 1);

        // The single most important assertion in this file. An installer can report an
        // error after the files were in fact replaced, so the version check comes first
        // and a false alarm needs two independent things to go wrong.
        Assert.Equal(
            UpdateRelaunchOutcome.Succeeded,
            UpdateOutcomeClassifier.Classify(attempt, failure, HeimdallVersion.Parse("2026.082202")));
    }

    [Theory]
    [InlineData(InnoSetupExitCode.CancelledBeforeInstall, UpdateRelaunchOutcome.CancelledByUser)]
    [InlineData(InnoSetupExitCode.CancelledDuringInstall, UpdateRelaunchOutcome.CancelledByUser)]
    [InlineData(InnoSetupExitCode.InitializationFailed, UpdateRelaunchOutcome.InstallerFailed)]
    [InlineData(InnoSetupExitCode.FatalPreparationError, UpdateRelaunchOutcome.InstallerFailed)]
    [InlineData(InnoSetupExitCode.FatalInstallError, UpdateRelaunchOutcome.InstallerFailed)]
    [InlineData(InnoSetupExitCode.CannotProceed, UpdateRelaunchOutcome.InstallerFailed)]
    public void Classify_KnownExitCode_SeparatesCancellationFromFailure(
        int exitCode,
        UpdateRelaunchOutcome expected)
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);
        UpdateFailureRecord failure = Failure(UpdateOutcomeStage.InstallerExit, exitCode, known: 1);

        // A user who declined the elevation prompt or cancelled the wizard did not suffer
        // a failure, and that is very probably the most frequent reason an update does
        // not apply. The two must not share wording.
        Assert.Equal(
            expected,
            UpdateOutcomeClassifier.Classify(attempt, failure, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Classify_IntegrityRejected_IsReportedAsItsOwnCause()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);
        UpdateFailureRecord failure = Failure(UpdateOutcomeStage.IntegrityRejected, 0, known: 0);

        Assert.Equal(
            UpdateRelaunchOutcome.IntegrityRejected,
            UpdateOutcomeClassifier.Classify(attempt, failure, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Classify_ExitCodeThatCouldNotBeRead_StatesOnlyWhatIsStillTrue()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);
        UpdateFailureRecord failure = Failure(UpdateOutcomeStage.InstallerExit, 0, known: 0);

        // Unknown is never a cause. The statement that the version did not move survives.
        Assert.Equal(
            UpdateRelaunchOutcome.NotApplied,
            UpdateOutcomeClassifier.Classify(attempt, failure, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Classify_UnknownStageToken_DoesNotThrowAndInventsNoCause()
    {
        UpdateAttemptRecord attempt = Attempt("2026.082202", DateTimeOffset.UtcNow);
        UpdateFailureRecord failure = Failure("SomethingAFutureBuildWrote", 0, known: 0);

        Assert.Equal(
            UpdateRelaunchOutcome.NotApplied,
            UpdateOutcomeClassifier.Classify(attempt, failure, HeimdallVersion.Parse("2026.082201")));
    }

    [Fact]
    public void Store_RoundTripsTheFailureRecordAlongsideTheAttempt()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpdateOutcomeStore(_directory);
        store.WriteAttempt("2026.082202");
        WriteFailure(@"{""schemaVersion"":1,""stage"":""InstallerExit"",""installerExitCode"":3,""installerExitCodeKnown"":1}");

        PendingUpdateOutcome? taken = store.TryTakePending();

        Assert.NotNull(taken);
        Assert.Equal(UpdateOutcomeStage.InstallerExit, taken!.Failure!.Stage);
        Assert.Equal(3, taken.Failure.InstallerExitCode);
        Assert.True(taken.Failure.HasExitCode);
        Assert.False(File.Exists(Path.Combine(_directory, "update-failure.json")));
    }

    [Fact]
    public void Store_UnparseableFailureRecord_CostsTheCauseNotTheReport()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpdateOutcomeStore(_directory);
        store.WriteAttempt("2026.082202");
        WriteFailure("{ truncated");

        PendingUpdateOutcome? taken = store.TryTakePending();

        // The attempt alone still supports the statement that the version did not move.
        Assert.NotNull(taken);
        Assert.Null(taken!.Failure);
    }

    [Fact]
    public void Store_WriteAttempt_DiscardsAPreviousRunsFailureRecord()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpdateOutcomeStore(_directory);
        WriteFailure(@"{""schemaVersion"":1,""stage"":""InstallerExit"",""installerExitCode"":3,""installerExitCodeKnown"":1}");

        store.WriteAttempt("2026.082202");

        // It described a different update entirely. Reading it against this attempt would
        // report a cause that belongs to something else.
        PendingUpdateOutcome? taken = store.TryTakePending();
        Assert.NotNull(taken);
        Assert.Null(taken!.Failure);
    }

    private void WriteFailure(string json) =>
        File.WriteAllText(Path.Combine(_directory, "update-failure.json"), json);

    [Fact]
    public void Store_RoundTripsAnAttemptAndRemovesItOnRead()
    {
        var store = new UpdateOutcomeStore(_directory);
        store.WriteAttempt("2026.082202");

        PendingUpdateOutcome? taken = store.TryTakePending();

        Assert.NotNull(taken);
        Assert.Equal("2026.082202", taken!.Attempt.AttemptedVersion);
        Assert.Null(taken.Failure);

        // Read once: a second startup must not report the same attempt again.
        Assert.Null(store.TryTakePending());
    }

    [Fact]
    public void Store_CorruptRecord_ReturnsNothingAndDeletesIt()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "update-attempt.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new UpdateOutcomeStore(_directory);

        Assert.Null(store.TryTakePending());

        // Deleted before parsing, deliberately: a record that cannot be parsed would
        // otherwise be re-read on every launch forever.
        Assert.False(File.Exists(path), "an unparseable record must not survive being read");
    }

    [Fact]
    public void Store_RecordWithABom_IsStillReadable()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpdateOutcomeStore(_directory);
        store.WriteAttempt("2026.082202");

        string path = Path.Combine(_directory, "update-attempt.json");
        string json = File.ReadAllText(path);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.NotNull(store.TryTakePending());
    }

    [Fact]
    public void Store_StaleRecord_IsDiscarded()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var store = new UpdateOutcomeStore(_directory, clock);
        store.WriteAttempt("2026.082202");

        // An attempt from some earlier session is not about the launch now starting.
        clock.Advance(TimeSpan.FromHours(7));

        Assert.Null(store.TryTakePending());
    }

    [Fact]
    public void Store_Clear_RemovesAPendingRecord()
    {
        var store = new UpdateOutcomeStore(_directory);
        store.WriteAttempt("2026.082202");

        store.Clear();

        Assert.Null(store.TryTakePending());
    }

    /// <summary>A clock the test moves by hand.</summary>
    /// <remarks>
    /// Written here rather than pulled from a testing package, which this project does
    /// not reference: adding one to check an age would move a lock file for nothing.
    /// </remarks>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
