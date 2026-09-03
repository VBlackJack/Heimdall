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
    /// wire. What the wire actually is arrives separately, in
    /// <see cref="GatewayRoute"/>.</para>
    /// </remarks>
    public bool ReusedExistingTunnel { get; init; }

    /// <summary>
    /// The gateway chain this tunnel was opened through, as it read when it was dialled; null
    /// for a direct connection and for a tunnel whose opening was not recorded.
    /// </summary>
    /// <remarks>
    /// <para><b>Recorded at the dial and handed back on reuse, rather than resolved again by
    /// whoever asks.</b> Two facts make a re-resolution wrong. The gateway list is re-read as a
    /// fresh clone each time it is asked for, so an edit made during a slow establishment lands
    /// between the dial and the question. And <see cref="ReusedExistingTunnel"/> above says a
    /// tunnel handed back here may have been dialled by an entirely different connection, from
    /// settings this process no longer holds.</para>
    /// <para><b>Withholding it instead was tried, and it reinstated the confusion it was meant to
    /// prevent.</b> A route that cannot be proven used to be reported as no route at all, which
    /// is honest and useless: the field exists so that two identically named profiles reaching
    /// two different sites can be told apart, and the reused tunnel - the very case where two
    /// such profiles are most likely to be open at once - was exactly the case that showed
    /// nothing. Saying nothing is safe against naming the wrong machine and not against being
    /// asked about two machines that look the same.</para>
    /// </remarks>
    public string? GatewayRoute { get; init; }
}
