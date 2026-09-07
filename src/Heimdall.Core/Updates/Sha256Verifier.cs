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
    /// <remarks>
    /// <see cref="SHA256.HashData(Stream)"/> rather than an owned
    /// <see cref="SHA256"/> instance: it reads the stream in bounded chunks instead of
    /// materialising the file, and there is no algorithm object to dispose or to leak
    /// when a caller forgets.
    /// </remarks>
    public static string ComputeHex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The lowercase hexadecimal SHA-256 of a stream, abandonable while it reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A-21, and the half of it that survived measurement. The synchronous overload was never a
    /// responsiveness problem: the largest installer this project ships is 284,834,865 bytes and
    /// hashes in 397 to 674 milliseconds, and no caller runs it on the UI thread. What it could
    /// not do is stop. <c>DownloadVerifiedAsync</c> accepts a cancellation token and used to
    /// ignore it for the whole verification pass, so a user who cancelled there waited for the
    /// file to be read anyway - and the read, unlike the hashing, is only as fast as the volume
    /// under it.
    /// </para>
    /// <para>
    /// The synchronous overload stays and is not deprecated. Hashing a short in-memory string,
    /// which is what <c>UpdateInstaller</c> does to the relauncher script, has nothing to
    /// abandon and would pay a state machine for nothing.
    /// </para>
    /// </remarks>
    public static async Task<string> ComputeHexAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that the file at <paramref name="filePath"/> matches the expected
    /// hexadecimal SHA-256 digest, comparing case-insensitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False means one thing only: the file was read and its digest is not the expected
    /// one. It never means the file could not be read.
    /// </para>
    /// <para>
    /// A file that cannot be opened throws, deliberately, and the contract is pinned by
    /// a test. Swallowing that into false would turn "locked, missing or unreadable"
    /// into "the digest does not match", and the one production caller reports a
    /// mismatch as tampering: an antivirus holding the freshly written installer would
    /// be reported to the user as a corrupted download. Callers that must survive an
    /// unreadable file catch <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/> around this call, as
    /// <c>UpdateInstaller</c> does.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be accessed.</exception>
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
