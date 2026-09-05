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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class AppSettingsRdpDefaultsTests
{
    [Fact]
    public void RdpConfirmDisconnect_DefaultsToEnabled()
    {
        var settings = new AppSettings();

        Assert.True(settings.RdpConfirmDisconnect);
    }

    [Fact]
    public void RdpConfirmReconnectOnResize_DefaultsToDisabled()
    {
        var settings = new AppSettings();

        Assert.False(settings.RdpConfirmReconnectOnResize);
    }

    [Fact]
    public void RdpDefaultStrictServerAuthentication_DefaultsToDisabled()
    {
        AppSettings settings = new AppSettings();

        Assert.False(settings.RdpDefaultStrictServerAuthentication);
    }

    // The two values were restated as literals by four surfaces that fall back to them when no
    // settings are reachable; the constants are what those surfaces read now.
    [Fact]
    public void RdpTimingDefaults_MatchConstants()
    {
        AppSettings settings = new AppSettings();

        Assert.Equal(90000, AppSettings.DefaultRdpCredentialAutofillTimeoutMs);
        Assert.Equal(AppSettings.DefaultRdpCredentialAutofillTimeoutMs, settings.RdpCredentialAutofillTimeoutMs);
        Assert.Equal(10000, AppSettings.DefaultRdpResizeEnableDelayMs);
        Assert.Equal(AppSettings.DefaultRdpResizeEnableDelayMs, settings.RdpResizeEnableDelayMs);
    }

    [Fact]
    public void SshTunnelTimingDefaults_MatchConstants()
    {
        AppSettings settings = new AppSettings();

        Assert.Equal(30, AppSettings.DefaultSshKeepAliveIntervalSeconds);
        Assert.Equal(AppSettings.DefaultSshKeepAliveIntervalSeconds, settings.SshKeepAliveIntervalSeconds);
        Assert.Equal(2000, AppSettings.DefaultPlinkPortCheckIntervalMs);
        Assert.Equal(AppSettings.DefaultPlinkPortCheckIntervalMs, settings.PlinkPortCheckIntervalMs);
        Assert.Equal(2000, AppSettings.DefaultPlinkKillGracePeriodMs);
        Assert.Equal(AppSettings.DefaultPlinkKillGracePeriodMs, settings.PlinkKillGracePeriodMs);
    }

    [Fact]
    public void RdpShortcutSettings_AreNotExposed()
    {
        Assert.Null(typeof(AppSettings).GetProperty("RdpReleaseFocusShortcut"));
        Assert.Null(typeof(AppSettings).GetProperty("RdpFullscreenToggleShortcut"));
    }

    [Fact]
    public void Serialize_DoesNotWriteLegacyRdpShortcutSettings()
    {
        var json = JsonSerializer.Serialize(
            new AppSettings(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.DoesNotContain("rdpReleaseFocusShortcut", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rdpFullscreenToggleShortcut", json, StringComparison.Ordinal);
    }
}
