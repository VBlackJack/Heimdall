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

namespace TwinShell.Persistence.Seeding;

/// <summary>
/// The hash of the seed directory as it stands now, and how it was obtained.
/// </summary>
/// <param name="Hash">The content hash of the seed files.</param>
/// <param name="Fingerprint">The metadata fingerprint taken just before the hash.</param>
/// <param name="ContentRead">
/// True when every seed file was read to produce <paramref name="Hash"/>; false when the stored
/// hash was reused because the fingerprint had not moved.
/// </param>
/// <param name="MatchesApplied">True when <paramref name="Hash"/> is the hash already applied, so no refresh is due.</param>
public sealed record SeedHashResolution(string Hash, string Fingerprint, bool ContentRead, bool MatchesApplied);

/// <summary>
/// Answers "what is the content hash of the seed directory?" without reading the seed files
/// unless their metadata says they may have changed.
/// </summary>
/// <remarks>
/// <para>Since the seed reached existing databases, every launch hashed all of the seed files
/// (527 of them) with synchronous reads. The metadata fingerprint is stored next to the applied
/// hash; while it is unchanged the stored hash is reused and no file is opened.</para>
/// <para>The fingerprint is taken BEFORE the content is read. A file changed in between makes
/// the stored fingerprint older than the hash it sits with, so the next launch sees a mismatch
/// and reads again; the opposite order could pair a new fingerprint with an old hash and hide a
/// change for good.</para>
/// <para>The metadata and content work runs on the thread pool, so the launch's UI thread is not
/// held while the disk answers.</para>
/// </remarks>
public sealed class SeedHashResolver
{
    private readonly SystemSeedRefresher _refresher;
    private readonly Func<string, byte[]> _readAllBytes;
    private readonly Func<string, SeedFileStamp> _stamp;

    /// <summary>Creates a resolver over the file system.</summary>
    public SeedHashResolver(SystemSeedRefresher refresher)
        : this(refresher, File.ReadAllBytes, SeedFileStamp.FromFileSystem)
    {
    }

    /// <summary>Creates a resolver with the file access given, which is how a test counts reads.</summary>
    public SeedHashResolver(
        SystemSeedRefresher refresher,
        Func<string, byte[]> readAllBytes,
        Func<string, SeedFileStamp> stamp)
    {
        ArgumentNullException.ThrowIfNull(refresher);
        ArgumentNullException.ThrowIfNull(readAllBytes);
        ArgumentNullException.ThrowIfNull(stamp);

        _refresher = refresher;
        _readAllBytes = readAllBytes;
        _stamp = stamp;
    }

    /// <summary>
    /// Returns the content hash of <paramref name="filePaths"/>, reading their content only when
    /// the fingerprint differs from the one stored with the applied hash.
    /// </summary>
    /// <remarks>
    /// When the content is read and turns out to be what was already applied (an installer that
    /// rewrote identical files with new times), the new fingerprint is stored so the next launch
    /// skips the read. When it differs, the refresh that follows stores the fingerprint together
    /// with the new hash.
    /// </remarks>
    public async Task<SeedHashResolution> ResolveAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        string fingerprint = await Task.Run(
            () => SeedCatalogueFingerprint.Compute(filePaths, _stamp),
            cancellationToken);

        string? appliedHash = await _refresher.ReadAppliedHashAsync(cancellationToken);
        string? appliedFingerprint = await _refresher.ReadAppliedFingerprintAsync(cancellationToken);
        if (appliedHash is not null
            && string.Equals(appliedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new SeedHashResolution(appliedHash, fingerprint, ContentRead: false, MatchesApplied: true);
        }

        string hash = await Task.Run(
            () => SeedCatalogueHash.Compute(filePaths, _readAllBytes),
            cancellationToken);

        bool matchesApplied = string.Equals(appliedHash, hash, StringComparison.Ordinal);
        if (matchesApplied)
        {
            await _refresher.RecordFingerprintAsync(fingerprint, cancellationToken);
        }

        return new SeedHashResolution(hash, fingerprint, ContentRead: true, matchesApplied);
    }
}
