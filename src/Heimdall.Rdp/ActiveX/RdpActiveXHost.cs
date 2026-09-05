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

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Threading;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Rdp;
using Heimdall.Rdp.Display;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Hosts the Microsoft Terminal Services ActiveX control (MsTscAx).
/// Inherits from <see cref="AxHost"/> to create the COM object and implements
/// <see cref="IRdpSession"/> for a clean abstraction layer.
///
/// IMPORTANT: Do NOT call <see cref="AttachEventSink"/> from the
/// <see cref="AxHost.CreateSink"/> override - this causes hangs in non-STA
/// contexts (e.g., unit tests). Call it explicitly after the handle is created.
/// </summary>
public sealed class RdpActiveXHost : AxHost, IRdpSession, IReusableHost, IRdpDisplayContextSink
{
    // MsTscAx ActiveX control CLSID. The registry names this coclass
    // "Microsoft RDP Client Control - version 2" (ProgID MsTscAx.MsTscAx.2); newer
    // generations live in the same mstscax.dll under their own CLSIDs. Which generation is
    // instantiated was measured to have no effect on memory: the CLSID selects a table entry
    // that points at the same class factory and the same constructor (issue #161).
    public const string DefaultMsTscAxClsid = "7cacbd7b-0d99-468f-ac33-22e495c0afe5";

    /// <summary>
    /// Name of the <c>IMsRdpExtendedSettings</c> property that decides whether the control
    /// decodes and presents through the graphics adapter.
    /// </summary>
    private const string HardwareModeProperty = "EnableHardwareMode";

    /// <summary>
    /// Name of the <c>MsRdpClientShell</c> .rdp property carrying the monitor selection.
    /// </summary>
    private const string SelectedMonitorsProperty = "selectedmonitors";

    /// <summary>
    /// Name of the advanced setting carrying PnP and USB device redirection, used for logging
    /// when the control refuses it.
    /// </summary>
    private const string RedirectDevicesProperty = "RedirectDevices";

    private static readonly Guid IidMsRdpExtendedSettings = new("302D8188-0052-4807-806A-362B628F9AC5");
    private static readonly Guid IidMsRdpClientNonScriptable5 = new("4F6996D5-D7B1-412C-B0FF-063718566907");
    // MsTscAx typelib slot: IUnknown(3) + inherited nonscriptable methods(50). Pinned against
    // the type library by MsTscAxVtableContractTests: the interface is vtable-only, so a wrong
    // slot calls whatever member sits there, with no error and no log line.
    internal const int NonScriptable5PutUseMultimonSlot = 53;

    /// <summary>
    /// Default number of auto-reconnect attempts; <see cref="SetResilienceOptions"/>
    /// can override it within the [1,60] MsTscAx range.
    /// </summary>
    public const int MaxAutoReconnectAttempts = RdpSessionState.DefaultMaxAutoReconnectAttempts;
    public const int NoExtendedDisconnectReason = (int)ExtendedDisconnectReasonCode.NoInfo;

    private const int MinAutoReconnectAttempts = 1;
    private const int MaxAutoReconnectAttemptsLimit = 60;

    /// <summary>TCP keep-alive interval in milliseconds (60 seconds).</summary>
    public const int DefaultKeepAliveIntervalMs = RdpSessionState.DefaultKeepAliveIntervalMs;

    /// <summary>Smart sizing applied to a control that has not been told otherwise.</summary>
    public const bool DefaultInitialSmartSizing = true;

    private const int MinKeepAliveIntervalMs = 5_000;
    private const int MaxKeepAliveIntervalMs = 300_000;

    private object? _activeX;
    private bool _disposed;
    private ConnectionPointCookie? _cookie;
    private MsTscAxEventSink? _sink;
    private readonly string _activeXClsid;
    private readonly RdpPostConnectStripTimer _postConnectStripTimer;

