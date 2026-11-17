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

using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

/// <summary>Who started the act that was refused: a listing nobody asked for never raises a question.</summary>
internal enum SftpSudoInitiator
{
    /// <summary>The user started it: a click, a drop, a navigation, a command.</summary>
    User,

    /// <summary>The application started it: a refresh after an operation, a follow, the first folder.</summary>
    Background,
}

/// <summary>What the gate decided about calling sudo for one refused act.</summary>
internal enum SftpSudoDecision
{
    /// <summary>Sudo may be called with no question: the setting is Automatic, or the answer was already given.</summary>
    Allowed,

    /// <summary>Sudo may be called: the user was asked just now and said yes.</summary>
    AllowedAfterQuestion,

    /// <summary>The user was asked, now or earlier in the same scope, and said no.</summary>
    Declined,

    /// <summary>The setting is Never: root is not tried and nobody is asked.</summary>
    RefusedBySetting,

    /// <summary>The act was not started by the user, so nobody was asked and root is not tried.</summary>
    NotAsked,

    /// <summary>An earlier act of the same scope showed that sudo cannot succeed, so it is not tried again.</summary>
    BatchStopped,
}

/// <summary>What a scope covers: one answer settles everything inside it.</summary>
internal enum SftpSudoScopeKind
{
    /// <summary>One user action, possibly over several entries (a delete or chmod batch).</summary>
    Action,

    /// <summary>One queued transfer job, its folder walk and its files.</summary>
    Job,

    /// <summary>One external edit session: the open and the saves that follow it.</summary>
    EditSession,

    /// <summary>One document of the integrated editor: its first refused save and the ones after.</summary>
    Document,
}

/// <summary>Convenience reading of a decision.</summary>
internal static class SftpSudoDecisionExtensions
{
    /// <summary>Whether sudo may be called.</summary>
    public static bool IsAllowed(this SftpSudoDecision decision)
        => decision is SftpSudoDecision.Allowed or SftpSudoDecision.AllowedAfterQuestion;
}

/// <summary>
/// The answer to one question and the batch guard that goes with it, for everything one user action
/// does as root. It generalises the single-answer object the delete used to keep for itself.
/// </summary>
/// <remarks>
/// One instance per user action, never shared between two: the first answer settles the rest of the
/// scope, and the guard stops the scope after a failure that cannot change from one entry to the next.
/// </remarks>
internal sealed class SftpSudoConsentScope
{
    /// <summary>Creates a scope.</summary>
    /// <param name="questionAct">The act the question is worded for (a job asks about its direction, not about each entry).</param>
    /// <param name="kind">What the scope covers.</param>
    /// <param name="jobName">The localized name of the job, for a job scope.</param>
    public SftpSudoConsentScope(
        SftpRootActKind questionAct,
        SftpSudoScopeKind kind = SftpSudoScopeKind.Action,
        string? jobName = null)
    {
        QuestionAct = questionAct;
        Kind = kind;
        JobName = jobName;
    }

    /// <summary>The act the question is worded for.</summary>
    public SftpRootActKind QuestionAct { get; }

    /// <summary>What the scope covers.</summary>
    public SftpSudoScopeKind Kind { get; }

    /// <summary>The name of the job, or null outside a job scope.</summary>
    public string? JobName { get; }

    /// <summary>Stops the scope after a batch-fatal sudo failure.</summary>
    public SudoBatchGuard Guard { get; } = new();

    /// <summary>The answer given in this scope, or null while nobody was asked.</summary>
    public bool? Answer { get; internal set; }
}

/// <summary>One question to put to the user.</summary>
/// <param name="Scope">The scope the answer will settle.</param>
/// <param name="Act">The act that was refused.</param>
/// <param name="Path">What the act was about, as the server named it (not yet written out visibly).</param>
internal sealed record SftpSudoQuestion(SftpSudoConsentScope Scope, SftpRootActKind Act, string Path);

/// <summary>One refused act asking whether sudo may be called.</summary>
/// <param name="Act">What was refused.</param>
/// <param name="Path">What it was about.</param>
/// <param name="Initiator">Who started it.</param>
/// <param name="PaneArmed">Whether the pane's sudo button is on; it consents to listings as root, and to nothing else.</param>
internal readonly record struct SftpSudoRequest(
    SftpRootActKind Act,
    string Path,
    SftpSudoInitiator Initiator = SftpSudoInitiator.User,
    bool PaneArmed = false);

