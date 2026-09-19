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

using System.Collections.Generic;

namespace Heimdall.Core.Models;

/// <summary>
/// One open terminal a generated command can be sent to.
/// </summary>
/// <param name="Id">
/// Identifies the terminal for the length of one broadcast. It is opaque: nothing may parse it,
/// and a terminal closed between listing and sending simply stops resolving.
/// </param>
/// <param name="DisplayName">The tab's title, as the operator sees it on the tab strip.</param>
/// <param name="ConnectionType">SSH, Telnet and so on. Shown so two tabs on one host are told apart.</param>
/// <param name="IsOrigin">
/// True for the session the Command Library was opened from. That one is pre-selected, because
/// sending to it is what the Send button already does and a batch that silently dropped it would
/// surprise.
/// </param>
public sealed record CommandBroadcastTarget(
    string Id,
    string DisplayName,
    string ConnectionType,
    bool IsOrigin);

/// <summary>
/// Sends one generated command to several open terminals.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately narrow. It reaches terminals that are <b>already open</b>; it
/// never connects one. Connecting on the operator's behalf to run a command is a different
/// feature with a different set of things to get wrong (credentials, host keys, a guard that
/// refuses), and folding it in here would hide that behind a checkbox.
/// </para>
/// <para>
/// <see cref="Send"/> reports one terminal's outcome and never throws for a target that has gone
/// away: a tab can close between the moment the list is drawn and the moment the operator
/// confirms, and that is an ordinary fact to report rather than a failure of the batch.
/// </para>
/// </remarks>
public interface ICommandBroadcaster
{
    /// <summary>
    /// The terminals that can receive a command right now, in tab order.
    /// </summary>
    IReadOnlyList<CommandBroadcastTarget> GetTargets();

    /// <summary>
    /// Sends <paramref name="command"/> to one terminal.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the terminal received it; <c>false</c> when the target no longer resolves
    /// or no longer holds a terminal that can be written to.
    /// </returns>
    bool Send(string targetId, string command);
}
