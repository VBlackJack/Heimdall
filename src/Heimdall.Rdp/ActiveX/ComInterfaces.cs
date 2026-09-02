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

using System.Runtime.InteropServices;

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// COM interface for secure password injection into the RDP ActiveX control.
/// ClearTextPassword is only available via IMsTscNonScriptable, not the default IDispatch.
/// The vtable order must match the COM interface exactly.
/// </summary>
[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    void put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
    void put_PortablePassword([MarshalAs(UnmanagedType.BStr)] string portablePassword);
    void get_PortablePassword([MarshalAs(UnmanagedType.BStr)] out string portablePassword);
    void put_PortableSalt([MarshalAs(UnmanagedType.BStr)] string portableSalt);
    void get_PortableSalt([MarshalAs(UnmanagedType.BStr)] out string portableSalt);
    void put_BinaryPassword([MarshalAs(UnmanagedType.BStr)] string binaryPassword);
    void get_BinaryPassword([MarshalAs(UnmanagedType.BStr)] out string binaryPassword);
    void put_BinarySalt([MarshalAs(UnmanagedType.BStr)] string binarySalt);
    void get_BinarySalt([MarshalAs(UnmanagedType.BStr)] out string binarySalt);

    /// <summary>
    /// Resets every password representation held by the control (plaintext, portable encoded and
    /// binary encoded). Microsoft documents this member as the final slot of the interface and
    /// returns E_FAIL when the control is still connected, so callers must verify the connected
    /// state immediately before invoking it.
    /// </summary>
    void ResetPassword();
}

/// <summary>
/// Extended RDP client settings interface. The Microsoft docs define
/// IID_IMsRdpExtendedSettings as 302D8188-0052-4807-806A-362B628F9AC5.
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    [PreserveSig]
    int put_Property(
        [MarshalAs(UnmanagedType.BStr)] string bstrPropertyName,
        [In, MarshalAs(UnmanagedType.Struct)] ref object pValue);

    [PreserveSig]
    int get_Property(
        [MarshalAs(UnmanagedType.BStr)] string bstrPropertyName,
        [MarshalAs(UnmanagedType.Struct)] out object pValue);
}

/// <summary>
/// Marker interface for the nonscriptable RDP client v5 settings interface.
/// Microsoft defines IID_IMsRdpClientNonScriptable5 as
/// 4f6996d5-d7b1-412c-b0ff-063718566907. The interface is vtable-only, so
/// callers must use a correctly slotted native call for individual members.
/// </summary>
[ComImport]
[Guid("4F6996D5-D7B1-412C-B0FF-063718566907")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable5
{
}

// The seams below stand between the host and the late-bound parts of the control that a pooled
// instance carries from one session to the next. They exist so the rule those writes obey can be
// asserted without a COM apartment: every profile-driven setting is written on every connect,
// including when the profile asks for the default, because a value that is only ever written when
// it is set is a value the next session inherits without ever asking for it.

/// <summary>
/// The logon identity the control presents to the server.
/// </summary>
internal interface IRdpIdentitySettings
{
    string UserName { get; set; }

    string Domain { get; set; }
}

/// <summary>
/// Late-bound <c>UserName</c> and <c>Domain</c> on the MsTscAx control itself.
/// </summary>
internal sealed class DynamicRdpIdentitySettings : IRdpIdentitySettings
{
    private readonly dynamic _ocx;

    internal DynamicRdpIdentitySettings(object ocx)
    {
        _ocx = ocx;
    }

    public string UserName
    {
        get => _ocx.UserName;
        set => _ocx.UserName = value;
    }

    public string Domain
    {
        get => _ocx.Domain;
        set => _ocx.Domain = value;
    }
}

/// <summary>
/// PnP and USB device redirection, carried by the advanced settings of the control.
/// </summary>
internal interface IRdpDeviceRedirectionSettings
{
    bool RedirectDevices { get; set; }
}

/// <summary>
/// Late-bound <c>RedirectDevices</c> on <c>IMsRdpClientAdvancedSettings</c>.
/// </summary>
internal sealed class DynamicRdpDeviceRedirectionSettings : IRdpDeviceRedirectionSettings
{
    private readonly dynamic _advancedSettings;

    internal DynamicRdpDeviceRedirectionSettings(object advancedSettings)
    {
        _advancedSettings = advancedSettings;
    }

    public bool RedirectDevices
    {
        get => _advancedSettings.RedirectDevices;
        set => _advancedSettings.RedirectDevices = value;
    }
}

/// <summary>
/// The <c>MsRdpClientShell</c> property bag, which carries the .rdp-file settings that have no
/// COM property of their own.
/// </summary>
internal interface IRdpClientShellWriter
{
    /// <summary>Writes one .rdp property; returns false when the control refused it.</summary>
    bool TrySetRdpProperty(string propertyName, object value);
}

/// <summary>
/// The half of the control a resolved display context has to be pushed onto.
/// </summary>
/// <remarks>
/// Smart sizing is live-mutable and the display resolver flips it on a fullscreen toggle, so
/// remembering the resolved value is not the same as applying it.
/// </remarks>
internal interface IRdpDisplayContextSink
{
    void SetSmartSizing(bool enabled);
}

/// <summary>
/// COM event source interface for MsTscAx ActiveX control.
/// </summary>
/// <remarks>
/// This is a dispatch interface, so the DispId attribute alone decides which member receives which
/// event: declaration order plays no part, and a value the type library does not carry is simply
/// dropped by the control with no error surfaced to anyone. The values are pinned against the type
/// library itself by MsTscAxEventContractTests.
/// </remarks>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(2)]
    void OnConnected();

    [DispId(4)]
    void OnDisconnected(int discReason);

    // Canonical IMsTscAxEvents sequence: OnConnecting=1, OnConnected=2,
    // OnLoginComplete=3, OnDisconnected=4. DISPID 8 is OnRequestGoFullScreen.
    [DispId(3)]
    void OnLoginComplete();

    [DispId(10)]
    void OnFatalError(int errorCode);

    [DispId(12)]
    void OnRemoteDesktopSizeChange(int width, int height);

    /// <summary>
    /// Raised once per automatic reconnection attempt, before the attempt is made.
    /// </summary>
    /// <param name="disconnectReason">Reason the session dropped.</param>
    /// <param name="attemptCount">Number of the attempt about to be made.</param>
    /// <param name="continueStatus">
    /// Written back to tell the control whether to proceed. The type library declares it as an out
    /// pointer to this enumeration, which is also how the interop importer binds it.
    /// </param>
    /// <remarks>
    /// DISPID 17. It was declared as 22 before, which is OnLogonError: the control's dispatch of
    /// this event found no member and was dropped, so the handler never ran and the control's own
    /// reconnection was never vetoed.
    /// </remarks>
    [DispId(17)]
    void OnAutoReconnecting(
        int disconnectReason,
        int attemptCount,
        out AutoReconnectContinueState continueStatus);

    /// <summary>
    /// Raised once when an automatic reconnection has succeeded.
    /// </summary>
    /// <remarks>
    /// DISPID 33. It was declared as 23, which is OnFocusReleased. Both this and OnAutoReconnecting
    /// have to be right together: the state a reconnection sets up is only cleared here, so a
    /// session that reconnected would otherwise stay presented as reconnecting.
    /// </remarks>
    [DispId(33)]
    void OnAutoReconnected();
}

