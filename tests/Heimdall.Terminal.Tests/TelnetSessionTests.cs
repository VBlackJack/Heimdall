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
using System.Text;
using Heimdall.Terminal;

namespace Heimdall.Terminal.Tests;

public sealed class TelnetSessionTests
{
    [Fact]
    public async Task Write_BytesAppearOnTheWire()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            await using NetworkStream stream = client.GetStream();

            session.Write(Encoding.ASCII.GetBytes("abc"));

            byte[] received = await ReadExactlyAsync(stream, 3);

            Assert.Equal(new byte[] { 0x61, 0x62, 0x63 }, received);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ServerClose_RaisesNonZeroProcessExit()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

            client.Close();

            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(0, exitCode);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Kill_RaisesZeroProcessExit()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += exitCode => exited.TrySetResult(exitCode);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

            session.Kill();

            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, exitCode);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Naws_SizeContaining255_IsEscapedOnTheWire()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty, columns: 255, rows: 24);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            await using NetworkStream stream = client.GetStream();

            await stream.WriteAsync(new byte[] { Iac, Do, Naws });

            // WILL NAWS, then the frame with the 255 width byte doubled (RFC 1073).
            byte[] received = await ReadExactlyAsync(stream, 3 + 10);

            Assert.Equal(
                new byte[] { Iac, Will, Naws, Iac, Sb, Naws, 0, Iac, Iac, 0, 24, Iac, Se },
                received);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Naws_WithdrawnByThePeer_IsConfirmedAndNoLongerSent()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty, columns: 80, rows: 24);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            await using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(new byte[] { Iac, Do, Naws });
            _ = await ReadExactlyAsync(stream, 3 + 9);

            // A repeated DO is not acknowledged again; DONT is confirmed with WONT.
            await stream.WriteAsync(new byte[] { Iac, Do, Naws, Iac, Dont, Naws });
            Assert.Equal(new byte[] { Iac, Wont, Naws }, await ReadExactlyAsync(stream, 3));

            session.Resize(100, 30);
            session.Write(Encoding.ASCII.GetBytes("x"));

            // Only the data byte follows: no size frame after the peer said DONT.
            Assert.Equal(new byte[] { 0x78 }, await ReadExactlyAsync(stream, 1));
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task RepeatedWill_IsRefusedOnlyOnce()
    {
        const byte Echo = 1;
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient client = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            await using NetworkStream stream = client.GetStream();

            await stream.WriteAsync(new byte[] { Iac, Will, Echo, Iac, Will, Echo });
            Assert.Equal(new byte[] { Iac, Dont, Echo }, await ReadExactlyAsync(stream, 3));

            session.Write(Encoding.ASCII.GetBytes("x"));
            Assert.Equal(new byte[] { 0x78 }, await ReadExactlyAsync(stream, 1));
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Sb = 250;
    private const byte Se = 240;
    private const byte Naws = 31;

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;

        while (offset < length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, length - offset))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (bytesRead == 0)
            {
                break;
            }

            offset += bytesRead;
        }

        Assert.Equal(length, offset);
        return buffer;
    }
}
