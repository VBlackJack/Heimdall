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
using Heimdall.Core.Logging;
using Heimdall.Core.Rdp;
using Heimdall.Rdp;
using Heimdall.Rdp.Display;
using DrawingSize = System.Drawing.Size;
using WinForms = System.Windows.Forms;

namespace Heimdall.App.Services;

/// <summary>
/// Resolved display options for external RDP file generation.
/// </summary>
public sealed record RdpResolvedResolution(
    int Width,
    int Height,
    bool MultiMonitor,
    bool SmartSizing,
    int[] SelectedMonitorIndices,
    RdpFileScreenMode? ScreenMode = null,
    bool EmitDisabledMultiMonitor = false);

/// <summary>
/// Resolves connect-time RDP options from a server profile and the global settings.
/// </summary>
/// <summary>
/// The host and port a certificate probe should dial for a profile.
/// </summary>
public readonly record struct RdpCertificateProbeTarget(string Host, int Port);

internal static class RdpProfileResolver
{
    private const int FallbackWidth = 1920;
    private const int FallbackHeight = 1080;
    private const int MinimumFixedSize = RdpDisplayLimits.MinimumFixedDimension;
    private const int MaximumFixedWidth = RdpDisplayLimits.MaximumFixedWidth;
    private const int MaximumFixedHeight = RdpDisplayLimits.MaximumFixedHeight;

    /// <summary>
    /// Resolves the username/domain pair used for RDP credential injection.
    /// </summary>
    public static (string Username, string? Domain) ResolveCredentialIdentity(
        string? rdpUsername,
        string? rdpDomain)
    {
        if (!string.IsNullOrWhiteSpace(rdpDomain))
        {
            return (rdpUsername ?? string.Empty, rdpDomain);
        }

        if (string.IsNullOrWhiteSpace(rdpUsername))
        {
            return (string.Empty, null);
        }

        // DOMAIN\user format (NetBIOS) - keep the full down-level name in the username
        // field; the RDP ActiveX accepts DOMAIN\user directly, exactly like a UPN. Splitting
        // it into a separate Domain breaks NLA/CredSSP auto-logon on some hosts.
        int separatorIndex = rdpUsername.IndexOf('\\');
        if (separatorIndex > 0 && separatorIndex < rdpUsername.Length - 1)
        {
            return (rdpUsername, rdpUsername[..separatorIndex]);
        }

        // user@domain.com format (UPN) - pass the full UPN as the username
        // and extract the domain for logging/diagnostics. The RDP ActiveX control
        // accepts UPN directly in the UserName field.
        int atIndex = rdpUsername.IndexOf('@');
        if (atIndex > 0 && atIndex < rdpUsername.Length - 1)
        {
            return (rdpUsername, rdpUsername[(atIndex + 1)..]);
        }

        return (rdpUsername, null);
    }

