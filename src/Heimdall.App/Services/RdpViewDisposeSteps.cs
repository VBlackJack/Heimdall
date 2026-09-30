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
/// The releases an embedded RDP view runs ahead of its COM teardown, one member per
/// independent release. Kept UI-agnostic so the order and the containment of each step can
/// be tested without constructing a WPF control.
/// </summary>
internal interface IRdpViewDisposeTarget
{
    void EmitTeardownDisconnectEvent(DisconnectReason reason);

    void MarkDisposed(DisconnectReason reason);

    void SettleCertificatePrompt();

    void UnregisterEscapeHook();

    void UnregisterDpiChangedHandler();

    void DetachConnectionStateMachine();

    void DetachLayoutHandlers();

    void StopAutofillFilledTimer();

    void StopTransientToastTimer();

    void HideLetterboxHint();

    void StopStabilization();

    void StopReconnectElapsedTracking();

    void StopAntiIdleTimer();

    void StopConnectWatchdog();

    void ReleaseSleepPrevention();

    void CancelAutofill();

    void ResetSessionIndicators();
}

/// <summary>
/// The ordered release steps of an embedded RDP view's dispose.
/// </summary>
/// <remarks>
/// The view used to run these as one straight block inside a single try: the first release
/// that threw skipped every one after it, among them the connect watchdog stop and the sleep
/// prevention release, so a machine could stay awake for a session that no longer existed.
/// Handed to <see cref="DisposeSequence.Run(IReadOnlyList{Action}, Action, Action{Exception})"/>,
/// each step is contained on its own.
/// </remarks>
internal static class RdpViewDisposeSteps
{
    /// <summary>
    /// The steps in the order they run. The teardown event is emitted before the view marks
    /// itself disposed, and the certificate question is settled before any other release.
    /// </summary>
    public static IReadOnlyList<Action> Releases(IRdpViewDisposeTarget target, DisconnectReason reason)
    {
        ArgumentNullException.ThrowIfNull(target);

        return
        [
            () => target.EmitTeardownDisconnectEvent(reason),
            () => target.MarkDisposed(reason),
            target.SettleCertificatePrompt,
            target.UnregisterEscapeHook,
            target.UnregisterDpiChangedHandler,
            target.DetachConnectionStateMachine,
            target.DetachLayoutHandlers,
            target.StopAutofillFilledTimer,
            target.StopTransientToastTimer,
            target.HideLetterboxHint,
            target.StopStabilization,
            target.StopReconnectElapsedTracking,
            target.StopAntiIdleTimer,
            target.StopConnectWatchdog,
            target.ReleaseSleepPrevention,
            target.CancelAutofill,
            target.ResetSessionIndicators,
        ];
    }
}