/// <summary>
/// An act was refused by the server and root was not run for it: by the setting, by the user, or
/// because the user did not start it. It carries the server's refusal so the message can say both.
/// </summary>
internal sealed class SftpSudoNotRunException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SftpSudoNotRunException(SftpSudoDecision decision, Exception refusal)
        : base("The operation was refused and was not run as root.", refusal)
    {
        Decision = decision;
        Refusal = refusal;
    }

    /// <summary>Why root was not run.</summary>
    public SftpSudoDecision Decision { get; }

    /// <summary>The refusal the server gave.</summary>
    public Exception Refusal { get; }
}

/// <summary>
/// The one object that decides whether a refused SFTP act may be retried through sudo. Every
/// escalation site asks it first.
/// </summary>
/// <remarks>
/// <para>Order: the setting (Never refuses at once), the scope's earlier answer, a stopped scope,
/// the pane's armed listing, then the question. In Ask every refused act of the user's asks, once
/// per scope; in Automatic only a delete asks. Acts the user did not start (a follow, the first
/// folder, a refresh after an operation) never ask: in Ask they are not run as root.</para>
/// <para>One question is on screen at a time per pane: a second asker waits for the first answer,
/// then finds its own scope settled or asks in its turn.</para>
/// </remarks>
internal sealed class SftpSudoFallbackGate
{
    private readonly Func<SftpSudoQuestion, Task<bool>> _ask;
    private readonly SemaphoreSlim _oneQuestion = new(1, 1);
    private Func<SftpSudoFallbackMode>? _modeProvider;

    /// <summary>Creates the gate over the function that puts a question to the user.</summary>
    public SftpSudoFallbackGate(Func<SftpSudoQuestion, Task<bool>> ask)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
    }

    /// <summary>
    /// The effective mode, read live on each call. A gate that was never wired runs Automatic, so a
    /// view model built without a session keeps the behaviour it had before the setting existed.
    /// </summary>
    public SftpSudoFallbackMode Mode
        => Volatile.Read(ref _modeProvider)?.Invoke() ?? SftpSudoFallbackMode.Automatic;

    /// <summary>Wires the live mode; null leaves the gate Automatic.</summary>
    public void SetModeProvider(Func<SftpSudoFallbackMode>? provider)
        => Volatile.Write(ref _modeProvider, provider);

    /// <summary>Decides whether <paramref name="request"/> may call sudo, asking the user when the mode says so.</summary>
    public async Task<SftpSudoDecision> DecideAsync(
        SftpSudoRequest request,
        SftpSudoConsentScope scope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        SftpSudoFallbackMode mode = Mode;
        if (mode == SftpSudoFallbackMode.Never)
        {
            return SftpSudoDecision.RefusedBySetting;
        }

        if (scope.Answer == false)
        {
            return SftpSudoDecision.Declined;
        }

        if (scope.Guard.IsStopped)
        {
            return SftpSudoDecision.BatchStopped;
        }

        if (scope.Answer == true)
        {
            return SftpSudoDecision.Allowed;
        }

        // The sudo button is the user's own request to list as root. It covers listings only.
        if (request.Act == SftpRootActKind.Listing && request.PaneArmed)
        {
            return SftpSudoDecision.Allowed;
        }

        // A delete asks whatever the setting says: it cannot be undone.
        bool mustAsk = request.Act == SftpRootActKind.Delete || mode == SftpSudoFallbackMode.Ask;
        if (!mustAsk)
        {
            return SftpSudoDecision.Allowed;
        }

        if (request.Initiator == SftpSudoInitiator.Background)
        {
            return SftpSudoDecision.NotAsked;
        }

        await _oneQuestion.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (scope.Answer is { } settled)
            {
                return settled ? SftpSudoDecision.Allowed : SftpSudoDecision.Declined;
            }

            bool granted = await _ask(new SftpSudoQuestion(scope, request.Act, request.Path)).ConfigureAwait(false);
            scope.Answer = granted;
            return granted ? SftpSudoDecision.AllowedAfterQuestion : SftpSudoDecision.Declined;
        }
        finally
        {
            _ = _oneQuestion.Release();
        }
    }
}
