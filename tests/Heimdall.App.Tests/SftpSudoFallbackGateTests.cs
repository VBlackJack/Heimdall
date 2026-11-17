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
using Heimdall.Core.Models;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// The one object that decides whether a refused act may call sudo: Never refuses, Ask asks once per
/// scope, Automatic asks only for a delete, an act the user did not start never asks, and the sudo
/// button consents to listings and to nothing else.
/// </summary>
public sealed class SftpSudoFallbackGateTests
{
    private const string SomePath = "/srv/data/file.txt";

    public static TheoryData<string> AskedActs => new()
    {
        nameof(SftpRootActKind.Upload),
        nameof(SftpRootActKind.Overwrite),
        nameof(SftpRootActKind.InlineSave),
        nameof(SftpRootActKind.EditOpen),
        nameof(SftpRootActKind.NewFolder),
        nameof(SftpRootActKind.Rename),
        nameof(SftpRootActKind.PermissionChange),
        nameof(SftpRootActKind.Download),
        nameof(SftpRootActKind.Delete),
        nameof(SftpRootActKind.Listing),
    };

    public static TheoryData<string> AutomaticActs => new()
    {
        nameof(SftpRootActKind.Upload),
        nameof(SftpRootActKind.Overwrite),
        nameof(SftpRootActKind.InlineSave),
        nameof(SftpRootActKind.EditOpen),
        nameof(SftpRootActKind.NewFolder),
        nameof(SftpRootActKind.Rename),
        nameof(SftpRootActKind.PermissionChange),
        nameof(SftpRootActKind.Download),
        nameof(SftpRootActKind.Listing),
    };

    [Theory]
    [MemberData(nameof(AskedActs))]
    public async Task Never_RefusesEveryActWithoutAsking(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Never);

        SftpSudoDecision decision = await gate.DecideAsync(new SftpSudoRequest(act, SomePath), NewScope(act));

