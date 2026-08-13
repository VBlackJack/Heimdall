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

using System.Drawing;
using System.IO;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Rdp;

namespace Heimdall.App.Tests;

public sealed class RdpProfileResolverTests
{
    [Fact]
    public void ResolveCredentialIdentity_ExplicitDomainWins()
    {
        (string username, string? domain) = RdpProfileResolver.ResolveCredentialIdentity(
            @"LEGACY\admin",
            "CORP");

        Assert.Equal(@"LEGACY\admin", username);
        Assert.Equal("CORP", domain);
    }

    [Fact]
    public void ResolveCredentialIdentity_NetBiosUsername_KeepsFullUsernameAndExtractsDomain()
    {
        (string username, string? domain) = RdpProfileResolver.ResolveCredentialIdentity(
            @"NSBF2\a150058",
            null);

        Assert.Equal(@"NSBF2\a150058", username);
        Assert.Equal("NSBF2", domain);
    }

    [Fact]
    public void ResolveCredentialIdentity_UpnUsername_KeepsFullUsernameAndExtractsDomain()
    {
        (string username, string? domain) = RdpProfileResolver.ResolveCredentialIdentity(
            "admin@corp.local",
            null);

        Assert.Equal("admin@corp.local", username);
        Assert.Equal("corp.local", domain);
    }

    [Fact]
    public void ResolveCredentialIdentity_PlainUsername_HasNoDomain()
    {
        (string username, string? domain) = RdpProfileResolver.ResolveCredentialIdentity(
            "admin",
            null);

        Assert.Equal("admin", username);
        Assert.Null(domain);
    }

    [Fact]
    public void ResolveCredentialIdentity_NullOrEmptyUsername_ReturnsEmptyUsernameAndNoDomain()
    {
        (string nullUsername, string? nullDomain) = RdpProfileResolver.ResolveCredentialIdentity(
            null,
            null);
        (string emptyUsername, string? emptyDomain) = RdpProfileResolver.ResolveCredentialIdentity(
            "",
            null);

        Assert.Equal(string.Empty, nullUsername);
        Assert.Null(nullDomain);
        Assert.Equal(string.Empty, emptyUsername);
        Assert.Null(emptyDomain);
    }

