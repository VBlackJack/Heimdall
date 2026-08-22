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

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Everything one RDP session configures on the hosted ActiveX control, held apart from
/// the control itself so that it can be reset, and tested, without a COM apartment.
/// </summary>
/// <remarks>
/// <para>
/// The control is expensive to create: a measured 66 handles per instance that ever
/// connects, against roughly 3 when one instance is reused. Reuse is therefore worth
/// having, and it makes this state a hazard rather than a detail. Two profiles sharing
/// one control share whatever the first left behind, and a credential is among the
/// things left behind.
/// </para>
/// <para>
/// <see cref="Reset"/> is the contract that makes reuse safe. It must restore every
/// member, not merely the ones a caller is expected to set again: the members that are
/// applied conditionally are precisely the ones a later session would inherit without
/// ever asking for them.
/// </para>
/// </remarks>
public sealed class RdpSessionState
{
    /// <summary>Auto-reconnect attempts allowed before the control gives up.</summary>
    public const int DefaultMaxAutoReconnectAttempts = 20;

    /// <summary>TCP keep-alive interval in milliseconds.</summary>
    public const int DefaultKeepAliveIntervalMs = 60_000;

    internal const int DefaultWidth = 1024;
    internal const int DefaultHeight = 768;
    internal const int DefaultColorDepth = 32;
    internal const uint DefaultScaleFactor = 100;
    internal const double DefaultDpiScale = 1.0;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = DefaultPorts.Rdp;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Cleared by <see cref="Reset"/>. A control released for reuse must carry no secret.
    /// </summary>
    public string? Password { get; set; }

    public string? Domain { get; set; }

    public int Width { get; set; } = DefaultWidth;

    public int Height { get; set; } = DefaultHeight;

    public int ColorDepth { get; set; } = DefaultColorDepth;

    public uint DesktopScaleFactor { get; set; } = DefaultScaleFactor;

    public uint DeviceScaleFactor { get; set; } = DefaultScaleFactor;

    public double DpiScaleX { get; set; } = DefaultDpiScale;

    public double DpiScaleY { get; set; } = DefaultDpiScale;

    public RdpResolutionMode ResolutionMode { get; set; } = RdpResolutionMode.FitWindow;

    public bool IsFullscreen { get; set; }

    public IReadOnlyList<(int Width, int Height)> ResolutionPresets { get; set; } = [];

    public IReadOnlyList<int> SelectedMonitorIndices { get; set; } = [];

    public RdpRedirectionOptions Redirections { get; set; } = new();

    public int MaxAutoReconnectAttempts { get; set; } = DefaultMaxAutoReconnectAttempts;

    public int KeepAliveIntervalMs { get; set; } = DefaultKeepAliveIntervalMs;

    /// <summary>
    /// Restores every member to the value a freshly created state carries, so that a
    /// control handed to a new session starts from the same place as a new one would.
    /// </summary>
    public void Reset()
    {
        Host = string.Empty;
        Port = DefaultPorts.Rdp;
        Username = string.Empty;
        Password = null;
        Domain = null;
        Width = DefaultWidth;
        Height = DefaultHeight;
        ColorDepth = DefaultColorDepth;
        DesktopScaleFactor = DefaultScaleFactor;
        DeviceScaleFactor = DefaultScaleFactor;
        DpiScaleX = DefaultDpiScale;
        DpiScaleY = DefaultDpiScale;
        ResolutionMode = RdpResolutionMode.FitWindow;
        IsFullscreen = false;
        ResolutionPresets = [];
        SelectedMonitorIndices = [];
        Redirections = new RdpRedirectionOptions();
        MaxAutoReconnectAttempts = DefaultMaxAutoReconnectAttempts;
        KeepAliveIntervalMs = DefaultKeepAliveIntervalMs;
    }
}
