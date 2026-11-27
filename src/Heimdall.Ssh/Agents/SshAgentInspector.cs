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

namespace Heimdall.Ssh.Agents;

/// <summary>
/// Read-only snapshot of a public key loaded in a local SSH agent.
/// </summary>
public sealed record SshAgentKeyInfo(
    string AgentName,
    string KeyType,
    string Fingerprint,
    string PublicKeyOpenSsh,
    string Comment);

/// <summary>
/// Queries reachable local SSH agents and returns metadata for every loaded public key.
/// </summary>
public static class SshAgentInspector
{
    /// <summary>
    /// Lists every key currently held by available agents in <paramref name="registry"/>.
    /// Agents that fail to respond are skipped without throwing.
    /// </summary>
    public static IReadOnlyList<SshAgentKeyInfo> ListLoadedKeys(SshAgentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var results = new List<SshAgentKeyInfo>();
        foreach (var agent in registry.GetAvailableAgents())
        {
            IReadOnlyList<ISshAgentKey> identities;
            try
            {
                identities = agent.GetIdentities();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"SSH agent inspector: {agent.Name} failed: {ex.Message}");
                continue;
            }

            foreach (var key in identities)
            {
                var fingerprint = SshAgentKeyFingerprints.Compute(key.PublicKeyBlob);
                var base64 = Convert.ToBase64String(key.PublicKeyBlob);
                var comment = key.Comment ?? string.Empty;
                var openSshLine = string.IsNullOrWhiteSpace(comment)
                    ? $"{key.KeyType} {base64}"
                    : $"{key.KeyType} {base64} {comment}";

                results.Add(new SshAgentKeyInfo(
                    agent.Name,
                    key.KeyType,
                    fingerprint,
                    openSshLine,
                    comment));
            }
        }

        return results;
    }
}
