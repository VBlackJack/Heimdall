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

using System.Globalization;
using Heimdall.Core.Security;

namespace Heimdall.Rdp.ActiveX;

internal interface IRdpGatewayTransportSettings
{
    string GatewayHostname { get; set; }

    object GatewayUsageMethod { get; set; }

    object GatewayProfileUsageMethod { get; set; }

    object GatewayCredsSource { get; set; }
}

internal sealed class DynamicRdpGatewayTransportSettings : IRdpGatewayTransportSettings
{
    private readonly dynamic _transport;

    internal DynamicRdpGatewayTransportSettings(object transport)
    {
        _transport = transport;
    }

    public string GatewayHostname
    {
        get => _transport.GatewayHostname;
        set => _transport.GatewayHostname = value;
    }

    public object GatewayUsageMethod
    {
        get => _transport.GatewayUsageMethod;
        set => _transport.GatewayUsageMethod = value;
    }

    public object GatewayProfileUsageMethod
    {
        get => _transport.GatewayProfileUsageMethod;
        set => _transport.GatewayProfileUsageMethod = value;
    }

    public object GatewayCredsSource
    {
        get => _transport.GatewayCredsSource;
        set => _transport.GatewayCredsSource = value;
    }
}

internal static class RdpGatewayAttestation
{
    private const int GatewayUsageMethod = 1;
    private const int GatewayProfileUsageMethod = 1;
    private const int GatewayCredsSource = 0;

    /// <summary>TSC_PROXY_MODE_NONE_DIRECT: connect to the server without a gateway.</summary>
    private const int DirectUsageMethod = 0;

    /// <summary>The gateway profile the control uses when no gateway is configured.</summary>
    private const int DirectProfileUsageMethod = 0;

    /// <summary>
    /// Writes the route the profile asked for onto the control, and proves the control took it.
    /// </summary>
    /// <remarks>
    /// A profile that names no gateway is asking for a direct connection, which is an instruction
    /// and not an absence of one. The control is pooled, so a gateway written by one session
    /// stays on it until another session overwrites it: leaving the properties alone hands the
    /// next profile the previous profile's route, and its credentials with it.
    /// </remarks>
    internal static void Apply(
        string? gatewayHost,
        IRdpGatewayTransportSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(gatewayHost))
        {
            ApplyDirect(settings);
            return;
        }

        if (!InputValidator.Validate(gatewayHost, "Address"))
        {
            throw CreateFailure(gatewayHost, RdpGatewayAttestationStep.GatewayValidation);
        }

        if (settings is null)
        {
            throw CreateFailure(gatewayHost, RdpGatewayAttestationStep.SettingsAvailability);
        }

        WriteAndAttest(
            settings,
            gatewayHost,
            expectedHostname: gatewayHost,
            expectedUsageMethod: GatewayUsageMethod,
            expectedProfileUsageMethod: GatewayProfileUsageMethod);
    }

    /// <summary>
    /// Writes "no gateway" as positively as a gateway is written, and fails when one survives it.
    /// </summary>
    /// <param name="settings">
    /// The control's transport settings, or null when the control exposes none - in which case
    /// there is nowhere a gateway could have been written either, and nothing to undo.
    /// </param>
    private static void ApplyDirect(IRdpGatewayTransportSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        WriteAndAttest(
            settings,
            gatewayHost: string.Empty,
            expectedHostname: string.Empty,
            expectedUsageMethod: DirectUsageMethod,
            expectedProfileUsageMethod: DirectProfileUsageMethod);
    }

    private static void WriteAndAttest(
        IRdpGatewayTransportSettings settings,
        string gatewayHost,
        string expectedHostname,
        int expectedUsageMethod,
        int expectedProfileUsageMethod)
    {
        try
        {
            settings.GatewayHostname = expectedHostname;
            settings.GatewayUsageMethod = expectedUsageMethod;
            settings.GatewayProfileUsageMethod = expectedProfileUsageMethod;
            settings.GatewayCredsSource = GatewayCredsSource;
        }
        catch (Exception ex)
        {
            throw CreateFailure(gatewayHost, RdpGatewayAttestationStep.SettingsWrite, ex);
        }

        string readGatewayHost;
        int readUsageMethod;
        int readProfileUsageMethod;
        int readCredsSource;
        try
        {
            readGatewayHost = settings.GatewayHostname ?? string.Empty;
            readUsageMethod = Convert.ToInt32(settings.GatewayUsageMethod, CultureInfo.InvariantCulture);
            readProfileUsageMethod = Convert.ToInt32(
                settings.GatewayProfileUsageMethod,
                CultureInfo.InvariantCulture);
            readCredsSource = Convert.ToInt32(settings.GatewayCredsSource, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw CreateFailure(gatewayHost, RdpGatewayAttestationStep.SettingsReadBack, ex);
        }

        List<RdpGatewayAttestationProperty> divergentProperties = [];
        if (!string.Equals(readGatewayHost, expectedHostname, StringComparison.Ordinal))
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayHostname);
        }

        if (readUsageMethod != expectedUsageMethod)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayUsageMethod);
        }

        if (readProfileUsageMethod != expectedProfileUsageMethod)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayProfileUsageMethod);
        }

        if (readCredsSource != GatewayCredsSource)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayCredsSource);
        }

        if (divergentProperties.Count > 0)
        {
            // On a failed clear the requested host is empty, so the message names the gateway
            // that survived instead: that is the one a reader needs.
            string reportedHost = gatewayHost.Length == 0 ? readGatewayHost : gatewayHost;
            throw RdpGatewayAttestationException.ForComparison(reportedHost, divergentProperties);
        }
    }

    private static RdpGatewayAttestationException CreateFailure(
        string gatewayHost,
        RdpGatewayAttestationStep step,
        Exception? innerException = null)
    {
        return new RdpGatewayAttestationException(gatewayHost, step, innerException);
    }
}
