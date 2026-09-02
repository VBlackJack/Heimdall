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

using Heimdall.Core.Ssh;
using Heimdall.Ssh.OpenSsh;
using Heimdall.Ssh.Pageant;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// What the local SSH agents held at one instant, read in a single pass.
/// </summary>
/// <remarks>
/// Every member of <see cref="SshAgentRegistry"/> answers from the agents as
/// they are at the moment of the call. A message that states several of those
/// facts about one dial - how many keys were offered, and why no Plink retry
/// was attempted - must therefore read them once and keep the answers, or it
/// describes a state that never existed: a key loaded while a refusal was in
/// flight changes the count the message quotes without changing the dial it
/// claims to describe.
/// </remarks>
/// <param name="OfferableIdentityCount">
/// How many identities would be offered, from the first reachable agent that
/// holds any - see <see cref="SshAgentRegistry.OfferableIdentityCount"/>.
/// </param>
/// <param name="HasPlinkCompatibleAgent">Whether Pageant was reachable.</param>
/// <param name="HasAnyNonPlinkAgent">Whether any non-Pageant agent was reachable.</param>
public readonly record struct SshAgentObservation(
    int OfferableIdentityCount,
    bool HasPlinkCompatibleAgent,
    bool HasAnyNonPlinkAgent);

/// <summary>
/// Enumerates SSH agents in the configured priority order.
/// </summary>
public sealed class SshAgentRegistry
{
    private readonly IReadOnlyList<ISshAgent> _agents;
    private readonly Func<SshAgentPreference> _preferenceProvider;

    public SshAgentRegistry(
        IEnumerable<ISshAgent> agents,
        Func<SshAgentPreference>? preferenceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        _agents = agents.ToList();
        _preferenceProvider = preferenceProvider ?? (() => SshAgentPreference.AutoOpenSshFirst);
    }

    public static SshAgentRegistry CreateDefault(SshAgentPreference preference)
    {
        return new SshAgentRegistry(
            [new OpenSshPipeAgent(), new PageantAgent()],
            () => preference);
    }

    public IReadOnlyList<ISshAgent> GetAvailableAgents()
    {
        return EnumeratePreferredAgents()
            .Where(IsAvailableSafe)
            .ToList();
    }

    public ISshAgentKey? FindKey(byte[] publicKeyBlob)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);

        foreach (var agent in GetAvailableAgents())
        {
            IReadOnlyList<ISshAgentKey> identities;
            try
            {
                identities = agent.GetIdentities();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"SSH agent {agent.Name}: identity lookup failed: {ex.Message}");
                continue;
            }

            var key = identities.FirstOrDefault(identity =>
                identity.PublicKeyBlob.SequenceEqual(publicKeyBlob));
            if (key is not null)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// How many identities Heimdall would actually offer a gateway.
    /// <para>
    /// Authentication takes the first reachable agent that holds any identity
    /// and offers that agent's keys alone - see
    /// <c>SshConnectionFactory.TryCreateAgentAuth</c> - so this is the count of
    /// that one agent, not the sum over every agent running. Summing them would
    /// state, as an observation, a number of keys that was never presented.
    /// </para>
    /// <para>
    /// An agent that is running but empty is skipped, and so is one whose
    /// identity request fails, exactly as the authentication loop skips them.
    /// </para>
    /// </summary>
    public int OfferableIdentityCount()
    {
        foreach (var agent in GetAvailableAgents())
        {
            int count;
            try
            {
                count = agent.GetIdentities().Count;
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"SSH agent {agent.Name}: identity enumeration failed: {ex.Message}");
                continue;
            }

            if (count > 0)
            {
                return count;
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads every agent fact a refusal message quotes, in one pass over the
    /// reachable agents, so the whole message describes a single instant.
    /// </summary>
    /// <remarks>
    /// The three answers are the ones <see cref="OfferableIdentityCount"/>,
    /// <see cref="HasPlinkCompatibleAgent"/> and <see cref="HasAnyNonPlinkAgent"/>
    /// would give if all three were called at this moment, and they are computed
    /// the same way: the offerable count is that of the first reachable agent
    /// holding any identity, in the configured priority order, skipping one that
    /// is empty or whose identity request fails.
    /// </remarks>
    public SshAgentObservation Observe()
    {
        int offerable = 0;
        bool hasPlinkCompatible = false;
        bool hasNonPlink = false;

        foreach (var agent in GetAvailableAgents())
        {
            if (IsPageant(agent))
            {
                hasPlinkCompatible = true;
            }
            else
            {
                hasNonPlink = true;
            }

            if (offerable > 0)
            {
                continue;
            }

            int count;
            try
            {
                count = agent.GetIdentities().Count;
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"SSH agent {agent.Name}: identity enumeration failed: {ex.Message}");
                continue;
            }

            if (count > 0)
            {
                offerable = count;
            }
        }

        return new SshAgentObservation(offerable, hasPlinkCompatible, hasNonPlink);
    }

    public bool HasPlinkCompatibleAgent()
    {
        return GetAvailableAgents().Any(agent =>
            string.Equals(agent.Name, PageantAgent.AgentName, StringComparison.Ordinal));
    }

    public bool HasAnyNonPlinkAgent()
    {
        return GetAvailableAgents().Any(agent =>
            !string.Equals(agent.Name, PageantAgent.AgentName, StringComparison.Ordinal));
    }

    internal IReadOnlyList<ISshAgent> GetAgentsInPriorityOrder()
    {
        return EnumeratePreferredAgents().ToList();
    }

    private IEnumerable<ISshAgent> EnumeratePreferredAgents()
    {
        var preference = _preferenceProvider();
        return preference switch
        {
            SshAgentPreference.OpenSshOnly =>
                _agents.Where(IsOpenSsh),
            SshAgentPreference.PageantOnly =>
                _agents.Where(IsPageant),
            SshAgentPreference.AutoPageantFirst =>
                _agents.OrderBy(agent => IsPageant(agent) ? 0 : IsOpenSsh(agent) ? 1 : 2),
            _ =>
                _agents.OrderBy(agent => IsOpenSsh(agent) ? 0 : IsPageant(agent) ? 1 : 2)
        };
    }

    private static bool IsOpenSsh(ISshAgent agent) =>
        string.Equals(agent.Name, OpenSshPipeAgent.AgentName, StringComparison.Ordinal);

    private static bool IsPageant(ISshAgent agent) =>
        string.Equals(agent.Name, PageantAgent.AgentName, StringComparison.Ordinal);

    private static bool IsAvailableSafe(ISshAgent agent)
    {
        try
        {
            return agent.IsAvailable();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SSH agent {agent.Name}: availability probe failed: {ex.Message}");
            return false;
        }
    }
}
