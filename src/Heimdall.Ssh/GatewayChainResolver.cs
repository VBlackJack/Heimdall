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

using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Resolves a chain of SSH gateways by walking the <see cref="SshGatewayDto.ParentGatewayId"/>
/// links from a target gateway back to the root. Detects circular dependencies and
/// enforces a maximum chain depth to prevent runaway resolution.
/// </summary>
public static class GatewayChainResolver
{
    /// <summary>
    /// Builds an ordered list of <see cref="SshConnectionParams"/> from the root gateway
    /// to the target gateway by following ParentGatewayId references.
    /// </summary>
    /// <param name="targetGatewayId">ID of the gateway to start resolution from.</param>
    /// <param name="allGateways">All known gateway definitions.</param>
    /// <param name="decryptPassword">
    /// Callback that decrypts a DPAPI-encrypted password string.
    /// Receives the encrypted value, returns plaintext or null.
    /// </param>
    /// <param name="maxDepth">Maximum allowed chain depth (default 5).</param>
    /// <returns>Ordered list from root gateway to target gateway.</returns>
    /// <exception cref="ArgumentException">Target gateway not found.</exception>
    /// <exception cref="GatewayChainException">Circular dependency or depth exceeded.</exception>
    public static List<SshConnectionParams> ResolveChain(
        string targetGatewayId,
        IReadOnlyList<SshGatewayDto> allGateways,
        Func<string, string?> decryptPassword,
        int maxDepth = 5,
        SshAgentPreference sshAgentPreference = SshAgentPreference.AutoOpenSshFirst)
    {
        ArgumentNullException.ThrowIfNull(decryptPassword);

        var chain = ResolveChainDtos(targetGatewayId, allGateways, maxDepth);
        return ToConnectionParams(chain, decryptPassword, sshAgentPreference);
    }

    /// <summary>
    /// Builds an ordered list of gateway DTOs from the root gateway to the
    /// target gateway by following ParentGatewayId references.
    /// </summary>
    /// <param name="targetGatewayId">ID of the gateway to start resolution from.</param>
    /// <param name="allGateways">All known gateway definitions.</param>
    /// <param name="maxDepth">Maximum allowed chain depth (default 5).</param>
    /// <returns>Ordered list from root gateway to target gateway.</returns>
    /// <exception cref="ArgumentException">Target gateway not found.</exception>
    /// <exception cref="GatewayChainException">Circular dependency or depth exceeded.</exception>
    public static List<SshGatewayDto> ResolveChainDtos(
        string targetGatewayId,
        IReadOnlyList<SshGatewayDto> allGateways,
        int maxDepth = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetGatewayId);
        ArgumentNullException.ThrowIfNull(allGateways);

        var gatewayMap = allGateways.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        if (!gatewayMap.TryGetValue(targetGatewayId, out var targetGateway))
        {
            throw new ArgumentException($"Gateway '{targetGatewayId}' not found.", nameof(targetGatewayId));
        }

        // Walk from target to root, collecting the chain in reverse
        var chain = new List<SshGatewayDto>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = targetGateway;

        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                throw new GatewayChainException(
                    SshFailureCode.CircularChainDependency,
                    $"Circular dependency detected in gateway chain: '{current.Id}' was already visited.");
            }

            if (chain.Count >= maxDepth)
            {
                throw new GatewayChainException(
                    SshFailureCode.ChainDepthExceeded,
                    $"Gateway chain depth exceeds maximum of {maxDepth}. " +
                    "Check for excessively nested gateway configurations.");
            }

            chain.Add(current);

            if (string.IsNullOrWhiteSpace(current.ParentGatewayId))
            {
                break;
            }

            if (!gatewayMap.TryGetValue(current.ParentGatewayId, out current))
            {
                throw new ArgumentException(
                    $"Parent gateway '{chain[^1].ParentGatewayId}' referenced by " +
                    $"gateway '{chain[^1].Id}' not found.",
                    nameof(allGateways));
            }
        }

        // Reverse to get root-first order
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Converts an ordered gateway DTO chain to SSH connection parameters.
    /// </summary>
    public static List<SshConnectionParams> ToConnectionParams(
        IReadOnlyList<SshGatewayDto> gatewayChain,
        Func<string, string?> decryptPassword,
        SshAgentPreference sshAgentPreference = SshAgentPreference.AutoOpenSshFirst)
    {
        ArgumentNullException.ThrowIfNull(gatewayChain);
        ArgumentNullException.ThrowIfNull(decryptPassword);

        return gatewayChain.Select(gw => ToConnectionParams(gw, decryptPassword, sshAgentPreference)).ToList();
    }

    /// <summary>
    /// Converts a <see cref="SshGatewayDto"/> to <see cref="SshConnectionParams"/>,
    /// decrypting the password if present.
    /// </summary>
    private static SshConnectionParams ToConnectionParams(
        SshGatewayDto gateway,
        Func<string, string?> decryptPassword,
        SshAgentPreference sshAgentPreference)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(gateway.SshPasswordEncrypted))
        {
            password = decryptPassword(gateway.SshPasswordEncrypted);
        }

        string? keyPassphrase = null;
        if (!string.IsNullOrEmpty(gateway.SshKeyPassphraseEncrypted))
        {
            keyPassphrase = decryptPassword(gateway.SshKeyPassphraseEncrypted);
        }

        return new SshConnectionParams
        {
            Host = gateway.Host,
            Port = gateway.Port,
            Username = gateway.User,
            KeyPath = gateway.KeyPath,
            Password = password,
            KeyPassphrase = keyPassphrase,
            SshAgentPreference = sshAgentPreference,
            UseLegacyPasswordAsKeyPassphrase = gateway.UsesLegacySshCredentialMapping,
            LegacyCredentialName = gateway.Name
        };
    }
}
