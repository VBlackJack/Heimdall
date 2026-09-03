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

namespace Heimdall.Ssh;

/// <summary>
/// Result of an SSH tunnel establishment attempt.
/// </summary>
/// <param name="Success">True if the tunnel was established and is forwarding traffic.</param>
/// <param name="Tunnel">Tunnel details when successful; null on failure.</param>
/// <param name="ErrorMessage">Human-readable error description on failure; null on success.</param>
/// <param name="FailureCode">Structured failure classification; null on success.</param>
public sealed record TunnelResult(
    bool Success,
    TunnelInfo? Tunnel,
    string? ErrorMessage,
    SshFailureCode? FailureCode)
{
    /// <summary>
    /// Whether this attempt handed back a tunnel that was already open rather than opening one.
    /// </summary>
    /// <remarks>
    /// <para><b>The reuse decision is taken on gateway IDENTIFIERS, so a reused tunnel need not
    /// have been opened from the settings this attempt read.</b> The reuse key is a hash over the
    /// chain's gateway identifiers, and editing a gateway's host leaves its identifier alone: a
    /// tunnel opened through Paris is therefore still reused by a later connection whose settings
    /// now say Berlin, on the same local port, to the same target.</para>
    /// <para>Reported because a caller may then not claim that what it resolved describes the
    /// wire. Nothing records the chain a live tunnel was actually opened from, so the honest
    /// answer for a reused tunnel is to say nothing about its route rather than to name the
    /// chain this attempt resolved.</para>
    /// </remarks>
    public bool ReusedExistingTunnel { get; init; }
}
