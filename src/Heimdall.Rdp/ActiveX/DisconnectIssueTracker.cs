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

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Remembers whether the OCX <c>Disconnect()</c> already returned normally since the last
/// connect. The control refuses a repeated <c>Disconnect()</c> while the first is still
/// settling, so the decision is taken from what the host already did, not from the HRESULT.
/// </summary>
internal sealed class DisconnectIssueTracker
{
    private bool _disconnectIssued;

    /// <summary>True when no disconnect has returned normally since the last connect or reset.</summary>
    public bool ShouldIssueDisconnect => !_disconnectIssued;

    /// <summary>Called before the OCX connect call: a new session needs a new disconnect.</summary>
    public void OnConnectStarted() => _disconnectIssued = false;

    /// <summary>Called only after the OCX disconnect call returned without throwing.</summary>
    public void OnDisconnectReturned() => _disconnectIssued = true;

    /// <summary>Called once the control has been reset successfully for reuse.</summary>
    public void OnResetForReuse() => _disconnectIssued = false;
}
