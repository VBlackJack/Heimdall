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

namespace Heimdall.Core.Import;

/// <summary>
/// Curated subset of Microsoft RDP (.rdp) file keys mapped into a structured shape.
/// Only keys Heimdall consumes are surfaced directly; everything else is retained
/// in <see cref="UnknownKeys"/> for diagnostics.
/// </summary>
public sealed class RdpFileSchema
{
    public string? FullAddress { get; init; }

    public string? AlternateFullAddress { get; init; }

    public string? Username { get; init; }

    /// <summary>Value of <c>domain:s:</c>, the logon domain kept apart from the user name.</summary>
    public string? Domain { get; init; }

    public int? AudioMode { get; init; }

    /// <summary>Value of <c>audiocapturemode:i:</c>, the microphone redirection switch.</summary>
    public bool? AudioCaptureMode { get; init; }

    public bool? RedirectClipboard { get; init; }

    public bool? RedirectPrinters { get; init; }

    public bool? RedirectSmartCards { get; init; }

    /// <summary>Value of <c>redirectcomports:i:</c>.</summary>
    public bool? RedirectComPorts { get; init; }

    /// <summary>
    /// Value of <c>redirectdrives:i:</c>, the drive switch the Remote Desktop client reads and the
    /// one it writes back when it saves a file (measured on mstsc 10.0.26100, 2026-09-05). A file
    /// saved by the client carries this key and never <c>drivestoredirect</c>.
    /// </summary>
    public bool? RedirectDrives { get; init; }

    /// <summary>
    /// Value of <c>drivestoredirect:s:</c>. When both keys are present this one wins, which is
    /// what the client does with them.
    /// </summary>
    public string? DrivesToRedirect { get; init; }

    /// <summary>Value of <c>usbdevicestoredirect:s:</c>; a non-empty value redirects USB devices.</summary>
    public string? UsbDevicesToRedirect { get; init; }

    /// <summary>Value of <c>camerastoredirect:s:</c>; a non-empty value redirects cameras.</summary>
    public string? CamerasToRedirect { get; init; }

    /// <summary>Value of <c>administrative session:i:</c>, the console (/admin) switch.</summary>
    public bool? AdministrativeSession { get; init; }

    /// <summary>Value of <c>compression:i:</c>.</summary>
    public bool? Compression { get; init; }

    /// <summary>Value of <c>bitmapcachepersistenable:i:</c>.</summary>
    public bool? BitmapCachePersistEnable { get; init; }

    /// <summary>Value of <c>autoreconnection enabled:i:</c>.</summary>
    public bool? AutoReconnectionEnabled { get; init; }

    /// <summary>Value of <c>dynamic resolution:i:</c>.</summary>
    public bool? DynamicResolution { get; init; }

    public int? ScreenModeId { get; init; }

    public bool? UseMultiMon { get; init; }

    public int? DesktopWidth { get; init; }

    public int? DesktopHeight { get; init; }

    public int? SessionBpp { get; init; }

    public int? AuthenticationLevel { get; init; }

    /// <summary>
    /// Value of <c>enablecredsspsupport:i:</c>, the only field carrying NLA/CredSSP state.
    /// <c>authentication level</c> describes server authentication and must never be read as NLA.
    /// </summary>
    public int? EnableCredSspSupport { get; init; }

    public string? GatewayHostname { get; init; }

    public int? GatewayUsageMethod { get; init; }

    public bool HasPasswordBlob { get; init; }

    public IReadOnlyDictionary<string, string> UnknownKeys { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
