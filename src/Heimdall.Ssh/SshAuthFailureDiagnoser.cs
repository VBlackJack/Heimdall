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

using Heimdall.Ssh.Agents;

namespace Heimdall.Ssh;

/// <summary>
/// What Heimdall can prove about a refused SSH authentication, from the gateway
/// chain it dialled and the local SSH agent state observed at failure time.
/// </summary>
/// <param name="AgentIsSoleAuthSource">
/// True when at least one hop of the chain carries neither a key file nor a
/// stored password, so an SSH agent identity was that hop's only sign-in source.
/// </param>
/// <param name="AgentIdentityCount">
/// How many identities were offered to the gateway when the dial failed. That
/// is the identity count of the one agent whose keys authentication presents,
/// not the sum over every agent running - see
/// <see cref="SshAgentRegistry.OfferableIdentityCount"/>.
/// </param>
/// <param name="ContextMessageKey">
/// Localization key for the sentence appended after the server's own wording.
/// </param>
public sealed record SshAuthFailureDiagnosis(
    bool AgentIsSoleAuthSource,
    int AgentIdentityCount,
    string ContextMessageKey)
{
    /// <summary>True when at least one reachable agent offered at least one identity.</summary>
    public bool AgentIdentityAvailable => AgentIdentityCount > 0;
}

/// <summary>
/// States what the local SSH agent held when a gateway refused authentication.
/// <para>
/// The transport relays the server's own wording ("Permission denied
/// (password)"), which names the last method attempted. That sentence is true
/// and it is never replaced here: a wrong stored password and an unloaded agent
/// key produce the same refusal, and only the server knows which it was. What
/// this class adds is the one thing Heimdall observed and the user cannot -
/// how many keys an agent had loaded at that moment - as a sentence the caller
/// appends after the server's.
/// </para>
/// <para>
/// It never says "your agent is missing" as a cause. With no agent key loaded
/// it says no agent key was offered; with keys loaded it says how many were
/// offered and refused.
/// </para>
/// </summary>
public static class SshAuthFailureDiagnoser
{
    /// <summary>
    /// Whether the failure code says the server refused authentication, as
    /// opposed to a transport, host key, or port problem.
    /// </summary>
    public static bool IsAuthRejection(SshFailureCode? failureCode)
    {
        return failureCode is SshFailureCode.AuthRejected
            or SshFailureCode.KeyRejected
            or SshFailureCode.PasswordRejected
            or SshFailureCode.NoSupportedAuth;
    }

    /// <summary>
    /// Whether an SSH agent identity is this hop's only possible sign-in source.
    /// A hop with a key file or a stored password is not agent-dependent: it has
    /// something of its own to offer, whatever the agent state is.
    /// </summary>
    public static bool AgentIsSoleAuthSource(SshConnectionParams connectionParams)
    {
        ArgumentNullException.ThrowIfNull(connectionParams);

        return string.IsNullOrEmpty(connectionParams.KeyPath)
            && string.IsNullOrEmpty(connectionParams.Password);
    }

    /// <summary>
    /// Diagnoses a refused authentication against the live agent registry.
    /// </summary>
    public static SshAuthFailureDiagnosis Diagnose(
        IReadOnlyList<SshConnectionParams> gatewayChain,
        SshAgentRegistry agentRegistry)
    {
        ArgumentNullException.ThrowIfNull(agentRegistry);

        return Diagnose(gatewayChain, agentRegistry.OfferableIdentityCount());
    }

    /// <summary>
    /// Diagnoses a refused authentication from an already observed agent state.
    /// </summary>
    /// <remarks>
    /// The agent-is-sole-source observation selects no wording of its own. A hop
    /// with neither key file nor stored password and no agent identity to fall
    /// back on is refused by <see cref="AuthPreflightChecker.CheckChain"/>
    /// before the chain is dialled, on the same predicate, so this method never
    /// observes that pair from the tunnel path. The observation is still
    /// reported because it is proved, and because a caller that skips the chain
    /// pre-flight can act on it.
    /// </remarks>
    public static SshAuthFailureDiagnosis Diagnose(
        IReadOnlyList<SshConnectionParams> gatewayChain,
        int agentIdentityCount)
    {
        ArgumentNullException.ThrowIfNull(gatewayChain);
        ArgumentOutOfRangeException.ThrowIfNegative(agentIdentityCount);

        bool agentIsSoleSource = gatewayChain.Count > 0
            && gatewayChain.Any(AgentIsSoleAuthSource);

        string contextKey = agentIdentityCount switch
        {
            0 => SshAuthFailureLocaleKeys.NoAgentKeyLoaded,
            1 => SshAuthFailureLocaleKeys.OneAgentKeyRefused,
            _ => SshAuthFailureLocaleKeys.ManyAgentKeysRefused
        };

        return new SshAuthFailureDiagnosis(agentIsSoleSource, agentIdentityCount, contextKey);
    }
}
