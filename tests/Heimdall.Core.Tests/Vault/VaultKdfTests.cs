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
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultKdfTests
{
    // Known-answer vectors from the Argon2 reference implementation
    // (phc-winner-argon2, src/test.c), Argon2id, version 0x13 (v=19),
    // parallelism p=1, output length 32 bytes, no secret and no associated data
    // (matching the VaultKdf.DeriveKey surface). Columns: password, salt,
    // memoryKib, iterations, expected hex.
    public static TheoryData<string, string, int, int, string> ReferenceVectors => new()
    {
        { "password", "somesalt", 65536, 2, "09316115d5cf24ed5a15a31a3ba326e5cf32edc24702987c02b6566f61913cf7" },
        { "password", "somesalt", 256, 2, "9dfeb910e80bad0311fee20f9c0e2b12c17987b4cac90c2ef54d5b3021c68bfe" },
        { "password", "somesalt", 65536, 1, "f6a5adc1ba723dddef9b5ac1d464e180fcd9dffc9d1cbf76cca2fed795d9ca98" },
        { "password", "somesalt", 65536, 4, "9025d48e68ef7395cca9079da4c4ec3affb3c8911fe4f86d1a2520856f63172c" },
        { "differentpassword", "somesalt", 65536, 2, "0b84d652cf6b0c4beaef0dfe278ba6a80df6696281d7e0d2891b817d8c458fde" },
        { "password", "diffsalt", 65536, 2, "bdf32b05ccc42eb15d58fd19b1f856b113da1e9a5874fdcc544308565aa8141c" },
    };

    [Theory]
    [MemberData(nameof(ReferenceVectors))]
    public void DeriveKey_ReferenceVectors_MatchPublishedOutput(
        string password, string salt, int memoryKib, int iterations, string expectedHex)
    {
        var passwordBytes = Encoding.ASCII.GetBytes(password);
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        var parameters = new Argon2idParameters(memoryKib, iterations, Parallelism: 1);

        var derived = VaultKdf.DeriveKey(passwordBytes, saltBytes, parameters, outLen: 32);

        Assert.Equal(expectedHex, Convert.ToHexString(derived).ToLowerInvariant());
    }

    [Fact]
    public void DeriveKey_RespectsRequestedOutputLength()
    {
        var derived = VaultKdf.DeriveKey(
            Encoding.ASCII.GetBytes("password"),
            Encoding.ASCII.GetBytes("somesalt"),
            Argon2idParameters.Recommended,
            outLen: 64);

        Assert.Equal(64, derived.Length);
    }

    [Fact]
    public void DeriveKey_SaltShorterThanMinimum_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultKdf.DeriveKey(
            Encoding.ASCII.GetBytes("password"),
            new byte[VaultKdf.MinSaltLengthBytes - 1],
            Argon2idParameters.Recommended));
    }

    [Fact]
    public void DeriveKey_NonPositiveOutputLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultKdf.DeriveKey(
            Encoding.ASCII.GetBytes("password"),
            Encoding.ASCII.GetBytes("somesalt"),
            Argon2idParameters.Recommended,
            outLen: 0));
    }

    [Fact]
    public void DeriveKey_InvalidParameters_Throws()
    {
        var invalid = new Argon2idParameters(MemoryKib: 0, Iterations: 0, Parallelism: 0);

        Assert.Throws<ArgumentException>(() => VaultKdf.DeriveKey(
            Encoding.ASCII.GetBytes("password"),
            Encoding.ASCII.GetBytes("somesalt"),
            invalid));
    }

    [Fact]
    public void GenerateSalt_DefaultLength_Is16Bytes()
    {
        var salt = VaultKdf.GenerateSalt();

        Assert.Equal(VaultKdf.DefaultSaltLengthBytes, salt.Length);
    }

    [Fact]
    public void GenerateSalt_ProducesDistinctValues()
    {
        var first = VaultKdf.GenerateSalt();
        var second = VaultKdf.GenerateSalt();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GenerateSalt_BelowMinimum_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultKdf.GenerateSalt(VaultKdf.MinSaltLengthBytes - 1));
    }

    [Fact]
    public void RecommendedParameters_MatchAnssiOwaspBaseline()
    {
        var p = Argon2idParameters.Recommended;

        Assert.Equal(65536, p.MemoryKib);
        Assert.Equal(3, p.Iterations);
        Assert.Equal(1, p.Parallelism);
        Assert.True(p.IsValid);
    }
}
