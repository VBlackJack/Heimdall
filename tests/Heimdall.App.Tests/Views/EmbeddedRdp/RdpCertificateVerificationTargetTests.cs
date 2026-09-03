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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes which endpoint the certificate probe is allowed to dial.
/// </summary>
/// <remarks>
/// <para>The probe opens a raw TCP connection and reads the certificate the endpoint presents. For
/// a profile routed through an RD Gateway the session reaches the target through the gateway, so a
/// probe dialling the bare target name either resolves nothing or is filtered. The verifier maps
/// that to "could not verify" and the gate maps "could not verify" to Proceed, which is
/// indistinguishable from a check that ran and found nothing wrong - the security control is inert
/// for every gateway-routed profile, permanently, and the connect pays the probe's timeout first.</para>
/// <para>So the target is null when the endpoint is not reachable from here, and the caller has to
/// say so rather than reading a silent Proceed.</para>
/// </remarks>
public sealed class RdpCertificateVerificationTargetTests
{
    [Fact]
    public void AGatewayRoutedProfileHasNoProbeTarget()
    {
        var server = new ServerProfileDto
        {
            RemoteServer = "dc01.corp.internal",
            RemotePort = 3389,
            RdpGateway = "gw.corp.example.com",
            UseDirectConnection = true,
            RdpNla = false,
        };

        Assert.Null(RdpProfileResolver.BuildCertificateVerificationTarget(server, tunnelPort: null));
    }

    [Fact]
    public void ADirectProfileIsProbedAtItsOwnEndpoint()
    {
        var server = new ServerProfileDto
        {
            RemoteServer = "dc01.corp.internal",
            RemotePort = 3389,
            UseDirectConnection = true,
        };

        RdpCertificateProbeTarget? target =
            RdpProfileResolver.BuildCertificateVerificationTarget(server, tunnelPort: null);

        Assert.NotNull(target);
        Assert.Equal("dc01.corp.internal", target!.Value.Host);
        Assert.Equal(3389, target.Value.Port);
    }

    [Fact]
    public void ATunneledProfileIsProbedAtTheLocalEndOfTheTunnel()
    {
        var server = new ServerProfileDto
        {
            RemoteServer = "dc01.corp.internal",
            RemotePort = 3389,
            SshGatewayId = "bastion",
            UseDirectConnection = false,
            LocalPort = 13389,
        };

        RdpCertificateProbeTarget? target =
            RdpProfileResolver.BuildCertificateVerificationTarget(server, tunnelPort: 51234);

        Assert.NotNull(target);
        Assert.Equal("127.0.0.1", target!.Value.Host);
        Assert.Equal(51234, target.Value.Port);
    }

    [Fact]
    public void ATunneledProfileFallsBackToTheConfiguredLocalPort()
    {
        var server = new ServerProfileDto
        {
            RemoteServer = "dc01.corp.internal",
            RemotePort = 3389,
            SshGatewayId = "bastion",
            UseDirectConnection = false,
            LocalPort = 13389,
        };

        RdpCertificateProbeTarget? target =
            RdpProfileResolver.BuildCertificateVerificationTarget(server, tunnelPort: null);

        Assert.Equal(13389, target!.Value.Port);
    }

    [Fact]
    public void TheViewBuildsItsProbeTargetThroughTheResolver()
    {
        string body = ViewSource.HandlerBody("private async Task<RdpCertificateCheckResult> VerifyServerCertificateAsync()");

        Assert.Contains(
            "RdpProfileResolver.BuildCertificateVerificationTarget(",
            body,
            StringComparison.Ordinal);
    }
}
