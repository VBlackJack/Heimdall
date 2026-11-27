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

using System.Security.Cryptography;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// Optional write capability implemented by local SSH agents that accept key additions and removals.
/// </summary>
public interface IMutableSshAgent : ISshAgent
{
    bool AddIdentity(byte[] addIdentityWireMessage);

    bool RemoveIdentity(byte[] publicKeyBlob);

    bool RemoveAllIdentities();
}

/// <summary>Result of an active SSH agent management operation.</summary>
public sealed record SshAgentOperationResult(
    bool Success,
    string? AgentName = null,
    string? ErrorMessage = null);

/// <summary>
/// High-level facade for loading and removing SSH keys from local SSH agents
/// (Windows OpenSSH Agent, Pageant, gpg-agent).
/// </summary>
public static class SshAgentManager
{
    /// <summary>
    /// Adds a PEM/OpenSSH private key to the first available mutable SSH agent in
    /// <paramref name="registry"/>, with optional lifetime TTL (<paramref name="lifetimeSeconds"/>).
    /// </summary>
    public static SshAgentOperationResult AddKey(
        SshAgentRegistry registry,
        string privateKeyText,
        string? passphrase = null,
        string? comment = null,
        uint? lifetimeSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var targetAgent = registry.GetAvailableAgents().OfType<IMutableSshAgent>().FirstOrDefault();
        if (targetAgent is null)
        {
            return new SshAgentOperationResult(false, null, "No writable SSH agent is running.");
        }

        byte[]? message = null;
        try
        {
            message = SshAgentKeyPayloadBuilder.BuildAddIdentityMessage(
                privateKeyText,
                passphrase,
                comment,
                lifetimeSeconds);

            var added = targetAgent.AddIdentity(message);
            return added
                ? new SshAgentOperationResult(true, targetAgent.Name)
                : new SshAgentOperationResult(false, targetAgent.Name, "The SSH agent refused the key.");
        }
        catch (Exception ex)
        {
            return new SshAgentOperationResult(false, targetAgent.Name, ex.Message);
        }
        finally
        {
            if (message is not null)
            {
                CryptographicOperations.ZeroMemory(message);
            }
        }
    }

    /// <summary>
    /// Removes the key matching <paramref name="fingerprint"/> from every reachable mutable agent
    /// currently holding it.
    /// </summary>
    public static SshAgentOperationResult RemoveKeyByFingerprint(
        SshAgentRegistry registry,
        string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        foreach (var agent in registry.GetAvailableAgents().OfType<IMutableSshAgent>())
        {
            IReadOnlyList<ISshAgentKey> identities;
            try
            {
                identities = agent.GetIdentities();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var key in identities)
            {
                var fp = SshAgentKeyFingerprints.Compute(key.PublicKeyBlob);
                if (!string.Equals(fp, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    return agent.RemoveIdentity(key.PublicKeyBlob)
                        ? new SshAgentOperationResult(true, agent.Name)
                        : new SshAgentOperationResult(false, agent.Name, "The SSH agent refused to remove the key.");
                }
                catch (Exception ex)
                {
                    return new SshAgentOperationResult(false, agent.Name, ex.Message);
                }
            }
        }

        return new SshAgentOperationResult(false, null, "Key not found in any running SSH agent.");
    }

    /// <summary>Removes all loaded identities from every available mutable agent.</summary>
    public static SshAgentOperationResult RemoveAllKeys(SshAgentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var agents = registry.GetAvailableAgents().OfType<IMutableSshAgent>().ToList();
        if (agents.Count == 0)
        {
            return new SshAgentOperationResult(false, null, "No writable SSH agent is running.");
        }

        var anyRemoved = false;
        string? lastAgent = null;
        foreach (var agent in agents)
        {
            try
            {
                if (agent.RemoveAllIdentities())
                {
                    anyRemoved = true;
                    lastAgent = agent.Name;
                }
            }
            catch (Exception)
            {
                // Continue across remaining agents.
            }
        }

        return anyRemoved
            ? new SshAgentOperationResult(true, lastAgent)
            : new SshAgentOperationResult(false, agents[0].Name, "The SSH agent refused to remove identities.");
    }
}
