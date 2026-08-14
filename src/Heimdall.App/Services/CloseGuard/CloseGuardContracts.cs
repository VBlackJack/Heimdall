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

namespace Heimdall.App.Services;

/// <summary>
/// Atomic snapshot of a guard's protectable state.
/// </summary>
/// <remarks>
/// Both fields must come from ONE read under whatever lock the implementer uses. The protocol
/// compares <see cref="Epoch"/> across its synchronous and asynchronous phases, and a torn read
/// would compare an epoch against a busy flag sampled at a different instant.
/// </remarks>
/// <param name="IsBusy">True when tearing the content down right now would destroy in-flight work.</param>
/// <param name="Epoch">
/// Change stamp. Must be non-decreasing, and must change whenever the protected work set starts
/// or finishes. Never read as a quantity: only equality across the two phases matters.
/// </param>
public readonly record struct CloseGuardState(bool IsBusy, long Epoch);

/// <summary>Outcome of the synchronous close poll.</summary>
public enum CloseVerdict
{
    /// <summary>Nothing in flight, or consent already granted. The caller may tear down now.</summary>
    Allow = 0,

    /// <summary>
    /// An asynchronous decision is required. The caller must not tear down, and must drive
    /// <see cref="IPaneCloseArbiter.ResolveAsync"/> before retrying exactly once.
    /// </summary>
    Defer = 1,

    /// <summary>Terminal refusal for this request. The caller must neither tear down nor retry.</summary>
    Deny = 2,
}

/// <summary>A verdict, the locale key that explains it, and the epoch it was decided against.</summary>
public readonly record struct CloseDecision
{
    private CloseDecision(CloseVerdict verdict, string? reasonKey, long epoch)
    {
        Verdict = verdict;
        ReasonKey = reasonKey;
        Epoch = epoch;
    }

    public CloseVerdict Verdict { get; }

    /// <summary>
    /// Locale key explaining a defer or a deny. The template takes exactly one placeholder, filled
    /// with the pane title at the reporting site. Null for <see cref="CloseVerdict.Allow"/>.
    /// </summary>
    public string? ReasonKey { get; }

    /// <summary>The <see cref="CloseGuardState.Epoch"/> observed when this decision settled.</summary>
    public long Epoch { get; }

    public static CloseDecision Allow(long epoch) => new(CloseVerdict.Allow, null, epoch);

    public static CloseDecision Defer(string reasonKey, long epoch)
        => new(CloseVerdict.Defer, reasonKey ?? throw new ArgumentNullException(nameof(reasonKey)), epoch);

    public static CloseDecision Deny(string reasonKey, long epoch)
        => new(CloseVerdict.Deny, reasonKey ?? throw new ArgumentNullException(nameof(reasonKey)), epoch);
}

/// <summary>Whether a human may be asked about this close.</summary>
public enum CloseIntent
{
    /// <summary>A user gesture. Guards are polled and may prompt.</summary>
    Interactive = 0,

    /// <summary>
    /// Programmatic teardown: shutdown, vault lock, failed materialization, reconnect cleanup.
    /// Every guard is skipped and the request always resolves to allow. Deliberately a named,
    /// greppable value rather than a bool, so each bypass is auditable.
    /// </summary>
    Silent = 1,
}

/// <summary>
/// Identity of one close gesture.
/// </summary>
/// <remarks>
/// Minted by the gesture rather than by the pane or the session, so a guard that already consented
/// inside this gesture is not asked again when the close retries.
/// </remarks>
public sealed class CloseRequest
{
    private CloseRequest(DisconnectReason reason, CloseIntent intent)
    {
        Reason = reason;
        Intent = intent;
    }

    public Guid RequestId { get; } = Guid.NewGuid();

    public DisconnectReason Reason { get; }

    public CloseIntent Intent { get; }

    public bool IsInteractive => Intent == CloseIntent.Interactive;

    public static CloseRequest Interactive(DisconnectReason reason)
        => new(reason, CloseIntent.Interactive);

    /// <summary>
    /// Bypasses every guard. Only for programmatic teardown where no human is present, or where
    /// blocking would be worse than the loss: application exit, vault lock, failed materialization,
    /// reconnect teardown.
    /// </summary>
    public static CloseRequest Silent(DisconnectReason reason)
        => new(reason, CloseIntent.Silent);
}

/// <summary>What a close primitive did.</summary>
public enum PaneCloseOutcome
{
    /// <summary>Teardown happened, or the pane was already gone - a double close is idempotent.</summary>
    Closed = 0,

    /// <summary>Terminal refusal. Surface <see cref="PaneCloseResult.ReasonKey"/>; do not retry.</summary>
    Blocked = 1,

    /// <summary>A guard needs its asynchronous continuation. Drive ResolveAsync, then retry once.</summary>
    Deferred = 2,
}