        Assert.Equal(SftpSudoDecision.RefusedBySetting, decision);
        Assert.False(decision.IsAllowed());
        Assert.Equal(0, questions.Count);
    }

    [Theory]
    [MemberData(nameof(AutomaticActs))]
    public async Task Automatic_AllowsEveryActExceptADeleteWithoutAsking(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Automatic);

        SftpSudoDecision decision = await gate.DecideAsync(new SftpSudoRequest(act, SomePath), NewScope(act));

        Assert.Equal(SftpSudoDecision.Allowed, decision);
        Assert.Equal(0, questions.Count);
    }

    [Fact]
    public async Task Automatic_ADeleteStillAsksAndDeclinesWhenTheUserSaysNo()
    {
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Automatic);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Delete, SomePath),
            NewScope(SftpRootActKind.Delete));

        Assert.Equal(SftpSudoDecision.Declined, decision);
        Assert.Equal(1, questions.Count);
    }

    [Theory]
    [MemberData(nameof(AskedActs))]
    public async Task Ask_AYesAllowsTheActAndTheNextActOfTheScopeDoesNotAskAgain(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);
        SftpSudoConsentScope scope = NewScope(act);

        SftpSudoDecision first = await gate.DecideAsync(new SftpSudoRequest(act, SomePath), scope);
        SftpSudoDecision second = await gate.DecideAsync(new SftpSudoRequest(act, SomePath + "2"), scope);

        Assert.Equal(SftpSudoDecision.AllowedAfterQuestion, first);
        Assert.Equal(SftpSudoDecision.Allowed, second);
        Assert.Equal(1, questions.Count);
        Assert.Equal(act, questions.Asked[0].Act);
    }

    [Theory]
    [MemberData(nameof(AskedActs))]
    public async Task Ask_ANoDeclinesTheActAndEveryOtherActOfTheScope(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);
        SftpSudoConsentScope scope = NewScope(act);

        SftpSudoDecision first = await gate.DecideAsync(new SftpSudoRequest(act, SomePath), scope);
        SftpSudoDecision second = await gate.DecideAsync(new SftpSudoRequest(act, SomePath + "2"), scope);

        Assert.Equal(SftpSudoDecision.Declined, first);
        Assert.Equal(SftpSudoDecision.Declined, second);
        Assert.Equal(1, questions.Count);
    }

    [Fact]
    public async Task Ask_TwoScopesAskTwice()
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);

        _ = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Rename, SomePath), NewScope(SftpRootActKind.Rename));
        _ = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Rename, SomePath), NewScope(SftpRootActKind.Rename));

        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public async Task Ask_ADeclinedQuestionDefaultsToNoWhenNothingCanAnswer()
    {
        // The question function of a pane without a dialog service answers no: a refused act is
        // never turned into root because nobody could be asked.
        SftpSudoFallbackGate gate = new(_ => Task.FromResult(false));
        gate.SetModeProvider(() => SftpSudoFallbackMode.Ask);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Upload, SomePath),
            NewScope(SftpRootActKind.Upload));

        Assert.Equal(SftpSudoDecision.Declined, decision);
    }

    [Theory]
    [InlineData(SftpSudoFallbackMode.Ask)]
    [InlineData(SftpSudoFallbackMode.Automatic)]
    public async Task AListingTheUserDidNotStart_NeverAsksAndIsOnlyAllowedInAutomatic(SftpSudoFallbackMode mode)
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(mode);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Listing, "/root", SftpSudoInitiator.Background),
            NewScope(SftpRootActKind.Listing));

        Assert.Equal(0, questions.Count);
        Assert.Equal(
            mode == SftpSudoFallbackMode.Ask ? SftpSudoDecision.NotAsked : SftpSudoDecision.Allowed,
            decision);
    }

    [Fact]
    public async Task TheSudoButton_ConsentsToAListingWithoutAskingInAsk()
    {
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Listing, "/root", SftpSudoInitiator.User, PaneArmed: true),
            NewScope(SftpRootActKind.Listing));

        Assert.Equal(SftpSudoDecision.Allowed, decision);
        Assert.Equal(0, questions.Count);
    }

    [Theory]
    [InlineData(nameof(SftpRootActKind.Upload))]
    [InlineData(nameof(SftpRootActKind.Overwrite))]
    [InlineData(nameof(SftpRootActKind.InlineSave))]
    [InlineData(nameof(SftpRootActKind.NewFolder))]
    [InlineData(nameof(SftpRootActKind.Rename))]
    [InlineData(nameof(SftpRootActKind.PermissionChange))]
    [InlineData(nameof(SftpRootActKind.Download))]
    [InlineData(nameof(SftpRootActKind.Delete))]
    public async Task TheSudoButton_NeverLetsAWriteSkipTheQuestion(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(act, SomePath, SftpSudoInitiator.User, PaneArmed: true),
            NewScope(act));

        Assert.Equal(SftpSudoDecision.AllowedAfterQuestion, decision);
        Assert.Equal(1, questions.Count);
    }

    [Fact]
    public async Task TheSudoButton_DoesNothingInNever()
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Never);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Listing, "/root", SftpSudoInitiator.User, PaneArmed: true),
            NewScope(SftpRootActKind.Listing));

        Assert.Equal(SftpSudoDecision.RefusedBySetting, decision);
    }

    [Fact]
    public async Task AScopeThatProvedSudoCannotSucceed_StopsWithoutAsking()
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Automatic);
        SftpSudoConsentScope scope = NewScope(SftpRootActKind.Upload);
        scope.Guard.Record(new SudoAuthenticationException(SudoFailureKind.PasswordRejected, "raw"));

        SftpSudoDecision decision = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Upload, SomePath), scope);

        Assert.Equal(SftpSudoDecision.BatchStopped, decision);
        Assert.False(decision.IsAllowed());
        Assert.Equal(0, questions.Count);
    }

    [Fact]
    public async Task AFailureOfOneEntryOnly_DoesNotStopTheScope()
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Automatic);
        SftpSudoConsentScope scope = NewScope(SftpRootActKind.Download);
        scope.Guard.Record(new InvalidOperationException("one file failed"));

        SftpSudoDecision decision = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Download, SomePath), scope);

        Assert.Equal(SftpSudoDecision.Allowed, decision);
    }

    [Fact]
    public async Task ADeclineOutranksTheStopOfTheScope()
    {
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = questions.CreateGate(SftpSudoFallbackMode.Ask);
        SftpSudoConsentScope scope = NewScope(SftpRootActKind.Delete);
        _ = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Delete, SomePath), scope);
        scope.Guard.Record(new SudoAuthenticationException(SudoFailureKind.NotPermitted, "raw"));

        SftpSudoDecision decision = await gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Delete, SomePath), scope);

        Assert.Equal(SftpSudoDecision.Declined, decision);
    }

    [Fact]
    public async Task TheModeIsReadLiveAtEveryAct()
    {
        QuestionRecorder questions = new(answer: true);
        SftpSudoFallbackMode mode = SftpSudoFallbackMode.Automatic;
        SftpSudoFallbackGate gate = new(questions.AskAsync);
        gate.SetModeProvider(() => mode);

        SftpSudoDecision automatic = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Rename, SomePath), NewScope(SftpRootActKind.Rename));
        mode = SftpSudoFallbackMode.Never;
        SftpSudoDecision never = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Rename, SomePath), NewScope(SftpRootActKind.Rename));
        mode = SftpSudoFallbackMode.Ask;
        SftpSudoDecision ask = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Rename, SomePath), NewScope(SftpRootActKind.Rename));

        Assert.Equal(SftpSudoDecision.Allowed, automatic);
        Assert.Equal(SftpSudoDecision.RefusedBySetting, never);
        Assert.Equal(SftpSudoDecision.AllowedAfterQuestion, ask);
    }

    [Fact]
    public async Task AGateThatWasNeverWired_IsAutomatic()
    {
        QuestionRecorder questions = new(answer: false);
        SftpSudoFallbackGate gate = new(questions.AskAsync);

        SftpSudoDecision decision = await gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Upload, SomePath), NewScope(SftpRootActKind.Upload));

        Assert.Equal(SftpSudoFallbackMode.Automatic, gate.Mode);
        Assert.Equal(SftpSudoDecision.Allowed, decision);
        Assert.Equal(0, questions.Count);
    }

    [Fact]
    public async Task OnlyOneQuestionIsOpenAtATime_AndASecondAskerOfTheSameScopeSharesTheAnswer()
    {
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int open = 0;
        int mostOpen = 0;
        int asked = 0;
        SftpSudoFallbackGate gate = new(async _ =>
        {
            Interlocked.Increment(ref asked);
            int now = Interlocked.Increment(ref open);
            mostOpen = Math.Max(mostOpen, now);
            bool answer = await release.Task;
            Interlocked.Decrement(ref open);
            return answer;
        });
        gate.SetModeProvider(() => SftpSudoFallbackMode.Ask);
        SftpSudoConsentScope shared = NewScope(SftpRootActKind.Rename);

        Task<SftpSudoDecision> a = gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Rename, "/a"), shared);
        Task<SftpSudoDecision> b = gate.DecideAsync(new SftpSudoRequest(SftpRootActKind.Rename, "/b"), shared);
        Task<SftpSudoDecision> c = gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.NewFolder, "/c"), NewScope(SftpRootActKind.NewFolder));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref asked));
        release.SetResult(true);
        SftpSudoDecision[] all = await Task.WhenAll(a, b, c);

        Assert.Equal(1, mostOpen);
        Assert.Equal(2, asked);
        Assert.Equal(SftpSudoDecision.AllowedAfterQuestion, all[0]);
        Assert.Equal(SftpSudoDecision.Allowed, all[1]);
        Assert.Equal(SftpSudoDecision.AllowedAfterQuestion, all[2]);
    }

    [Fact]
    public async Task AnAskerWaitingForTheOpenQuestion_LeavesOnCancellation()
    {
        TaskCompletionSource<bool> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SftpSudoFallbackGate gate = new(_ => never.Task);
        gate.SetModeProvider(() => SftpSudoFallbackMode.Ask);
        Task<SftpSudoDecision> first = gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Rename, "/a"), NewScope(SftpRootActKind.Rename));
        using CancellationTokenSource cancel = new();

        Task<SftpSudoDecision> second = gate.DecideAsync(
            new SftpSudoRequest(SftpRootActKind.Rename, "/b"), NewScope(SftpRootActKind.Rename), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        never.SetResult(false);
        Assert.Equal(SftpSudoDecision.Declined, await first);
    }

    [Fact]
    public void TheDecisionsThatAllowSudo_AreExactlyTheTwoThatSayYes()
    {
        SftpSudoDecision[] allowed = Enum.GetValues<SftpSudoDecision>().Where(d => d.IsAllowed()).ToArray();

        Assert.Equal([SftpSudoDecision.Allowed, SftpSudoDecision.AllowedAfterQuestion], allowed);
    }

    private static SftpSudoConsentScope NewScope(SftpRootActKind act) => new(act);

    private sealed class QuestionRecorder(bool answer)
    {
        private int _count;

        public List<SftpSudoQuestion> Asked { get; } = [];

        public int Count => Volatile.Read(ref _count);

        public SftpSudoFallbackGate CreateGate(SftpSudoFallbackMode mode)
        {
            SftpSudoFallbackGate gate = new(AskAsync);
            gate.SetModeProvider(() => mode);
            return gate;
        }

        public Task<bool> AskAsync(SftpSudoQuestion question)
        {
            Interlocked.Increment(ref _count);
            lock (Asked)
            {
                Asked.Add(question);
            }

            return Task.FromResult(answer);
        }
    }
}