    /// <summary>
    /// The endpoint the certificate probe should dial for a profile, or <c>null</c> when the
    /// endpoint the session will actually use cannot be reached by a direct TCP connection from
    /// this machine.
    /// </summary>
    /// <remarks>
    /// A profile routed through an RD Gateway is the case that matters: the session reaches the
    /// target through the gateway, while a probe dialling the bare target name resolves nothing or
    /// is filtered. That failure is indistinguishable from "the check ran and found no problem",
    /// so the caller must be able to tell the two apart rather than reading a silent Proceed.
    /// </remarks>
    /// <param name="server">The profile about to be connected.</param>
    /// <param name="tunnelPort">The dynamically allocated SSH tunnel port, when one is open.</param>
    public static RdpCertificateProbeTarget? BuildCertificateVerificationTarget(
        ServerProfileDto server,
        int? tunnelPort)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!string.IsNullOrWhiteSpace(server.RdpGateway))
        {
            return null;
        }

        bool direct = server.UseDirectConnection || string.IsNullOrWhiteSpace(server.SshGatewayId);

        return direct
            ? new RdpCertificateProbeTarget(server.RemoteServer, server.RemotePort)
            : new RdpCertificateProbeTarget("127.0.0.1", tunnelPort ?? server.LocalPort);
    }

    /// <summary>
    /// Builds the RDP redirection options using strict global-default semantics.
    /// </summary>
    public static RdpRedirectionOptions BuildRedirections(
        ServerProfileDto server,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        if (server.RdpUseGlobalDefaults)
        {
            return new RdpRedirectionOptions
            {
                Clipboard = settings.RdpDefaultRedirectClipboard,
                Drives = settings.RdpDefaultRedirectDrives,
                Printers = settings.RdpDefaultRedirectPrinters,
                ComPorts = settings.RdpDefaultRedirectComPorts,
                SmartCards = settings.RdpDefaultRedirectSmartCards,
                Webcam = settings.RdpDefaultRedirectWebcam,
                Usb = settings.RdpDefaultRedirectUsb,
                AudioCapture = settings.RdpDefaultAudioCapture,
                AudioMode = settings.RdpDefaultAudioMode,
                MultiMonitor = ResolveMultiMonitor(server, settings),
                DynamicResolution = settings.RdpDefaultDynamicResolution,
                Nla = settings.RdpDefaultNla,
                StrictServerAuthentication = settings.RdpDefaultStrictServerAuthentication,
                BitmapCaching = settings.RdpDefaultBitmapCaching,
                Compression = settings.RdpDefaultCompression,
                HardwareAcceleration = settings.RdpDefaultHardwareAcceleration,
                AutoReconnect = settings.RdpDefaultAutoReconnect,
                PerformanceFlags = server.RdpPerformanceFlags,
                DisableUdp = server.RdpDisableUdp,
                GatewayHostname = server.RdpGateway
            };
        }

        return new RdpRedirectionOptions
        {
            Clipboard = server.RdpRedirectClipboard,
            Drives = server.RdpRedirectDrives,
            Printers = server.RdpRedirectPrinters,
            ComPorts = server.RdpRedirectComPorts,
            SmartCards = server.RdpRedirectSmartCards,
            Webcam = server.RdpRedirectWebcam,
            Usb = server.RdpRedirectUsb,
            AudioCapture = server.RdpAudioCapture,
            AudioMode = server.RdpAudioMode,
            MultiMonitor = ResolveMultiMonitor(server, settings),
            DynamicResolution = server.RdpDynamicResolution,
            Nla = server.RdpNla,
            StrictServerAuthentication = server.RdpStrictServerAuthentication,
            BitmapCaching = server.RdpBitmapCaching,
            Compression = server.RdpCompression,
            HardwareAcceleration = server.RdpHardwareAcceleration,
            AutoReconnect = server.RdpAutoReconnect,
            PerformanceFlags = server.RdpPerformanceFlags,
            DisableUdp = server.RdpDisableUdp,
            GatewayHostname = server.RdpGateway
        };
    }

    /// <summary>
    /// Resolves and normalizes the RDP color depth using the same governance rule.
    /// </summary>
    public static int ResolveColorDepth(ServerProfileDto server, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        var raw = server.RdpUseGlobalDefaults
            ? settings.RdpDefaultColorDepth
            : server.RdpColorDepth;

        return raw switch
        {
            <= 16 => 16,
            <= 24 => 24,
            _ => 32
        };
    }

    /// <summary>
    /// Resolves and normalizes the display options used by external mstsc.exe sessions.
    /// </summary>
    public static RdpResolvedResolution ResolveResolution(
        ServerProfileDto server,
        AppSettings settings,
        int? availableMonitorCount = null,
        DrawingSize? primaryWorkingArea = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        var defaultWidth = settings.DefaultResolutionWidth > 0
            ? settings.DefaultResolutionWidth
            : FallbackWidth;
        var defaultHeight = settings.DefaultResolutionHeight > 0
            ? settings.DefaultResolutionHeight
            : FallbackHeight;

        if (server.RdpResolutionMode == RdpResolutionMode.Auto)
        {
            var autoSize = RdpDisplayResolver.ResolveExternalAutoWindowedSize(
                primaryWorkingArea ?? GetPrimaryWorkingArea(),
                new DrawingSize(defaultWidth, defaultHeight));

            return new RdpResolvedResolution(
                autoSize.Width,
                autoSize.Height,
                MultiMonitor: false,
                SmartSizing: true,
                SelectedMonitorIndices: [],
                ScreenMode: RdpFileScreenMode.Windowed,
                EmitDisabledMultiMonitor: true);
        }

        return server.RdpResolutionMode switch
        {
            RdpResolutionMode.FitWindow => new RdpResolvedResolution(
                defaultWidth,
                defaultHeight,
                MultiMonitor: false,
                SmartSizing: true,
                SelectedMonitorIndices: []),
            RdpResolutionMode.Fixed => new RdpResolvedResolution(
                Math.Clamp(server.RdpFixedWidth, MinimumFixedSize, MaximumFixedWidth),
                Math.Clamp(server.RdpFixedHeight, MinimumFixedSize, MaximumFixedHeight),
                MultiMonitor: false,
                SmartSizing: false,
                SelectedMonitorIndices: []),
            RdpResolutionMode.SmartSizing => new RdpResolvedResolution(
                defaultWidth,
                defaultHeight,
                MultiMonitor: false,
                SmartSizing: true,
                SelectedMonitorIndices: []),
            RdpResolutionMode.Multimon => new RdpResolvedResolution(
                defaultWidth,
                defaultHeight,
                MultiMonitor: true,
                SmartSizing: false,
                SelectedMonitorIndices: ResolveSelectedMonitorIndices(server, availableMonitorCount)),
            _ => new RdpResolvedResolution(
                defaultWidth,
                defaultHeight,
                ResolveAutoMultiMonitor(server, settings),
                SmartSizing: false,
                SelectedMonitorIndices: [])
        };
    }

    // Physical pixels, never device-independent units: the value is written verbatim as
    // desktopwidth/desktopheight in the generated .rdp file, and mstsc reads it as pixels.
    private static DrawingSize GetPrimaryWorkingArea()
        => WindowWorkingAreaProvider.GetPrimaryWorkingAreaPhysicalPx();

    private static int[] ResolveSelectedMonitorIndices(
        ServerProfileDto server,
        int? availableMonitorCount)
    {
        var monitorCount = availableMonitorCount ?? GetAvailableMonitorCount();
        return RdpSelectedMonitorValidator.Validate(
            server.RdpSelectedMonitorIndices,
            monitorCount,
            message => FileLogger.Warn($"[RdpProfileResolver] {message}"));
    }

    private static int GetAvailableMonitorCount()
    {
        try
        {
            return WinForms.Screen.AllScreens.Length;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"RDP selected monitor validation fallback: {ex.Message}");
            return 0;
        }
    }

    private static bool ResolveAutoMultiMonitor(ServerProfileDto server, AppSettings settings)
        => server.RdpMultiMonitor || settings.RdpDefaultMultiMonitor;

    private static bool ResolveMultiMonitor(ServerProfileDto server, AppSettings settings)
    {
        if (server.HasRdpResolutionModeField)
        {
            return server.RdpResolutionMode == RdpResolutionMode.Multimon;
        }

        return server.RdpUseGlobalDefaults
            ? settings.RdpDefaultMultiMonitor
            : server.RdpMultiMonitor;
    }
}