/// <summary>Result of a synchronous close primitive.</summary>
public readonly record struct PaneCloseResult(PaneCloseOutcome Outcome, string? ReasonKey)
{
    public static PaneCloseResult Closed { get; } = new(PaneCloseOutcome.Closed, null);

    public static PaneCloseResult Blocked(string reasonKey)
        => new(PaneCloseOutcome.Blocked, reasonKey);

    public static PaneCloseResult Deferred(string reasonKey)
        => new(PaneCloseOutcome.Deferred, reasonKey);

    public bool IsClosed => Outcome == PaneCloseOutcome.Closed;
}

/// <summary>
/// Cross-cutting close-guard contract for any object hosted in a pane: a tool view, a protocol
/// view, an editor overlay, or a future non-view occupant.
/// </summary>
/// <remarks>
/// Deliberately independent of the tool contract - a host may implement one, the other, both or
/// neither, and the guard is consulted for every pane whatever its protocol.
/// <para>
/// Two-phase protocol. Phase one, <see cref="PollClose"/>, is synchronous and runs inside the close
/// primitive. Phase two, <see cref="ResolveCloseAsync"/>, runs only from an already-asynchronous
/// driver and may await user input or in-flight operation state. Splitting it this way is what lets
/// a genuinely asynchronous decision be honoured without any close primitive becoming asynchronous.
/// </para>
/// </remarks>
public interface ICloseGuard
{
    /// <summary>
    /// Non-blocking, side-effect-free sample of the protected state, used by both phases to detect
    /// that the world moved between them.
    /// </summary>
    /// <remarks>Must not await, block on a lock held by awaiting code, prompt, or start I/O.</remarks>
    CloseGuardState SampleCloseGuardState();

    /// <summary>
    /// Synchronous poll, always on the UI thread. Same prohibitions as
    /// <see cref="SampleCloseGuardState"/>: read cached flags only.
    /// </summary>
    /// <remarks>
    /// Returning <see cref="CloseVerdict.Defer"/> is how an implementation asks for its
    /// asynchronous continuation to run. The decision must carry the epoch observed while deciding.
    /// </remarks>
    CloseDecision PollClose(CloseRequest request);

    /// <summary>
    /// Asynchronous continuation, called only for guards whose <see cref="PollClose"/> deferred in
    /// the same pass. May await user input or in-flight work. True means the content consents.
    /// </summary>
    /// <remarks>
    /// Must not mutate the protected work - do not cancel a transfer here. The teardown that
    /// follows a granted close cancels anyway, so a side effect at this point would destroy work
    /// for a close the user may still abandon at a later prompt. Never invoked concurrently for one
    /// instance: the arbiter memoizes the task.
    /// </remarks>
    Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the synchronous/asynchronous split of the close protocol and all of its bookkeeping.
/// </summary>
/// <remarks>
/// Guards stay stateless with respect to requests; the arbiter is what makes retry-exactly-once
/// terminate. Single instance, and UI-thread affine by contract: every member is called on the
/// dispatcher thread, so the internal collections are unsynchronized by design.
/// </remarks>
public interface IPaneCloseArbiter
{
    /// <summary>
    /// Synchronous. Never awaits, blocks, pumps or dispatches.
    /// </summary>
    /// <remarks>
    /// Honours grants already issued for this request, defers while a resolution for the same guard
    /// is in flight, and re-polls otherwise. A deny short-circuits; otherwise a defer beats an
    /// allow. A <see cref="CloseIntent.Silent"/> request answers allow without touching a guard.
    /// </remarks>
    CloseDecision Poll(CloseRequest request, IReadOnlyList<object?> hosts);

    /// <summary>
    /// Asynchronous continuation. Resolves every guard that deferred in the matching
    /// <see cref="Poll"/>, one after another so N panes never raise N concurrent dialogs, records a
    /// grant per consenting guard stamped with the epoch it consented against, and returns false on
    /// the first refusal. Joins an in-flight resolution instead of starting a second one.
    /// </summary>
    Task<bool> ResolveAsync(CloseRequest request, IReadOnlyList<object?> hosts);

    /// <summary>Drops every grant for a request. Idempotent; call it from a finally.</summary>
    void Release(CloseRequest request);
}

/// <summary>
/// Locale keys the close protocol itself surfaces, as opposed to those a particular guard chooses.
/// </summary>
/// <remarks>
/// Held as constants rather than literals so the localization test can reflect over this class: an
/// eleventh key added later without a translation is then a red test rather than silent drift.
/// </remarks>
public static class CloseGuardLocaleKeys
{
    public const string BlockedTitle = "CloseGuardBlockedTitle";

    public const string BlockedGeneric = "CloseGuardBlockedGeneric";

    public const string BlockedTool = "CloseGuardBlockedTool";

    public const string BlockedStale = "CloseGuardBlockedStale";

    public const string BatchBlockedTitle = "CloseGuardBatchBlockedTitle";

    public const string BatchBlockedMessage = "CloseGuardBatchBlockedMessage";
}
