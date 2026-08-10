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

    internal static void Apply(
        string? gatewayHost,
        IRdpGatewayTransportSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(gatewayHost))
        {
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

        try
        {
            settings.GatewayHostname = gatewayHost;
            settings.GatewayUsageMethod = GatewayUsageMethod;
            settings.GatewayProfileUsageMethod = GatewayProfileUsageMethod;
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
            readGatewayHost = settings.GatewayHostname;
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
        if (!string.Equals(readGatewayHost, gatewayHost, StringComparison.Ordinal))
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayHostname);
        }

        if (readUsageMethod != GatewayUsageMethod)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayUsageMethod);
        }

        if (readProfileUsageMethod != GatewayProfileUsageMethod)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayProfileUsageMethod);
        }

        if (readCredsSource != GatewayCredsSource)
        {
            divergentProperties.Add(RdpGatewayAttestationProperty.GatewayCredsSource);
        }

        if (divergentProperties.Count > 0)
        {
            throw RdpGatewayAttestationException.ForComparison(gatewayHost, divergentProperties);
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
