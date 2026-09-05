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
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public class SchemaValidatorTests
{
    // ── ValidateSettings: valid inputs ────────────────────────────────

    [Fact]
    public void ValidateSettings_DefaultAppSettings_IsValid()
    {
        var settings = new AppSettings();

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateSettings_CustomValidSettings_IsValid()
    {
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 2560,
            DefaultResolutionHeight = 1440,
            DefaultLocale = "fr",
            DefaultTheme = "Drakul",
            TunnelEstablishmentDelayMs = 5000,
            TunnelRetryDelayMs = 3000,
            ProcessKillTimeoutMs = 4000,
            SshDefaultMode = "Embedded",
            RdpDefaultMode = "Embedded",
            AntiIdleIntervalSeconds = 120,
            RdpDefaultColorDepth = 16,
            MaxEmbeddedSessions = 5,
            SidebarWidth = 300
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ── ValidateSettings: null input ──────────────────────────────────

    [Fact]
    public void ValidateSettings_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaValidator.ValidateSettings(null!));
    }

    // ── ValidateSettings: invalid locale/theme/mode ───────────────────

    [Fact]
    public void ValidateSettings_InvalidLocale_ReturnsError()
    {
        var settings = new AppSettings { DefaultLocale = "de" };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DefaultLocale"));
    }

    [Fact]
    public void ValidateSettings_EmptyLocale_ReturnsError()
    {
        var settings = new AppSettings { DefaultLocale = "" };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DefaultLocale"));
    }

    [Fact]
    public void ValidateSettings_UnknownTheme_IsAccepted()
    {
        var settings = new AppSettings { DefaultTheme = "Blue" };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Contains("DefaultTheme"));
    }

    [Fact]
    public void ValidateSettings_InvalidSshDefaultMode_ReturnsError()
    {
        var settings = new AppSettings { SshDefaultMode = "Inline" };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SshDefaultMode"));
    }

    [Fact]
    public void ValidateSettings_InvalidRdpDefaultMode_ReturnsError()
    {
        var settings = new AppSettings { RdpDefaultMode = "Detached" };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpDefaultMode"));
    }

    // ── ValidateSettings: range violations ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(639)]
    [InlineData(7681)]
    [InlineData(-1)]
    public void ValidateSettings_InvalidResolutionWidth_ReturnsError(int width)
    {
        var settings = new AppSettings { DefaultResolutionWidth = width };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DefaultResolutionWidth"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(639)]
    [InlineData(7681)]
    public void ValidateSettings_InvalidResolutionHeight_ReturnsError(int height)
    {
        var settings = new AppSettings { DefaultResolutionHeight = height };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DefaultResolutionHeight"));
    }

    [Fact]
    public void ValidateSettings_BoundaryResolution_640_IsValid()
    {
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 640,
            DefaultResolutionHeight = 640
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateSettings_BoundaryResolution_7680_IsValid()
    {
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 7680,
            DefaultResolutionHeight = 7680
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void ValidateSettings_TunnelEstablishmentDelayMs_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { TunnelEstablishmentDelayMs = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TunnelEstablishmentDelayMs"));
    }

    [Fact]
    public void ValidateSettings_TunnelEstablishmentDelayMs_Zero_IsValid()
    {
        var settings = new AppSettings { TunnelEstablishmentDelayMs = 0 };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void ValidateSettings_TunnelRetryDelayMs_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { TunnelRetryDelayMs = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TunnelRetryDelayMs"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void ValidateSettings_ProcessKillTimeoutMs_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { ProcessKillTimeoutMs = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ProcessKillTimeoutMs"));
    }

    [Theory]
    // 0 is the value that turns the timer off and is declared as such; 3600 is the ceiling the
    // settings screen has always accepted. Both used to be warned about by the loader alone.
    [InlineData(9)]
    [InlineData(3601)]
    [InlineData(1)]
    [InlineData(-5)]
    public void ValidateSettings_AntiIdleIntervalSeconds_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { AntiIdleIntervalSeconds = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("AntiIdleIntervalSeconds"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(600)]
    [InlineData(3600)]
    public void ValidateSettings_AntiIdleIntervalSeconds_BoundaryValues_IsValid(int value)
    {
        var settings = new AppSettings { AntiIdleIntervalSeconds = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    // 8 and 15 sit below the floor: the connect-time resolver rewrote them to 16 in silence, so
    // the bound now says what the session will actually get.
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(33)]
    [InlineData(0)]
    public void ValidateSettings_RdpDefaultColorDepth_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { RdpDefaultColorDepth = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpDefaultColorDepth"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void ValidateSettings_RdpDefaultColorDepth_ValidValues_IsValid(int value)
    {
        var settings = new AppSettings { RdpDefaultColorDepth = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }


    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(60001)]
    public void ValidateSettings_RdpResizeEnableDelayMs_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { RdpResizeEnableDelayMs = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.RdpResizeEnableDelayMs)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(60000)]
    public void ValidateSettings_RdpResizeEnableDelayMs_BoundaryValues_IsValid(int value)
    {
        var settings = new AppSettings { RdpResizeEnableDelayMs = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4999)]
    [InlineData(600001)]
    public void ValidateSettings_RdpConnectWatchdogTimeoutMs_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { RdpConnectWatchdogTimeoutMs = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.RdpConnectWatchdogTimeoutMs)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    [InlineData(600000)]
    public void ValidateSettings_RdpConnectWatchdogTimeoutMs_BoundaryValues_IsValid(int value)
    {
        AppSettings settings = new() { RdpConnectWatchdogTimeoutMs = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4999)]
    [InlineData(300001)]
    public void ValidateSettings_RdpKeepAliveIntervalMs_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { RdpKeepAliveIntervalMs = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.RdpKeepAliveIntervalMs)));
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(300000)]
    public void ValidateSettings_RdpKeepAliveIntervalMs_BoundaryValues_IsValid(int value)
    {
        AppSettings settings = new() { RdpKeepAliveIntervalMs = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ValidateSettings_SshAutoReconnectFirstDelaySeconds_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { SshAutoReconnectFirstDelaySeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.SshAutoReconnectFirstDelaySeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ValidateSettings_SshAutoReconnectSecondDelaySeconds_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { SshAutoReconnectSecondDelaySeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.SshAutoReconnectSecondDelaySeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void ValidateSettings_SshAutoReconnectSubsequentDelaySeconds_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { SshAutoReconnectSubsequentDelaySeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.SshAutoReconnectSubsequentDelaySeconds)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    public void ValidateSettings_SshAutoReconnectDelaySeconds_BoundaryValues_AreValid(int value)
    {
        AppSettings settings = new()
        {
            SshAutoReconnectFirstDelaySeconds = value,
            SshAutoReconnectSecondDelaySeconds = value,
            SshAutoReconnectSubsequentDelaySeconds = value
        };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void ValidateSettings_SshConnectTimeExitWindowSeconds_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { SshConnectTimeExitWindowSeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.SshConnectTimeExitWindowSeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public void ValidateSettings_SshConnectTimeExitWindowSeconds_BoundaryValues_AreValid(int value)
    {
        AppSettings settings = new() { SshConnectTimeExitWindowSeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void ValidateSettings_SshTmoutResetIntervalSeconds_OutOfRange_ReturnsError(int value)
    {
        AppSettings settings = new() { SshTmoutResetIntervalSeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(AppSettings.SshTmoutResetIntervalSeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public void ValidateSettings_SshTmoutResetIntervalSeconds_BoundaryValues_AreValid(int value)
    {
        AppSettings settings = new() { SshTmoutResetIntervalSeconds = value };

        ValidationResult result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-1)]
    public void ValidateSettings_MaxEmbeddedSessions_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { MaxEmbeddedSessions = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MaxEmbeddedSessions"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void ValidateSettings_MaxEmbeddedSessions_BoundaryValues_IsValid(int value)
    {
        var settings = new AppSettings { MaxEmbeddedSessions = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void ValidateSettings_SidebarWidth_OutOfRange_ReturnsError(int value)
    {
        var settings = new AppSettings { SidebarWidth = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SidebarWidth"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void ValidateSettings_SidebarWidth_BoundaryValues_IsValid(int value)
    {
        var settings = new AppSettings { SidebarWidth = value };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateSettings_MultipleErrors_ReportsAll()
    {
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 0,
            DefaultResolutionHeight = 0,
            DefaultLocale = "xx",
            SshDefaultMode = "invalid",
            RdpDefaultMode = "invalid",
            MaxEmbeddedSessions = 0
        };

        var result = SchemaValidator.ValidateSettings(settings);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 6);
    }

    // ── ValidateServer: valid inputs ──────────────────────────────────

    [Fact]
    public void ValidateServer_ValidRdpServer_IsValid()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-1",
            DisplayName = "Production Web",
            RemoteServer = "10.0.0.1",
            RemotePort = 3389,
            LocalPort = 33890,
            SshPort = 22,
            ConnectionType = "RDP",
            SshMode = "External",
            RdpMode = "Embedded",
            RdpAspectRatio = "Stretch",
            RdpColorDepth = 32,
            RdpAudioMode = 0
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateServer_ValidSshServer_IsValid()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-2",
            DisplayName = "Dev SSH",
            RemoteServer = "dev.example.com",
            ConnectionType = "SSH",
            SshMode = "Embedded",
            RdpMode = "External",
            RdpAspectRatio = "Auto"
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateServer_ValidSftpServer_IsValid()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-3",
            DisplayName = "File Server",
            RemoteServer = "files.example.com",
            ConnectionType = "SFTP",
            SshMode = "External",
            RdpMode = "External",
            RdpAspectRatio = "16:9"
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateServer_FixedResolutionProfileWithinRange_IsValid()
    {
        var server = CreateValidServer();
        server.RdpResolutionMode = RdpResolutionMode.Fixed;
        server.RdpFixedWidth = 1920;
        server.RdpFixedHeight = 1080;
        server.RdpResizeEnableDelayMs = 1000;

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(199, 1080)]
    [InlineData(7681, 1080)]
    [InlineData(1920, 199)]
    [InlineData(1920, 4321)]
    public void ValidateServer_FixedResolutionProfileOutOfRange_ReturnsError(int width, int height)
    {
        var server = CreateValidServer();
        server.RdpResolutionMode = RdpResolutionMode.Fixed;
        server.RdpFixedWidth = width;
        server.RdpFixedHeight = height;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpFixed"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(60000)]
    public void ValidateServer_RdpResizeEnableDelayAllowedRange_IsValid(int? delayMs)
    {
        var server = CreateValidServer();
        server.RdpResizeEnableDelayMs = delayMs;

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(60001)]
    public void ValidateServer_RdpResizeEnableDelayOutOfRange_ReturnsError(int delayMs)
    {
        var server = CreateValidServer();
        server.RdpResizeEnableDelayMs = delayMs;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(nameof(ServerProfileDto.RdpResizeEnableDelayMs)));
    }

    // ── ValidateServer: null input ────────────────────────────────────

    [Fact]
    public void ValidateServer_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaValidator.ValidateServer(null!));
    }

    // ── ValidateServer: missing required fields ───────────────────────

    [Fact]
    public void ValidateServer_EmptyId_ReturnsError()
    {
        var server = CreateValidServer();
        server.Id = "";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Id"));
    }

    [Fact]
    public void ValidateServer_WhitespaceId_ReturnsError()
    {
        var server = CreateValidServer();
        server.Id = "   ";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Id"));
    }

    [Fact]
    public void ValidateServer_EmptyDisplayName_ReturnsError()
    {
        var server = CreateValidServer();
        server.DisplayName = "";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DisplayName"));
    }

    [Fact]
    public void ValidateServer_EmptyRemoteServer_ReturnsError()
    {
        var server = CreateValidServer();
        server.RemoteServer = "";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RemoteServer"));
    }

    // ── ValidateServer: invalid hostname ──────────────────────────────

    [Theory]
    [InlineData("host with spaces")]
    [InlineData("host@invalid")]
    [InlineData("-invalid-start")]
    [InlineData("invalid..double.dot")]
    public void ValidateServer_InvalidHostname_ReturnsError(string hostname)
    {
        var server = CreateValidServer();
        server.RemoteServer = hostname;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RemoteServer"));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.100")]
    [InlineData("myhost")]
    [InlineData("server.example.com")]
    [InlineData("sub.domain.example.co.uk")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void ValidateServer_ValidHostname_IsAccepted(string hostname)
    {
        var server = CreateValidServer();
        server.RemoteServer = hostname;

        var result = SchemaValidator.ValidateServer(server);

        Assert.DoesNotContain(result.Errors, e => e.Contains("RemoteServer"));
    }

    // ── ValidateServer: invalid ports ─────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(-100)]
    public void ValidateServer_InvalidRemotePort_ReturnsError(int port)
    {
        var server = CreateValidServer();
        server.RemotePort = port;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RemotePort"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ValidateServer_InvalidLocalPort_ReturnsError(int port)
    {
        var server = CreateValidServer();
        server.LocalPort = port;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("LocalPort"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ValidateServer_InvalidSshPort_ReturnsError(int port)
    {
        var server = CreateValidServer();
        server.SshPort = port;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SshPort"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(3389)]
    [InlineData(65535)]
    public void ValidateServer_ValidPortBoundaries_IsAccepted(int port)
    {
        var server = CreateValidServer();
        server.RemotePort = port;
        server.LocalPort = port;
        server.SshPort = port;

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    // ── ValidateServer: invalid connection type / modes ────────────────

    [Fact]
    public void ValidateServer_ToolConnectionType_IsValid()
    {
        ServerProfileDto server = CreateValidServer();
        server.ConnectionType = "TOOL:PING";

        ValidationResult result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateServer_InvalidConnectionType_ReturnsError()
    {
        var server = CreateValidServer();
        server.ConnectionType = "GOPHER";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ConnectionType"));
    }

    [Fact]
    public void ValidateServer_EmptyConnectionType_ReturnsError()
    {
        var server = CreateValidServer();
        server.ConnectionType = "";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ConnectionType"));
    }

    [Fact]
    public void ValidateServer_InvalidSshMode_ReturnsError()
    {
        var server = CreateValidServer();
        server.SshMode = "Inline";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SshMode"));
    }

    [Fact]
    public void ValidateServer_InvalidRdpMode_ReturnsError()
    {
        var server = CreateValidServer();
        server.RdpMode = "Detached";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpMode"));
    }

    [Fact]
    public void ValidateServer_InvalidAspectRatio_ReturnsError()
    {
        var server = CreateValidServer();
        server.RdpAspectRatio = "32:9";

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpAspectRatio"));
    }

    [Theory]
    [InlineData("Stretch")]
    [InlineData("Auto")]
    [InlineData("16:9")]
    [InlineData("4:3")]
    [InlineData("21:9")]
    public void ValidateServer_ValidAspectRatio_IsAccepted(string ratio)
    {
        var server = CreateValidServer();
        server.RdpAspectRatio = ratio;

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    // ── ValidateServer: RDP color depth and audio mode ────────────────

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(33)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateServer_InvalidRdpColorDepth_ReturnsError(int depth)
    {
        var server = CreateValidServer();
        server.RdpColorDepth = depth;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpColorDepth"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ValidateServer_InvalidRdpAudioMode_ReturnsError(int mode)
    {
        var server = CreateValidServer();
        server.RdpAudioMode = mode;

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RdpAudioMode"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidateServer_ValidRdpAudioMode_IsAccepted(int mode)
    {
        var server = CreateValidServer();
        server.RdpAudioMode = mode;

        var result = SchemaValidator.ValidateServer(server);

        Assert.True(result.IsValid);
    }

    // ── ValidateServer: multiple errors ───────────────────────────────

    [Fact]
    public void ValidateServer_MultipleInvalidFields_ReportsAllErrors()
    {
        var server = new ServerProfileDto
        {
            Id = "",
            DisplayName = "",
            RemoteServer = "",
            RemotePort = 0,
            ConnectionType = "INVALID",
            SshMode = "INVALID",
            RdpMode = "INVALID",
            RdpAspectRatio = "INVALID",
            RdpColorDepth = 0,
            RdpAudioMode = -1
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.False(result.IsValid);
        // At least: Id, DisplayName, RemoteServer, RemotePort, ConnectionType,
        // SshMode, RdpMode, RdpAspectRatio, RdpColorDepth, RdpAudioMode
        Assert.True(result.Errors.Count >= 10);
    }

    // ── ValidateServer: case-insensitive enums ────────────────────────

    [Theory]
    [InlineData("rdp")]
    [InlineData("Rdp")]
    [InlineData("RDP")]
    public void ValidateServer_ConnectionType_CaseInsensitive(string type)
    {
        var server = CreateValidServer();
        server.ConnectionType = type;

        var result = SchemaValidator.ValidateServer(server);

        Assert.DoesNotContain(result.Errors, e => e.Contains("ConnectionType"));
    }

    [Theory]
    [InlineData("external")]
    [InlineData("EMBEDDED")]
    [InlineData("Embedded")]
    public void ValidateServer_Modes_CaseInsensitive(string mode)
    {
        var server = CreateValidServer();
        server.SshMode = mode;
        server.RdpMode = mode;

        var result = SchemaValidator.ValidateServer(server);

        Assert.DoesNotContain(result.Errors, e => e.Contains("SshMode"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("RdpMode"));
    }

    // ── ValidateServer: WinRM transport coherence ─────────────────────

    // WINRM-011. The dialog moves the port when TLS is ticked, so a profile made in the
    // interface is coherent. Every other boundary keeps an explicit port as written: the
    // deserializer only derives one when the field is ABSENT, the launch builder honours an
    // explicit port whether or not TLS is on, and nothing validated the pair. An imported or
    // hand-edited profile therefore spoke TLS to the plaintext port and said nothing.
    [Fact]
    public void ValidateServer_WinRmOverTlsOnThePlaintextPort_IsReported()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-winrm-tls",
            DisplayName = "Imported WinRM",
            RemoteServer = "winrm.example.com",
            ConnectionType = "WINRM",
            SshMode = "External",
            RdpMode = "External",
            RdpAspectRatio = "Auto",
            WinRmUseSsl = true,
            WinRmPort = DefaultPorts.WinRmHttp
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.Contains(result.Errors, error => error.Contains(nameof(server.WinRmPort), StringComparison.Ordinal));
    }

    // The pair is only wrong together. Neither half alone is a defect, and flagging one of
    // them would put a warning on every ordinary profile.
    [Theory]
    [InlineData(true, 5986)]
    [InlineData(false, 5985)]
    [InlineData(false, 5986)]
    public void ValidateServer_CoherentWinRmTransport_IsAccepted(bool useSsl, int port)
    {
        var server = new ServerProfileDto
        {
            Id = "srv-winrm-ok",
            DisplayName = "WinRM",
            RemoteServer = "winrm.example.com",
            ConnectionType = "WINRM",
            SshMode = "External",
            RdpMode = "External",
            RdpAspectRatio = "Auto",
            WinRmUseSsl = useSsl,
            WinRmPort = port
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.DoesNotContain(result.Errors, error => error.Contains(nameof(server.WinRmPort), StringComparison.Ordinal));
    }

    // A non-WinRM profile carries a WinRmPort like any other field. Reading it as a transport
    // choice would report a defect on profiles that never speak WinRM at all.
    [Fact]
    public void ValidateServer_NonWinRmProfileCarryingTheSamePair_IsLeftAlone()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-ssh",
            DisplayName = "SSH box",
            RemoteServer = "ssh.example.com",
            ConnectionType = "SSH",
            SshMode = "External",
            RdpMode = "External",
            RdpAspectRatio = "Auto",
            WinRmUseSsl = true,
            WinRmPort = DefaultPorts.WinRmHttp
        };

        var result = SchemaValidator.ValidateServer(server);

        Assert.DoesNotContain(result.Errors, error => error.Contains(nameof(server.WinRmPort), StringComparison.Ordinal));
    }

    // Load reports it, and does not refuse the profile. A deliberate listener with TLS on
    // 5985 is unusual, not impossible, and this arrives on databases already written.
    [Fact]
    public void DiagnoseServerLoad_WinRmOverTlsOnThePlaintextPort_WarnsWithoutBlocking()
    {
        var server = new ServerProfileDto
        {
            Id = "srv-winrm-tls",
            DisplayName = "Imported WinRM",
            RemoteServer = "winrm.example.com",
            ConnectionType = "WINRM",
            SshMode = "External",
            RdpMode = "External",
            RdpAspectRatio = "Auto",
            WinRmUseSsl = true,
            WinRmPort = DefaultPorts.WinRmHttp
        };

        var diagnostics = SchemaValidator.DiagnoseServerLoad(server).Diagnostics;

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Message.Contains(nameof(server.WinRmPort), StringComparison.Ordinal)
                && diagnostic.Severity == ValidationSeverity.Warning);
    }

    // ── ValidateGateway: valid inputs ─────────────────────────────────

    [Fact]
    public void ValidateGateway_ValidGateway_IsValid()
    {
        var gateway = new SshGatewayDto
        {
            Id = "gw-1",
            Name = "Bastion Host",
            Host = "bastion.example.com",
            Port = 22,
            User = "admin"
        };

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateGateway_ValidGatewayWithIpAddress_IsValid()
    {
        var gateway = new SshGatewayDto
        {
            Id = "gw-2",
            Name = "Jump Server",
            Host = "192.168.1.1",
            Port = 2222,
            User = "root"
        };

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    // ── ValidateGateway: null input ───────────────────────────────────

    [Fact]
    public void ValidateGateway_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaValidator.ValidateGateway(null!));
    }

    // ── ValidateGateway: missing required fields ──────────────────────

    [Fact]
    public void ValidateGateway_EmptyId_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.Id = "";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Id"));
    }

    [Fact]
    public void ValidateGateway_EmptyName_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.Name = "";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Name"));
    }

    [Fact]
    public void ValidateGateway_EmptyHost_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.Host = "";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Host"));
    }

    [Fact]
    public void ValidateGateway_EmptyUser_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.User = "";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("User"));
    }

    [Fact]
    public void ValidateGateway_WhitespaceFields_ReturnsErrors()
    {
        var gateway = new SshGatewayDto
        {
            Id = "   ",
            Name = "   ",
            Host = "   ",
            Port = 22,
            User = "   "
        };

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 4);
    }

    // ── ValidateGateway: invalid hostname ─────────────────────────────

    [Fact]
    public void ValidateGateway_InvalidHost_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.Host = "host with spaces";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Host") && e.Contains("invalid hostname"));
    }

    // ── ValidateGateway: invalid port ─────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ValidateGateway_InvalidPort_ReturnsError(int port)
    {
        var gateway = CreateValidGateway();
        gateway.Port = port;

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Port"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(65535)]
    public void ValidateGateway_ValidPortBoundaries_IsAccepted(int port)
    {
        var gateway = CreateValidGateway();
        gateway.Port = port;

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    // ── ValidateGateway: KeyPath validation ───────────────────────────

    [Fact]
    public void ValidateGateway_NullKeyPath_IsValid()
    {
        var gateway = CreateValidGateway();
        gateway.KeyPath = null;

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateGateway_EmptyKeyPath_IsValid()
    {
        var gateway = CreateValidGateway();
        gateway.KeyPath = "";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateGateway_NonExistentKeyPath_IsAccepted()
    {
        var gateway = CreateValidGateway();
        gateway.KeyPath = @"C:\nonexistent\path\key.ppk";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Contains("KeyPath"));
    }

    [Fact]
    public void ValidateGateway_ExistingKeyPath_IsValid()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var gateway = CreateValidGateway();
            gateway.KeyPath = tempFile;

            var result = SchemaValidator.ValidateGateway(gateway);

            Assert.True(result.IsValid);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── ValidateGateway: self-referencing parent ──────────────────────

    [Fact]
    public void ValidateGateway_SelfReferencingParent_ReturnsError()
    {
        var gateway = CreateValidGateway();
        gateway.Id = "gw-1";
        gateway.ParentGatewayId = "gw-1";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ParentGatewayId") && e.Contains("own parent"));
    }

    [Fact]
    public void ValidateGateway_DifferentParent_IsValid()
    {
        var gateway = CreateValidGateway();
        gateway.Id = "gw-1";
        gateway.ParentGatewayId = "gw-2";

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateGateway_NullParent_IsValid()
    {
        var gateway = CreateValidGateway();
        gateway.ParentGatewayId = null;

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.True(result.IsValid);
    }

    // ── ValidateGateway: multiple errors ──────────────────────────────

    [Fact]
    public void ValidateGateway_MultipleInvalidFields_ReportsAllErrors()
    {
        var gateway = new SshGatewayDto
        {
            Id = "",
            Name = "",
            Host = "",
            Port = 0,
            User = ""
        };

        var result = SchemaValidator.ValidateGateway(gateway);

        Assert.False(result.IsValid);
        // At least: Id, Name, Host, Port, User
        Assert.True(result.Errors.Count >= 5);
    }

    // ── ValidationResult record ───────────────────────────────────────

    [Fact]
    public void ValidationResult_ValidResult_HasEmptyErrors()
    {
        var result = new ValidationResult(true, new List<string>());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidationResult_InvalidResult_HasErrors()
    {
        var errors = new List<string> { "field: error message" };
        var result = new ValidationResult(false, errors);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static ServerProfileDto CreateValidServer()
    {
        return new ServerProfileDto
        {
            Id = "srv-test",
            DisplayName = "Test Server",
            RemoteServer = "10.0.0.1",
            RemotePort = 3389,
            LocalPort = 33890,
            SshPort = 22,
            ConnectionType = "RDP",
            SshMode = "External",
            RdpMode = "Embedded",
            RdpAspectRatio = "Stretch",
            RdpColorDepth = 32,
            RdpAudioMode = 0
        };
    }

    private static SshGatewayDto CreateValidGateway()
    {
        return new SshGatewayDto
        {
            Id = "gw-test",
            Name = "Test Gateway",
            Host = "bastion.example.com",
            Port = 22,
            User = "admin"
        };
    }
}
