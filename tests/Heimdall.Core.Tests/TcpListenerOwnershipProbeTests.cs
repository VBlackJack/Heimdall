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

using System.Net;
using System.Net.Sockets;
using Heimdall.Core.Network;

namespace Heimdall.Core.Tests;

public sealed class TcpListenerOwnershipProbeTests
{
    [Fact]
    public void Classify_Ipv4ListenerOwnedByExpectedProcess_ReturnsOwned()
    {
        IReadOnlyList<Tcp4RawRow> rows =
        [
            Tcp4(IPAddress.Loopback, 13389, TcpConnectionStateTable.ListeningState, 42)
        ];

        TcpListenerOwnership result = WindowsTcpListenerOwnershipProbe.Classify(
            IPAddress.Loopback,
            13389,
            42,
            rows,
            []);

        Assert.Equal(TcpListenerOwnership.OwnedByExpectedProcess, result);
    }

    [Fact]
    public void Classify_Ipv4WildcardListener_MatchesSpecificLoopback()
    {
        IReadOnlyList<Tcp4RawRow> rows =
        [
            Tcp4(IPAddress.Any, 13389, TcpConnectionStateTable.ListeningState, 42)
        ];

        TcpListenerOwnership result = WindowsTcpListenerOwnershipProbe.Classify(
            IPAddress.Parse("127.0.0.2"),
            13389,
            42,
            rows,
            []);

        Assert.Equal(TcpListenerOwnership.OwnedByExpectedProcess, result);
    }

    [Fact]
    public void Classify_Ipv6WildcardListener_MatchesSpecificLoopback()
    {
        IReadOnlyList<Tcp6RawRow> rows =
        [
            Tcp6(IPAddress.IPv6Any, 13389, TcpConnectionStateTable.ListeningState, 42)
        ];

        TcpListenerOwnership result = WindowsTcpListenerOwnershipProbe.Classify(
            IPAddress.IPv6Loopback,
            13389,
            42,
            [],
            rows);

        Assert.Equal(TcpListenerOwnership.OwnedByExpectedProcess, result);
    }

    [Fact]
    public void Classify_DifferentOwner_ReturnsDifferentProcess()
    {
        IReadOnlyList<Tcp4RawRow> rows =
        [
            Tcp4(IPAddress.Loopback, 13389, TcpConnectionStateTable.ListeningState, 99)
        ];

        TcpListenerOwnership result = WindowsTcpListenerOwnershipProbe.Classify(
            IPAddress.Loopback,
            13389,
            42,
            rows,
            []);

        Assert.Equal(TcpListenerOwnership.OwnedByDifferentProcess, result);
    }

    [Fact]
    public void Classify_NonListeningAndWrongPortRows_ReturnsNothingListening()
    {
        IReadOnlyList<Tcp4RawRow> rows =
        [
            Tcp4(IPAddress.Loopback, 13389, 5, 42),
            Tcp4(IPAddress.Loopback, 13390, TcpConnectionStateTable.ListeningState, 42)
        ];

        TcpListenerOwnership result = WindowsTcpListenerOwnershipProbe.Classify(
            IPAddress.Loopback,
            13389,
            42,
            rows,
            []);

        Assert.Equal(TcpListenerOwnership.NothingListening, result);
    }

    [Fact]
    public void Probe_RealWindowsListener_AttributesCurrentProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int differentProcessId = Environment.ProcessId == 1 ? 2 : 1;

        TcpListenerOwnership owned = WindowsTcpListenerOwnershipProbe.Instance.Probe(
            IPAddress.Loopback.ToString(),
            port,
            Environment.ProcessId);
        TcpListenerOwnership different = WindowsTcpListenerOwnershipProbe.Instance.Probe(
            IPAddress.Loopback.ToString(),
            port,
            differentProcessId);

        Assert.Equal(TcpListenerOwnership.OwnedByExpectedProcess, owned);
        Assert.Equal(TcpListenerOwnership.OwnedByDifferentProcess, different);
    }

    private static Tcp4RawRow Tcp4(IPAddress address, int port, uint state, uint owningPid)
    {
        byte[] bytes = address.GetAddressBytes();
        uint rawAddress = BitConverter.ToUInt32(bytes);
        return new Tcp4RawRow(rawAddress, ToNetworkPort(port), 0, 0, state, owningPid);
    }

    private static Tcp6RawRow Tcp6(IPAddress address, int port, uint state, uint owningPid)
    {
        return new Tcp6RawRow(
            address.GetAddressBytes(),
            ToNetworkPort(port),
            IPAddress.IPv6None.GetAddressBytes(),
            0,
            state,
            owningPid);
    }

    private static uint ToNetworkPort(int port)
        => unchecked((uint)(ushort)IPAddress.HostToNetworkOrder((short)port));
}
