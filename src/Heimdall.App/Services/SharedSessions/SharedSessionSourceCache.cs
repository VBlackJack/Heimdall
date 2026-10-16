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

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// The last copy of each shared source file that was read and loaded, kept in the user data
/// directory so the sessions stay reachable when the file is not.
/// </summary>
/// <remarks>
/// <para>One JSON document per source, <c>&lt;source id&gt;.json</c>, holding the raw bytes of the
/// file (Base64) and what is known about them: the source, the path they came from, when they
/// were read, and their SHA-256. Written atomically with the restrictive ACL the configuration
/// files get, in a directory created with the same ACL.</para>
/// <para>The cache holds bytes, never profiles. Whoever reads it runs them through the parser and
/// the sanitizer again, exactly as a file read from the share: a copy on this disk is trusted no
/// more than the file it copies. A document whose hash does not match its bytes is ignored.</para>
/// </remarks>
public sealed class SharedSessionSourceCache
{
    /// <summary>The folder of the cache under the data root.</summary>
    public const string DirectoryName = "SharedSessionCache";

    private const int FormatVersion = 1;
    private const string DocumentExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    /// <param name="directory">The folder the copies are kept in; created on first write.</param>
    public SharedSessionSourceCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>The cache in the data root of this process.</summary>
    public static SharedSessionSourceCache ForDataRoot() =>
        new(Path.Combine(ApplicationDataPathResolver.Resolve(), DirectoryName));

    /// <summary>The folder the copies are kept in.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Where the copy of <paramref name="sourceId"/> is kept.</summary>
    public string PathFor(string sourceId)
    {
        if (!SharedSessionIds.IsValidSourceId(sourceId))
        {
            throw new ArgumentException($"'{sourceId}' is not a valid shared source identifier.", nameof(sourceId));
        }

        return Path.Combine(_directory, sourceId + DocumentExtension);
    }

    /// <summary>
    /// Keeps <paramref name="content"/> as the copy of <paramref name="sourceId"/>. A failure is
    /// logged and swallowed: the sessions loaded from the file stand either way.
    /// </summary>
    public async Task StoreAsync(
        string sourceId,
        string originalPath,
        byte[] content,
        DateTime loadedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            EnsureDirectory();
            var document = new CacheDocument
            {
                FormatVersion = FormatVersion,
                SourceId = sourceId,
                OriginalPath = originalPath,
                LoadedAtUtc = loadedAtUtc,
                Sha256 = HashOf(content),
                ContentBase64 = Convert.ToBase64String(content),
            };
            string json = JsonSerializer.Serialize(document, JsonOptions);
            await SecureFileWriter.WriteAllTextAtomicAsync(PathFor(sourceId), json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            FileLogger.Warn($"Shared sessions: the offline copy of source {sourceId} could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// The copy of <paramref name="sourceId"/>, or null when there is none, it cannot be read, it
    /// names another source, or its bytes do not match its hash.
    /// </summary>
    public async Task<SharedSessionSourceCacheEntry?> TryReadAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(sourceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(json);
            if (document is null
                || document.FormatVersion != FormatVersion
                || !string.Equals(document.SourceId, sourceId, StringComparison.Ordinal)
                || string.IsNullOrEmpty(document.ContentBase64))
            {
                FileLogger.Warn($"Shared sessions: the offline copy of source {sourceId} is not one this build reads; ignored.");
                return null;
            }

            byte[] content = Convert.FromBase64String(document.ContentBase64);
            if (!string.Equals(HashOf(content), document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Warn($"Shared sessions: the offline copy of source {sourceId} does not match its hash; ignored.");
                return null;
            }

            return new SharedSessionSourceCacheEntry(
                sourceId,
                document.OriginalPath ?? string.Empty,
                DateTime.SpecifyKind(document.LoadedAtUtc, DateTimeKind.Utc),
                content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            FileLogger.Warn($"Shared sessions: the offline copy of source {sourceId} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deletes the copy of <paramref name="sourceId"/>, if any.</summary>
    public void Delete(string sourceId)
    {
        try
        {
            File.Delete(PathFor(sourceId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"Shared sessions: the offline copy of source {sourceId} could not be deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes every copy whose source is not in <paramref name="keptSourceIds"/>: a source removed
    /// from the settings takes its copy with it.
    /// </summary>
    public void DeleteAllExcept(IReadOnlySet<string> keptSourceIds)
    {
        ArgumentNullException.ThrowIfNull(keptSourceIds);
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_directory, "*" + DocumentExtension))
        {
            string sourceId = Path.GetFileNameWithoutExtension(path);
            if (SharedSessionIds.IsValidSourceId(sourceId) && !keptSourceIds.Contains(sourceId))
            {
                Delete(sourceId);
            }
        }
    }

    private void EnsureDirectory()
    {
        if (Directory.Exists(_directory))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(Path.GetFullPath(_directory));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        SecureFileWriter.CreateRestrictedDirectory(_directory);
    }

    private static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private sealed class CacheDocument
    {
        public int FormatVersion { get; set; }

        public string? SourceId { get; set; }

        public string? OriginalPath { get; set; }

        public DateTime LoadedAtUtc { get; set; }

        public string? Sha256 { get; set; }

        public string? ContentBase64 { get; set; }
    }
}

/// <summary>A copy read back from <see cref="SharedSessionSourceCache"/>.</summary>
/// <param name="SourceId">The source the copy belongs to.</param>
/// <param name="OriginalPath">The path the bytes were read from.</param>
/// <param name="LoadedAtUtc">When they were read.</param>
/// <param name="Content">The raw bytes of the file, unparsed.</param>
public sealed record SharedSessionSourceCacheEntry(
    string SourceId,
    string OriginalPath,
    DateTime LoadedAtUtc,
    byte[] Content);