    [Fact]
    public void BuildRedirections_UsesSettingsWhenFlagIsTrue()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = true,
            RdpRedirectClipboard = false,
            RdpRedirectDrives = true,
            RdpRedirectPrinters = true,
            RdpRedirectComPorts = true,
            RdpRedirectSmartCards = true,
            RdpRedirectWebcam = true,
            RdpRedirectUsb = true,
            RdpAudioCapture = true,
            RdpAudioMode = 2,
            RdpMultiMonitor = true,
            RdpDynamicResolution = false,
            RdpNla = false,
            RdpStrictServerAuthentication = false,
            RdpBitmapCaching = false,
            RdpCompression = false,
            RdpAutoReconnect = false
        };
        var settings = new AppSettings
        {
            RdpDefaultRedirectClipboard = true,
            RdpDefaultRedirectDrives = false,
            RdpDefaultRedirectPrinters = false,
            RdpDefaultRedirectComPorts = false,
            RdpDefaultRedirectSmartCards = false,
            RdpDefaultRedirectWebcam = false,
            RdpDefaultRedirectUsb = false,
            RdpDefaultAudioCapture = false,
            RdpDefaultAudioMode = 0,
            RdpDefaultMultiMonitor = false,
            RdpDefaultDynamicResolution = true,
            RdpDefaultNla = true,
            RdpDefaultStrictServerAuthentication = true,
            RdpDefaultBitmapCaching = true,
            RdpDefaultCompression = true,
            RdpDefaultAutoReconnect = true
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, settings);

        Assert.True(redirections.Clipboard);
        Assert.False(redirections.Drives);
        Assert.False(redirections.Printers);
        Assert.False(redirections.ComPorts);
        Assert.False(redirections.SmartCards);
        Assert.False(redirections.Webcam);
        Assert.False(redirections.Usb);
        Assert.False(redirections.AudioCapture);
        Assert.Equal(0, redirections.AudioMode);
        Assert.False(redirections.MultiMonitor);
        Assert.True(redirections.DynamicResolution);
        Assert.True(redirections.Nla);
        Assert.True(redirections.StrictServerAuthentication);
        Assert.True(redirections.BitmapCaching);
        Assert.True(redirections.Compression);
        Assert.True(redirections.AutoReconnect);
    }

    [Fact]
    public void BuildRedirections_UsesServerWhenFlagIsFalse()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = false,
            RdpRedirectClipboard = false,
            RdpRedirectDrives = true,
            RdpRedirectPrinters = true,
            RdpRedirectComPorts = true,
            RdpRedirectSmartCards = true,
            RdpRedirectWebcam = true,
            RdpRedirectUsb = true,
            RdpAudioCapture = true,
            RdpAudioMode = 2,
            RdpMultiMonitor = true,
            RdpDynamicResolution = false,
            RdpNla = false,
            RdpStrictServerAuthentication = true,
            RdpBitmapCaching = false,
            RdpCompression = false,
            RdpAutoReconnect = false
        };
        var settings = new AppSettings
        {
            RdpDefaultRedirectClipboard = true,
            RdpDefaultRedirectDrives = false,
            RdpDefaultRedirectPrinters = false,
            RdpDefaultRedirectComPorts = false,
            RdpDefaultRedirectSmartCards = false,
            RdpDefaultRedirectWebcam = false,
            RdpDefaultRedirectUsb = false,
            RdpDefaultAudioCapture = false,
            RdpDefaultAudioMode = 0,
            RdpDefaultMultiMonitor = false,
            RdpDefaultDynamicResolution = true,
            RdpDefaultNla = true,
            RdpDefaultStrictServerAuthentication = false,
            RdpDefaultBitmapCaching = true,
            RdpDefaultCompression = true,
            RdpDefaultAutoReconnect = true
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, settings);

        Assert.False(redirections.Clipboard);
        Assert.True(redirections.Drives);
        Assert.True(redirections.Printers);
        Assert.True(redirections.ComPorts);
        Assert.True(redirections.SmartCards);
        Assert.True(redirections.Webcam);
        Assert.True(redirections.Usb);
        Assert.True(redirections.AudioCapture);
        Assert.Equal(2, redirections.AudioMode);
        Assert.True(redirections.MultiMonitor);
        Assert.False(redirections.DynamicResolution);
        Assert.False(redirections.Nla);
        Assert.True(redirections.StrictServerAuthentication);
        Assert.False(redirections.BitmapCaching);
        Assert.False(redirections.Compression);
        Assert.False(redirections.AutoReconnect);
    }

    [Fact]
    public void BuildRedirections_ExplicitMultimonModeWinsOverGlobalDefaults()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = true,
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpMultiMonitor = false
        };
        var settings = new AppSettings
        {
            RdpDefaultMultiMonitor = false
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, settings);

        Assert.True(redirections.MultiMonitor);
    }

    [Fact]
    public void BuildRedirections_ExplicitNonMultimonModeDisablesLegacyBool()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = true,
            RdpResolutionMode = RdpResolutionMode.FitWindow,
            RdpMultiMonitor = true
        };
        var settings = new AppSettings
        {
            RdpDefaultMultiMonitor = true
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, settings);

        Assert.False(redirections.MultiMonitor);
    }

    [Fact]
    public void BuildRedirections_LegacyMultimonBoolStillWorksBeforeMigration()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = false,
            RdpMultiMonitor = true
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, new AppSettings());

        Assert.True(redirections.MultiMonitor);
    }

    [Fact]
    public void BuildRedirections_PerformanceFlagsAndDisableUdpAreAlwaysPerServer()
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = true,
            RdpPerformanceFlags = 0xFF,
            RdpDisableUdp = true
        };

        var redirections = RdpProfileResolver.BuildRedirections(server, new AppSettings());

        Assert.Equal(0xFF, redirections.PerformanceFlags);
        Assert.True(redirections.DisableUdp);
    }

    [Theory]
    [InlineData(true, 16, 32, 16)]
    [InlineData(true, 32, 16, 32)]
    [InlineData(false, 16, 32, 32)]
    [InlineData(false, 32, 16, 16)]
    public void ResolveColorDepth_GovernsByFlag(
        bool useGlobalDefaults,
        int settingsValue,
        int serverValue,
        int expected)
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = useGlobalDefaults,
            RdpColorDepth = serverValue
        };
        var settings = new AppSettings { RdpDefaultColorDepth = settingsValue };

        Assert.Equal(expected, RdpProfileResolver.ResolveColorDepth(server, settings));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(8, 16)]
    [InlineData(16, 16)]
    [InlineData(20, 24)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(40, 32)]
    [InlineData(64, 32)]
    public void ResolveColorDepth_NormalizesToSupportedValues(int raw, int expected)
    {
        var server = new ServerProfileDto
        {
            RdpUseGlobalDefaults = false,
            RdpColorDepth = raw
        };

        Assert.Equal(expected, RdpProfileResolver.ResolveColorDepth(server, new AppSettings()));
    }

    [Fact]
    public void ResolveResolution_Auto_UsesPrimaryWorkingAreaSmartSizingAndSingleMonitor()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Auto,
            RdpMultiMonitor = true
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 1600,
            DefaultResolutionHeight = 900,
            RdpDefaultMultiMonitor = true
        };

        var resolution = RdpProfileResolver.ResolveResolution(
            server,
            settings,
            primaryWorkingArea: new Size(1366, 768));

        Assert.Equal(1364, resolution.Width);
        Assert.Equal(768, resolution.Height);
        Assert.False(resolution.MultiMonitor);
        Assert.True(resolution.SmartSizing);
        Assert.Equal(RdpFileScreenMode.Windowed, resolution.ScreenMode);
        Assert.True(resolution.EmitDisabledMultiMonitor);
    }

    [Fact]
    public void GenerateExternalRdp_Auto_WritesEmbeddedParityDisplaySettings()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Auto,
            RdpMultiMonitor = true,
            RdpFullScreen = true
        };
        var settings = new AppSettings
        {
            RdpDefaultMultiMonitor = true
        };

        var content = GenerateExternalRdpContent(
            server,
            settings,
            primaryWorkingArea: new Size(1366, 768));

        Assert.Contains("desktopwidth:i:1364", content);
        Assert.Contains("desktopheight:i:768", content);
        Assert.Contains("smart sizing:i:1", content);
        Assert.Contains("use multimon:i:0", content);
        Assert.Contains("screen mode id:i:1", content);
        Assert.DoesNotContain("use multimon:i:1", content);
    }

    [Fact]
    public void GenerateExternalRdp_MultimonMode_WritesUseMultimon()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpMultiMonitor = true
        };

        var content = GenerateExternalRdpContent(
            server,
            new AppSettings(),
            availableMonitorCount: 2);

        Assert.Contains("use multimon:i:1", content);
        Assert.DoesNotContain("use multimon:i:0", content);
    }

    [Fact]
    public void ResolveResolution_Fixed_UsesServerWidthHeight()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 2560,
            RdpFixedHeight = 1440
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings());

        Assert.Equal(2560, resolution.Width);
        Assert.Equal(1440, resolution.Height);
        Assert.False(resolution.MultiMonitor);
        Assert.False(resolution.SmartSizing);
    }

    [Fact]
    public void ResolveResolution_Fixed_ClampsOutOfRangeValues()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 100,
            RdpFixedHeight = 5000
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings());

        Assert.Equal(200, resolution.Width);
        Assert.Equal(4320, resolution.Height);
    }

    [Fact]
    public void ResolveResolution_Multimon_FlagsMultiMonitorTrue()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 1366,
            DefaultResolutionHeight = 768
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, settings);

        Assert.Equal(1366, resolution.Width);
        Assert.Equal(768, resolution.Height);
        Assert.True(resolution.MultiMonitor);
        Assert.False(resolution.SmartSizing);
    }

    [Fact]
    public void ResolveResolution_Multimon_EmptySelectedMonitorsUsesAll()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = []
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings(), availableMonitorCount: 3);

        Assert.Empty(resolution.SelectedMonitorIndices);
    }

    [Fact]
    public void ResolveResolution_Multimon_KeepsValidSelectedMonitors()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1]
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings(), availableMonitorCount: 3);

        Assert.Equal(new[] { 0, 1 }, resolution.SelectedMonitorIndices);
    }

    [Fact]
    public void ResolveResolution_Multimon_AllOutOfRangeSelectedMonitorsFallsBackToAll()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [5, 7]
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings(), availableMonitorCount: 2);

        Assert.Empty(resolution.SelectedMonitorIndices);
    }

    [Fact]
    public void ResolveResolution_Multimon_DropsOutOfRangeSelectedMonitors()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 5]
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, new AppSettings(), availableMonitorCount: 2);

        Assert.Equal(new[] { 0 }, resolution.SelectedMonitorIndices);
    }

    [Fact]
    public void ResolveResolution_SmartSizing_EnablesSmartSizing()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.SmartSizing
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 1920,
            DefaultResolutionHeight = 1200
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, settings);

        Assert.Equal(1920, resolution.Width);
        Assert.Equal(1200, resolution.Height);
        Assert.False(resolution.MultiMonitor);
        Assert.True(resolution.SmartSizing);
    }

    [Fact]
    public void ResolveResolution_FitWindow_EnablesSmartSizing()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.FitWindow
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 1440,
            DefaultResolutionHeight = 900
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, settings);

        Assert.Equal(1440, resolution.Width);
        Assert.Equal(900, resolution.Height);
        Assert.False(resolution.MultiMonitor);
        Assert.True(resolution.SmartSizing);
    }

    [Fact]
    public void ResolveResolution_FallbackUsedWhenSettingsZero()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = RdpResolutionMode.Auto
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 0,
            DefaultResolutionHeight = -1
        };

        var resolution = RdpProfileResolver.ResolveResolution(
            server,
            settings,
            primaryWorkingArea: Size.Empty);

        Assert.Equal(1920, resolution.Width);
        Assert.Equal(1080, resolution.Height);
        Assert.False(resolution.MultiMonitor);
        Assert.True(resolution.SmartSizing);
    }

    [Fact]
    public void ResolveResolution_UnknownEnum_UsesAutoFallback()
    {
        var server = new ServerProfileDto
        {
            RdpResolutionMode = (RdpResolutionMode)999,
            RdpMultiMonitor = true
        };
        var settings = new AppSettings
        {
            DefaultResolutionWidth = 1280,
            DefaultResolutionHeight = 720
        };

        var resolution = RdpProfileResolver.ResolveResolution(server, settings);

        Assert.Equal(1280, resolution.Width);
        Assert.Equal(720, resolution.Height);
        Assert.True(resolution.MultiMonitor);
        Assert.False(resolution.SmartSizing);
    }

    [Fact]
    public void BuildRedirections_WithDefaultDtoAndDefaultSettingsIsConsistent()
    {
        var settings = new AppSettings();
        var globalServer = new ServerProfileDto { RdpUseGlobalDefaults = true };
        var localServer = new ServerProfileDto { RdpUseGlobalDefaults = false };

        var asGlobal = RdpProfileResolver.BuildRedirections(globalServer, settings);
        var asServer = RdpProfileResolver.BuildRedirections(localServer, settings);

        Assert.Equal(asGlobal.Clipboard, asServer.Clipboard);
        Assert.Equal(asGlobal.Drives, asServer.Drives);
        Assert.Equal(asGlobal.Printers, asServer.Printers);
        Assert.Equal(asGlobal.ComPorts, asServer.ComPorts);
        Assert.Equal(asGlobal.SmartCards, asServer.SmartCards);
        Assert.Equal(asGlobal.Webcam, asServer.Webcam);
        Assert.Equal(asGlobal.Usb, asServer.Usb);
        Assert.Equal(asGlobal.AudioCapture, asServer.AudioCapture);
        Assert.Equal(asGlobal.AudioMode, asServer.AudioMode);
        Assert.Equal(asGlobal.MultiMonitor, asServer.MultiMonitor);
        Assert.Equal(asGlobal.DynamicResolution, asServer.DynamicResolution);
        Assert.Equal(asGlobal.Nla, asServer.Nla);
        Assert.Equal(asGlobal.BitmapCaching, asServer.BitmapCaching);
        Assert.Equal(asGlobal.Compression, asServer.Compression);
        Assert.Equal(asGlobal.AutoReconnect, asServer.AutoReconnect);
    }

    // The dimensions below are chosen so that a 1.25, 1.5 or 2.0 device-independent
    // conversion yields a different integer in every case: an implementation that still
    // scales cannot pass, and neither can one that always returns an empty size.
    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(0, 0, 2560, 1440)]
    [InlineData(1920, 0, 1920, 1032)]
    public void ToPhysicalSize_PreservesPixelDimensionsExactly(int left, int top, int width, int height)
    {
        Size size = WindowWorkingAreaProvider.ToPhysicalSize(new Rectangle(left, top, width, height));

        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
    }

    [Fact]
    public void ToPhysicalSize_EmptyRectangle_YieldsEmptySize()
    {
        Size size = WindowWorkingAreaProvider.ToPhysicalSize(Rectangle.Empty);

        Assert.Equal(Size.Empty, size);
    }

    [Fact]
    public void RdpProfileResolver_ResolvesPrimaryWorkingAreaWithoutTheWpfWorkArea()
    {
        string resolverPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            "Services",
            "RdpProfileResolver.cs");

        // Matched line by line on purpose: the repository stores CRLF, so an anchored
        // multiline regex over the raw file text would silently never match and the
        // guard would pass without measuring anything.
        List<string> offendingLines = File.ReadLines(resolverPath)
            .Select((text, index) => new { Text = text, Number = index + 1 })
            .Where(line => line.Text.Contains("SystemParameters.WorkArea", StringComparison.Ordinal))
            .Select(line => $"{line.Number}: {line.Text.Trim()}")
            .ToList();

        Assert.True(
            offendingLines.Count == 0,
            "The external Auto path must resolve the primary working area in physical pixels. " +
            "SystemParameters.WorkArea is expressed in WPF device-independent units and must not " +
            "appear in RdpProfileResolver:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offendingLines));
    }

    private static string GenerateExternalRdpContent(
        ServerProfileDto server,
        AppSettings settings,
        int? availableMonitorCount = null,
        Size? primaryWorkingArea = null)
    {
        var resolution = RdpProfileResolver.ResolveResolution(
            server,
            settings,
            availableMonitorCount,
            primaryWorkingArea);
        var redirections = RdpProfileResolver.BuildRedirections(server, settings);
        if (resolution.EmitDisabledMultiMonitor)
        {
            redirections.MultiMonitor = false;
        }

        return RdpFileGenerator.Generate(new RdpFileOptions
        {
            Host = "srv",
            Width = resolution.Width,
            Height = resolution.Height,
            FullScreen = server.RdpFullScreen,
            ScreenMode = resolution.ScreenMode,
            MultiMonitor = resolution.MultiMonitor,
            EmitDisabledMultiMonitor = resolution.EmitDisabledMultiMonitor,
            SmartSizing = resolution.SmartSizing,
            SelectedMonitorIndices = resolution.SelectedMonitorIndices,
            Redirections = redirections
        });
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find the repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
