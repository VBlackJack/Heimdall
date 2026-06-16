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
}
