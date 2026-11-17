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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Exercises the size-then-read protocol and the table parsing of the open ports service through a fake
/// native seam, without touching the network. The fake writes a real table layout so the real parser runs.
/// </summary>
public sealed class OpenPortsServiceTableTests
{
    private const uint Success = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorAccessDenied = 5;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int CountHeaderSize = sizeof(uint);
    private const int Tcp4RowSize = 24;
    private const int Udp4RowSize = 12;
    private const int Tcp6RowSize = 56;
    private const int Udp6RowSize = 28;
    private const int InitialTableSize = 100;
    private const int GrownTableSize = 400;
    private const uint FirstPid = 4242;
    private const uint SecondPid = 77;

    public static TheoryData<TableKind> AllKinds() => new()
    {
        TableKind.Tcp4,
        TableKind.Udp4,
        TableKind.Tcp6,
        TableKind.Udp6,
    };

    public enum TableKind
    {
        Tcp4,
        Udp4,
        Tcp6,
        Udp6,
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_GrowsOnceThenSucceeds_ReturnsRowsAndFreesEveryBuffer(TableKind kind)
    {
        byte[] firstTable = Table(kind, FirstPid);
        byte[] secondTable = Table(kind, FirstPid, SecondPid);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, firstTable.Length),
            new Step(ErrorInsufficientBuffer, secondTable.Length),
            new Step(Success, secondTable.Length, secondTable));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Equal(new[] { FirstPid, SecondPid }, pids);
        Assert.Equal(new[] { firstTable.Length, secondTable.Length }, native.AllocatedSizes);
        Assert.Equal(2, native.SizedCalls);
        AssertCalls(kind, native);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_SucceedsAtFirstSizedCall_ReturnsRowsWithOneAllocation(TableKind kind)
    {
        byte[] table = Table(kind, FirstPid);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Equal(new[] { FirstPid }, pids);
        Assert.Equal(1, native.SizedCalls);
        Assert.Equal(1, native.Allocations);
        AssertCalls(kind, native);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_GrowsOnEveryAttempt_FailsEmptyAfterExactlyMaxAttempts(TableKind kind)
    {
        var steps = new List<Step> { new(ErrorInsufficientBuffer, InitialTableSize) };
        for (var i = 0; i < OpenPortsService.MaxTableReadAttempts; i++)
        {
            steps.Add(new Step(ErrorInsufficientBuffer, GrownTableSize + i));
        }

        using var native = new FakeNative(steps.ToArray());

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        Assert.Equal(OpenPortsService.MaxTableReadAttempts, native.SizedCalls);
        Assert.Equal(OpenPortsService.MaxTableReadAttempts, native.Allocations);
        AssertCalls(kind, native);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_OtherErrorOnSizedCall_DoesNotRetryAndReturnsEmpty(TableKind kind)
    {
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(ErrorAccessDenied, InitialTableSize));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        Assert.Equal(1, native.SizedCalls);
        Assert.Equal(1, native.Allocations);
        AssertCalls(kind, native);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_ReportedSizeZeroOnSizeQuery_ReturnsEmptyWithoutAllocating(TableKind kind)
    {
        using var native = new FakeNative(new Step(Success, 0));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        Assert.Equal(0, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_ReportedSizeZeroAfterInsufficientBuffer_FailsEmptyAndFreesBuffer(TableKind kind)
    {
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(ErrorInsufficientBuffer, 0));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        Assert.Equal(1, native.SizedCalls);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_NativeThrowsOnSizedCall_PropagatesAndFreesBuffer(TableKind kind)
    {
        var failure = new InvalidOperationException("native failure");
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, InitialTableSize),
            new Step(Success, InitialTableSize, Throws: failure));

        var error = Assert.Throws<InvalidOperationException>(() => Load(kind, native));

        Assert.Same(failure, error);
        Assert.Equal(1, native.Allocations);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_HeaderCountExceedsBuffer_TreatedAsFailedAndFreesBuffer(TableKind kind)
    {
        byte[] table = Table(kind, FirstPid);
        BitConverter.GetBytes(2u).CopyTo(table, 0);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_HeaderCountHugeOrNegative_TreatedAsFailedAndFreesBuffer(TableKind kind)
    {
        foreach (var count in new[] { int.MaxValue, -1 })
        {
            byte[] table = Table(kind, FirstPid);
            BitConverter.GetBytes(count).CopyTo(table, 0);
            using var native = new FakeNative(
                new Step(ErrorInsufficientBuffer, table.Length),
                new Step(Success, table.Length, table));

            IReadOnlyList<uint> pids = Load(kind, native);

            Assert.Empty(pids);
            native.AssertNothingLeaked();
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_HeaderCountFitsExactly_ReturnsAllRows(TableKind kind)
    {
        byte[] table = Table(kind, FirstPid, SecondPid);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, table.Length),
            new Step(Success, table.Length, table));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Equal(2, pids.Count);
        native.AssertNothingLeaked();
    }

    [Fact]
    public void Load_RealNativePath_ReturnsRowsOnThisMachine()
    {
        var entries = new OpenPortsService().Load();

        Assert.NotEmpty(entries);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_SuccessReportsSmallerSizeThanAllocation_ParsesRowsThatFit(TableKind kind)
    {
        byte[] oneRow = Table(kind, FirstPid);
        byte[] twoRows = Table(kind, FirstPid, SecondPid);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, twoRows.Length),
            new Step(Success, oneRow.Length, oneRow));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Equal(new[] { FirstPid }, pids);
        Assert.Equal(new[] { twoRows.Length }, native.AllocatedSizes);
        AssertCalls(kind, native);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_HeaderCountFitsAllocationButNotReportedSize_TreatedAsFailed(TableKind kind)
    {
        byte[] oneRow = Table(kind, FirstPid);
        byte[] twoRows = Table(kind, FirstPid, SecondPid);
        using var native = new FakeNative(
            new Step(ErrorInsufficientBuffer, twoRows.Length),
            new Step(Success, oneRow.Length, twoRows));

        IReadOnlyList<uint> pids = Load(kind, native);

        Assert.Empty(pids);
        native.AssertNothingLeaked();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Load_SuccessReportsNonPositiveSize_UsesAllocatedSizeAsCapacity(TableKind kind)
    {
        foreach (var reported in new[] { 0, -1 })
        {
            byte[] twoRows = Table(kind, FirstPid, SecondPid);
            using var native = new FakeNative(
                new Step(ErrorInsufficientBuffer, twoRows.Length),
                new Step(Success, reported, twoRows));

            IReadOnlyList<uint> pids = Load(kind, native);

            Assert.Equal(new[] { FirstPid, SecondPid }, pids);
            AssertCalls(kind, native);
            native.AssertNothingLeaked();
        }
    }

    private static void AssertCalls(TableKind kind, FakeNative native)
    {
        var expectedCall = IsUdp(kind) ? FakeNative.Call.Udp : FakeNative.Call.Tcp;
        var expectedFamily = kind is TableKind.Tcp6 or TableKind.Udp6 ? AfInet6 : AfInet;
        Assert.NotEmpty(native.Calls);
        Assert.All(native.Calls, call =>
        {
            Assert.Equal(expectedCall, call.Call);
            Assert.Equal(expectedFamily, call.AddressFamily);
        });
    }

    private static bool IsUdp(TableKind kind) => kind is TableKind.Udp4 or TableKind.Udp6;

    private static int RowSize(TableKind kind) => kind switch
    {
        TableKind.Tcp4 => Tcp4RowSize,
        TableKind.Udp4 => Udp4RowSize,
        TableKind.Tcp6 => Tcp6RowSize,
        TableKind.Udp6 => Udp6RowSize,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Builds a real table layout: a row-count header, then rows whose last field is the owning pid.</summary>
    private static byte[] Table(TableKind kind, params uint[] pids)
    {
        var rowSize = RowSize(kind);
        var bytes = new byte[CountHeaderSize + (pids.Length * rowSize)];
        BitConverter.GetBytes((uint)pids.Length).CopyTo(bytes, 0);
        for (var i = 0; i < pids.Length; i++)
        {
            var pidOffset = CountHeaderSize + (i * rowSize) + rowSize - sizeof(uint);
            BitConverter.GetBytes(pids[i]).CopyTo(bytes, pidOffset);
        }

        return bytes;
    }

    private static IReadOnlyList<uint> Load(TableKind kind, OpenPortsService.IOpenPortsTableNative native) => kind switch
    {
        TableKind.Tcp4 => OpenPortsService.LoadTcp4Rows(native).Select(x => x.OwningPid).ToArray(),
        TableKind.Udp4 => OpenPortsService.LoadUdp4Rows(native).Select(x => x.OwningPid).ToArray(),
        TableKind.Tcp6 => OpenPortsService.LoadTcp6Rows(native).Select(x => x.OwningPid).ToArray(),
        TableKind.Udp6 => OpenPortsService.LoadUdp6Rows(native).Select(x => x.OwningPid).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>One scripted answer: status, the size the API reports, the table written on success, or an exception to throw on a sized call.</summary>
    private sealed record Step(uint Status, int ReportedSize, byte[]? Table = null, Exception? Throws = null);

    private sealed class FakeNative : OpenPortsService.IOpenPortsTableNative, IDisposable
    {
        private readonly Queue<Step> _steps;
        private readonly HashSet<IntPtr> _live = [];

        public FakeNative(params Step[] steps) => _steps = new Queue<Step>(steps);

        public enum Call
        {
            None,
            Tcp,
            Udp,
        }

        public List<int> AllocatedSizes { get; } = [];

        public int Allocations => AllocatedSizes.Count;

        public int SizedCalls { get; private set; }

        public List<(Call Call, int AddressFamily)> Calls { get; } = [];

        public uint GetExtendedTcpTable(IntPtr table, ref int size, int addressFamily)
        {
            Calls.Add((Call.Tcp, addressFamily));
            return Answer(table, ref size);
        }

        public uint GetExtendedUdpTable(IntPtr table, ref int size, int addressFamily)
        {
            Calls.Add((Call.Udp, addressFamily));
            return Answer(table, ref size);
        }

        public IntPtr Allocate(int size)
        {
            var buffer = Marshal.AllocHGlobal(size);
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
            foreach (var buffer in _live)
            {
                Marshal.FreeHGlobal(buffer);
            }

            _live.Clear();
        }

        private uint Answer(IntPtr table, ref int size)
        {
            var step = _steps.Dequeue();
            if (table != IntPtr.Zero)
            {
                SizedCalls++;
                if (step.Throws is not null)
                {
                    throw step.Throws;
                }

                if (step.Status == Success && step.Table is { } bytes)
                {
                    Assert.True(size >= bytes.Length, "Buffer smaller than the table written.");
                    Marshal.Copy(bytes, 0, table, bytes.Length);
                }
                else if (step.Status == Success)
                {
                    Marshal.WriteInt32(table, 0);
                }
            }

            size = step.ReportedSize;
            return step.Status;
        }
    }
}
