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

using System.Security.Cryptography;

namespace Heimdall.Core.Updates;

/// <summary>
/// Computes and verifies SHA-256 digests of update assets.
/// </summary>
public static class Sha256Verifier
{
    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 digest of a stream.
    /// </summary>
    public static string ComputeHex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that the file at <paramref name="filePath"/> matches the expected
    /// hexadecimal SHA-256 digest, comparing case-insensitively.
    /// </summary>
    public static bool Verify(string filePath, string expectedHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var actual = ComputeHex(stream);
        return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
