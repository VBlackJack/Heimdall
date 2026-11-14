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

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Heimdall.Core.Network;

namespace Heimdall.App.Services;

/// <summary>
/// Synchronous loader for open TCP/UDP ports backed by Win32 IP helper APIs.
/// </summary>
public interface IOpenPortsService
{
    IReadOnlyList<PortEntry> Load();
}

public sealed class OpenPortsService : IOpenPortsService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
    private const int CountHeaderSize = sizeof(int);

    /// <summary>
    /// Maximum number of sized table calls per read. Connections and sockets can be added between the size
    /// query and the read, so the sized call may answer ERROR_INSUFFICIENT_BUFFER with a larger requirement.
    /// Four attempts absorb several consecutive growth races on a busy machine while still bounding the work;
    /// after that the read fails exactly as it did before the retry existed.
    /// </summary>
    internal const int MaxTableReadAttempts = 4;

    private readonly Func<IReadOnlyList<Tcp4RawRow>> _loadTcp4;
    private readonly Func<IReadOnlyList<Udp4RawRow>> _loadUdp4;
    private readonly Func<IReadOnlyList<Tcp6RawRow>> _loadTcp6;
    private readonly Func<IReadOnlyList<Udp6RawRow>> _loadUdp6;
    private readonly Func<int, string> _resolveProcessName;

    public OpenPortsService()
        : this(
            () => LoadTcp4Rows(Win32OpenPortsTableNative.Instance),
            () => LoadUdp4Rows(Win32OpenPortsTableNative.Instance),
            () => LoadTcp6Rows(Win32OpenPortsTableNative.Instance),
            () => LoadUdp6Rows(Win32OpenPortsTableNative.Instance),
            ResolveProcessName)
    {
    }

    internal OpenPortsService(
        Func<IReadOnlyList<Tcp4RawRow>> loadTcp4,
        Func<IReadOnlyList<Udp4RawRow>> loadUdp4,
        Func<IReadOnlyList<Tcp6RawRow>> loadTcp6,
        Func<IReadOnlyList<Udp6RawRow>> loadUdp6,
        Func<int, string> resolveProcessName)
    {
        ArgumentNullException.ThrowIfNull(loadTcp4);
        ArgumentNullException.ThrowIfNull(loadUdp4);
        ArgumentNullException.ThrowIfNull(loadTcp6);
        ArgumentNullException.ThrowIfNull(loadUdp6);
        ArgumentNullException.ThrowIfNull(resolveProcessName);

        _loadTcp4 = loadTcp4;
        _loadUdp4 = loadUdp4;
        _loadTcp6 = loadTcp6;
        _loadUdp6 = loadUdp6;
        _resolveProcessName = resolveProcessName;
    }

    public IReadOnlyList<PortEntry> Load()
    {
        var entries = new List<PortEntry>();
        var cache = new Dictionary<int, string>();
        string ResolveCached(int pid)
        {
            if (cache.TryGetValue(pid, out var name))
            {
                return name;
            }

            name = _resolveProcessName(pid);
            cache[pid] = name;
            return name;
        }

        TryAdd(entries, _loadTcp4, row => PortMapper.FromTcp4Row(row, ResolveCached));
        TryAdd(entries, _loadUdp4, row => PortMapper.FromUdp4Row(row, ResolveCached));
        TryAdd(entries, _loadTcp6, row => PortMapper.FromTcp6Row(row, ResolveCached));
        TryAdd(entries, _loadUdp6, row => PortMapper.FromUdp6Row(row, ResolveCached));
        return entries;
    }

    private static void TryAdd<TRow>(
        List<PortEntry> entries,
        Func<IReadOnlyList<TRow>> loader,
        Func<TRow, PortEntry> map)
    {
        try
        {
            foreach (var row in loader())
            {
                entries.Add(map(row));
            }
        }
        catch
        {
            // Preserve current behavior: one failing source should not crash the view.
        }
    }

    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return pid == 0 ? "System Idle" : $"[{pid}]";
        }
    }

    internal static IReadOnlyList<Tcp4RawRow> LoadTcp4Rows(IOpenPortsTableNative native)
        => LoadRows(
            native,
            isUdp: false,
            AfInet,
            Marshal.SizeOf<MibTcpRowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                return new Tcp4RawRow(row.dwLocalAddr, row.dwLocalPort, row.dwRemoteAddr, row.dwRemotePort, row.dwState, row.dwOwningPid);
            });

    internal static IReadOnlyList<Udp4RawRow> LoadUdp4Rows(IOpenPortsTableNative native)
        => LoadRows(
            native,
            isUdp: true,
            AfInet,
            Marshal.SizeOf<MibUdpRowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr);
                return new Udp4RawRow(row.dwLocalAddr, row.dwLocalPort, row.dwOwningPid);
            });

    internal static IReadOnlyList<Tcp6RawRow> LoadTcp6Rows(IOpenPortsTableNative native)
        => LoadRows(
            native,
            isUdp: false,
            AfInet6,
            Marshal.SizeOf<MibTcp6RowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                return new Tcp6RawRow((byte[])row.LocalAddr.Clone(), row.dwLocalPort, (byte[])row.RemoteAddr.Clone(), row.dwRemotePort, row.dwState, row.dwOwningPid);
            });

    internal static IReadOnlyList<Udp6RawRow> LoadUdp6Rows(IOpenPortsTableNative native)
        => LoadRows(
            native,
            isUdp: true,
            AfInet6,
            Marshal.SizeOf<MibUdp6RowOwnerPid>(),
            rowPtr =>
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr);
                return new Udp6RawRow((byte[])row.LocalAddr.Clone(), row.dwLocalPort, row.dwOwningPid);
            });

    private static List<TRow> LoadRows<TRow>(
        IOpenPortsTableNative native,
        bool isUdp,
        int addressFamily,
        int rowSize,
        Func<IntPtr, TRow> readRow)
    {
        var result = new List<TRow>();
        if (!TryReadTable(native, isUdp, addressFamily, out var buffer, out var capacity))
        {
            return result;
        }

        try
        {
            if (capacity < CountHeaderSize)
            {
                return result;
            }

            var rowCount = Marshal.ReadInt32(buffer);

            // The header count is untrusted: the rows it announces must fit in the buffer, otherwise the table
            // is treated as failed, like a read failure.
            if (rowCount < 0 || CountHeaderSize + ((long)rowCount * rowSize) > capacity)
            {
                return result;
            }

            var rowPtr = buffer + CountHeaderSize;
            for (var i = 0; i < rowCount; i++)
            {
                result.Add(readRow(rowPtr));
                rowPtr += rowSize;
            }
        }
        finally
        {
            native.Free(buffer);
        }

        return result;
    }

    /// <summary>
    /// Size-then-read protocol with a bounded retry on ERROR_INSUFFICIENT_BUFFER. On success the caller owns the
    /// returned buffer and must free it; on any failure every buffer allocated here has already been freed.
    /// </summary>
    private static bool TryReadTable(
        IOpenPortsTableNative native,
        bool isUdp,
        int addressFamily,
        out IntPtr buffer,
        out int capacity)
    {
        buffer = IntPtr.Zero;
        capacity = 0;

        // The result of the size query is ignored on purpose: only the reported size matters.
        var bufferSize = 0;
        CallTable(native, isUdp, IntPtr.Zero, ref bufferSize, addressFamily);
        if (bufferSize <= 0)
        {
            return false;
        }

        for (var attempt = 1; ; attempt++)
        {
            var allocatedSize = bufferSize;
            var candidate = native.Allocate(allocatedSize);
            var handedOver = false;
            uint status;
            try
            {
                // On ERROR_INSUFFICIENT_BUFFER the API writes the size it now requires into bufferSize.
                status = CallTable(native, isUdp, candidate, ref bufferSize, addressFamily);
                if (status == 0)
                {
                    buffer = candidate;
                    capacity = bufferSize > 0 ? Math.Min(allocatedSize, bufferSize) : allocatedSize;
                    handedOver = true;
                    return true;
                }
            }
            finally
            {
                if (!handedOver)
                {
                    native.Free(candidate);
                }
            }

            if (status != ErrorInsufficientBuffer || attempt >= MaxTableReadAttempts || bufferSize <= 0)
            {
                return false;
            }
        }
    }

    private static uint CallTable(IOpenPortsTableNative native, bool isUdp, IntPtr table, ref int size, int addressFamily)
        => isUdp
            ? native.GetExtendedUdpTable(table, ref size, addressFamily)
            : native.GetExtendedTcpTable(table, ref size, addressFamily);

    /// <summary>
    /// Native table access and buffer ownership, so the size-then-read protocol can be tested without the network.
    /// </summary>
    internal interface IOpenPortsTableNative
    {
        /// <summary>Calls GetExtendedTcpTable for the owner-pid, all-states table; returns the Win32 status.</summary>
        uint GetExtendedTcpTable(IntPtr table, ref int size, int addressFamily);

        /// <summary>Calls GetExtendedUdpTable for the owner-pid table; returns the Win32 status.</summary>
        uint GetExtendedUdpTable(IntPtr table, ref int size, int addressFamily);

        /// <summary>Allocates an unmanaged buffer of the given size.</summary>
        IntPtr Allocate(int size);

        /// <summary>Frees a buffer returned by <see cref="Allocate"/>.</summary>
        void Free(IntPtr buffer);
    }

    private sealed class Win32OpenPortsTableNative : IOpenPortsTableNative
    {
        internal static Win32OpenPortsTableNative Instance { get; } = new();

        public uint GetExtendedTcpTable(IntPtr table, ref int size, int addressFamily)
            => OpenPortsService.GetExtendedTcpTable(table, ref size, true, addressFamily, TcpTableOwnerPidAll, 0);

        public uint GetExtendedUdpTable(IntPtr table, ref int size, int addressFamily)
            => OpenPortsService.GetExtendedUdpTable(table, ref size, true, addressFamily, UdpTableOwnerPid, 0);

        public IntPtr Allocate(int size) => Marshal.AllocHGlobal(size);

        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }
}
