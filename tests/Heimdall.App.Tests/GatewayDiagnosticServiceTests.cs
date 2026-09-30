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
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins audit 2026-09-30 S-06: a gateway diagnostic step gets the time a real tunnel hop gets.
/// </summary>
/// <remarks>
/// Each step connects AND authenticates one hop, and the real tunnel gives each hop the
/// connection parameters' own connect timeout. The step used the host key probe timeout
/// (8 s by default), a bound meant for a banner and a key exchange alone, so a hop that a real
/// tunnel reaches in ten seconds was reported as timed out.
/// </remarks>
public sealed class GatewayDiagnosticServiceTests
{
    [Fact]
    public void AStep_IsBoundedByTheHopConnectTimeout_NotTheHostKeyProbeTimeout()
    {
        AppSettings settings = new();
        List<SshConnectionParams> chain = GatewayChainResolver.ToConnectionParams(
            [new SshGatewayDto { Id = "g", Name = "g", Host = "gateway.invalid", Port = 22, User = "audit" }],
            _ => null);

        TimeSpan step = GatewayDiagnosticService.ResolveStepTimeout(chain);

        Assert.Equal(chain[0].ConnectTimeout, step);
        Assert.NotEqual(TimeSpan.FromMilliseconds(settings.HostKeyProbeTimeoutMs), step);
    }

    [Fact]
    public void AHopWithALongerConnectTimeout_WidensTheStep()
    {
        SshConnectionParams quick = new() { Host = "a.invalid", Username = "audit" };
        SshConnectionParams slow = new()
        {
            Host = "b.invalid",
            Username = "audit",
            ConnectTimeout = quick.ConnectTimeout * 2,
        };

        TimeSpan step = GatewayDiagnosticService.ResolveStepTimeout([quick, slow]);

        Assert.Equal(slow.ConnectTimeout, step);
    }
}
