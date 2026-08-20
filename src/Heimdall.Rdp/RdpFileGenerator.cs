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

using System.Net;
using System.Net.Sockets;
using System.Text;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Rdp;

/// <summary>
/// Generates .rdp files for launching external mstsc.exe connections.
/// User-supplied string values are sanitized against CRLF injection (CWE-93).
/// </summary>
public static class RdpFileGenerator
{
    /// <summary>
    /// Generate .rdp file content as a string.
    /// </summary>
    public static string Generate(RdpFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Port, 1, nameof(options.Port));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, 65535, nameof(options.Port));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Width, 0, nameof(options.Width));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Height, 0, nameof(options.Height));

        var sb = new StringBuilder();
        var useSmartSizing = options.SmartSizing;
        var useMultiMonitor = options.MultiMonitor || options.Redirections.MultiMonitor;
        var screenMode = options.ScreenMode
            ?? (options.FullScreen || useSmartSizing
                ? RdpFileScreenMode.FullScreen
                : RdpFileScreenMode.Windowed);
        var selectedMonitorIndices = options.SelectedMonitorIndices
            .Where(index => index >= 0)
            .Distinct()
            .ToArray();

        // Connection
        string host = options.Host;
        if (!host.StartsWith("[", StringComparison.Ordinal)
            && IPAddress.TryParse(host, out IPAddress? parsedHost)
            && parsedHost.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = $"[{host}]";
        }

        AppendSanitized(sb, "full address:s:", $"{host}:{options.Port}");

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            AppendSanitized(sb, "username:s:", options.Username);
        }
        if (!string.IsNullOrWhiteSpace(options.Domain))
        {
            AppendSanitized(sb, "domain:s:", options.Domain);
        }

        // Display
        sb.AppendLine($"desktopwidth:i:{options.Width}");
        sb.AppendLine($"desktopheight:i:{options.Height}");
        sb.AppendLine($"screen mode id:i:{(int)screenMode}");
        sb.AppendLine($"session bpp:i:{options.ColorDepth}");

        // Admin mode
        if (options.AdminMode)
        {
            sb.AppendLine("administrative session:i:1");
        }

        // Redirections
        var r = options.Redirections;
        sb.AppendLine($"redirectclipboard:i:{BoolToInt(r.Clipboard)}");
        sb.AppendLine($"redirectdrives:i:{BoolToInt(r.Drives)}");
        sb.AppendLine($"redirectprinters:i:{BoolToInt(r.Printers)}");
        sb.AppendLine($"redirectcomports:i:{BoolToInt(r.ComPorts)}");
        sb.AppendLine($"redirectsmartcards:i:{BoolToInt(r.SmartCards)}");

        // Audio
        sb.AppendLine($"audiomode:i:{RdpRedirectionOptions.MapAudioModeToRdpValue(r.AudioMode)}");
        sb.AppendLine($"audiocapturemode:i:{BoolToInt(r.AudioCapture)}");

        // NLA - shared resolver keeps this in parity with the embedded ActiveX host
        RdpAuthenticationSettings auth = RdpAuthenticationResolver.Resolve(
            r.Nla,
            r.StrictServerAuthentication);
        sb.AppendLine($"authentication level:i:{auth.AuthenticationLevel}");
        sb.AppendLine($"enablecredsspsupport:i:{BoolToInt(auth.EnableCredSspSupport)}");

        // Performance
        sb.AppendLine($"bitmapcachepersistenable:i:{BoolToInt(r.BitmapCaching)}");
        sb.AppendLine($"compression:i:{BoolToInt(r.Compression)}");
        sb.AppendLine($"autoreconnection enabled:i:{BoolToInt(r.AutoReconnect)}");

        // Multi-monitor
        if (useMultiMonitor)
        {
            sb.AppendLine("use multimon:i:1");
            if (selectedMonitorIndices.Length > 0)
            {
                sb.AppendLine($"selectedmonitors:s:{string.Join(',', selectedMonitorIndices)}");
            }
        }
        else if (options.EmitDisabledMultiMonitor)
        {
            sb.AppendLine("use multimon:i:0");
        }

        // Dynamic resolution
        if (useSmartSizing || r.DynamicResolution)
        {
            sb.AppendLine("smart sizing:i:1");
        }

        if (r.DynamicResolution)
        {
            sb.AppendLine("dynamic resolution:i:1");
        }

        // USB / Webcam
        if (r.Usb) sb.AppendLine("usbdevicestoredirect:s:*");
        if (r.Webcam) sb.AppendLine("camerastoredirect:s:*");

        // Performance experience flags
        var pf = r.PerformanceFlags;
        if (pf > 0)
        {
            sb.AppendLine($"disable wallpaper:i:{((pf & 0x01) != 0 ? 1 : 0)}");
            sb.AppendLine($"disable full window drag:i:{((pf & 0x02) != 0 ? 1 : 0)}");
            sb.AppendLine($"disable menu anims:i:{((pf & 0x04) != 0 ? 1 : 0)}");
            sb.AppendLine($"disable themes:i:{((pf & 0x08) != 0 ? 1 : 0)}");
            sb.AppendLine($"disable cursor setting:i:{((pf & 0x20) != 0 ? 1 : 0)}");
            sb.AppendLine($"allow font smoothing:i:{((pf & 0x80) != 0 ? 1 : 0)}");
            sb.AppendLine($"allow desktop composition:i:{((pf & 0x100) != 0 ? 1 : 0)}");
        }

        // Suppress the UDP probe by disabling network auto-detection and declaring an explicit
        // connection type. This does not force TCP: no client-side setting can.
        if (r.DisableUdp)
        {
            sb.AppendLine("networkautodetect:i:0");
            sb.AppendLine("bandwidthautodetect:i:0");
            sb.AppendLine("connection type:i:6");
        }
        else
        {
            sb.AppendLine("networkautodetect:i:1");
            sb.AppendLine("bandwidthautodetect:i:1");
            sb.AppendLine("connection type:i:7");
        }

        // RD Gateway — validate hostname before writing to .rdp file
        if (!string.IsNullOrWhiteSpace(options.GatewayHostname))
        {
            if (!Core.Security.InputValidator.Validate(options.GatewayHostname, "Address"))
            {
                throw new RdpGatewayAttestationException(
                    options.GatewayHostname,
                    RdpGatewayAttestationStep.GatewayValidation);
            }

            sb.AppendLine("gatewayusagemethod:i:1");
            sb.AppendLine("gatewayprofileusagemethod:i:1");
            AppendSanitized(sb, "gatewayhostname:s:", options.GatewayHostname);
            sb.AppendLine("gatewaycredentialssource:i:0");
        }
        else
        {
            sb.AppendLine("gatewayusagemethod:i:0");
        }

        return sb.ToString().ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// Write an .rdp file with ACL restricted to the current user + Administrators + SYSTEM.
    /// The ACL is applied atomically at file-creation time to eliminate the TOCTOU
    /// window where the file briefly exists with the parent directory's inherited ACL.
    /// </summary>
    public static Task WriteToFileAsync(string filePath, RdpFileOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var content = Generate(options);
        return SecureFileWriter.WriteAndProtectAsync(filePath, content, ct);
    }

    /// <summary>
    /// Sanitize a string value to prevent CRLF injection in .rdp files (CWE-93).
    /// Strips CR, LF, and null bytes from the value.
    /// </summary>
    private static string SanitizeValue(string value)
    {
        return value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a sanitized key:value line to the StringBuilder.
    /// </summary>
    private static void AppendSanitized(StringBuilder sb, string prefix, string value)
    {
        sb.AppendLine($"{prefix}{SanitizeValue(value)}");
    }

    private static int BoolToInt(bool value) => value ? 1 : 0;
}

