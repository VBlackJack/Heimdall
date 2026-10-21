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
using System.Text;

namespace TwinShell.Persistence.Seeding;

/// <summary>
/// A content hash of the shipped seed files: their names and bytes, in ordinal name order.
/// </summary>
/// <remarks>
/// The name is part of the hash so that renaming a file is a change, and each name and body
/// is length-prefixed so that moving bytes from one file to the next is one too.
/// </remarks>
public static class SeedCatalogueHash
{
    /// <summary>
    /// Returns the lower-case hexadecimal SHA-256 of <paramref name="filePaths"/>.
    /// </summary>
    /// <param name="filePaths">The seed files; their order does not matter.</param>
    public static string Compute(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string path in filePaths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            AppendBlock(hash, Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            AppendBlock(hash, File.ReadAllBytes(path));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendBlock(IncrementalHash hash, byte[] block)
    {
        hash.AppendData(BitConverter.GetBytes((long)block.Length));
        hash.AppendData(block);
    }
}