    // Everything one session configures, held apart so that it can be reset wholesale
    // before this control is handed to another session.
    private readonly RdpSessionState _session = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutUseMultimonDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.VariantBool)] bool useMultimon);

    private const int GWL_STYLE = -16;
    private const long WS_HSCROLL = 0x0010_0000L;
    private const long WS_VSCROLL = 0x0020_0000L;
    private const long ScrollbarStyleMask = WS_HSCROLL | WS_VSCROLL;
    private const int SB_BOTH = 3;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(
        IntPtr hwnd,
        int bar,
        [MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr hwndParent,
        EnumChildProc enumFunc,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    /// <inheritdoc />
    public event Action? Connected;

    /// <inheritdoc />
    public event Action<int>? Disconnected;

    /// <inheritdoc />
    public event Action<int>? FatalError;

    /// <summary>Raised when the server has accepted credentials and login is complete.</summary>
    public event Action? LoginComplete;

    /// <summary>Raised when the client begins an auto-reconnect attempt (args: disconnectReason, attemptCount).</summary>
    public event Action<int, int>? AutoReconnecting;

    /// <summary>Raised when an auto-reconnect attempt succeeds.</summary>
    public event Action? AutoReconnected;

    /// <summary>
    /// Set to <c>true</c> to cancel any in-progress auto-reconnect attempt.
    /// The COM event sink checks this flag on each <c>OnAutoReconnecting</c> callback.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool CancelAutoReconnect { get; set; }

    /// <summary>Stores the last error message for diagnostics.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>
    /// True while the COM event sink is advised. Derived from the connection point rather
    /// than mirrored in a caller, so no second copy of this fact can disagree with it.
    /// </summary>
    public bool IsEventSinkAttached => _cookie is not null;

    /// <summary>
    /// False once this control has met something that makes reuse unsafe. A control that
    /// reported a fatal error, or that failed to reset, is discarded rather than handed to
    /// another session: recycling a poisoned control costs more than the leak reuse avoids.
    /// </summary>
    public bool IsReusable { get; private set; } = true;

    /// <summary>The ActiveX CLSID used to instantiate this control.</summary>
    public string ActiveXClsid => _activeXClsid;

    /// <summary>Current host window handle, or <see cref="IntPtr.Zero"/> when not created.</summary>
    public IntPtr HostHandle => IsHandleCreated ? Handle : IntPtr.Zero;

    /// <summary>
    /// Initial SmartSizing state applied before connect. Defaults to the historical behavior.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool InitialSmartSizing { get; set; } = DefaultInitialSmartSizing;

    public int LastExtendedDisconnectReason { get; private set; } = NoExtendedDisconnectReason;

    public RdpActiveXHost(string? activeXClsid = null)
        : base(activeXClsid ?? DefaultMsTscAxClsid)
    {
        _activeXClsid = activeXClsid ?? DefaultMsTscAxClsid;
        _postConnectStripTimer = new RdpPostConnectStripTimer(
            () => new DispatcherRdpStripTimer(Dispatcher.CurrentDispatcher, DispatcherPriority.Background),
            SystemRdpPostConnectStripTimerClock.Instance,
            () => StripScrollbarStylesRecursiveOnUiThread("post-connect-timer"),
            Core.Logging.FileLogger.Info);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StripScrollbarStylesRecursive();
    }

    /// <summary>
    /// Returns the raw ActiveX COM object obtained via <see cref="AxHost.GetOcx"/>.
    /// </summary>
    public object? GetActiveXInstance()
    {
        if (_activeX is null && IsHandleCreated)
        {
            _activeX = GetOcx();
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.GetActiveXInstance: handle=0x{HostHandle.ToInt64():X} ocxType={_activeX?.GetType().FullName ?? "null"} clsid={_activeXClsid}");
        }
        return _activeX;
    }

    /// <summary>
    /// Called by <see cref="AxHost"/> after the underlying COM object is created.
    /// Caches the ActiveX reference for later use.
    /// </summary>
    protected override void AttachInterfaces()
    {
        _activeX = GetOcx();
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.AttachInterfaces: handle=0x{HostHandle.ToInt64():X} ocxType={_activeX?.GetType().FullName ?? "null"} clsid={_activeXClsid}");
    }

    /// <summary>
    /// Gives keyboard focus to the ActiveX child HWND used by the embedded RDP surface.
    /// </summary>
    public void FocusRdpSurface()
    {
        if (_disposed || !IsHandleCreated)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.FocusRdpSurface skipped: disposed={_disposed} handleCreated={IsHandleCreated}");
            return;
        }

        try
        {
            IntPtr handle = Handle;
            _ = Focus();
            _ = SetFocus(handle);
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.FocusRdpSurface: focused handle=0x{handle.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.FocusRdpSurface failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void SetServer(string host, int port = DefaultPorts.Rdp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _session.Host = host;
        _session.Port = port;
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetServer: host={host} port={port} handleCreated={IsHandleCreated} clsid={_activeXClsid}");

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplyServerSettings(ocx);
        }
    }

    /// <inheritdoc />
    public void SetCredentials(string username, string? password = null, string? domain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        _session.Username = username;
        _session.Password = password;
        _session.Domain = domain;

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplyCredentialSettings(ocx);
        }
    }

    /// <inheritdoc />
    public void SetDisplay(int width, int height, int colorDepth = 32)
    {
        _session.Width = SnapDesktopWidth(width);
        _session.Height = height;
        _session.ColorDepth = colorDepth;
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetDisplay: width={_session.Width} height={height} colorDepth={colorDepth} handleCreated={IsHandleCreated}");

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplyDisplaySettings(ocx);
        }
    }

    /// <summary>
    /// Configure the profile display mode that will be resolved immediately before connect.
    /// </summary>
    public void SetResolutionMode(
        RdpResolutionMode resolutionMode,
        bool isFullscreen,
        IReadOnlyList<(int Width, int Height)>? presets = null,
        IReadOnlyList<int>? selectedMonitorIndices = null)
    {
        _session.ResolutionMode = resolutionMode;
        _session.IsFullscreen = isFullscreen;
        _session.ResolutionPresets = presets is null
            ? []
            : presets.Where(preset => preset.Width > 0 && preset.Height > 0).ToArray();
        _session.SelectedMonitorIndices = selectedMonitorIndices is null
            ? []
            : selectedMonitorIndices.Where(index => index >= 0).ToArray();

        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetResolutionMode: mode={resolutionMode} fullscreen={isFullscreen} presets={_session.ResolutionPresets.Count} selectedMonitors={string.Join(',', _session.SelectedMonitorIndices)} handleCreated={IsHandleCreated}");
    }

    /// <summary>
    /// Configure initial scale factors through IMsRdpExtendedSettings before connect.
    /// </summary>
    public void SetDisplayScaleFactors(
        uint desktopScaleFactor,
        uint deviceScaleFactor,
        double dpiScaleX = 1.0,
        double dpiScaleY = 1.0)
    {
        _session.DesktopScaleFactor = desktopScaleFactor;
        _session.DeviceScaleFactor = deviceScaleFactor;
        _session.DpiScaleX = dpiScaleX;
        _session.DpiScaleY = dpiScaleY;
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetDisplayScaleFactors: desktop={desktopScaleFactor} device={deviceScaleFactor} dpi={dpiScaleX:0.##}x{dpiScaleY:0.##} connected={IsConnected} handleCreated={IsHandleCreated}");

        if (IsConnected)
        {
            Core.Logging.FileLogger.Info("RdpActiveXHost.SetDisplayScaleFactors skipped: ExtendedSettings scale factors are pre-connect only.");
            return;
        }

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplyDisplayScaleSettings(ocx);
        }
    }

    /// <summary>
    /// Sets SmartSizing on the ActiveX control. This is live-mutable.
    /// </summary>
    public void SetSmartSizing(bool enabled)
    {
        InitialSmartSizing = enabled;

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplySmartSizing(ocx, enabled);
        }
    }

    /// <inheritdoc />
    public void SetRedirections(RdpRedirectionOptions redirections)
    {
        ArgumentNullException.ThrowIfNull(redirections);
        _session.Redirections = redirections;
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetRedirections: clipboard={redirections.Clipboard} drives={redirections.Drives} printers={redirections.Printers} dynamicResolution={redirections.DynamicResolution}");

        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            ApplyRedirectionSettings(ocx);
        }
    }

    /// <inheritdoc />
    public void SetResilienceOptions(int maxAutoReconnectAttempts, int keepAliveIntervalMs)
    {
        _session.MaxAutoReconnectAttempts = Math.Clamp(
            maxAutoReconnectAttempts,
            MinAutoReconnectAttempts,
            MaxAutoReconnectAttemptsLimit);
        _session.KeepAliveIntervalMs = Math.Clamp(
            keepAliveIntervalMs,
            MinKeepAliveIntervalMs,
            MaxKeepAliveIntervalMs);

        // Remembered, not applied: both values are written by ApplyRedirectionSettings, which
        // Connect() runs over every pending setting. Applying here as well ran that method a
        // third time before every connect - two interface queries, twenty late-bound writes
        // and a gateway attestation with read-back - for values Connect() was about to write.
        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.SetResilienceOptions: reconnectAttempts={_session.MaxAutoReconnectAttempts} keepAliveMs={_session.KeepAliveIntervalMs} handleCreated={IsHandleCreated}");
    }

    public int EffectiveMaxAutoReconnectAttempts => _session.MaxAutoReconnectAttempts;

    public int EffectiveKeepAliveIntervalMs => _session.KeepAliveIntervalMs;

    /// <inheritdoc />
    public void Connect()
    {
        object ocx = GetActiveXInstance()
            ?? throw new InvalidOperationException("ActiveX control is not initialized. Ensure the host control handle is created first.");

        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.Connect: handle=0x{HostHandle.ToInt64():X} clsid={_activeXClsid} ocxType={ocx.GetType().FullName ?? "unknown"} size={_session.Width}x{_session.Height}");

        try
        {
            ResolveAndApplyPendingDisplayContext();

            // Apply all pending settings before connecting
            ApplyServerSettings(ocx);
            ApplyCredentialSettings(ocx);
            ApplyDisplaySettings(ocx);
            ApplyDisplayScaleSettings(ocx);
            ApplyRedirectionSettings(ocx);

            ((dynamic)ocx).Connect();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
        finally
        {
            // Clear plaintext password from managed memory after the connection attempt.
            _session.Password = null;
        }
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        var ocx = GetActiveXInstance();
        if (ocx is not null)
        {
            try
            {
                ((dynamic)ocx).Disconnect();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    /// <inheritdoc />
    public RdpDisplayUpdateResult UpdateResolution(
        int width,
        int height,
        uint physicalWidthMm = 0,
        uint physicalHeightMm = 0,
        uint desktopScaleFactor = 100,
        uint deviceScaleFactor = 100,
        bool allowReconnectFallback = true)
    {
        if (!CanAttemptResolutionUpdate(_disposed, IsConnected))
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.UpdateResolution skipped: disposed={_disposed} connected={IsConnected} for {width}x{height}");
            return RdpDisplayUpdateResult.Skipped;
        }

        object? ocx = GetActiveXInstance();
        if (ocx is null)
        {
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.UpdateResolution skipped: no ActiveX instance for {width}x{height}");
            return RdpDisplayUpdateResult.Skipped;
        }

        width = SnapDesktopWidth(width);
        physicalWidthMm = physicalWidthMm == 0 ? (uint)width : physicalWidthMm;
        physicalHeightMm = physicalHeightMm == 0 ? (uint)height : physicalHeightMm;

        try
        {
            // IMsRdpClient9+ (RDP 8.1+): change resolution without reconnection.
            // Parameters: desktopWidth, desktopHeight, physicalWidth, physicalHeight,
            //             orientation(0), desktopScaleFactor, deviceScaleFactor
            ocx.GetType().InvokeMember(
                "UpdateSessionDisplaySettings",
                BindingFlags.InvokeMethod,
                null,
                ocx,
                new object[] { (uint)width, (uint)height, physicalWidthMm, physicalHeightMm,
                               (uint)0, desktopScaleFactor, deviceScaleFactor });

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.UpdateResolution: handle=0x{HostHandle.ToInt64():X} {width}x{height} physical={physicalWidthMm}x{physicalHeightMm}mm scale={desktopScaleFactor}/{deviceScaleFactor} (seamless)");
            StripScrollbarStylesRecursiveOnUiThread("UpdateSessionDisplaySettings");
            return RdpDisplayUpdateResult.Seamless;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.UpdateSessionDisplaySettings failed: {ex.Message}");

            if (!allowReconnectFallback)
            {
                LastError = ex.Message;
                return RdpDisplayUpdateResult.ReconnectRequired;
            }

            // Fallback: Reconnect(width, height) on IMsRdpClient7+ (older servers/clients)
            try
            {
                ocx.GetType().InvokeMember(
                    "Reconnect",
                    BindingFlags.InvokeMethod,
                    null,
                    ocx,
                    [(uint)width, (uint)height]);

                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.UpdateResolution: handle=0x{HostHandle.ToInt64():X} {width}x{height} (reconnect fallback)");
                StripScrollbarStylesRecursiveOnUiThread("Reconnect");
                return RdpDisplayUpdateResult.ReconnectFallback;
            }
            catch (Exception exFallback)
            {
                LastError = exFallback.Message;
                Core.Logging.FileLogger.Warn(
                    $"RdpActiveXHost.UpdateResolution failed: {exFallback.Message}");
                return RdpDisplayUpdateResult.Failed;
            }
        }
    }

    /// <summary>
    /// Updates the pending fullscreen context and re-runs the display resolver.
    /// The caller applies the returned dimensions through <see cref="UpdateResolution"/>.
    /// </summary>
    public EffectiveDisplayContext? RecomputeDisplayForFullscreen(bool isFullscreen)
    {
        if (_disposed || HostHandle == IntPtr.Zero)
        {
            return null;
        }

        _session.IsFullscreen = isFullscreen;

        try
        {
            return ResolveAndApplyPendingDisplayContext();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.RecomputeDisplayForFullscreen failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Attach the COM event sink via connection point.
    /// Must be called explicitly after the control handle is created, on the STA thread.
    /// Returns true if the event sink was successfully connected.
    /// </summary>
    public bool AttachEventSink()
    {
        try
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.AttachEventSink: handle=0x{HostHandle.ToInt64():X} handleCreated={IsHandleCreated} clsid={_activeXClsid}");
            var ocx = GetOcx();
            if (ocx is null)
            {
                LastError = "GetOcx() returned null - connection point not available for event sink";
                Core.Logging.FileLogger.Warn("RdpActiveXHost.AttachEventSink failed: GetOcx returned null");
                return false;
            }

            _sink = new MsTscAxEventSink(this);
            _cookie = new ConnectionPointCookie(ocx, _sink, typeof(IMsTscAxEvents));
            Core.Logging.FileLogger.Info("RdpActiveXHost.AttachEventSink succeeded");

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.AttachEventSink failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Detach the COM event sink. Call during cleanup before Dispose.
    /// </summary>
    public void DetachEventSink()
    {
        if (_cookie is not null)
        {
            try
            {
                _cookie.Disconnect();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            _cookie = null;
        }
        _sink = null;
    }

    /// <inheritdoc />
    public Control GetHostControl() => this;

    /// <summary>
    /// Inject password via the IMsTscNonScriptable COM interface (QueryInterface).
    /// Returns true on success.
    /// </summary>
    public bool SetClearTextPassword(string password)
    {
        try
        {
            // The cached instance, not GetOcx(): teardown destroys the handle before the control
            // is released for reuse, and the password still has to be reachable after that.
            var ocx = GetActiveXInstance();
            if (ocx is null)
            {
                LastError = "GetOcx() returned null";
                return false;
            }

            var nonScriptable = (IMsTscNonScriptable)ocx;
            nonScriptable.put_ClearTextPassword(password);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.SetClearTextPassword: success=False error={ex.Message}");
            return false;
        }
    }

    // The password stays on the control for the life of the session on purpose. The resolution
    // change falls back to Reconnect(width, height) on clients without
    // UpdateSessionDisplaySettings, and that reconnect authenticates again from what the control
    // holds; a control cleared at OnConnected would prompt instead. The secret is reset by
    // RdpPasswordReset once the control reports itself disconnected, and overwritten before the
    // control is handed to another session.

    #region Internal event raisers (called by MsTscAxEventSink)

    internal void RaiseConnected()
    {
        try
        {
            IsConnected = true;
            LastExtendedDisconnectReason = NoExtendedDisconnectReason;
            try
            {
                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseConnected: event subscriber threw: {ex.Message}");
            }

            StripScrollbarStylesRecursiveOnUiThread("OnConnected");
            BeginPostConnectStripTimerOnUiThread("OnConnected");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseConnected: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseDisconnected(int discReason)
    {
        try
        {
            IsConnected = false;
            LastExtendedDisconnectReason = ReadExtendedDisconnectReason();
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.RaiseDisconnected: reason={discReason} extendedReason={LastExtendedDisconnectReason}");
            StopPostConnectStripTimerOnUiThread($"OnDisconnected reason={discReason}");
            try
            {
                Disconnected?.Invoke(discReason);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseDisconnected: event subscriber threw: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseDisconnected: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseFatalError(int errorCode)
    {
        try
        {
            IsConnected = false;
            IsReusable = false;
            StopPostConnectStripTimerOnUiThread($"OnFatalError error={errorCode}");
            try
            {
                FatalError?.Invoke(errorCode);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseFatalError: event subscriber threw: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseFatalError: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseLoginComplete()
    {
        try
        {
            try
            {
                LoginComplete?.Invoke();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseLoginComplete: event subscriber threw: {ex.Message}");
            }

            StripScrollbarStylesRecursiveOnUiThread("OnLoginComplete");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseLoginComplete: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseRemoteDesktopSizeChanged(int width, int height)
    {
        try
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.OnRemoteDesktopSizeChange: width={width} height={height}");
            StripScrollbarStylesRecursiveOnUiThread("OnRemoteDesktopSizeChange");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseRemoteDesktopSizeChanged: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseAutoReconnecting(int disconnectReason, int attemptCount)
    {
        try
        {
            IsConnected = false;
            LastExtendedDisconnectReason = ReadExtendedDisconnectReason();
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.RaiseAutoReconnecting: reason={disconnectReason} extendedReason={LastExtendedDisconnectReason} attempt={attemptCount}");
            try
            {
                AutoReconnecting?.Invoke(disconnectReason, attemptCount);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseAutoReconnecting: event subscriber threw: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseAutoReconnecting: COM event handling failed: {ex.Message}");
        }
    }

    internal void RaiseAutoReconnected()
    {
        try
        {
            IsConnected = true;
            LastExtendedDisconnectReason = NoExtendedDisconnectReason;
            try
            {
                AutoReconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseAutoReconnected: event subscriber threw: {ex.Message}");
            }

            StripScrollbarStylesRecursiveOnUiThread("OnAutoReconnected");
            BeginPostConnectStripTimerOnUiThread("OnAutoReconnected");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost.RaiseAutoReconnected: COM event handling failed: {ex.Message}");
        }
    }

    #endregion

    internal static long StripScrollbarBits(long style) => style & ~ScrollbarStyleMask;

    /// <summary>
    /// Disposed or disconnected hosts are not touched because the reconnect fallback could otherwise revive the session.
    /// </summary>
    internal static bool CanAttemptResolutionUpdate(bool disposed, bool isConnected)
        => !disposed && isConnected;

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, newLong)
            : new IntPtr(SetWindowLong32(hwnd, index, unchecked((int)newLong.ToInt64())));
    }

    private static void StripScrollbarStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            var newStyle = StripScrollbarBits(style);
            if (newStyle != style)
            {
                _ = SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));
                _ = SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.StripScrollbarStyles: hwnd=0x{hwnd.ToInt64():X} style=0x{style:X}->0x{newStyle:X}");
            }

            // Some native controls can also keep scrollbar visibility outside WS_*SCROLL.
            _ = ShowScrollBar(hwnd, SB_BOTH, false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[RdpActiveXHost] StripScrollbarStyles({hwnd}): {ex.Message}");
        }
    }

    private void StripScrollbarStylesRecursive(string reason = "direct")
    {
        if (!IsHandleCreated)
        {
            return;
        }

        StripScrollbarStyles(Handle);

        try
        {
            EnumChildProc enumChild = (child, _) =>
            {
                StripScrollbarStyles(child);
                return true;
            };

            _ = EnumChildWindows(Handle, enumChild, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[RdpActiveXHost] EnumChildWindows ({reason}): {ex.Message}");
        }
    }

    private void StripScrollbarStylesRecursiveOnUiThread(string reason)
    {
        if (_disposed || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                _ = BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                {
                    StripScrollbarStylesRecursive(reason);
                }));
                return;
            }

            StripScrollbarStylesRecursive(reason);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[RdpActiveXHost] StripScrollbarStylesRecursive ({reason}): {ex.Message}");
        }
    }

    private void BeginPostConnectStripTimerOnUiThread(string reason)
    {
        if (_disposed || IsDisposed || !IsHandleCreated)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.PostConnectStripTimer start skipped: reason={reason} disposed={_disposed} isDisposed={IsDisposed} handleCreated={IsHandleCreated} handle=0x{GetHandleForLog().ToInt64():X}");
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                _ = BeginInvoke((System.Windows.Forms.MethodInvoker)(() => _postConnectStripTimer.Begin(reason)));
                return;
            }

            _postConnectStripTimer.Begin(reason);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[RdpActiveXHost] PostConnectStripTimer start failed: {ex.Message}");
        }
    }

    private void StopPostConnectStripTimerOnUiThread(string reason)
    {
        try
        {
            if (!_disposed && !IsDisposed && IsHandleCreated && InvokeRequired)
            {
                _ = BeginInvoke((System.Windows.Forms.MethodInvoker)(() => _postConnectStripTimer.Stop(reason)));
                return;
            }

            _postConnectStripTimer.Stop(reason);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[RdpActiveXHost] PostConnectStripTimer stop failed: {ex.Message}");
        }
    }

    private IntPtr GetHandleForLog()
    {
        try
        {
            return IsHandleCreated ? Handle : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    #region Disconnect reason decoder

    /// <summary>
    /// Translates an MsTscAx disconnect reason code into an i18n key suffix.
    /// The caller prepends "RdpDisconnect" to build the full i18n key.
    /// Returns <c>null</c> for unknown codes (caller falls back to the raw number).
    /// </summary>
    public static string? GetDisconnectReasonKey(int reason) => reason switch
    {
        0 => "NoInfo",
        1 => "LocalUser",
        2 => "UserLogoff",
        3 => "AdminDisconnect",
        260 => "DnsLookupFailed",
        262 => "OutOfMemory",
        264 => "ConnectionTimeout",
        516 => "SocketConnectFailed",
        772 => "NetworkError",
        1030 => "SecurityError",
        1796 => "TimeoutOccurred",
        1800 => "ConsoleSessionInProgress",
        2055 => "BadCredentials",
        2056 => "LicensingError",
        2308 => "SocketClosed",
        2311 => "CertificateWarning",
        2567 => "UserNotFound",
        2822 => "EncryptionError",
        2823 => "AccountDisabled",
        2825 => "NlaNotSupported",
        3079 => "TimeOfDayRestriction",
        3080 => "ClientDecompressionFailed",
        3335 => "AccountLockedOut",
        3591 => "AccountExpired",
        3847 => "PasswordExpired",
        3848 => "CredSspPolicyError",
        3592 or 4360 => "ReconnectFailed",
        4617 => "NlaRequired",
        6151 => "NoAuthenticationAuthority",
        6919 => "CertificateExpired",
        7431 => "ClockSkew",
        // Unknown codes intentionally fall through to raw-number display; MsTscAx
        // extended-reason bit-packing is not decoded here.
        //
        // The labels above are the client's own, cross-checked against the Win32 interface
        // reference and against the strings the installed client returns for each code. Three of
        // them used to say something else: 1796 is a time-out and was called an internal error,
        // 2825 is a server that requires Network Level Authentication and was called a
        // decompression failure, and 4360 is a failed reconnection and was called a resolution
        // change. Reading a label off the client's decoder is what produced the first of those: it
        // answers "an internal error has occurred" for every code it does not know, which is a
        // not-found sentinel rather than a meaning.
        _ => null
    };

    public static string? GetExtendedDisconnectReasonKey(int extendedReason)
    {
        ExtendedDisconnectReasonCode extendedDisconnectReason =
            (ExtendedDisconnectReasonCode)extendedReason;

        return extendedDisconnectReason switch
        {
            ExtendedDisconnectReasonCode.ServerDeniedConnection
                or ExtendedDisconnectReasonCode.ServerDeniedConnectionFips
                or ExtendedDisconnectReasonCode.ServerInsufficientPrivileges
                or ExtendedDisconnectReasonCode.ServerFreshCredsRequired
                or ExtendedDisconnectReasonCode.RdpEncInvalidCredentials
                => "BadCredentials",
            ExtendedDisconnectReasonCode.ServerLogonTimeout
                => "ServerLogonTimeout",
            ExtendedDisconnectReasonCode.GatewayCredentialsRejected
                => "RdGatewayCredentials",
            ExtendedDisconnectReasonCode.GatewayCertificateRejected
                or ExtendedDisconnectReasonCode.GatewayCertificateUntrusted
                => "RdGatewayCertificate",
            ExtendedDisconnectReasonCode.GatewayUnreachable
                => "RdGatewayUnreachable",
            ExtendedDisconnectReasonCode.GatewayTimeout
                or ExtendedDisconnectReasonCode.GatewayTimeoutSecondary
                => "RdGatewayTimeout",
            _ when IsLicenseExtendedDisconnectReason(extendedReason)
                => "LicenseError",
            _ when IsGatewayExtendedDisconnectReason(extendedReason)
                => "RdGatewayError",
            _ when IsRemoteAppExtendedDisconnectReason(extendedReason)
                => "RemoteAppError",
            _ => null
        };
    }

    /// <summary>
    /// Whether an extended reason belongs to the Remote Desktop Gateway block.
    /// </summary>
    /// <remarks>
    /// <para>A range rather than a member list, and for a different reason than the licensing one:
    /// the block holds more than ninety values, of which only the handful named above have an
    /// established meaning. Recognising the range turns every other one from a bare number into
    /// "the gateway is what refused you", which is the single most useful thing to tell someone
    /// whose connection goes through one.</para>
    /// <para>The named members come from the client error reference, not from a disconnect observed
    /// here: no Remote Desktop Gateway is reachable from this machine. What IS established without
    /// a gateway is that these values cannot arrive on the primary channel - a primary reason is a
    /// two-byte encoding and these are eight-digit - so the extended channel is where they land.
    /// </para>
    /// </remarks>
    private static bool IsGatewayExtendedDisconnectReason(int extendedReason)
        => extendedReason is >= 0x0300_0000 and <= 0x0300_FFFF;

    /// <summary>
    /// Whether an extended reason belongs to the RemoteApp block.
    /// </summary>
    /// <remarks>
    /// Same treatment as the gateway block and for the same reason: no member of it has an
    /// established meaning here, so the family is named and the raw number is left to speak for
    /// the specifics.
    /// </remarks>
    private static bool IsRemoteAppExtendedDisconnectReason(int extendedReason)
        => extendedReason is >= 0x0200_0000 and <= 0x0200_FFFF;

    /// <summary>
    /// Resolves the one message key for a disconnect, from both codes the client reports.
    /// </summary>
    /// <remarks>
    /// <para>The two decoders used to be composed in opposite orders by the two consumers of the
    /// same disconnect: the on-screen diagnostic took the extended code first, the persisted
    /// session event took the primary one. For a disconnect where both decode, the message a user
    /// photographed and the line an engineer read back named different causes.</para>
    /// <para>The extended code wins, because it is what the server said about the attempt, with one
    /// exception: it does not get to overwrite a primary code that names a specific account state.
    /// The five extended codes below all decode to the same generic "the credentials were not
    /// accepted", which is true of a locked, expired or must-change-password account as well - so
    /// where the primary code says WHICH of those it is, that is the more useful of two compatible
    /// answers.</para>
    /// </remarks>
    public static string? ResolveDisconnectReasonKey(int reason, int extendedReason)
    {
        string? extended = GetExtendedDisconnectReasonKey(extendedReason);
        if (extended is null)
        {
            return GetDisconnectReasonKey(reason);
        }

        return IsGenericCredentialRejection(extendedReason) && NamesAnAccountState(reason)
            ? GetDisconnectReasonKey(reason)
            : extended;
    }

    /// <summary>
    /// Whether an extended code says only that the credentials were refused, without saying why.
    /// </summary>
    /// <remarks>
    /// Exactly the set that <see cref="GetExtendedDisconnectReasonKey"/> collapses onto the single
    /// "BadCredentials" key. Deliberately not derived from the severity table: that table answers
    /// "does this need credential action", which is a different question from "is this the vaguer
    /// of two compatible answers", and reusing it would make one change to either silently move
    /// the other.
    /// </remarks>
    private static bool IsGenericCredentialRejection(int extendedReason) =>
        (ExtendedDisconnectReasonCode)extendedReason is
            ExtendedDisconnectReasonCode.ServerDeniedConnection
            or ExtendedDisconnectReasonCode.ServerDeniedConnectionFips
            or ExtendedDisconnectReasonCode.ServerInsufficientPrivileges
            or ExtendedDisconnectReasonCode.ServerFreshCredsRequired
            or ExtendedDisconnectReasonCode.RdpEncInvalidCredentials;

    /// <summary>
    /// Whether a primary code names the state of an account that exists.
    /// </summary>
    /// <remarks>
    /// <para>Three codes, and the boundary is deliberate. A locked, expired or expired-password
    /// account is a reason a generic rejection happened, so the two agree and the specific one is
    /// worth more.</para>
    /// <para><c>2567 UserNotFound</c> is excluded although it sits beside these in the severity
    /// table: it asserts the account does not exist, which
    /// <see cref="ExtendedDisconnectReasonCode.ServerInsufficientPrivileges"/> contradicts outright
    /// - that code means the server recognised the principal and refused it rights. Preferring the
    /// primary there would turn a hedged message into a specific and false one.</para>
    /// <para><c>2055 BadCredentials</c> is excluded because both decoders already produce the same
    /// key for it, so there is nothing to choose.</para>
    /// </remarks>
    internal static bool NamesAnAccountState(int reason) =>
        reason is 3335 or 3591 or 3847;

    /// <summary>
    /// The separator between the parts of a formatted disconnect code.
    /// </summary>
    /// <remarks>
    /// The code is shown on the reconnect overlay and copied into the clipboard report that ends
    /// up in support tickets, mails and consoles. It stays ASCII so it survives a Windows console
    /// code page, a diff and a ticket system that re-encodes what it is pasted: the middle dot it
    /// used to carry was the one non-ASCII character in the whole report.
    /// </remarks>
    public const string DisconnectCodeSeparator = " | ";

    /// <summary>
    /// Formats a disconnect reason as a symbolic support code plus the raw numeric value.
    /// </summary>
    public static string FormatDisconnectCode(int reason)
        => FormatDisconnectCode(reason, NoExtendedDisconnectReason);

    /// <summary>
    /// Formats a disconnect for the reconnect overlay, from both codes.
    /// </summary>
    /// <param name="reason">The high-level MsTscAx disconnect reason.</param>
    /// <param name="extendedReason">The optional extended disconnect reason.</param>
    /// <returns>The symbolic name of the resolved cause, the reason, and the extended reason.</returns>
    /// <remarks>
    /// <para>The symbolic name comes from <see cref="ResolveDisconnectReasonKey"/>, which is the
    /// same resolution the displayed message comes from. Deriving it from the primary reason alone
    /// let the overlay print one cause beside a message naming another: a gateway timeout was shown
    /// as RDP_SOCKET_CLOSED, because the socket close is what the primary code reports when a
    /// gateway is what actually refused the session.</para>
    /// <para>The extended reason is appended whenever there is one, decoded or not. When it decoded,
    /// it is the number the message came from and a reader needs it to re-derive the message; when
    /// it did not, it is the only specific thing anyone has to go on.</para>
    /// </remarks>
    public static string FormatDisconnectCode(int reason, int extendedReason)
    {
        string? reasonKey = ResolveDisconnectReasonKey(reason, extendedReason);
        string symbolicCode = reasonKey is null
            ? "UNKNOWN"
            : ToUpperSnakeCase(reasonKey);

        string formatted = $"RDP_{symbolicCode}{DisconnectCodeSeparator}{reason}";
        return extendedReason == NoExtendedDisconnectReason
            ? formatted
            : $"{formatted}{DisconnectCodeSeparator}EXT {extendedReason}";
    }

    private static string ToUpperSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (i > 0 && char.IsUpper(current))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(current));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Groups disconnect codes by user actionability: transient failures are
    /// likely retryable, auth issues need credential/account action, and
    /// terminal errors usually need admin or protocol remediation.
    /// </summary>
    public enum RdpDisconnectSeverity
    {
        Transient,
        AuthIssue,
        TerminalError
    }

    /// <summary>
    /// Translates an MsTscAx disconnect reason code into an overlay severity.
    /// Unknown and clean-exit codes default to terminal because they are not
    /// expected to be displayed by the reconnect overlay.
    /// </summary>
    public static RdpDisconnectSeverity GetDisconnectSeverity(int reason) => reason switch
    {
        // 3592 and 4360 are the same failure: the decoder resolves both to ReconnectFailed and the
        // user is shown one sentence for either. They were split here, so the same drop retried
        // under one code and was torn down on the first bounce under the other.
        260 or 264 or 516 or 772 or 2308 or 3080 or 3592 or 4360
            => RdpDisconnectSeverity.Transient,
        2055 or 2567 or 3335 or 3591 or 3847
            => RdpDisconnectSeverity.AuthIssue,
        _ => RdpDisconnectSeverity.TerminalError
    };

    /// <summary>
    /// Translates a high-level MsTscAx disconnect reason and the optional
    /// ExtendedDisconnectReason into an overlay severity.
    /// </summary>
    public static RdpDisconnectSeverity GetDisconnectSeverity(int reason, int extendedReason)
    {
        ExtendedDisconnectReasonCode extendedDisconnectReason =
            (ExtendedDisconnectReasonCode)extendedReason;

        return extendedDisconnectReason switch
        {
            ExtendedDisconnectReasonCode.ServerLogonTimeout
                or ExtendedDisconnectReasonCode.ServerDeniedConnection
                or ExtendedDisconnectReasonCode.ServerDeniedConnectionFips
                or ExtendedDisconnectReasonCode.ServerInsufficientPrivileges
                or ExtendedDisconnectReasonCode.ServerFreshCredsRequired
                or ExtendedDisconnectReasonCode.RdpEncInvalidCredentials
                => RdpDisconnectSeverity.AuthIssue,
            _ when IsLicenseExtendedDisconnectReason(extendedReason)
                => RdpDisconnectSeverity.TerminalError,
            _ => GetDisconnectSeverity(reason)
        };
    }

    /// <summary>
    /// Fail-closed auto-reconnect policy: only transient (network-level) disconnects
    /// are eligible for automatic reconnection. Auth, security, terminal, clean-exit
    /// and unknown reasons are NOT retried, to avoid hammering credentials/accounts
    /// and to surface the disconnect to the user instead of silently looping.
    /// </summary>
    public static bool AllowsAutoReconnect(int reason)
        => GetDisconnectSeverity(reason) == RdpDisconnectSeverity.Transient;

    /// <summary>
    /// Fail-closed auto-reconnect policy using the optional extended disconnect reason.
    /// </summary>
    public static bool AllowsAutoReconnect(int reason, int extendedReason)
        => GetDisconnectSeverity(reason, extendedReason) == RdpDisconnectSeverity.Transient;

    /// <summary>
    /// Whether an extended reason falls in the contiguous MsTscAx licensing block.
    /// </summary>
    /// <remarks>
    /// A numeric range rather than a member list, which holds because the block runs unbroken from
    /// 256 to 267 with no non-licensing member inside it. The upper bound stopped at 265 before,
    /// leaving 266 and 267 to fall through to the high-level reason.
    /// </remarks>
    private static bool IsLicenseExtendedDisconnectReason(int extendedReason)
        => extendedReason >= (int)ExtendedDisconnectReasonCode.LicenseInternal
            && extendedReason <= (int)ExtendedDisconnectReasonCode.LicenseCreatingLicStoreAccDenied;

    #endregion

    #region Private apply methods (late-bound COM property access)

    /// <summary>
    /// Invokes a late-bound COM property setter and logs + swallows exceptions.
    /// Used for optional properties that may not exist on older IMsRdpClient interface versions.
    /// </summary>
    private static void TrySetDynamic(string propertyName, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[RdpActiveXHost] {propertyName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes PnP and USB device redirection onto the control.
    /// </summary>
    /// <remarks>
    /// Written on every connect, including when the profile asks for no redirection: the header
    /// strip states the profile's own answer, so a control that silently kept the previous
    /// session's answer makes the indicator lie about what the session is exposing.
    /// </remarks>
    internal static void ApplyDeviceRedirection(
        IRdpDeviceRedirectionSettings settings,
        bool usbRedirectionEnabled)
    {
        TrySetDynamic(
            RedirectDevicesProperty,
            () => settings.RedirectDevices = usbRedirectionEnabled);
    }

    private int ReadExtendedDisconnectReason()
    {
        object? ocx = GetActiveXInstance();
        if (ocx is null)
        {
            return NoExtendedDisconnectReason;
        }

        try
        {
            object? rawExtendedReason = ((dynamic)ocx).ExtendedDisconnectReason;
            int extendedReason = Convert.ToInt32(
                rawExtendedReason,
                System.Globalization.CultureInfo.InvariantCulture);
            return extendedReason;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.ReadExtendedDisconnectReason threw {FormatExceptionForLog(ex)} ocx={DescribeComObject(ocx)}");
            return NoExtendedDisconnectReason;
        }
    }

    private static bool TrySetUseMultimon(object ocx, bool enabled)
    {
        IntPtr nonScriptable5Ptr = IntPtr.Zero;
        try
        {
            if (!TryGetNonScriptable5(ocx, out nonScriptable5Ptr, out var acquisitionPath))
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpClientNonScriptable5 UseMultimon fallback: interface unavailable; requested={enabled}");
                return false;
            }

            var vtable = Marshal.ReadIntPtr(nonScriptable5Ptr);
            var putUseMultimon = Marshal.ReadIntPtr(
                vtable,
                NonScriptable5PutUseMultimonSlot * IntPtr.Size);
            var setter = Marshal.GetDelegateForFunctionPointer<PutUseMultimonDelegate>(putUseMultimon);
            var hr = setter(nonScriptable5Ptr, enabled);
            if (hr < 0)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpClientNonScriptable5.put_UseMultimon failed hr=0x{unchecked((uint)hr):X8} value={enabled} via={acquisitionPath}");
                return false;
            }

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5.put_UseMultimon set value={enabled} hr=0x{unchecked((uint)hr):X8} via={acquisitionPath}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5.put_UseMultimon threw {FormatExceptionForLog(ex)} value={enabled}");
            return false;
        }
        finally
        {
            if (nonScriptable5Ptr != IntPtr.Zero)
            {
                Marshal.Release(nonScriptable5Ptr);
            }
        }
    }

    /// <summary>
    /// Writes the monitor selection through the client shell property bag.
    /// </summary>
    /// <remarks>
    /// An empty selection is an instruction - "every monitor" - and not an absence of one. A
    /// pooled control keeps the list the previous session wrote, so the empty case has to be
    /// stated rather than skipped, or the next profile spans whichever monitors the last one
    /// picked while the desktop is sized for all of them.
    /// </remarks>
    internal static bool ApplySelectedMonitors(
        IRdpClientShellWriter shell,
        IReadOnlyList<int> selectedMonitorIndices)
    {
        string selectedMonitors = string.Join(',', selectedMonitorIndices);
        return shell.TrySetRdpProperty(SelectedMonitorsProperty, selectedMonitors);
    }

    /// <summary>
    /// Writes one <c>MsRdpClientShell</c> .rdp property on the live control.
    /// </summary>
    private sealed class DynamicRdpClientShellWriter : IRdpClientShellWriter
    {
        private readonly object _ocx;

        internal DynamicRdpClientShellWriter(object ocx)
        {
            _ocx = ocx;
        }

        public bool TrySetRdpProperty(string propertyName, object value)
            => TrySetClientShellRdpProperty(_ocx, propertyName, value);
    }

    private static bool TrySetClientShellRdpProperty(object ocx, string propertyName, object value)
    {
        try
        {
            var shell = ((dynamic)ocx).MsRdpClientShell;
            shell.SetRdpProperty(propertyName, value);
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.MsRdpClientShell.SetRdpProperty set {propertyName}={value}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.MsRdpClientShell.SetRdpProperty threw {FormatExceptionForLog(ex)} property={propertyName} value={value}");
            return false;
        }
    }

    private static bool TryGetNonScriptable5(
        object ocx,
        out IntPtr nonScriptable5Ptr,
        out string acquisitionPath)
    {
        nonScriptable5Ptr = IntPtr.Zero;
        acquisitionPath = "none";

        try
        {
            var nonScriptable5 = ocx as IMsRdpClientNonScriptable5;
            if (nonScriptable5 is not null)
            {
                nonScriptable5Ptr = Marshal.GetComInterfaceForObject(
                    nonScriptable5,
                    typeof(IMsRdpClientNonScriptable5));
                acquisitionPath = "direct cast";
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpClientNonScriptable5 reached via direct cast ocx={DescribeComObject(ocx)} ptr=0x{nonScriptable5Ptr.ToInt64():X}");
                return true;
            }

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5 direct cast returned null ocx={DescribeComObject(ocx)}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5 direct cast threw {FormatExceptionForLog(ex)} ocx={DescribeComObject(ocx)}");
        }

        IntPtr unknown = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(ocx);
            var iid = IidMsRdpClientNonScriptable5;
            var hr = Marshal.QueryInterface(unknown, in iid, out nonScriptable5Ptr);
            if (hr < 0 || nonScriptable5Ptr == IntPtr.Zero)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpClientNonScriptable5 Marshal.QueryInterface failed HRESULT=0x{unchecked((uint)hr):X8} ppv=0x{nonScriptable5Ptr.ToInt64():X} ocx={DescribeComObject(ocx)}");
                nonScriptable5Ptr = IntPtr.Zero;
                return false;
            }

            acquisitionPath = "Marshal.QueryInterface";
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5 reached via Marshal.QueryInterface HRESULT=0x{unchecked((uint)hr):X8} ppv=0x{nonScriptable5Ptr.ToInt64():X}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpClientNonScriptable5 Marshal.QueryInterface threw {FormatExceptionForLog(ex)} ocx={DescribeComObject(ocx)}");
            nonScriptable5Ptr = IntPtr.Zero;
            return false;
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private static int SnapDesktopWidth(int width)
    {
        var snapped = RdpDisplayHelper.SnapToMultipleOf(width, 4);
        return snapped > 0 ? snapped : 4;
    }

    private static bool TryGetExtendedSettings(object ocx, out object? extendedSettings)
    {
        extendedSettings = null;

        try
        {
            extendedSettings = ocx as IMsRdpExtendedSettings;
            if (extendedSettings is not null)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpExtendedSettings reached via direct cast ocx={DescribeComObject(ocx)} extendedSettings={DescribeComObject(extendedSettings)}");
                return true;
            }

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings direct cast returned null ocx={DescribeComObject(ocx)}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings direct cast threw {FormatExceptionForLog(ex)} ocx={DescribeComObject(ocx)}");
        }

        IntPtr unknown = IntPtr.Zero;
        IntPtr extendedSettingsPtr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(ocx);
            var iid = IidMsRdpExtendedSettings;
            var hr = Marshal.QueryInterface(unknown, in iid, out extendedSettingsPtr);
            if (hr < 0 || extendedSettingsPtr == IntPtr.Zero)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.IMsRdpExtendedSettings Marshal.QueryInterface failed HRESULT=0x{unchecked((uint)hr):X8} ppv=0x{extendedSettingsPtr.ToInt64():X} ocx={DescribeComObject(ocx)}");
                return false;
            }

            extendedSettings = Marshal.GetTypedObjectForIUnknown(extendedSettingsPtr, typeof(IMsRdpExtendedSettings));
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings reached via Marshal.QueryInterface HRESULT=0x{unchecked((uint)hr):X8} ppv=0x{extendedSettingsPtr.ToInt64():X} extendedSettings={DescribeComObject(extendedSettings)}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings Marshal.QueryInterface threw {FormatExceptionForLog(ex)} ocx={DescribeComObject(ocx)}");
            return false;
        }
        finally
        {
            if (extendedSettingsPtr != IntPtr.Zero)
            {
                Marshal.Release(extendedSettingsPtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>
    /// Sets an <c>IMsRdpExtendedSettings</c> property whose variant type is <c>VT_BOOL</c>.
    /// </summary>
    /// <remarks>
    /// The scale factors take a numeric variant, but the presenter switches take a boolean one,
    /// and the control answers <c>E_FAIL</c> rather than coercing. Boxing a <see cref="bool"/>
    /// marshals as <c>VT_BOOL</c>, which is what those properties expect.
    /// </remarks>
    private static bool TrySetExtendedSettingBoolean(object extendedSettings, string propertyName, bool value)
    {
        try
        {
            if (extendedSettings is not IMsRdpExtendedSettings settings)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.Property(\"{propertyName}\") set failed: object is not IMsRdpExtendedSettings extendedSettings={DescribeComObject(extendedSettings)}");
                return false;
            }

            object variantValue = value;
            var hr = settings.put_Property(propertyName, ref variantValue);
            if (hr < 0)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.Property(\"{propertyName}\") boolean set failed hr=0x{unchecked((uint)hr):X8} value={value} extendedSettings={DescribeComObject(extendedSettings)}");
                return false;
            }

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings.Property[{propertyName}] boolean set value={value} hr=0x{unchecked((uint)hr):X8}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.Property(\"{propertyName}\") boolean set threw {FormatExceptionForLog(ex)}");
            return false;
        }
    }

    private static bool TrySetExtendedSetting(object extendedSettings, string propertyName, uint value)
    {
        try
        {
            if (extendedSettings is not IMsRdpExtendedSettings settings)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.Property(\"{propertyName}\") set failed: object is not IMsRdpExtendedSettings extendedSettings={DescribeComObject(extendedSettings)}");
                return false;
            }

            object variantValue = value;
            var hr = settings.put_Property(propertyName, ref variantValue);
            if (hr < 0)
            {
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.Property(\"{propertyName}\") set failed hr=0x{unchecked((uint)hr):X8} value={value} extendedSettings={DescribeComObject(extendedSettings)}");
                return false;
            }

            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.IMsRdpExtendedSettings.Property[{propertyName}] set value={value} hr=0x{unchecked((uint)hr):X8} extendedSettings={DescribeComObject(extendedSettings)}");
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.Property(\"{propertyName}\") set threw {FormatExceptionForLog(ex)} extendedSettings={DescribeComObject(extendedSettings)}");
            return false;
        }
    }

    private static string FormatExceptionForLog(Exception ex)
    {
        return $"{ex.GetType().FullName}: {ex.Message} HRESULT=0x{unchecked((uint)ex.HResult):X8}";
    }

    private static string DescribeComObject(object? obj)
    {
        if (obj is null)
        {
            return "<null>";
        }

        var typeName = obj.GetType().FullName ?? obj.GetType().Name;
        var isComObject = Marshal.IsComObject(obj);
        var dispatchState = "not-checked";

        if (isComObject)
        {
            IntPtr dispatch = IntPtr.Zero;
            try
            {
                dispatch = Marshal.GetIDispatchForObject(obj);
                dispatchState = dispatch == IntPtr.Zero ? "null" : "available";
            }
            catch (Exception ex)
            {
                dispatchState = $"threw-{ex.GetType().Name}-0x{unchecked((uint)ex.HResult):X8}";
            }
            finally
            {
                if (dispatch != IntPtr.Zero)
                {
                    Marshal.Release(dispatch);
                }
            }
        }

        return $"type={typeName} isComObject={isComObject} idispatch={dispatchState}";
    }

    private EffectiveDisplayContext ResolveAndApplyPendingDisplayContext()
    {
        HostDisplayContext? hostContext = null;
        try
        {
            hostContext = BuildHostDisplayContext();
            var effectiveContext = RdpDisplayResolver.Resolve(
                _session.ResolutionMode,
                hostContext,
                _session.ResolutionPresets,
                _session.Width,
                _session.Height);

            if (_session.ResolutionMode == RdpResolutionMode.Fixed)
            {
                effectiveContext = effectiveContext with
                {
                    SmartSizingEnabled = InitialSmartSizing
                };
            }

            AdoptResolvedDisplayContext(
                _session,
                effectiveContext,
                hostContext.DesktopDpiScale,
                this);

            Core.Logging.FileLogger.Info(
                $"RDP display mode: configured={effectiveContext.ConfiguredMode} effective={effectiveContext.EffectiveMode} {effectiveContext.Width}x{effectiveContext.Height} dpi={effectiveContext.DesktopScaleFactor}/{effectiveContext.DeviceScaleFactor} smartSizing={effectiveContext.SmartSizingEnabled} multimon={effectiveContext.MultiMonitorEnabled} reason={effectiveContext.Reason}");

            return effectiveContext;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error(
                $"RDP display resolver failed: {SerializeDisplayResolverInputs(hostContext)}",
                ex);
            throw;
        }
    }

    /// <summary>
    /// Copies a resolved display context onto the session and pushes onto the control the part of
    /// it the control has to be told about.
    /// </summary>
    /// <remarks>
    /// Smart sizing is the one resolved value that is live-mutable, and the resolver flips it on a
    /// fullscreen toggle. Remembering it is not applying it: a control that is never told keeps
    /// scaling the desktop while the log line below says it does not.
    /// </remarks>
    internal static void AdoptResolvedDisplayContext(
        RdpSessionState session,
        EffectiveDisplayContext effectiveContext,
        double hostDpiScale,
        IRdpDisplayContextSink sink)
    {
        session.Width = effectiveContext.Width;
        session.Height = effectiveContext.Height;
        session.DesktopScaleFactor = effectiveContext.DesktopScaleFactor;
        session.DeviceScaleFactor = effectiveContext.DeviceScaleFactor;
        session.DpiScaleX = hostDpiScale;
        session.DpiScaleY = hostDpiScale;
        session.Redirections.MultiMonitor = effectiveContext.MultiMonitorEnabled;
        sink.SetSmartSizing(effectiveContext.SmartSizingEnabled);
    }

    private HostDisplayContext BuildHostDisplayContext()
    {
        var screen = Screen.FromControl(this);
        var allScreens = GetAllScreensSafe();
        var targetScreens = ResolveDisplayTargetScreens(screen, allScreens);
        var monitorBounds = ResolveUnionBounds(targetScreens.Select(target => target.Bounds), screen.Bounds);
        var workingArea = ResolveUnionBounds(targetScreens.Select(target => target.WorkingArea), screen.WorkingArea);
        var viewport = new DrawingSize(ClientSize.Width, ClientSize.Height);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            viewport = new DrawingSize(_session.Width, _session.Height);
        }

        return new HostDisplayContext
        {
            MonitorBoundsPhysicalPx = new DrawingSize(monitorBounds.Width, monitorBounds.Height),
            WorkingAreaPhysicalPx = new DrawingSize(workingArea.Width, workingArea.Height),
            DesktopDpiScale = ResolveHostDpiScale(),
            ViewportPhysicalPx = viewport,
            IsFullscreen = _session.IsFullscreen,
            ScreenCount = allScreens.Length,
            IsMultiMonitorRequested = _session.ResolutionMode == RdpResolutionMode.Multimon
                || _session.Redirections.MultiMonitor
        };
    }

    private static Screen[] GetAllScreensSafe()
    {
        try
        {
            return Screen.AllScreens;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost monitor enumeration fallback: {ex.Message}");
            return [];
        }
    }

    private Screen[] ResolveDisplayTargetScreens(Screen currentScreen, Screen[] allScreens)
    {
        if (_session.ResolutionMode != RdpResolutionMode.Multimon)
        {
            return [currentScreen];
        }

        if (allScreens.Length == 0)
        {
            return [currentScreen];
        }

        var selectedMonitorIndices = ResolvePendingSelectedMonitorIndices(allScreens.Length);
        if (selectedMonitorIndices.Length == 0)
        {
            return allScreens;
        }

        return selectedMonitorIndices
            .Select(index => allScreens[index])
            .ToArray();
    }

    private int[] ResolvePendingSelectedMonitorIndices()
        => ResolvePendingSelectedMonitorIndices(GetAllScreensSafe().Length);

    private int[] ResolvePendingSelectedMonitorIndices(int availableMonitorCount)
        => RdpSelectedMonitorValidator.Validate(
            _session.SelectedMonitorIndices,
            availableMonitorCount,
            message => Core.Logging.FileLogger.Warn($"[RdpActiveXHost] {message}"));

    private static DrawingRectangle ResolveUnionBounds(
        IEnumerable<DrawingRectangle> bounds,
        DrawingRectangle fallback)
    {
        var hasAny = false;
        var union = DrawingRectangle.Empty;

        foreach (var rect in bounds)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            union = hasAny
                ? DrawingRectangle.Union(union, rect)
                : rect;
            hasAny = true;
        }

        return hasAny ? union : fallback;
    }

    private double ResolveHostDpiScale()
    {
        if (_session.DpiScaleX > 0 && !double.IsNaN(_session.DpiScaleX) && !double.IsInfinity(_session.DpiScaleX))
        {
            return _session.DpiScaleX;
        }

        try
        {
            using var graphics = CreateGraphics();
            return graphics.DpiX > 0
                ? graphics.DpiX / 96.0
                : 1.0;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"RdpActiveXHost DPI fallback: {ex.Message}");
            return 1.0;
        }
    }

    private string SerializeDisplayResolverInputs(HostDisplayContext? hostContext)
    {
        return JsonSerializer.Serialize(new
        {
            configuredMode = _session.ResolutionMode.ToString(),
            configuredWidthPx = _session.Width,
            configuredHeightPx = _session.Height,
            isFullscreen = _session.IsFullscreen,
            selectedMonitorIndices = _session.SelectedMonitorIndices,
            presets = _session.ResolutionPresets
                .Select(preset => new { preset.Width, preset.Height })
                .ToArray(),
            hostContext = hostContext is null
                ? null
                : new
                {
                    monitorBoundsPhysicalPx = SerializeSize(hostContext.MonitorBoundsPhysicalPx),
                    workingAreaPhysicalPx = SerializeSize(hostContext.WorkingAreaPhysicalPx),
                    desktopDpiScale = hostContext.DesktopDpiScale,
                    viewportPhysicalPx = SerializeSize(hostContext.ViewportPhysicalPx),
                    isFullscreen = hostContext.IsFullscreen,
                    screenCount = hostContext.ScreenCount,
                    isMultiMonitorRequested = hostContext.IsMultiMonitorRequested
                }
        });
    }

    private static object SerializeSize(DrawingSize size)
        => new
        {
            width = size.Width,
            height = size.Height
        };

    private void ApplyServerSettings(object ocx)
    {
        if (string.IsNullOrWhiteSpace(_session.Host)) return;

        dynamic ax = ocx;
        ax.Server = _session.Host;
        ax.AdvancedSettings2.RDPPort = _session.Port;
    }

    private void ApplyCredentialSettings(object ocx)
    {
        ApplyIdentitySettings(new DynamicRdpIdentitySettings(ocx), _session.Username, _session.Domain);

        // Password must be set via IMsTscNonScriptable, not via IDispatch
        if (_session.Password is not null)
        {
            SetClearTextPassword(_session.Password);
        }
    }

    /// <summary>
    /// Writes the logon identity onto the control, including when the profile names none.
    /// </summary>
    /// <remarks>
    /// The same rule as PerformanceFlags: an identity that is only written when the profile
    /// carries one is an identity a pooled control inherits from the session before it, and the
    /// next profile then authenticates as somebody it never named.
    /// </remarks>
    internal static void ApplyIdentitySettings(
        IRdpIdentitySettings settings,
        string? username,
        string? domain)
    {
        settings.UserName = username ?? string.Empty;
        settings.Domain = domain ?? string.Empty;
    }

    private void ApplyDisplaySettings(object ocx)
    {
        dynamic ax = ocx;
        ax.DesktopWidth = _session.Width;
        ax.DesktopHeight = _session.Height;
        ax.ColorDepth = _session.ColorDepth;

        ApplySmartSizing(ocx, InitialSmartSizing);
        StripScrollbarStylesRecursive();
    }

    /// <summary>
    /// Applies the graphics presenter options to the control, before it connects.
    /// </summary>
    /// <remarks>
    /// <c>EnableHardwareMode</c> cannot be written once a connection has started, so it belongs
    /// on the pre-connect path, and it is written on every connect so a control taken from the
    /// pool cannot inherit the choice made by the session before it.
    /// </remarks>
    private void ApplyPresenterSettings(object extendedSettings)
        => ApplyPresenterSettings(extendedSettings, _session.Redirections.HardwareAcceleration);

    /// <summary>
    /// Writes the presenter switch that decides whether the control decodes through the graphics
    /// adapter, and reports whether the control accepted it.
    /// </summary>
    /// <returns>True when the control took the value.</returns>
    internal static bool ApplyPresenterSettings(object extendedSettings, bool hardwareAcceleration)
    {
        // The property is VT_BOOL; a numeric variant is answered with E_FAIL.
        bool applied = TrySetExtendedSettingBoolean(
            extendedSettings, HardwareModeProperty, hardwareAcceleration);

        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.ApplyPresenterSettings: {HardwareModeProperty}={hardwareAcceleration} applied={applied}");

        if (!applied)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost presenter fallback: {HardwareModeProperty} could not be written; " +
                "the MsTscAx default remains in effect for this session.");
        }

        return applied;
    }

    private void ApplyDisplayScaleSettings(object ocx)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var extendedReached = TryGetExtendedSettings(ocx, out var extendedSettings);
        var desktopSet = false;
        var deviceSet = false;
        if (extendedReached && extendedSettings is not null)
        {
            desktopSet = TrySetExtendedSetting(extendedSettings, "DesktopScaleFactor", _session.DesktopScaleFactor);
            deviceSet = TrySetExtendedSetting(extendedSettings, "DeviceScaleFactor", _session.DeviceScaleFactor);
            ApplyPresenterSettings(extendedSettings);
        }

        stopwatch.Stop();

        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.ApplyDisplayScaleSettings elapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.###}");
        if (desktopSet && deviceSet)
        {
            Core.Logging.FileLogger.Info(
                $"RdpActiveXHost.ApplyDisplayScaleSettings Successfully set DesktopScaleFactor={_session.DesktopScaleFactor} DeviceScaleFactor={_session.DeviceScaleFactor} extendedSettings={DescribeComObject(extendedSettings)}");
        }

        Core.Logging.FileLogger.Info(
            $"RdpActiveXHost.ApplyDisplayScaleSettings: desktopScaleFactor={_session.DesktopScaleFactor} deviceScaleFactor={_session.DeviceScaleFactor} dpi={_session.DpiScaleX:0.##}x{_session.DpiScaleY:0.##} extendedSettings={(desktopSet && deviceSet ? "reached" : "fallback")}");

        if (!desktopSet || !deviceSet)
        {
            Core.Logging.FileLogger.Info(
                "RdpActiveXHost display scale fallback: ExtendedSettings unavailable; MsTscAx defaults remain in effect.");
        }
    }

    private static void ApplySmartSizing(object ocx, bool enabled)
    {
        try
        {
            dynamic ax = ocx;
            ax.AdvancedSettings2.SmartSizing = enabled;
            Core.Logging.FileLogger.Info($"RdpActiveXHost.SmartSizing={enabled}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[RdpActiveXHost] SmartSizing: {ex.Message}");
        }
    }

    private void ApplyRedirectionSettings(object ocx)
    {
        dynamic ax = ocx;
        var adv = ax.AdvancedSettings9;

        // Clipboard
        adv.RedirectClipboard = _session.Redirections.Clipboard;

        // Drives
        adv.RedirectDrives = _session.Redirections.Drives;

        // Printers
        adv.RedirectPrinters = _session.Redirections.Printers;

        // COM ports
        adv.RedirectPorts = _session.Redirections.ComPorts;

        // Smart cards
        adv.RedirectSmartCards = _session.Redirections.SmartCards;

        // Audio mode: 0 = redirect to client, 1 = play at remote, 2 = disable.
        adv.AudioRedirectionMode = RdpRedirectionOptions.MapAudioModeToRdpValue(_session.Redirections.AudioMode);

        // Audio capture (COM property expects int: 0=disabled, 1=enabled)
        adv.AudioCaptureRedirectionMode = _session.Redirections.AudioCapture ? 1 : 0;

        // NLA - shared resolver keeps the embedded host in parity with the .rdp generator
        RdpAuthenticationSettings auth = RdpAuthenticationResolver.Resolve(
            _session.Redirections.Nla,
            _session.Redirections.StrictServerAuthentication);
        adv.EnableCredSspSupport = auth.EnableCredSspSupport;
        adv.AuthenticationLevel = auth.AuthenticationLevel;

        // Bitmap caching
        adv.BitmapPersistence = _session.Redirections.BitmapCaching ? 1 : 0;

        // Compression
        adv.Compress = _session.Redirections.Compression ? 1 : 0;

        // Auto-reconnect with bounded retry count
        adv.EnableAutoReconnect = _session.Redirections.AutoReconnect;
        if (_session.Redirections.AutoReconnect)
        {
            TrySetDynamic("MaxReconnectAttempts", () => adv.MaxReconnectAttempts = _session.MaxAutoReconnectAttempts);
        }

        // USB / PnP device redirection
        object advancedSettings = adv;
        ApplyDeviceRedirection(
            new DynamicRdpDeviceRedirectionSettings(advancedSettings),
            _session.Redirections.Usb);

        // NOTE: Webcam (camerastoredirect) requires IMsRdpClientNonScriptable7
        // CameraRedirConfigCollection which is not available via simple IDispatch.
        // Webcam redirection works in external mode (.rdp file) only.

        // NOTE: DynamicResolution is handled at the view layer via UpdateResolution()
        // after connect, not via a COM property on the ActiveX control.

        // Allow background input - CRITICAL for anti-idle on background tabs.
        // Without this, the RDP ActiveX control discards PostMessage input
        // when it does not have focus, silently breaking anti-idle.
        TrySetDynamic("allowBackgroundInput", () => adv.allowBackgroundInput = 1);

        // TCP keep-alive interval for network break detection
        TrySetDynamic("KeepAliveInterval", () => adv.KeepAliveInterval = _session.KeepAliveIntervalMs);

        // Performance flags (disable visual effects for bandwidth optimization).
        // Written unconditionally, including when the profile asks for none: a value
        // that is only ever written when non-zero is a value a reused control inherits
        // from whichever session set it last.
        TrySetDynamic("PerformanceFlags", () => adv.PerformanceFlags = _session.Redirections.PerformanceFlags);

        // Network auto-detect: let the server continuously adapt encoding to bandwidth.
        // Skipped when DisableUdp is set (that path forces LAN profile instead).
        if (!_session.Redirections.DisableUdp)
        {
            TrySetDynamic("BandwidthDetection", () => adv.BandwidthDetection = true);
            TrySetDynamic("NetworkConnectionType", () => adv.NetworkConnectionType = 7); // CONNECTION_TYPE_AUTODETECT
        }

        // Multi-monitor is a pre-Connect nonscriptable setting. Runtime changes require reconnect.
        TrySetUseMultimon(ocx, _session.Redirections.MultiMonitor);
        IReadOnlyList<int> selectedMonitorIndices =
            _session.Redirections.MultiMonitor && _session.ResolutionMode == RdpResolutionMode.Multimon
                ? ResolvePendingSelectedMonitorIndices()
                : [];
        ApplySelectedMonitors(new DynamicRdpClientShellWriter(ocx), selectedMonitorIndices);

        // Suppress the UDP probe: disable bandwidth auto-detection (which uses UDP probes)
        // and set an explicit network type so the client does not attempt UDP transport.
        // The MsTscAx ActiveX control has no direct "DisableUDP" COM property;
        // disabling BandwidthDetection + explicit NetworkConnectionType achieves the
        // same result by preventing the UDP probe that times out behind firewalls.
        if (_session.Redirections.DisableUdp)
        {
            TrySetDynamic("DisableUdp BandwidthDetection", () => adv.BandwidthDetection = false);
            TrySetDynamic("DisableUdp NetworkConnectionType", () => adv.NetworkConnectionType = 6); // LAN - no probing needed
        }

        ApplyGatewaySettings(ocx);
    }

    /// <summary>
    /// Puts the profile's route onto the control: its RD Gateway, or the direct route when it
    /// names none.
    /// </summary>
    /// <remarks>
    /// The direct case is a write and not a skip. A pooled control keeps the gateway of the
    /// session before it, so a profile that never named one would otherwise be tunnelled through
    /// it, and its credentials presented to it.
    /// </remarks>
    private void ApplyGatewaySettings(object ocx)
    {
        string? gateway = _session.Redirections.GatewayHostname;
        bool hasGateway = !string.IsNullOrWhiteSpace(gateway);

        object? transport;
        try
        {
            dynamic ax = ocx;
            transport = ax.TransportSettings;
        }
        catch (Exception ex)
        {
            if (!hasGateway)
            {
                // Nothing asked for a gateway and the property one would have been written
                // through is out of reach, so there is nothing on the control to undo.
                Core.Logging.FileLogger.Info(
                    $"RdpActiveXHost.ApplyGatewaySettings: TransportSettings unavailable on a direct profile: {ex.Message}");
                return;
            }

            throw new RdpGatewayAttestationException(
                gateway!,
                RdpGatewayAttestationStep.SettingsAvailability,
                ex);
        }

        IRdpGatewayTransportSettings? settings = transport is null
            ? null
            : new DynamicRdpGatewayTransportSettings(transport);
        RdpGatewayAttestation.Apply(gateway, settings);
        Core.Logging.FileLogger.Info(
            hasGateway
                ? $"RdpActiveXHost.ApplyGatewaySettings: RD Gateway attested host={gateway}"
                : "RdpActiveXHost.ApplyGatewaySettings: direct route attested, the profile names no RD Gateway");
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Returns this control to the state a freshly created one would be in, so it can serve
    /// another session, and reports whether that succeeded.
    /// </summary>
    /// <returns>
    /// True when the control is safe to hand to another session. False when it is not, in
    /// which case the caller must dispose it instead of reusing it.
    /// </returns>
    /// <remarks>
    /// Creating a control that ever connects costs a measured 66 kernel handles that are
    /// never returned, against roughly 3 for reusing one, which is why this exists. The
    /// price of reuse is that every trace of the previous session has to go: its
    /// credential, its settings, and its event subscribers, which would otherwise keep
    /// receiving events belonging to a session they know nothing about.
    /// </remarks>
    public bool ResetForReuse()
    {
        if (_disposed || !IsReusable)
        {
            return false;
        }

        try
        {
            StopPostConnectStripTimerOnUiThread("ResetForReuse");
            DetachEventSink();
            ClearEventSubscribers();

            if (!TryCompleteResetForReuse(ClearRemoteCredential, ResetSessionForReuse))
            {
                IsReusable = false;
                Core.Logging.FileLogger.Warn(
                    "RdpActiveXHost.ResetForReuse: the control's password could not be overwritten, it will not be reused");
                return false;
            }

            Core.Logging.FileLogger.Info("RdpActiveXHost.ResetForReuse: control returned to a reusable state");
            return true;
        }
        catch (Exception ex)
        {
            IsReusable = false;
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.ResetForReuse failed, control will not be reused: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The reuse decision, held apart from the control so it can be asserted: a control whose
    /// password could not be overwritten is not handed to another session.
    /// </summary>
    /// <param name="clearRemoteCredential">
    /// Overwrites the password held by the control; false when that could not be established.
    /// </param>
    /// <param name="resetSessionState">Restores everything else a new session starts from.</param>
    /// <returns>True when the control may be reused.</returns>
    internal static bool TryCompleteResetForReuse(
        Func<bool> clearRemoteCredential,
        Action resetSessionState)
    {
        if (!clearRemoteCredential())
        {
            return false;
        }

        resetSessionState();
        return true;
    }

    /// <summary>
    /// Restores everything a new session starts from, apart from the credential.
    /// </summary>
    private void ResetSessionForReuse()
    {
        _session.Reset();
        InitialSmartSizing = DefaultInitialSmartSizing;
        CancelAutoReconnect = false;
        IsConnected = false;
        LastError = null;
        LastExtendedDisconnectReason = NoExtendedDisconnectReason;
    }

    /// <summary>
    /// Drops every subscriber. A view that has been torn down must not keep receiving the
    /// events of the session that follows it on the same control.
    /// </summary>
    private void ClearEventSubscribers()
    {
        Connected = null;
        Disconnected = null;
        FatalError = null;
        LoginComplete = null;
        AutoReconnecting = null;
        AutoReconnected = null;
    }

    /// <summary>
    /// Overwrites the password held by the control itself. Resetting the pending state is
    /// not enough: the secret was pushed across to the OCX and stays there until replaced.
    /// </summary>
    /// <returns>True when the control is known to hold no password any more.</returns>
    private bool ClearRemoteCredential()
    {
        try
        {
            if (GetActiveXInstance() is null)
            {
                // No COM instance was ever created on this control, so there is no secret on the
                // other side of it to overwrite.
                return true;
            }

            return SetClearTextPassword(string.Empty);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RdpActiveXHost.ClearRemoteCredential failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            if (disposing)
            {
                try { _postConnectStripTimer.Dispose(); }
                catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[RdpActiveXHost] Dispose PostConnectStripTimer: {ex.Message}"); }

                try { DetachEventSink(); }
                catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[RdpActiveXHost] Dispose DetachEventSink: {ex.Message}"); }
            }

            // Clear our cached reference; let AxHost.Dispose handle COM cleanup.
            // Do NOT call Marshal.ReleaseComObject here - AxHost holds its own
            // internal reference to the same RCW, and releasing it first causes
            // "COM object separated from its underlying RCW" in base.Dispose().
            _activeX = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}

internal interface IRdpStripTimer : IDisposable
{
    event EventHandler? Tick;

    TimeSpan Interval { get; set; }

    void Start();

    void Stop();
}

internal interface IRdpPostConnectStripTimerClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemRdpPostConnectStripTimerClock : IRdpPostConnectStripTimerClock
{
    public static SystemRdpPostConnectStripTimerClock Instance { get; } = new();

    private SystemRdpPostConnectStripTimerClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class DispatcherRdpStripTimer : IRdpStripTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherRdpStripTimer(Dispatcher dispatcher, DispatcherPriority priority)
    {
        _timer = new DispatcherTimer(priority, dispatcher);
        _timer.Tick += OnTick;
    }

    public event EventHandler? Tick;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Tick?.Invoke(this, e);
    }
}

internal sealed class RdpPostConnectStripTimer : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMilliseconds(12_000);

    private readonly Func<IRdpStripTimer> _timerFactory;
    private readonly IRdpPostConnectStripTimerClock _clock;
    private readonly Action _stripAction;
    private readonly Action<string> _logInfo;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maxDuration;

    private IRdpStripTimer? _timer;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public RdpPostConnectStripTimer(
        Func<IRdpStripTimer> timerFactory,
        IRdpPostConnectStripTimerClock clock,
        Action stripAction,
        Action<string> logInfo,
        TimeSpan? interval = null,
        TimeSpan? maxDuration = null)
    {
        ArgumentNullException.ThrowIfNull(timerFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stripAction);
        ArgumentNullException.ThrowIfNull(logInfo);

        _timerFactory = timerFactory;
        _clock = clock;
        _stripAction = stripAction;
        _logInfo = logInfo;
        _interval = interval ?? DefaultInterval;
        _maxDuration = maxDuration ?? DefaultMaxDuration;
    }

    public bool IsRunning => _timer is not null;

    public void Begin(string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopCore($"restart-before-{reason}", logWhenStopped: _timer is not null);

        _startedAt = _clock.UtcNow;
        _timer = _timerFactory();
        _timer.Interval = _interval;
        _timer.Tick += OnTick;
        _timer.Start();

        _logInfo(
            $"RdpActiveXHost.PostConnectStripTimer started: reason={reason} intervalMs={_interval.TotalMilliseconds:0} maxDurationMs={_maxDuration.TotalMilliseconds:0}");
    }

    public void Stop(string reason)
    {
        StopCore(reason, logWhenStopped: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopCore("Dispose", logWhenStopped: true);
        _disposed = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _stripAction();

        if (_clock.UtcNow - _startedAt >= _maxDuration)
        {
            StopCore("max-duration", logWhenStopped: true);
        }
    }

    private void StopCore(string reason, bool logWhenStopped)
    {
        var timer = _timer;
        if (timer is null)
        {
            return;
        }

        _timer = null;
        timer.Tick -= OnTick;
        timer.Stop();
        timer.Dispose();

        if (logWhenStopped)
        {
            var elapsed = _clock.UtcNow - _startedAt;
            _logInfo(
                $"RdpActiveXHost.PostConnectStripTimer stopped: reason={reason} elapsedMs={elapsed.TotalMilliseconds:0}");
        }
    }
}
