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

using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Composes what the user reads when a gateway or a host refuses an SSH
/// sign-in: the refusal the remote end sent, first and unaltered, then whatever
/// Heimdall observed locally.
/// </summary>
/// <remarks>
/// The rule lives here, once, rather than in each surface that shows a refusal.
/// It was written inside the tunnel service first and held only there: the
/// manual-tunnel command and the embedded SSH handler each composed their own
/// message, and one of them replaced the remote end's sentence outright. Every
/// surface calls these two methods so the rule is one decision, not three
/// copies of a paragraph.
/// <para>
/// The remote end's sentence is never dropped or rewritten. A wrong stored
/// password and an unloaded agent key both come back as "Permission denied
/// (password)", and only the remote end knows which it was; anything Heimdall
/// adds is an observation about Heimdall and goes after it.
/// </para>
/// </remarks>
internal static class SshAuthFailureMessageComposer
{
    /// <summary>
    /// Appends the agent observation for a refused sign-in after the remote
    /// end's own sentence.
    /// </summary>
    /// <param name="localizer">Catalogue the observation sentence is read from.</param>
    /// <param name="remoteMessage">The refusal as relayed, kept at the head.</param>
    /// <param name="chain">The hops that were dialled.</param>
    /// <param name="agentIdentityCountAtDial">
    /// Identities offerable when the dial was made, from
    /// <see cref="Heimdall.Ssh.Agents.SshAgentRegistry.Observe"/>. It is a count read
    /// before the dial on purpose: the sentence states what was offered to the
    /// remote end, and re-reading the agents after the refusal would quote keys
    /// loaded while it was in flight.
    /// </param>
    public static string? AppendAgentObservation(
        LocalizationManager localizer,
        string? remoteMessage,
        IReadOnlyList<SshConnectionParams> chain,
        int agentIdentityCountAtDial)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(chain);

        SshAuthFailureDiagnosis diagnosis =
            SshAuthFailureDiagnoser.Diagnose(chain, agentIdentityCountAtDial);
        string context = localizer.Format(diagnosis.ContextMessageKey, diagnosis.AgentIdentityCount);

        // A key missing from the catalogue resolves to itself. Showing an
        // identifier would be worse than showing nothing, and worse still than
        // the remote end's own sentence, so the context is dropped rather than
        // shipped raw. CSharpLocaleKeyCoverageTests is what makes that state
        // visible before a release; this only bounds the damage if it ships.
        return AppendSentence(remoteMessage, context, diagnosis.ContextMessageKey);
    }

    /// <summary>
    /// Joins one more sentence onto a message already composed. The head is
    /// never rewritten: what the remote end said stays first, whatever Heimdall
    /// has to add after it.
    /// <para>
    /// A sentence is dropped when it is empty, when it resolved to its own
    /// locale key - showing an identifier would be worse than showing nothing -
    /// or when the head already carries it.
    /// </para>
    /// </summary>
    public static string? AppendSentence(string? head, string? sentence, string? sentenceKey = null)
    {
        if (string.IsNullOrWhiteSpace(sentence)
            || (sentenceKey is not null
                && string.Equals(sentence, sentenceKey, StringComparison.Ordinal)))
        {
            return head;
        }

        if (string.IsNullOrWhiteSpace(head))
        {
            return sentence;
        }

        return head.Contains(sentence, StringComparison.Ordinal)
            ? head
            : $"{head} {sentence}";
    }
}
