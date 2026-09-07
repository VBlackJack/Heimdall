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

using System.Text;
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class Sha256VerifierTests
{
    [Fact]
    public void ComputeHex_ReturnsKnownVector()
    {
        // SHA-256 of "abc" (NIST test vector).
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(expected, Sha256Verifier.ComputeHex(stream));
    }

    [Fact]
    public void Verify_CorrectHash_Accepted()
    {
        var buffer = Encoding.ASCII.GetBytes("heimdall-update-payload");
        var expected = Sha256Verifier.ComputeHex(new MemoryStream(buffer));

        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(filePath, buffer);

            Assert.True(Sha256Verifier.Verify(filePath, expected));
            // Case-insensitive comparison.
            Assert.True(Sha256Verifier.Verify(filePath, expected.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Verify_WrongHash_Rejected()
    {
        var buffer = Encoding.ASCII.GetBytes("heimdall-update-payload");
        var wrong = new string('0', 64);

        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(filePath, buffer);

            Assert.False(Sha256Verifier.Verify(filePath, wrong));
            Assert.False(Sha256Verifier.Verify(filePath, string.Empty));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Verify_UnreadableFile_Throws_RatherThanReportingAMismatch()
    {
        // False has one meaning and it must keep it: the file was read and its digest is
        // not the expected one. The single production caller reports false as tampering,
        // so an antivirus holding the freshly written installer would otherwise be shown
        // to the user as a corrupted download. This pins the contract the doc comment
        // states: a file that cannot be read throws, and the caller decides what that is.
        string missing = Path.Combine(Path.GetTempPath(), $"heimdall-absent-{Guid.NewGuid():N}.bin");
        string expected = new('a', 64);

        Assert.Throws<FileNotFoundException>(() => Sha256Verifier.Verify(missing, expected));
    }

    [Fact]
    public void ComputeHex_ReadsTheWholeStreamFromWhereItStands()
    {
        // ComputeHex moved to SHA256.HashData, which reads the stream in bounded chunks
        // instead of materialising it. The digest must not change with that, and it must
        // still consume from the current position, since Verify hands it a fresh handle.
        byte[] payload = Encoding.ASCII.GetBytes("heimdall-update-payload");
        using var stream = new MemoryStream(payload);

        string first = Sha256Verifier.ComputeHex(stream);

        stream.Position = 0;
        string second = Sha256Verifier.ComputeHex(stream);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, first.ToLowerInvariant());
        Assert.Equal(payload.Length, stream.Position);
    }
}
