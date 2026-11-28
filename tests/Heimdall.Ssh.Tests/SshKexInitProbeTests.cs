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

using System.Buffers.Binary;
using System.IO;
using System.Text;
using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class SshKexInitProbeTests
{
    [Fact]
    public void ParseKexInitPayload_ValidPayload_ExtractsAllEightNameLists()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(20); // SSH_MSG_KEXINIT
        ms.Write(new byte[16]); // cookie

        WriteNameList(ms, "mlkem768x25519-sha256,curve25519-sha256,kex-strict-s-v00@openssh.com");
        WriteNameList(ms, "ssh-ed25519,rsa-sha2-512");
        WriteNameList(ms, "chacha20-poly1305@openssh.com,aes256-gcm@openssh.com");
        WriteNameList(ms, "chacha20-poly1305@openssh.com,aes256-gcm@openssh.com");
        WriteNameList(ms, "hmac-sha2-512-etm@openssh.com");
        WriteNameList(ms, "hmac-sha2-512-etm@openssh.com");
        WriteNameList(ms, "none,zlib@openssh.com");
        WriteNameList(ms, "none,zlib@openssh.com");

        var snap = SshKexInitProbe.ParseKexInitPayload("srv.local", 22, "SSH-2.0-OpenSSH_9.9", ms.ToArray());

        Assert.Equal("srv.local", snap.Host);
        Assert.Equal(22, snap.Port);
        Assert.Equal("SSH-2.0-OpenSSH_9.9", snap.ServerBanner);
        Assert.Equal(["mlkem768x25519-sha256", "curve25519-sha256", "kex-strict-s-v00@openssh.com"], snap.KeyExchange);
        Assert.Equal(["ssh-ed25519", "rsa-sha2-512"], snap.HostKeys);
        Assert.Equal(["hmac-sha2-512-etm@openssh.com"], snap.MacC2S);
        Assert.Equal(["none", "zlib@openssh.com"], snap.CompressionS2C);
    }

    [Fact]
    public void ParseKexInitPayload_WrongMessageTypeOrTruncated_ThrowsInvalidData()
    {
        byte[] wrongType = new byte[20];
        wrongType[0] = 21;
        Assert.Throws<InvalidDataException>(() =>
            SshKexInitProbe.ParseKexInitPayload("h", 22, "SSH-2.0-Test", wrongType));

        Assert.Throws<InvalidDataException>(() =>
            SshKexInitProbe.ParseKexInitPayload("h", 22, "SSH-2.0-Test", [20, 1, 2, 3]));
    }

    private static void WriteNameList(Stream stream, string csv)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(csv);
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)bytes.Length);
        stream.Write(lenBuf);
        stream.Write(bytes);
    }
}
