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

namespace Heimdall.Rdp;

/// <summary>
/// Configuration options for RDP device and feature redirections.
/// </summary>
public sealed class RdpRedirectionOptions
{
    /// <summary>Redirect clipboard contents between local and remote sessions.</summary>
    public bool Clipboard { get; set; } = true;

    /// <summary>Redirect local drives to the remote session.</summary>
    public bool Drives { get; set; }

    /// <summary>Redirect local printers to the remote session.</summary>
    public bool Printers { get; set; }

    /// <summary>Redirect COM (serial) ports to the remote session.</summary>
    public bool ComPorts { get; set; }

    /// <summary>Redirect smart card readers to the remote session.</summary>
    public bool SmartCards { get; set; }

    /// <summary>Redirect USB devices to the remote session.</summary>
    public bool Usb { get; set; }

    /// <summary>Redirect webcam to the remote session.</summary>
    public bool Webcam { get; set; }

    /// <summary>Redirect audio capture (microphone) to the remote session.</summary>
    public bool AudioCapture { get; set; }

    /// <summary>Audio playback mode: 0 = disabled, 1 = play locally, 2 = play on remote.</summary>
    public int AudioMode { get; set; }

    /// <summary>
    /// Maps Heimdall audio playback options to mstsc/ActiveX audio redirection values.
    /// </summary>
    public static int MapAudioModeToRdpValue(int audioMode)
    {
        return audioMode switch
        {
            1 => 0,
            2 => 1,
            _ => 2
        };
    }

    /// <summary>Enable multi-monitor spanning.</summary>
    public bool MultiMonitor { get; set; }

    /// <summary>Enable dynamic resolution updates when the window is resized.</summary>
    public bool DynamicResolution { get; set; }

    /// <summary>Require Network Level Authentication before connecting.</summary>
    public bool Nla { get; set; } = true;

    /// <summary>Fail closed when the RDP server identity cannot be authenticated.</summary>
    public bool StrictServerAuthentication { get; set; }

    /// <summary>Enable bitmap caching for improved rendering performance.</summary>
    public bool BitmapCaching { get; set; } = true;

    /// <summary>Enable RDP data compression.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>
    /// Let the RDP control decode and present through the graphics adapter.
    /// </summary>
    /// <remarks>
    /// Off by default because it is expensive: the control builds a Direct3D device and its
    /// decoding context <b>per control instance</b>, and three concurrent 1920x1080 sessions
    /// measured 1145.9 MB of private commit with it on against 763.3 MB with it off, for 840
    /// extra kernel handles (issue #161, 2026-08-24). The cost of turning it off is processor
    /// decoding, which was not measured under sustained motion; that is why this is a setting
    /// and not a constant.
    /// </remarks>
    public bool HardwareAcceleration { get; set; }

    /// <summary>Enable automatic reconnection on network interruption.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// RDP experience performance flags bitmask (TS_PERF_* constants).
    /// 0x01 = Disable wallpaper, 0x02 = Disable full-window drag,
    /// 0x04 = Disable menu animations, 0x08 = Disable themes,
    /// 0x10 = Enable enhanced graphics (RemoteFX),
    /// 0x20 = Disable cursor shadow, 0x40 = Disable cursor blinking,
    /// 0x80 = Enable font smoothing (ClearType),
    /// 0x100 = Enable desktop composition (Aero/DWM).
    /// </summary>
    public int PerformanceFlags { get; set; }

    /// <summary>
    /// Stop the client probing for UDP transport, by disabling automatic bandwidth detection and
    /// declaring an explicit network type.
    /// </summary>
    /// <remarks>
    /// This does not force TCP. The Remote Desktop client exposes no way for an application to
    /// disable UDP transport; only the machine policy <c>fClientDisableUDP</c> does that. What this
    /// removes is the probe, which is what times out behind a firewall that drops UDP.
    /// </remarks>
    public bool DisableUdp { get; set; }

    /// <summary>
    /// Microsoft RD Gateway hostname. Consumed by the embedded ActiveX host
    /// (RdpActiveXHost). Null or empty means a direct connection. The external
    /// .rdp generator uses RdpFileOptions.GatewayHostname instead.
    /// </summary>
    public string? GatewayHostname { get; set; }
}
