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

/// <inheritdoc cref="IPaneCloseArbiter"/>
public sealed class PaneCloseArbiter : IPaneCloseArbiter
{
    /// <summary>Hard cap on tracked requests; the oldest entry is evicted past this bound.</summary>
    private const int MaxTrackedRequests = 64;

    /// <summary>Consent already obtained inside one gesture, per request and guard, epoch-stamped.</summary>
    private readonly Dictionary<Guid, Dictionary<ICloseGuard, long>> _grants = [];
    private readonly Queue<Guid> _grantOrder = new();

    /// <summary>
    /// Guards that deferred during the last <see cref="Poll"/> for a request. Resolution consults
    /// only this set, so a guard that answered allow is never handed to its async continuation.
    /// </summary>
    private readonly Dictionary<Guid, List<ICloseGuard>> _deferred = [];

    /// <summary>
    /// In-flight resolutions keyed by guard rather than by request: a second click on the close
    /// button mints a NEW request id, so a request-keyed defence would miss the double gesture.
    /// </summary>
    private readonly Dictionary<ICloseGuard, Task<bool>> _inFlight = [];
    private readonly Dictionary<ICloseGuard, string> _inFlightReason = [];

    public CloseDecision Poll(CloseRequest request, IReadOnlyList<object?> hosts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hosts);

        if (!request.IsInteractive)
        {
            // Silent bypass: not a single guard is sampled.
            return CloseDecision.Allow(0);
        }

        List<ICloseGuard> deferredNow = [];
        CloseDecision worst = CloseDecision.Allow(0);

        foreach (object? host in hosts)
        {
            if (host is not ICloseGuard guard)
            {
                // Neutral: unguarded content always closes, tool or not.
                continue;
            }

            CloseGuardState state;
            try
            {
                state = guard.SampleCloseGuardState();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"CloseGuard SampleCloseGuardState threw; deferring. {ex.Message}");
                deferredNow.Add(guard);
                worst = CloseDecision.Defer(CloseGuardLocaleKeys.BlockedGeneric, 0);
                continue;
            }

            // The order here is load-bearing. A guard that is no longer busy needs no clearance,
            // because the async answer became irrelevant when the work finished. Testing this
            // BEFORE the grant comparison is what stops a transfer that just completed from being
            // refused on the grounds that completing it moved the epoch.
            if (!state.IsBusy)
            {
                continue;
            }

            if (IsGrantedAt(request, guard, state.Epoch))
            {
                // Already consented in this gesture, against this epoch.
                continue;
            }

            if (WasGranted(request, guard))
            {
                // Consent exists but the protected work changed while we were deciding. Refuse
                // terminally rather than silently honouring a stale consent.
                return CloseDecision.Deny(CloseGuardLocaleKeys.BlockedStale, state.Epoch);
            }

            if (_inFlight.ContainsKey(guard))
            {
                // A re-entrant poll raised by a modal's nested pump, or a second click on the close
                // button. Defer without polling the guard again and without a second dialog.
                deferredNow.Add(guard);
                worst = CloseDecision.Defer(
                    _inFlightReason.GetValueOrDefault(guard, CloseGuardLocaleKeys.BlockedGeneric),
                    state.Epoch);
                continue;
            }

            CloseDecision decision;
            try
            {
                decision = guard.PollClose(request);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"CloseGuard PollClose threw; deferring to the async stage. {ex.Message}");
                decision = CloseDecision.Defer(CloseGuardLocaleKeys.BlockedGeneric, state.Epoch);
            }

            if (decision.Verdict == CloseVerdict.Deny)
            {
                _deferred.Remove(request.RequestId);

                // Terminal: the guards after this one are deliberately not polled.
                return decision;
            }

            if (decision.Verdict == CloseVerdict.Defer)
            {
                deferredNow.Add(guard);
                _inFlightReason[guard] = decision.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric;
                worst = decision;
            }
        }

        _deferred[request.RequestId] = deferredNow;
        return worst;
    }

    public async Task<bool> ResolveAsync(CloseRequest request, IReadOnlyList<object?> hosts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hosts);

        if (!request.IsInteractive)
        {
            return true;
        }

        if (!_deferred.TryGetValue(request.RequestId, out List<ICloseGuard>? pending))
        {
            return true;
        }

        foreach (ICloseGuard guard in pending)
        {
            if (WasGranted(request, guard))
            {
                continue;
            }

            Task<bool> resolution = _inFlight.TryGetValue(guard, out Task<bool>? existing)
                ? existing
                : StartResolution(guard, request);

            if (!await resolution.ConfigureAwait(true))
            {
                // The first refusal aborts the whole request.
                return false;
            }

            long epoch;
            try
            {
                epoch = guard.SampleCloseGuardState().Epoch;
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"CloseGuard SampleCloseGuardState threw after consent; refusing. {ex.Message}");
                return false;
            }

            Grant(request, guard, epoch);
        }

        return true;
    }

    public void Release(CloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _grants.Remove(request.RequestId);
        _deferred.Remove(request.RequestId);
    }

    /// <summary>Registers the resolution before invoking the guard, then starts it.</summary>
    /// <remarks>
    /// Registering first is load-bearing rather than a style choice. A confirmation dialog runs a
    /// blocking modal with its own nested message pump and then hands back an already-completed
    /// task, so the whole dialog lifetime executes inside the synchronous prefix of the guard's
    /// continuation. Recording the task after the call returned would leave this dictionary empty
    /// for exactly the window it exists to cover.
    /// </remarks>
    private Task<bool> StartResolution(ICloseGuard guard, CloseRequest request)
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight[guard] = completion.Task;

        _ = RunResolutionAsync(guard, request, completion);
        return completion.Task;
    }

    private async Task RunResolutionAsync(
        ICloseGuard guard,
        CloseRequest request,
        TaskCompletionSource<bool> completion)
    {
        bool granted;
        try
        {
            granted = await guard.ResolveCloseAsync(request, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            granted = false;
        }
        catch (Exception ex)
        {
            // Fail closed on the async stage: a user is present, the refusal is visible and can be
            // retried, and every silent-intent path bypasses guards entirely, so no pane can become
            // permanently unclosable. The synchronous stage fails to a defer for the same reason.
            Core.Logging.FileLogger.Warn(
                $"CloseGuard ResolveCloseAsync threw; treated as a refusal. {ex.Message}");
            granted = false;
        }

        _inFlight.Remove(guard);
        _inFlightReason.Remove(guard);
        completion.TrySetResult(granted);
    }

    private bool WasGranted(CloseRequest request, ICloseGuard guard)
        => _grants.TryGetValue(request.RequestId, out Dictionary<ICloseGuard, long>? set)
            && set.ContainsKey(guard);

    private bool IsGrantedAt(CloseRequest request, ICloseGuard guard, long epoch)
        => _grants.TryGetValue(request.RequestId, out Dictionary<ICloseGuard, long>? set)
            && set.TryGetValue(guard, out long granted)
            && granted == epoch;

    private void Grant(CloseRequest request, ICloseGuard guard, long epoch)
    {
        if (!_grants.TryGetValue(request.RequestId, out Dictionary<ICloseGuard, long>? set))
        {
            set = [];
            _grants[request.RequestId] = set;
            _grantOrder.Enqueue(request.RequestId);

            while (_grantOrder.Count > MaxTrackedRequests)
            {
                Guid evicted = _grantOrder.Dequeue();
                _grants.Remove(evicted);
                _deferred.Remove(evicted);
            }
        }

        set[guard] = epoch;
    }
}
