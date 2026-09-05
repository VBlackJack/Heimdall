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

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Heimdall.Rdp;

namespace Heimdall.Rdp.Tests;

public class RdpFileGeneratorTests
{
    // ── Basic generation ──────────────────────────────────────────────

    [Fact]
    public void Generate_MinimalOptions_ProducesValidRdpContent()
    {
        var options = new RdpFileOptions { Host = "server.example.com" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("full address:s:", content);
        Assert.Contains("desktopwidth:i:", content);
        Assert.Contains("desktopheight:i:", content);
        Assert.Contains("screen mode id:i:", content);
        Assert.Contains("redirectclipboard:i:", content);
        Assert.Contains("authentication level:i:", content);
    }

    [Fact]
    public void Generate_UsesCrLfLineEndings()
    {
        RdpFileOptions options = new RdpFileOptions { Host = "server.example.com" };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("\r\n", content);
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] == '\n')
            {
                Assert.True(
                    index > 0 && content[index - 1] == '\r',
                    $"Bare LF found at index {index}.");
            }
        }
    }

    // ── Host and port ─────────────────────────────────────────────────

    [Fact]
    public void Generate_HostAndDefaultPort_AppearsInFullAddress()
    {
        var options = new RdpFileOptions { Host = "10.0.0.1" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("full address:s:10.0.0.1:3389", content);
    }

    [Fact]
    public void Generate_CustomPort_AppearsInFullAddress()
    {
        var options = new RdpFileOptions { Host = "10.0.0.1", Port = 3390 };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("full address:s:10.0.0.1:3390", content);
    }

    [Fact]
    public void Generate_IPv6Host_BracketsFullAddressButLeavesIpv4AndHostnamesUnbracketed()
    {
        RdpFileOptions ipv6Options = new RdpFileOptions { Host = "2001:db8::1", Port = 3390 };
        RdpFileOptions ipv4Options = new RdpFileOptions { Host = "10.0.0.1", Port = 3390 };
        RdpFileOptions hostnameOptions = new RdpFileOptions { Host = "server.example.com", Port = 3390 };

        string ipv6Content = RdpFileGenerator.Generate(ipv6Options);
        string ipv4Content = RdpFileGenerator.Generate(ipv4Options);
        string hostnameContent = RdpFileGenerator.Generate(hostnameOptions);

        Assert.Contains("full address:s:[2001:db8::1]:3390", ipv6Content);
        Assert.Contains("full address:s:10.0.0.1:3390", ipv4Content);
        Assert.DoesNotContain("full address:s:[10.0.0.1]:3390", ipv4Content);
        Assert.Contains("full address:s:server.example.com:3390", hostnameContent);
        Assert.DoesNotContain("full address:s:[server.example.com]:3390", hostnameContent);
    }

    // ── Username ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_WithUsername_IncludesUsernameField()
    {
        var options = new RdpFileOptions { Host = "srv", Username = "admin" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("username:s:admin", content);
    }

    [Fact]
    public void Generate_WithDomain_IncludesDomainField()
    {
        var options = new RdpFileOptions { Host = "srv", Domain = "CORP" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("domain:s:CORP", content);
    }

    [Fact]
    public void Generate_NullUsername_OmitsUsernameField()
    {
        var options = new RdpFileOptions { Host = "srv" };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("username:s:", content);
    }

    // ── CRLF injection sanitization ───────────────────────────────────

    [Fact]
    public void Generate_UsernameCrlfInjection_StrippedFromOutput()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Username = "admin\r\nevil:s:payload"
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("username:s:adminevil:s:payload", content);
        Assert.DoesNotContain("\r\nevil", content);
    }

    [Fact]
    public void Generate_HostWithNullBytes_StrippedFromOutput()
    {
        var options = new RdpFileOptions { Host = "srv\0.local" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("full address:s:srv.local:", content);
    }

    // ── Gateway settings ──────────────────────────────────────────────

    [Fact]
    public void Generate_WithGateway_IncludesGatewaySettings()
    {
        RdpFileOptions options = new()
        {
            Host = "srv",
            GatewayHostname = "gw.example.com"
        };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("gatewayusagemethod:i:1\r\n", content, StringComparison.Ordinal);
        Assert.Contains("gatewayprofileusagemethod:i:1\r\n", content, StringComparison.Ordinal);
        Assert.Contains("gatewayhostname:s:gw.example.com\r\n", content, StringComparison.Ordinal);
        Assert.Contains("gatewaycredentialssource:i:0\r\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_InvalidGateway_ThrowsValidationFailure()
    {
        RdpFileOptions options = new() { Host = "srv", GatewayHostname = "invalid gateway!" };

        RdpGatewayAttestationException exception = Assert.Throws<RdpGatewayAttestationException>(
            () => RdpFileGenerator.Generate(options));

        Assert.Equal("invalid gateway!", exception.GatewayHost);
        Assert.Equal(RdpGatewayAttestationStep.GatewayValidation, exception.Step);
        Assert.Empty(exception.DivergentProperties);
    }

    [Fact]
    public void Generate_EmptyGateway_DisablesGatewayWithoutOtherGatewaySettings()
    {
        RdpFileOptions options = new() { Host = "srv", GatewayHostname = string.Empty };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("gatewayusagemethod:i:0\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewayprofileusagemethod:i:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewayhostname:s:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewaycredentialssource:i:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_NullGateway_DisablesGateway()
    {
        RdpFileOptions options = new() { Host = "srv", GatewayHostname = null };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("gatewayusagemethod:i:0\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewayprofileusagemethod:i:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewayhostname:s:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("gatewaycredentialssource:i:", content, StringComparison.Ordinal);
    }

    // ── Redirection options ───────────────────────────────────────────

    [Fact]
    public void Generate_ClipboardEnabled_RedirectClipboard1()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Clipboard = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("redirectclipboard:i:1", content);
    }

    [Fact]
    public void Generate_DrivesEnabled_RedirectDrives1()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Drives = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("redirectdrives:i:1", content);
    }

    // mstsc 10.0.26100 reads both drive keys, and "drivestoredirect:s:*" wins over
    // "redirectdrives:i:0" (measured 2026-09-05 in the mstsc /edit dialog). A generator that
    // emitted the newer key unconditionally would therefore redirect drives on a profile that
    // said no. The file carries the one key mstsc itself writes, and never the other.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generate_NeverEmitsDrivesToRedirect_WhateverTheDrivesSetting(bool drives)
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Drives = drives }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("drivestoredirect", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"redirectdrives:i:{(drives ? 1 : 0)}", content);
    }

    [Fact]
    public void Generate_PrintersEnabled_RedirectPrinters1()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Printers = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("redirectprinters:i:1", content);
    }

    [Fact]
    public void Generate_AllRedirectionsDisabled_AllZero()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions
            {
                Clipboard = false,
                Drives = false,
                Printers = false,
                ComPorts = false,
                SmartCards = false
            }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("redirectclipboard:i:0", content);
        Assert.Contains("redirectdrives:i:0", content);
        Assert.Contains("redirectprinters:i:0", content);
        Assert.Contains("redirectcomports:i:0", content);
        Assert.Contains("redirectsmartcards:i:0", content);
    }

    // ── Color depth and resolution ────────────────────────────────────

    [Fact]
    public void Generate_DefaultColorDepth_SessionBpp32()
    {
        var options = new RdpFileOptions { Host = "srv" };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("session bpp:i:32", content);
    }

    [Fact]
    public void Generate_CustomColorDepth_SessionBpp16()
    {
        var options = new RdpFileOptions { Host = "srv", ColorDepth = 16 };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("session bpp:i:16", content);
    }

    [Fact]
    public void Generate_CustomResolution_IncludedInOutput()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Width = 2560,
            Height = 1440
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("desktopwidth:i:2560", content);
        Assert.Contains("desktopheight:i:1440", content);
    }

    [Fact]
    public void Generate_FullScreen_ScreenModeId2()
    {
        var options = new RdpFileOptions { Host = "srv", FullScreen = true };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("screen mode id:i:2", content);
    }

    [Fact]
    public void Generate_Windowed_ScreenModeId1()
    {
        var options = new RdpFileOptions { Host = "srv", FullScreen = false };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("screen mode id:i:1", content);
    }

    [Fact]
    public void Generate_SmartSizingOption_SetsFullscreenScreenModeAndSmartSizing()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            FullScreen = false,
            SmartSizing = true
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("screen mode id:i:2", content);
        Assert.Contains("smart sizing:i:1", content);
        Assert.DoesNotContain("dynamic resolution:i:1", content);
    }

    // ── NLA setting ───────────────────────────────────────────────────

    [Fact]
    public void Generate_NlaEnabled_AuthLevel2()
    {
        RdpFileOptions options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Nla = true }
        };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("authentication level:i:2", content);
        Assert.Contains("enablecredsspsupport:i:1", content);
    }

    [Fact]
    public void Generate_NlaEnabledStrictServerAuthentication_AuthLevel1()
    {
        RdpFileOptions options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions
            {
                Nla = true,
                StrictServerAuthentication = true
            }
        };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("authentication level:i:1", content);
        Assert.Contains("enablecredsspsupport:i:1", content);
    }

    [Fact]
    public void Generate_NlaDisabled_AuthLevel0()
    {
        RdpFileOptions options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Nla = false }
        };

        string content = RdpFileGenerator.Generate(options);

        Assert.Contains("authentication level:i:0", content);
        Assert.Contains("enablecredsspsupport:i:0", content);
    }

    // ── Admin mode ────────────────────────────────────────────────────

    [Fact]
    public void Generate_AdminMode_IncludesAdministrativeSession()
    {
        var options = new RdpFileOptions { Host = "srv", AdminMode = true };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("administrative session:i:1", content);
    }

    [Fact]
    public void Generate_NoAdminMode_OmitsAdministrativeSession()
    {
        var options = new RdpFileOptions { Host = "srv", AdminMode = false };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("administrative session:i:", content);
    }

    // ── Null / empty optional fields ──────────────────────────────────

    [Fact]
    public void Generate_NullDomain_OmitsDomainField()
    {
        var options = new RdpFileOptions { Host = "srv", Domain = null };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("domain:s:", content);
    }

    [Fact]
    public void Generate_EmptyUsername_OmitsUsernameField()
    {
        var options = new RdpFileOptions { Host = "srv", Username = "" };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("username:s:", content);
    }

    [Fact]
    public void Generate_WhitespaceGateway_DisablesGateway()
    {
        var options = new RdpFileOptions { Host = "srv", GatewayHostname = "   " };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("gatewayusagemethod:i:0", content);
        Assert.DoesNotContain("gatewayhostname:s:", content);
    }

    [Fact]
    public void Generate_NullHost_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RdpFileGenerator.Generate(null!));
    }

    [Fact]
    public void Generate_EmptyHost_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            RdpFileGenerator.Generate(new RdpFileOptions { Host = "" }));
    }

    [Fact]
    public void Generate_PortZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RdpFileGenerator.Generate(new RdpFileOptions { Host = "srv", Port = 0 }));
    }

    [Fact]
    public void Generate_PortAboveMaximum_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RdpFileGenerator.Generate(new RdpFileOptions { Host = "srv", Port = 70000 }));
    }

    [Fact]
    public void Generate_WidthZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RdpFileGenerator.Generate(new RdpFileOptions { Host = "srv", Width = 0 }));
    }

    [Fact]
    public void Generate_HeightZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RdpFileGenerator.Generate(new RdpFileOptions { Host = "srv", Height = 0 }));
    }

    // ── Multi-monitor and dynamic resolution ──────────────────────────

    [Fact]
    public void Generate_MultiMonitor_IncludesUseMultimon()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { MultiMonitor = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("use multimon:i:1", content);
    }

    [Fact]
    public void Generate_MultiMonitorOption_IncludesUseMultimon()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            MultiMonitor = true
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("use multimon:i:1", content);
    }

    [Fact]
    public void Generate_SelectedMonitorsWithMultiMonitor_IncludesSelectedMonitors()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            MultiMonitor = true,
            SelectedMonitorIndices = [0, 2]
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("use multimon:i:1", content);
        Assert.Contains("selectedmonitors:s:0,2", content);
    }

    [Fact]
    public void Generate_SelectedMonitorsEmpty_OmitsSelectedMonitors()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            MultiMonitor = true,
            SelectedMonitorIndices = []
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("use multimon:i:1", content);
        Assert.DoesNotContain("selectedmonitors:s:", content);
    }

    [Fact]
    public void Generate_SelectedMonitorsWithoutMultiMonitor_OmitsSelectedMonitors()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            MultiMonitor = false,
            SelectedMonitorIndices = [0, 2]
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("use multimon:i:1", content);
        Assert.DoesNotContain("selectedmonitors:s:", content);
    }

    [Fact]
    public void Generate_DynamicResolution_IncludesSmartSizing()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { DynamicResolution = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("smart sizing:i:1", content);
        Assert.Contains("dynamic resolution:i:1", content);
    }

    // ── Performance flags ────────────────────────────────────────────

    [Fact]
    public void Generate_PerformanceFlags_DecomposesToRdpSettings()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions
            {
                PerformanceFlags = 0x01 | 0x04 | 0x80 | 0x100
            }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("disable wallpaper:i:1", content);
        Assert.Contains("disable full window drag:i:0", content);
        Assert.Contains("disable menu anims:i:1", content);
        Assert.Contains("disable themes:i:0", content);
        Assert.Contains("allow font smoothing:i:1", content);
        Assert.Contains("allow desktop composition:i:1", content);
    }

    [Fact]
    public void Generate_PerformanceFlagsZero_OmitsPerformanceSettings()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { PerformanceFlags = 0 }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("disable wallpaper:i:", content);
        Assert.DoesNotContain("allow font smoothing:i:", content);
    }

    // ── DisableUdp / network auto-detect ─────────────────────────────

    [Fact]
    public void Generate_DisableUdp_ForcesTcpOnly()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { DisableUdp = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("networkautodetect:i:0", content);
        Assert.Contains("bandwidthautodetect:i:0", content);
        Assert.Contains("connection type:i:6", content);
    }

    [Fact]
    public void Generate_DefaultUdp_EnablesAutoDetect()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { DisableUdp = false }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("networkautodetect:i:1", content);
        Assert.Contains("bandwidthautodetect:i:1", content);
        Assert.Contains("connection type:i:7", content);
    }

    // ── USB / Webcam ─────────────────────────────────────────────────

    [Fact]
    public void Generate_UsbRedirect_IncludesUsbDevices()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Usb = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("usbdevicestoredirect:s:*", content);
    }

    [Fact]
    public void Generate_WebcamRedirect_IncludesCameraRedirect()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { Webcam = true }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("camerastoredirect:s:*", content);
    }

    [Fact]
    public void Generate_NoUsbWebcam_OmitsDeviceStrings()
    {
        var options = new RdpFileOptions { Host = "srv" };

        var content = RdpFileGenerator.Generate(options);

        Assert.DoesNotContain("usbdevicestoredirect", content);
        Assert.DoesNotContain("camerastoredirect", content);
    }

    // ── Audio mode ───────────────────────────────────────────────────

    [Fact]
    public void Generate_AudioModeLocal_MapsToRdpMode0()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { AudioMode = 1 }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("audiomode:i:0", content);
    }

    [Fact]
    public void Generate_AudioModeRemote_MapsToRdpMode1()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { AudioMode = 2 }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("audiomode:i:1", content);
    }

    [Fact]
    public void Generate_AudioModeDisabled_MapsToRdpMode2()
    {
        var options = new RdpFileOptions
        {
            Host = "srv",
            Redirections = new RdpRedirectionOptions { AudioMode = 0 }
        };

        var content = RdpFileGenerator.Generate(options);

        Assert.Contains("audiomode:i:2", content);
    }

    // ── WriteToFileAsync - atomic ACL (TOCTOU regression) ─────────────

    [Fact]
    public async Task WriteToFileAsync_WritesExpectedContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heimdall-rdp-test-{Guid.NewGuid():N}.rdp");
        try
        {
            var options = new RdpFileOptions { Host = "server.example.com", Port = 3389 };

            await RdpFileGenerator.WriteToFileAsync(path, options);

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("full address:s:server.example.com:3389", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteToFileAsync_OverwritesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heimdall-rdp-test-{Guid.NewGuid():N}.rdp");
        try
        {
            await File.WriteAllTextAsync(path, "stale content");

            var options = new RdpFileOptions { Host = "fresh.example.com" };
            await RdpFileGenerator.WriteToFileAsync(path, options);

            var content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("stale content", content);
            Assert.Contains("full address:s:fresh.example.com", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteToFileAsync_AppliesRestrictedAclAtCreation()
    {
        // Regression test for the TOCTOU window between file creation and ACL
        // application. Verify that the file is created with inheritance disabled
        // and only the current user / Administrators / SYSTEM are explicitly
        // allowed - no inherited entries from %TEMP% leak through.
        var path = Path.Combine(Path.GetTempPath(), $"heimdall-rdp-test-{Guid.NewGuid():N}.rdp");
        try
        {
            await RdpFileGenerator.WriteToFileAsync(path, new RdpFileOptions { Host = "srv" });

            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();

            Assert.True(
                security.AreAccessRulesProtected,
                "Inheritance should be disabled - TOCTOU regression");

            var rules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            // No inherited rules - every rule must be explicit
            Assert.All(rules, r => Assert.False(r.IsInherited));

            // Only known SIDs are allowed
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var allowedSids = new HashSet<SecurityIdentifier> { currentUser, admins, system };

            foreach (var rule in rules)
            {
                Assert.True(
                    allowedSids.Contains((SecurityIdentifier)rule.IdentityReference),
                    $"Unexpected ACE for {rule.IdentityReference.Value}");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
