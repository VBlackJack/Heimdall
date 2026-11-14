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

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Heimdall.Core.Network;

/// <summary>
/// Result of attributing a listening TCP endpoint to an expected process.
/// </summary>
public enum TcpListenerOwnership
{
    OwnedByExpectedProcess,
    OwnedByDifferentProcess,
    NothingListening,
    Indeterminate
}

/// <summary>
/// Attributes a listening TCP endpoint to a process.
/// </summary>
public interface ITcpListenerOwnershipProbe
{
    TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId);
}

/// <summary>
/// Windows implementation backed by the IP Helper API owner-pid TCP tables.
/// </summary>
public sealed class WindowsTcpListenerOwnershipProbe : ITcpListenerOwnershipProbe
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Maximum number of sized <c>GetExtendedTcpTable</c> calls per table. Connections can be added between
    /// the size query and the read, so the sized call may answer ERROR_INSUFFICIENT_BUFFER with a larger
    /// requirement. Four attempts absorb several consecutive growth races on a busy machine while still
    /// bounding the work; after that the probe fails exactly as it did before the retry existed.
    /// </summary>
    internal const int MaxTableReadAttempts = 4;

    private const int Ipv6AddressBytes = 16;

    private WindowsTcpListenerOwnershipProbe()
    {
    }

    /// <summary>Gets the shared production instance.</summary>
    public static WindowsTcpListenerOwnershipProbe Instance { get; } = new();

    public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId)
    {
        if (!OperatingSystem.IsWindows()
            || !IPAddress.TryParse(bindHost, out IPAddress? bindAddress)
            || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort
            || expectedProcessId <= 0)
        {
            return TcpListenerOwnership.Indeterminate;
        }

        try
        {
            return Classify(
                bindAddress,
                port,
                expectedProcessId,
                LoadTcp4Rows(Win32TcpTableNative.Instance),
                LoadTcp6Rows(Win32TcpTableNative.Instance));
        }
        catch (ExternalException)
        {
            return TcpListenerOwnership.Indeterminate;
        }
    }

    internal static TcpListenerOwnership Classify(
        IPAddress bindAddress,
        int port,
        int expectedProcessId,
        IReadOnlyList<Tcp4RawRow> tcp4Rows,
        IReadOnlyList<Tcp6RawRow> tcp6Rows)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(tcp4Rows);
        ArgumentNullException.ThrowIfNull(tcp6Rows);

        var ownerPids = new List<uint>();
        if (bindAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            foreach (Tcp4RawRow row in tcp4Rows)
            {
                if (Matches(row, bindAddress, port))
                {
                    ownerPids.Add(row.OwningPid);
                }
            }
        }
        else if (bindAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            foreach (Tcp6RawRow row in tcp6Rows)
            {
                if (Matches(row, bindAddress, port))
                {
                    ownerPids.Add(row.OwningPid);
                }
            }
        }
        else
        {
            return TcpListenerOwnership.Indeterminate;
        }

        if (ownerPids.Contains(unchecked((uint)expectedProcessId)))
        {
            return TcpListenerOwnership.OwnedByExpectedProcess;
        }

        return ownerPids.Count > 0
            ? TcpListenerOwnership.OwnedByDifferentProcess
            : TcpListenerOwnership.NothingListening;
    }

    private static bool Matches(Tcp4RawRow row, IPAddress bindAddress, int port)
    {
        if (row.State != TcpConnectionStateTable.ListeningState || ToPort(row.LocalPort) != port)
        {
            return false;
        }

        IPAddress rowAddress = new(row.LocalAddr);
        return rowAddress.Equals(IPAddress.Any) || rowAddress.Equals(bindAddress);
    }

    private static bool Matches(Tcp6RawRow row, IPAddress bindAddress, int port)
    {
        if (row.State != TcpConnectionStateTable.ListeningState || ToPort(row.LocalPort) != port)
        {
            return false;
        }

        IPAddress rowAddress = new(row.LocalAddr);
        return rowAddress.Equals(IPAddress.IPv6Any) || rowAddress.Equals(bindAddress);
    }

    private static int ToPort(uint port)
        => (ushort)IPAddress.NetworkToHostOrder((short)port);

    internal static IReadOnlyList<Tcp4RawRow> LoadTcp4Rows(ITcpTableNative native)
    {
        var result = new List<Tcp4RawRow>();
        IntPtr buffer = LoadTable(native, AfInet, out int rowCount);
        try
        {
            IntPtr rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < rowCount; i++)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                result.Add(new Tcp4RawRow(
                    row.LocalAddress,
                    row.LocalPort,
                    row.RemoteAddress,
                    row.RemotePort,
                    row.State,
                    row.OwningPid));
                rowPtr += rowSize;
            }
        }
        finally
        {
            native.Free(buffer);
        }

        return result;
    }

    internal static IReadOnlyList<Tcp6RawRow> LoadTcp6Rows(ITcpTableNative native)
    {
        var result = new List<Tcp6RawRow>();
        IntPtr buffer = LoadTable(native, AfInet6, out int rowCount);
        try
        {
            IntPtr rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            for (int i = 0; i < rowCount; i++)
            {
                MibTcp6RowOwnerPid row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                result.Add(new Tcp6RawRow(
                    (byte[])row.LocalAddress.Clone(),
                    row.LocalPort,
                    (byte[])row.RemoteAddress.Clone(),
                    row.RemotePort,
                    row.State,
                    row.OwningPid));
                rowPtr += rowSize;
            }
        }
        finally
        {
            native.Free(buffer);
        }

        return result;
    }

    private static IntPtr LoadTable(ITcpTableNative native, int addressFamily, out int rowCount)
    {
        int bufferSize = 0;
        uint result = native.GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, addressFamily);
        if (result is not 0 and not ErrorInsufficientBuffer)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        if (bufferSize <= 0)
        {
            rowCount = 0;
            return native.Allocate(sizeof(uint));
        }

        for (int attempt = 1; ; attempt++)
        {
            IntPtr buffer = native.Allocate(bufferSize);
            bool handedOver = false;
            try
            {
                // On ERROR_INSUFFICIENT_BUFFER the API writes the size it now requires into bufferSize.
                result = native.GetExtendedTcpTable(buffer, ref bufferSize, addressFamily);
                if (result == 0)
                {
                    rowCount = Marshal.ReadInt32(buffer);
                    handedOver = true;
                    return buffer;
                }
            }
            finally
            {
                if (!handedOver)
                {
                    native.Free(buffer);
                }
            }

            if (result != ErrorInsufficientBuffer || attempt >= MaxTableReadAttempts || bufferSize <= 0)
            {
                throw new Win32Exception(unchecked((int)result));
            }
        }
    }

    /// <summary>
    /// Native table access and buffer ownership, so the size-then-read protocol can be tested without the network.
    /// </summary>
    internal interface ITcpTableNative
    {
        /// <summary>Calls GetExtendedTcpTable for the owner-pid, all-states table; returns the Win32 status.</summary>
        uint GetExtendedTcpTable(IntPtr tcpTable, ref int outputBufferLength, int addressFamily);

        /// <summary>Allocates an unmanaged buffer of the given size.</summary>
        IntPtr Allocate(int size);

        /// <summary>Frees a buffer returned by <see cref="Allocate"/>.</summary>
        void Free(IntPtr buffer);
    }

    private sealed class Win32TcpTableNative : ITcpTableNative
    {
        internal static Win32TcpTableNative Instance { get; } = new();

        public uint GetExtendedTcpTable(IntPtr tcpTable, ref int outputBufferLength, int addressFamily)
            => WindowsTcpListenerOwnershipProbe.GetExtendedTcpTable(
                tcpTable,
                ref outputBufferLength,
                true,
                addressFamily,
                TcpTableOwnerPidAll,
                0);

        public IntPtr Allocate(int size) => Marshal.AllocHGlobal(size);

        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outputBufferLength,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Ipv6AddressBytes)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Ipv6AddressBytes)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
