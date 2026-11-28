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

using Heimdall.App.Services.Handlers;

namespace Heimdall.App.Tests;

public sealed class SshCapabilityScopeTests
{
    [Fact]
    public void Evaluate_ReturnsX11Notice_WhenDirectPathRequestsX11()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: true,
            compression: false);

        Assert.NotNull(notice);
        Assert.Equal(SshUnavailableCapability.X11Forwarding, notice.UnavailableCapabilities);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenDirectPathRequestsCompressionOnly()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: false,
            compression: true);

        Assert.Null(notice);
    }

    [Fact]
    public void Evaluate_ReturnsX11OnlyNotice_WhenDirectPathRequestsBothCapabilities()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: true,
            compression: true);

        Assert.NotNull(notice);
        Assert.Equal(
            SshUnavailableCapability.X11Forwarding,
            notice.UnavailableCapabilities);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenDirectPathRequestsNeitherCapability()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: false,
            compression: false);

        Assert.Null(notice);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenExternalPuttyRequestsBothCapabilities()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.ExternalPutty,
            x11Forwarding: true,
            compression: true);

        Assert.Null(notice);
    }

    [Fact]
    public void Evaluate_ReturnsNull_WhenPlinkPipeRequestsBothCapabilities()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.PlinkPipe,
            x11Forwarding: true,
            compression: true);

        Assert.Null(notice);
    }

    [Fact]
    public void Evaluate_ReturnsStrictCryptoNotice_WhenExternalPuttyUsesStrictModern()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.ExternalPutty,
            x11Forwarding: false,
            compression: false,
            cryptoPolicy: Heimdall.Core.Ssh.SshCryptoPolicy.StrictModern);

        Assert.NotNull(notice);
        Assert.Equal(SshUnavailableCapability.StrictCryptoPolicy, notice.UnavailableCapabilities);
        Assert.Equal(
            Heimdall.App.Localization.SshLocalizationKeys.StatusSshPlinkStrictCryptoUnavailable,
            notice.StatusLocalizationKey);
    }

    [Fact]
    public void Evaluate_ReturnsStrictCryptoNotice_WhenPlinkPipeUsesStrictModern()
    {
        SshCapabilityNotice? notice = SshCapabilityScope.Evaluate(
            SshResolvedPath.PlinkPipe,
            x11Forwarding: false,
            compression: false,
            cryptoPolicy: Heimdall.Core.Ssh.SshCryptoPolicy.StrictModern);

        Assert.NotNull(notice);
        Assert.Equal(SshUnavailableCapability.StrictCryptoPolicy, notice.UnavailableCapabilities);
        Assert.Equal(
            Heimdall.App.Localization.SshLocalizationKeys.StatusSshPlinkStrictCryptoUnavailable,
            notice.StatusLocalizationKey);
    }

    [Fact]
    public void SupportsForwarding_ReturnsFalse_ForDirectPath()
    {
        bool supportsForwarding = SshCapabilityScope.SupportsForwarding(SshResolvedPath.Direct);

        Assert.False(supportsForwarding);
    }

    [Fact]
    public void SupportsForwarding_ReturnsTrue_ForExternalPutty()
    {
        bool supportsForwarding = SshCapabilityScope.SupportsForwarding(SshResolvedPath.ExternalPutty);

        Assert.True(supportsForwarding);
    }

    [Fact]
    public void SupportsForwarding_ReturnsTrue_ForPlinkPipe()
    {
        bool supportsForwarding = SshCapabilityScope.SupportsForwarding(SshResolvedPath.PlinkPipe);

        Assert.True(supportsForwarding);
    }
}
