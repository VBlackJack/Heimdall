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
using System.Text;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Writes a note file atomically: the content is staged in a temp file next to the target, then
/// published over it, so a crash, a full disk or a failing write leaves the previous content
/// intact instead of a truncated file.
/// </summary>
/// <remarks>
/// Deliberately not a restricted-ACL writer: a note lives in a folder the user may share or
/// sync, so the staged file keeps the folder's inherited permissions, and replacing an existing
/// note keeps that note's own permissions. The temp name never ends in ".md", so a note list
/// never shows a half-written file. When the file system cannot replace in place (a reader
/// without delete sharing, a redirector that lacks ReplaceFile), the publish degrades to an
/// overwriting move and, as a last resort, to an in-place write, rather than failing a save
/// that a plain write would have completed.
/// </remarks>
internal sealed class NoteFileWriter
{
    /// <summary>Suffix of every staged file; also the tail of the orphan-sweep pattern.</summary>
    internal const string TempSuffix = ".tmp";

    /// <summary>Search pattern that matches a staged note file left behind by a crash.</summary>
    internal const string OrphanSearchPattern = "*.md.????????" + TempSuffix;

    private const string TempIdFormat = "x8";
    private const int StreamBufferSize = 4096;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Waits between publish attempts. An antivirus scanner, the search indexer or a sync client
    /// holds a note for a moment on Windows; after the last wait the fallbacks take over.
    /// </summary>
    private static readonly TimeSpan[] PublishRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(450),
    ];

    private readonly Action<string>? _afterStaging;

    public NoteFileWriter()
    {
    }

    /// <summary>
    /// Takes a hook run with the temp path once the content is staged and before it is published,
    /// so a test can fail the write at the point where a non-atomic writer would already have
    /// destroyed the target.
    /// </summary>
    internal NoteFileWriter(Action<string> afterStaging)
    {
        ArgumentNullException.ThrowIfNull(afterStaging);
        _afterStaging = afterStaging;
    }

    public void Write(string targetPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string tempPath = CreateTempPath(targetPath);
        byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
        bool staged = false;
        try
        {
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, FileOptions.None))
            {
                staged = true;
                MarkStaged(tempPath);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            _afterStaging?.Invoke(tempPath);
            UnmarkStaged(tempPath);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Publish(tempPath, targetPath);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (attempt >= PublishRetryDelays.Length)
                    {
                        PublishWithFallback(tempPath, targetPath, bytes, ex);
                        return;
                    }

                    Thread.Sleep(PublishRetryDelays[attempt]);
                }
            }
        }
        finally
        {
            // Only a file this call created: a name collision must not delete someone else's.
            if (staged)
            {
                DeleteQuietly(tempPath);
            }
        }
    }

    public async Task WriteAsync(string targetPath, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string tempPath = CreateTempPath(targetPath);
        byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
        bool staged = false;
        try
        {
            await using (var stream = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, FileOptions.Asynchronous))
            {
                staged = true;
                MarkStaged(tempPath);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _afterStaging?.Invoke(tempPath);
            UnmarkStaged(tempPath);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Publish(tempPath, targetPath);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (attempt >= PublishRetryDelays.Length)
                    {
                        PublishWithFallback(tempPath, targetPath, bytes, ex);
                        return;
                    }

                    await Task.Delay(PublishRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (staged)
            {
                DeleteQuietly(tempPath);
            }
        }
    }

    private static void Publish(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, targetPath);
    }

    /// <summary>
    /// The atomic publish kept failing: first an overwriting move, then an in-place write. The
    /// last one is not atomic, but it is what a plain write did before, and a save that a plain
    /// write completes must not fail because the file system lacks ReplaceFile.
    /// </summary>
    private static void PublishWithFallback(string tempPath, string targetPath, byte[] bytes, Exception cause)
    {
        FileLogger.Warn(
            $"Atomic replace of note '{targetPath}' failed ({cause.Message}); moving over it instead.");
        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
            return;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            FileLogger.Warn(
                $"Overwriting move of note '{targetPath}' failed ({ex.Message}); writing it in place instead.");
        }

        // FileShare.Read: readers that opened the note for reading stay allowed.
        using var stream = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.Read, StreamBufferSize, FileOptions.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void MarkStaged(string tempPath)
        => File.SetAttributes(tempPath, FileAttributes.Hidden | FileAttributes.Temporary);

    /// <summary>The published note must not inherit the staging attributes.</summary>
    private static void UnmarkStaged(string tempPath)
        => File.SetAttributes(tempPath, FileAttributes.Normal);

    private static bool IsTransient(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static string CreateTempPath(string targetPath)
    {
        string directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        string tempName = $"{Path.GetFileName(targetPath)}.{Random.Shared.Next().ToString(TempIdFormat)}{TempSuffix}";
        return Path.Combine(directory, tempName);
    }

    private static void DeleteQuietly(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Could not remove the staged note file '{tempPath}': {ex.Message}");
        }
    }
}
