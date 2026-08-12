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

/// <summary>
/// COM event source interface for MsTscAx ActiveX control.
/// DispId values must match the ActiveX type library exactly.
/// </summary>
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

    [DispId(22)]
    void OnAutoReconnecting(int disconnectReason, int attemptCount, out bool continueReconnect);

    [DispId(23)]
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

    public void OnAutoReconnecting(int disconnectReason, int attemptCount, out bool continueReconnect)
    {
        continueReconnect = true;
        try
        {
            // Raise first so a listener can synchronously cancel the current retry.
            _host.RaiseAutoReconnecting(disconnectReason, attemptCount);
        }
        finally
        {
            continueReconnect = !_host.CancelAutoReconnect;
        }
    }

    public void OnAutoReconnected() => _host.RaiseAutoReconnected();
}