/// <summary>
/// COM event sink bridging ActiveX connection point events to .NET events.
/// Implements IMsTscAxEvents and forwards calls to the host control.
/// </summary>
public class MsTscAxEventSink : IMsTscAxEvents
{
    private readonly RdpActiveXHost _host;

    public MsTscAxEventSink(RdpActiveXHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public void OnConnected() => _host.RaiseConnected();
    public void OnDisconnected(int discReason) => _host.RaiseDisconnected(discReason);
    public void OnLoginComplete() => _host.RaiseLoginComplete();
    public void OnFatalError(int errorCode) => _host.RaiseFatalError(errorCode);
    public void OnRemoteDesktopSizeChange(int width, int height) => _host.RaiseRemoteDesktopSizeChanged(width, height);

    /// <summary>
    /// Forwards the attempt to the host and writes back whether the control should proceed.
    /// </summary>
    /// <remarks>
    /// <para>The polarity is not a detail. The control reads zero as "keep reconnecting", so the
    /// previous boolean was inverted on both branches once it reached the control: asking to stop
    /// wrote zero and asked it to continue, and asking to continue wrote one and would have stopped
    /// it at the first attempt. Writing the state explicitly removes the coercion that hid this.
    /// </para>
    /// <para>The verdict is read after the host has been told, and the listeners that set it do so
    /// on the dispatcher rather than inside this call. That works because the host raises
    /// synchronously: a listener moved to an asynchronous dispatch would return here before setting
    /// the flag, and the veto would be lost with nothing to show for it.</para>
    /// </remarks>
    public void OnAutoReconnecting(
        int disconnectReason,
        int attemptCount,
        out AutoReconnectContinueState continueStatus)
    {
        continueStatus = AutoReconnectContinueState.Automatic;
        try
        {
            // Raise first so a listener can synchronously cancel the current retry.
            _host.RaiseAutoReconnecting(disconnectReason, attemptCount);
        }
        finally
        {
            continueStatus = ResolveContinueStatus(_host.CancelAutoReconnect);
        }
    }

    /// <summary>
    /// The verdict written back to the control, as a pure function of whether a cancel was asked
    /// for.
    /// </summary>
    /// <remarks>
    /// Separated so the polarity can be asserted without a live control. It is the half of this
    /// handler that fails silently: the control accepts any integer, and reads the wrong one as a
    /// valid instruction.
    /// </remarks>
    internal static AutoReconnectContinueState ResolveContinueStatus(bool cancelRequested)
        => cancelRequested
            ? AutoReconnectContinueState.Stop
            : AutoReconnectContinueState.Automatic;

    public void OnAutoReconnected() => _host.RaiseAutoReconnected();
}
