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
                LoadTcp4Rows(),
                LoadTcp6Rows());
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

    private static IReadOnlyList<Tcp4RawRow> LoadTcp4Rows()
    {
        var result = new List<Tcp4RawRow>();
        IntPtr buffer = LoadTable(AfInet, out int rowCount);
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
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static IReadOnlyList<Tcp6RawRow> LoadTcp6Rows()
    {
        var result = new List<Tcp6RawRow>();
        IntPtr buffer = LoadTable(AfInet6, out int rowCount);
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
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static IntPtr LoadTable(int addressFamily, out int rowCount)
    {
        int bufferSize = 0;
        uint result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            true,
            addressFamily,
            TcpTableOwnerPidAll,
            0);
        if (result is not 0 and not ErrorInsufficientBuffer)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        if (bufferSize <= 0)
        {
            rowCount = 0;
            return Marshal.AllocHGlobal(sizeof(uint));
        }

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        result = GetExtendedTcpTable(
            buffer,
            ref bufferSize,
            true,
            addressFamily,
            TcpTableOwnerPidAll,
            0);
        if (result != 0)
        {
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(unchecked((int)result));
        }

        rowCount = Marshal.ReadInt32(buffer);
        return buffer;
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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