public enum RdpFileScreenMode
{
    Windowed = 1,
    FullScreen = 2
}

/// <summary>
/// Options for generating an .rdp file.
/// </summary>
public sealed class RdpFileOptions
{
    /// <summary>Target host address.</summary>
    public required string Host { get; init; }

    /// <summary>RDP port (default remote desktop port).</summary>
    public int Port { get; init; } = DefaultPorts.Rdp;

    /// <summary>Pre-filled username.</summary>
    public string? Username { get; init; }

    /// <summary>Domain for domain-joined authentication.</summary>
    public string? Domain { get; init; }

    /// <summary>Desktop width in pixels.</summary>
    public int Width { get; init; } = 1920;

    /// <summary>Desktop height in pixels.</summary>
    public int Height { get; init; } = 1080;

    /// <summary>Color depth in bits per pixel (16, 24, or 32).</summary>
    public int ColorDepth { get; init; } = 32;

    /// <summary>Whether to launch in full-screen mode.</summary>
    public bool FullScreen { get; init; }

    /// <summary>Explicit screen mode override. Null preserves the legacy generator mapping.</summary>
    public RdpFileScreenMode? ScreenMode { get; init; }

    /// <summary>Whether to request multi-monitor mode.</summary>
    public bool MultiMonitor { get; init; }

    /// <summary>Whether to write use multimon:i:0 when multi-monitor is disabled.</summary>
    public bool EmitDisabledMultiMonitor { get; init; }

    /// <summary>Selected local monitor indices for multi-monitor mode. Empty means all monitors.</summary>
    public int[] SelectedMonitorIndices { get; init; } = [];

    /// <summary>Whether to enable client-side smart sizing.</summary>
    public bool SmartSizing { get; init; }

    /// <summary>Whether to connect in admin/console mode (/admin).</summary>
    public bool AdminMode { get; init; }

    /// <summary>Device and feature redirection options.</summary>
    public RdpRedirectionOptions Redirections { get; init; } = new();

    /// <summary>RD Gateway hostname (null to disable gateway).</summary>
    public string? GatewayHostname { get; init; }
}
