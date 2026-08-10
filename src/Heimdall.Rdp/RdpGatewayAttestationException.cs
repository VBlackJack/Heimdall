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

namespace Heimdall.Rdp;

/// <summary>
/// Identifies the gateway attestation stage that failed.
/// </summary>
public enum RdpGatewayAttestationStep
{
    GatewayPresence,
    GatewayValidation,
    SettingsAvailability,
    SettingsWrite,
    SettingsReadBack,
    SettingsComparison
}

/// <summary>
/// Identifies an RD Gateway setting whose read-back value diverged from the requested value.
/// </summary>
public enum RdpGatewayAttestationProperty
{
    /// <summary>The configured gateway hostname.</summary>
    GatewayHostname,

    /// <summary>The gateway usage method.</summary>
    GatewayUsageMethod,

    /// <summary>The gateway profile usage method.</summary>
    GatewayProfileUsageMethod,

    /// <summary>The gateway credential source.</summary>
    GatewayCredsSource
}

/// <summary>
/// Raised when an explicitly configured RD Gateway cannot be positively attested.
/// </summary>
public sealed class RdpGatewayAttestationException : Exception
{
    public RdpGatewayAttestationException(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        Exception? innerException = null)
        : this(
            gatewayHost,
            step,
            innerException,
            Array.Empty<RdpGatewayAttestationProperty>())
    {
    }

    /// <summary>
    /// Initializes a comparison failure with every divergent setting in declaration order.
    /// </summary>
    public RdpGatewayAttestationException(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        IReadOnlyList<RdpGatewayAttestationProperty> divergentProperties,
        Exception? innerException = null)
        : this(
            gatewayHost,
            step,
            innerException,
            NormalizeDivergentProperties(step, divergentProperties))
    {
    }

    private RdpGatewayAttestationException(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        Exception? innerException,
        IReadOnlyList<RdpGatewayAttestationProperty> divergentProperties)
        : base(CreateMessage(gatewayHost, step, divergentProperties), innerException)
    {
        GatewayHost = gatewayHost;
        Step = step;
        DivergentProperties = divergentProperties;
    }

    /// <summary>Gets the configured gateway host.</summary>
    public string GatewayHost { get; }

    /// <summary>Gets the attestation stage that failed.</summary>
    public RdpGatewayAttestationStep Step { get; }

    /// <summary>
    /// Gets every divergent setting for a comparison failure, or an empty list for other stages.
    /// </summary>
    public IReadOnlyList<RdpGatewayAttestationProperty> DivergentProperties { get; }

    private static IReadOnlyList<RdpGatewayAttestationProperty> NormalizeDivergentProperties(
        RdpGatewayAttestationStep step,
        IReadOnlyList<RdpGatewayAttestationProperty> divergentProperties)
    {
        ArgumentNullException.ThrowIfNull(divergentProperties);
        if (step != RdpGatewayAttestationStep.SettingsComparison || divergentProperties.Count == 0)
        {
            return Array.Empty<RdpGatewayAttestationProperty>();
        }

        return Array.AsReadOnly(divergentProperties.ToArray());
    }

    private static string CreateMessage(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        IReadOnlyList<RdpGatewayAttestationProperty> divergentProperties)
    {
        string message = $"RD Gateway attestation failed for host '{gatewayHost}' at step '{step}'";
        if (divergentProperties.Count == 0)
        {
            return $"{message}.";
        }

        return $"{message} (divergent: {string.Join(", ", divergentProperties)}).";
    }
}
