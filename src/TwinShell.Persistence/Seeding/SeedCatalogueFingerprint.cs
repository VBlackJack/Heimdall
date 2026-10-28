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
/// A hash of the seed files' metadata: their names, sizes and last write times, in ordinal name
/// order. It never reads a file's content.
/// </summary>
/// <remarks>
/// <para>It is the cheap question "can the seed have changed since the content hash was last
/// taken?". The content hash (<see cref="SeedCatalogueHash"/>) reads every byte of every seed
/// file, which on a cold disk after an update or a reboot took seconds on the UI thread of the
/// launch. The fingerprint touches only directory entries.</para>
/// <para>It is a heuristic of the same kind as a build tool's timestamp check: a file rewritten
/// with the same size and the same last write time is not seen. Installers and checkouts stamp a
/// changed file with a new time, so a shipped change moves it.</para>
/// </remarks>
public static class SeedCatalogueFingerprint
{
    /// <summary>
    /// Returns the lower-case hexadecimal SHA-256 of the metadata of <paramref name="filePaths"/>.
    /// </summary>
    /// <param name="filePaths">The seed files; their order does not matter.</param>
    public static string Compute(IEnumerable<string> filePaths)
        => Compute(filePaths, SeedFileStamp.FromFileSystem);

    /// <summary>
    /// Returns the fingerprint of <paramref name="filePaths"/> using <paramref name="stamp"/> to
    /// read each file's metadata.
    /// </summary>
    public static string Compute(IEnumerable<string> filePaths, Func<string, SeedFileStamp> stamp)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(stamp);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string path in filePaths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            hash.AppendData(BitConverter.GetBytes((long)name.Length));
            hash.AppendData(name);

            SeedFileStamp fileStamp = stamp(path);
            hash.AppendData(BitConverter.GetBytes(fileStamp.Length));
            hash.AppendData(BitConverter.GetBytes(fileStamp.LastWriteUtcTicks));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
