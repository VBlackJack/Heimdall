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
/// Raised when an explicitly configured RD Gateway cannot be positively attested.
/// </summary>
public sealed class RdpGatewayAttestationException : Exception
{
    public RdpGatewayAttestationException(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        Exception? innerException = null)
        : base($"RD Gateway attestation failed for host '{gatewayHost}' at step '{step}'.", innerException)
    {
        GatewayHost = gatewayHost;
        Step = step;
    }

    /// <summary>Gets the configured gateway host.</summary>
    public string GatewayHost { get; }

    /// <summary>Gets the attestation stage that failed.</summary>
    public RdpGatewayAttestationStep Step { get; }
}
