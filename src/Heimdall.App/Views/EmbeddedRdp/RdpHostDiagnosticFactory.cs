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

using Heimdall.Core.SessionDiagnostics;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Maps ActiveX-host disconnect and fatal-error callbacks to the shared session diagnostic contract.
/// </summary>
internal static class RdpHostDiagnosticFactory
{
    /// <summary>
    /// Names the diagnostic Heimdall's own connect watchdog produces. It carries no disconnect
    /// code, because the stack never reported one, so the key is the only thing that identifies
    /// it to the overlay - which has to paint it the way it paints the stack's own
    /// ConnectionTimeout, since both describe the same unreachable host.
    /// </summary>
    internal const string ConnectTimeoutMessageKey = "RdpDisconnectConnectTimeout";

    internal static SessionDiagnostic FromDisconnect(int reason)
        => FromDisconnect(reason, RdpActiveXHost.NoExtendedDisconnectReason);

    internal static SessionDiagnostic FromDisconnect(int reason, int extendedReason)
    {
        // One shared resolver rather than a composition of its own: this consumer and the session
        // event log used to compose the same two decoders in opposite orders, so a single
        // disconnect could be named one thing on screen and another in the audit trail.
        string? suffix = RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason);
        string messageKey = suffix is not null
            ? $"RdpDisconnect{suffix}"
            : "RdpDisconnectUnknownCode";

        return new SessionDiagnostic(
            SessionFailureStage.RdpActiveXDisconnect,
            messageKey,
            reason,
            null);
    }

    internal static SessionDiagnostic FromFatalError(int errorCode)
    {
        return new SessionDiagnostic(
            SessionFailureStage.RdpActiveXDisconnect,
            "RdpStatusFatalErrorDetail",
            errorCode,
            null);
    }

    internal static SessionDiagnostic FromConnectTimeout()
    {
        return new SessionDiagnostic(
            SessionFailureStage.RdpActiveXConnect,
            ConnectTimeoutMessageKey,
            null,
            null);
    }

    /// <summary>
    /// Builds a diagnostic for a tunneled session dropped because the SSH
    /// gateway could not reach the forward's target. The target endpoint is
    /// carried in <see cref="SessionDiagnostic.Detail"/> and formatted into the
    /// localized message.
    /// </summary>
    internal static SessionDiagnostic FromTunnelForwardedPortFailure(
        Heimdall.Ssh.TunnelForwardedPortFailure failure,
        int disconnectReason)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new SessionDiagnostic(
            SessionFailureStage.RdpTunnel,
            "RdpDisconnectGatewayTargetUnreachable",
            disconnectReason,
            $"{failure.RemoteHost}:{failure.RemotePort}");
    }
}
