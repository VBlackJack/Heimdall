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

using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpConnectWatchdogPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShouldArm_ReturnsTrue_ForInProgressPhases(int phaseValue)
    {
        RdpConnectionPhase phase = (RdpConnectionPhase)phaseValue;

        bool actual = RdpConnectWatchdogPolicy.ShouldArm(phase);

        Assert.True(actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ShouldArm_ReturnsFalse_ForInactiveOrConnectedPhases(int phaseValue)
    {
        RdpConnectionPhase phase = (RdpConnectionPhase)phaseValue;

        bool actual = RdpConnectWatchdogPolicy.ShouldArm(phase);

        Assert.False(actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ShouldCancel_ReturnsTrue_ForInactiveOrConnectedPhases(int phaseValue)
    {
        RdpConnectionPhase phase = (RdpConnectionPhase)phaseValue;

        bool actual = RdpConnectWatchdogPolicy.ShouldCancel(phase);

        Assert.True(actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShouldCancel_ReturnsFalse_ForInProgressPhases(int phaseValue)
    {
        RdpConnectionPhase phase = (RdpConnectionPhase)phaseValue;

        bool actual = RdpConnectWatchdogPolicy.ShouldCancel(phase);

        Assert.False(actual);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1000, 5000)]
    [InlineData(45000, 45000)]
    [InlineData(1000000, 600000)]
    public void ResolveTimeoutMs_ReturnsDisabledOrClampedValue(int configured, int expected)
    {
        int actual = RdpConnectWatchdogPolicy.ResolveTimeoutMs(configured);

        Assert.Equal(expected, actual);
    }

    // Producer: src/Heimdall.App/Views/EmbeddedRdp/RdpConnectWatchdogPolicy.cs:57
    // ResolveStageTwoTimeoutMs picks max(watchdog, autofill) + grace, clamped.
    [Fact]
    public void ResolveStageTwoTimeoutMs_WhenAutofillExceedsWatchdog_UsesAutofillPlusGrace()
    {
        int actual = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(45_000, 90_000);

        Assert.Equal(90_000 + RdpConnectWatchdogPolicy.CredentialWaitGraceMs, actual);
    }

    // Producer: src/Heimdall.App/Views/EmbeddedRdp/RdpConnectWatchdogPolicy.cs:57
    [Fact]
    public void ResolveStageTwoTimeoutMs_WhenWatchdogExceedsAutofill_UsesWatchdogPlusGrace()
    {
        int actual = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(120_000, 90_000);

        Assert.Equal(120_000 + RdpConnectWatchdogPolicy.CredentialWaitGraceMs, actual);
    }

    // Producer: src/Heimdall.App/Views/EmbeddedRdp/RdpConnectWatchdogPolicy.cs:57
    // The grace addition must never push the budget past MaxTimeoutMs.
    [Fact]
    public void ResolveStageTwoTimeoutMs_ClampsToMaxTimeoutMs()
    {
        int actual = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(600_000, 600_000);

        Assert.Equal(RdpConnectWatchdogPolicy.MaxTimeoutMs, actual);
    }

    // Producer: src/Heimdall.App/Views/EmbeddedRdp/RdpConnectWatchdogPolicy.cs:57
    // A disabled watchdog (configured <= 0) stays disabled and is never re-armed.
    [Theory]
    [InlineData(0, 90_000)]
    [InlineData(-1, 90_000)]
    public void ResolveStageTwoTimeoutMs_WhenWatchdogDisabled_ReturnsDisabled(int configured, int autofill)
    {
        int actual = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(configured, autofill);

        Assert.Equal(RdpConnectWatchdogPolicy.DisabledTimeoutMs, actual);
    }

    // Producer: src/Heimdall.App/Views/EmbeddedRdp/RdpConnectWatchdogPolicy.cs:57
    // Total over the full int range: negative autofill and overflow-prone inputs
    // are clamped without throwing.
    [Theory]
    [InlineData(45_000, -1, 45_000 + 15_000)]
    [InlineData(int.MaxValue, int.MaxValue, 600_000)]
    [InlineData(10_000, int.MaxValue, 600_000)]
    public void ResolveStageTwoTimeoutMs_IsTotalAndClamped(int configured, int autofill, int expected)
    {
        int actual = RdpConnectWatchdogPolicy.ResolveStageTwoTimeoutMs(configured, autofill);

        Assert.Equal(expected, actual);
    }
}
