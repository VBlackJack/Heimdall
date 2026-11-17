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
using System.Runtime.InteropServices;
using Heimdall.Core.Network;

namespace Heimdall.Core.Tests;

/// <summary>
/// Exercises the size-then-read protocol and the table parsing of the Windows ownership probe
/// through a fake native seam, without touching the network.
/// </summary>
public sealed class TcpListenerOwnershipProbeTableTests
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint Success = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInvalidData = 13;
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;
    private const int CountHeaderSize = sizeof(uint);
    private const int Ipv6AddressBytes = 16;
    private const int ListenPort = 13389;
    private const int OwnerPid = 4242;
    private const int OtherPid = 77;
    private const int InitialTableSize = 100;
    private const int GrownTableSize = 400;
    private const int RemotePort = 443;
    private const uint RemoteAddressBytesValue = 0x0403020A;

    [Fact]
    public void LoadTcp4Rows_SucceedsAtFirstTry_ParsesRowsAndFreesBuffers()
    {
        byte[] table = Ipv4Table((IPAddress.Loopback, ListenPort, TcpConnectionStateTable.ListeningState, OwnerPid));
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        IReadOnlyList<Tcp4RawRow> rows = WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native);

        Tcp4RawRow row = Assert.Single(rows);
        Assert.Equal(TcpConnectionStateTable.ListeningState, row.State);
        Assert.Equal((uint)OwnerPid, row.OwningPid);
        Assert.Equal(IPAddress.Loopback, new IPAddress(row.LocalAddr));
        Assert.Equal(ToNetworkPort(ListenPort), row.LocalPort);
        Assert.Equal(RemoteAddressBytesValue, row.RemoteAddr);
        Assert.Equal(ToNetworkPort(RemotePort), row.RemotePort);
        Assert.Equal(AfInet, native.LastAddressFamily);
        Assert.Equal(
            TcpListenerOwnership.OwnedByExpectedProcess,
            WindowsTcpListenerOwnershipProbe.Classify(IPAddress.Loopback, ListenPort, OwnerPid, rows, []));
        Assert.Equal(1, native.SizedCalls);
        Assert.Equal(1, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_InsufficientBufferOnce_RetriesWithReportedSizeAndUsesSecondAnswer()
    {
        byte[] firstTable = Ipv4Table((IPAddress.Loopback, ListenPort, TcpConnectionStateTable.ListeningState, OtherPid));
        byte[] secondTable = Ipv4Table(
            (IPAddress.Loopback, ListenPort, TcpConnectionStateTable.ListeningState, OwnerPid),
            (IPAddress.Loopback, ListenPort + 1, TcpConnectionStateTable.ListeningState, OtherPid));
        Assert.True(secondTable.Length > firstTable.Length);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, firstTable.Length),
            new Step(ErrorInsufficientBuffer, secondTable.Length),
            new Step(Success, secondTable.Length, secondTable));

        IReadOnlyList<Tcp4RawRow> rows = WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native);

        Assert.Equal(2, rows.Count);
        Assert.Equal((uint)OwnerPid, rows[0].OwningPid);
        Assert.Equal(new[] { firstTable.Length, secondTable.Length }, native.AllocatedSizes);
        Assert.Equal(2, native.SizedCalls);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_InsufficientBufferOnEveryAttempt_FailsAfterExactlyMaxAttempts()
    {
        var steps = new List<Step> { new(ErrorInsufficientBuffer, InitialTableSize) };
        for (int i = 0; i < WindowsTcpListenerOwnershipProbe.MaxTableReadAttempts; i++)
        {
            steps.Add(new Step(ErrorInsufficientBuffer, GrownTableSize + i));
        }

        using var native = new FakeNative(steps.ToArray());

        Win32Exception error = Assert.Throws<Win32Exception>(() => WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native));

        Assert.Equal((int)ErrorInsufficientBuffer, error.NativeErrorCode);
        Assert.Equal(WindowsTcpListenerOwnershipProbe.MaxTableReadAttempts, native.SizedCalls);
        Assert.Equal(WindowsTcpListenerOwnershipProbe.MaxTableReadAttempts, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_OtherErrorOnSizedCall_DoesNotRetry()
    {
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(ErrorAccessDenied, InitialTableSize));

        Win32Exception error = Assert.Throws<Win32Exception>(() => WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native));

        Assert.Equal((int)ErrorAccessDenied, error.NativeErrorCode);
        Assert.Equal(1, native.SizedCalls);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_ReportedSizeZeroAfterInsufficientBuffer_FailsAndFreesBuffer()
    {
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(ErrorInsufficientBuffer, 0));

        Win32Exception error = Assert.Throws<Win32Exception>(() => WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native));

        Assert.Equal((int)ErrorInsufficientBuffer, error.NativeErrorCode);
        Assert.Equal(1, native.SizedCalls);
        Assert.Equal(1, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_SeamThrowsOnSizedCall_PropagatesAndFreesBuffer()
    {
        var failure = new InvalidOperationException("native failure");
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(Success, InitialTableSize, Throws: failure));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native));

        Assert.Same(failure, error);
        Assert.Equal(1, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_OtherErrorOnSizeQuery_ThrowsWithoutAllocating()
    {
        using var native = new FakeNative(new Step(ErrorAccessDenied, 0));

        Win32Exception error = Assert.Throws<Win32Exception>(() => WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native));

        Assert.Equal((int)ErrorAccessDenied, error.NativeErrorCode);
        Assert.Equal(0, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp4Rows_EmptyTable_ReturnsNoRowsAndFreesBuffer()
    {
        using var native = new FakeNative(new Step(Success, 0));

        IReadOnlyList<Tcp4RawRow> rows = WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native);

        Assert.Empty(rows);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void LoadTcp6Rows_ParsesRowAndPortByteOrder_AfterRetry()
    {
        IPAddress address = IPAddress.IPv6Loopback;
        byte[] table = Ipv6Table((address, ListenPort, TcpConnectionStateTable.ListeningState, OwnerPid));

        // Deliberate: the retry reports a SMALLER size (100 then 60). It covers the case where the reported
        // size is not larger than the buffer already allocated; the buffer must still be reallocated and read.
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        IReadOnlyList<Tcp6RawRow> rows = WindowsTcpListenerOwnershipProbe.LoadTcp6Rows(native);

        Tcp6RawRow row = Assert.Single(rows);
        Assert.Equal(address.GetAddressBytes(), row.LocalAddr);
        Assert.Equal(
            TcpListenerOwnership.OwnedByExpectedProcess,
            WindowsTcpListenerOwnershipProbe.Classify(address, ListenPort, OwnerPid, [], rows));
        Assert.Equal(
            TcpListenerOwnership.OwnedByDifferentProcess,
            WindowsTcpListenerOwnershipProbe.Classify(address, ListenPort, OtherPid, [], rows));
        Assert.Equal(
            TcpListenerOwnership.NothingListening,
            WindowsTcpListenerOwnershipProbe.Classify(address, ListenPort + 1, OwnerPid, [], rows));
        Assert.Equal(AfInet6, native.LastAddressFamily);
        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 100)]
    [InlineData(true, 1)]
    [InlineData(true, 100)]
    public void LoadRows_CountLargerThanBuffer_IsRefusedAsInvalidDataAndFreesBuffer(bool ipv6, int extraRows)
    {
        byte[] table = WithCount(TwoRowTable(ipv6), 2 + extraRows);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        AssertRefusedAsInvalidData(native, ipv6);

        Assert.Equal(1, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, int.MinValue)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, -1)]
    [InlineData(true, int.MinValue)]
    [InlineData(true, int.MaxValue)]
    public void LoadRows_NegativeOrHugeCount_IsRefusedAsInvalidDataAndFreesBuffer(bool ipv6, int count)
    {
        byte[] table = WithCount(TwoRowTable(ipv6), count);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        AssertRefusedAsInvalidData(native, ipv6);

        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadRows_CountFittingExactly_IsParsed(bool ipv6)
    {
        byte[] table = TwoRowTable(ipv6);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        Assert.Equal(2, CountRows(native, ipv6));
        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadRows_SuccessReportingSmallerSize_ParsesOnlyRowsThatFitTheReportedSize(bool ipv6)
    {
        byte[] full = TwoRowTable(ipv6);
        int oneRowSize = CountHeaderSize + RowSize(ipv6);
        byte[] oneRowAnnounced = WithCount(full, 1);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, full.Length),
            new Step(Success, oneRowSize, oneRowAnnounced));

        Assert.Equal(1, CountRows(native, ipv6));
        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadRows_CountFitsAllocationButNotReportedSize_IsRefusedAndFreesBuffer(bool ipv6)
    {
        byte[] full = TwoRowTable(ipv6);
        int oneRowSize = CountHeaderSize + RowSize(ipv6);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, full.Length),
            new Step(Success, oneRowSize, full));

        AssertRefusedAsInvalidData(native, ipv6);

        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -5)]
    [InlineData(true, 0)]
    [InlineData(true, -5)]
    public void LoadRows_SuccessReportingZeroOrNegativeSize_UsesAllocatedSize(bool ipv6, int reportedSize)
    {
        byte[] table = TwoRowTable(ipv6);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, reportedSize, table));

        Assert.Equal(2, CountRows(native, ipv6));
        native.AssertNothingLeaked();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadRows_ZeroOrNegativeReportedSizeWithCountBeyondAllocation_IsRefused(bool ipv6)
    {
        byte[] table = WithCount(TwoRowTable(ipv6), 3);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, 0, table));

        AssertRefusedAsInvalidData(native, ipv6);

        native.AssertNothingLeaked();
    }

    private static int RowSize(bool ipv6) => ipv6 ? Ipv6RowSize : Ipv4RowSize;

    private static byte[] TwoRowTable(bool ipv6)
        => ipv6
            ? Ipv6Table(
                (IPAddress.IPv6Loopback, ListenPort, TcpConnectionStateTable.ListeningState, OwnerPid),
                (IPAddress.IPv6Loopback, ListenPort + 1, TcpConnectionStateTable.ListeningState, OtherPid))
            : Ipv4Table(
                (IPAddress.Loopback, ListenPort, TcpConnectionStateTable.ListeningState, OwnerPid),
                (IPAddress.Loopback, ListenPort + 1, TcpConnectionStateTable.ListeningState, OtherPid));

    private static byte[] WithCount(byte[] table, int count)
    {
        byte[] copy = (byte[])table.Clone();
        BitConverter.GetBytes(count).CopyTo(copy, 0);
        return copy;
    }

    private static int CountRows(FakeNative native, bool ipv6)
        => ipv6
            ? WindowsTcpListenerOwnershipProbe.LoadTcp6Rows(native).Count
            : WindowsTcpListenerOwnershipProbe.LoadTcp4Rows(native).Count;

    /// <summary>
    /// The refusal is a Win32Exception, an ExternalException: the exact type the probe turns into Indeterminate.
    /// </summary>
    private static void AssertRefusedAsInvalidData(FakeNative native, bool ipv6)
    {
        ExternalException error = Assert.ThrowsAny<ExternalException>(() => CountRows(native, ipv6));

        Win32Exception win32 = Assert.IsType<Win32Exception>(error);
        Assert.Equal((int)ErrorInvalidData, win32.NativeErrorCode);
    }

    private static uint ToNetworkPort(int port)
        => (ushort)IPAddress.HostToNetworkOrder((short)port);

    private static byte[] Ipv4Table(params (IPAddress Address, int Port, uint State, int Pid)[] rows)
    {
        byte[] bytes = new byte[CountHeaderSize + (rows.Length * Ipv4RowSize)];
        BitConverter.GetBytes((uint)rows.Length).CopyTo(bytes, 0);
        int offset = CountHeaderSize;
        foreach ((IPAddress address, int port, uint state, int pid) in rows)
        {
            BitConverter.GetBytes(state).CopyTo(bytes, offset);
            address.GetAddressBytes().CopyTo(bytes, offset + sizeof(uint));
            BitConverter.GetBytes(ToNetworkPort(port)).CopyTo(bytes, offset + (2 * sizeof(uint)));
            BitConverter.GetBytes(RemoteAddressBytesValue).CopyTo(bytes, offset + (3 * sizeof(uint)));
            BitConverter.GetBytes(ToNetworkPort(RemotePort)).CopyTo(bytes, offset + (4 * sizeof(uint)));
            BitConverter.GetBytes((uint)pid).CopyTo(bytes, offset + (5 * sizeof(uint)));
            offset += Ipv4RowSize;
        }

        return bytes;
    }

    private static byte[] Ipv6Table(params (IPAddress Address, int Port, uint State, int Pid)[] rows)
    {
        byte[] bytes = new byte[CountHeaderSize + (rows.Length * Ipv6RowSize)];
        BitConverter.GetBytes((uint)rows.Length).CopyTo(bytes, 0);
        int offset = CountHeaderSize;
        foreach ((IPAddress address, int port, uint state, int pid) in rows)
        {
            address.GetAddressBytes().CopyTo(bytes, offset);
            int afterAddress = offset + Ipv6AddressBytes;
            BitConverter.GetBytes(ToNetworkPort(port)).CopyTo(bytes, afterAddress + sizeof(uint));
            int afterRemote = afterAddress + (2 * sizeof(uint)) + Ipv6AddressBytes;
            BitConverter.GetBytes(state).CopyTo(bytes, afterRemote + (2 * sizeof(uint)));
            BitConverter.GetBytes((uint)pid).CopyTo(bytes, afterRemote + (3 * sizeof(uint)));
            offset += Ipv6RowSize;
        }

        return bytes;
    }

    /// <summary>One scripted answer: status, the size the API reports, the table written on success, or an exception to throw on a sized call.</summary>
    private sealed record Step(uint Status, int ReportedSize, byte[]? Table = null, Exception? Throws = null);

    private sealed class FakeNative : WindowsTcpListenerOwnershipProbe.ITcpTableNative, IDisposable
    {
        private readonly Queue<Step> _steps;
        private readonly HashSet<IntPtr> _live = [];

        public FakeNative(params Step[] steps) => _steps = new Queue<Step>(steps);

        public List<int> AllocatedSizes { get; } = [];

        public int Allocations => AllocatedSizes.Count;

        public int SizedCalls { get; private set; }

        public int LastAddressFamily { get; private set; }

        public uint GetExtendedTcpTable(IntPtr tcpTable, ref int outputBufferLength, int addressFamily)
        {
            LastAddressFamily = addressFamily;
            Step step = _steps.Dequeue();
            if (tcpTable != IntPtr.Zero)
            {
                SizedCalls++;
                if (step.Throws is not null)
                {
                    throw step.Throws;
                }

                if (step.Status == Success && step.Table is { } table)
                {
                    Assert.True(outputBufferLength >= table.Length, "Buffer smaller than the table written.");
                    Marshal.Copy(table, 0, tcpTable, table.Length);
                }
                else if (step.Status == Success)
                {
                    Marshal.WriteInt32(tcpTable, 0);
                }
            }

            outputBufferLength = step.ReportedSize;
            return step.Status;
        }

        public IntPtr Allocate(int size)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            _live.Add(buffer);
            AllocatedSizes.Add(size);
            return buffer;
        }

        public void Free(IntPtr buffer)
        {
            Assert.True(_live.Remove(buffer), "Freed a buffer that is not live.");
            Marshal.FreeHGlobal(buffer);
        }

        public void AssertNothingLeaked() => Assert.Empty(_live);

        public void Dispose()
        {
            foreach (IntPtr buffer in _live)
            {
                Marshal.FreeHGlobal(buffer);
            }

            _live.Clear();
        }
    }
}
