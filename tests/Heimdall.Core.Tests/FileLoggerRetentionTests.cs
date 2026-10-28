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

using System.Globalization;
using Heimdall.Core.Logging;

namespace Heimdall.Core.Tests;

/// <summary>
/// The diagnostic log is bounded: a file stops growing at its size cap and continues in a
/// numbered file, and files not written for the retention period are deleted at start, without
/// touching anything else in the folder.
/// </summary>
[Collection("FileLogger")]
public sealed class FileLoggerRetentionTests : IDisposable
{
    private const int TinyCapBytes = 400;
    private const int RetentionDays = 30;
    private const int NeverFlushMs = 600_000;

    private readonly string _dir;

    public FileLoggerRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Heimdall.LogRetention." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        FileLogger.SetEnabled(true);
    }

    public void Dispose()
    {
        // Leave a singleton that points at nothing under the folder about to be deleted.
        FileLogger.Initialize(Path.Combine(Path.GetTempPath(), "Heimdall.LogRetention.idle"));
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void AFileAtItsCap_ContinuesInNumberedFiles_WithNoEntryLostOrReordered()
    {
        FileLogger.Initialize(_dir, NeverFlushMs, TinyCapBytes, RetentionDays);
        var messages = Enumerable.Range(0, 30).Select(i => $"entry-{i:D3}-" + new string('x', 40)).ToList();

        foreach (var message in messages)
        {
            FileLogger.Info(message);
            FileLogger.Flush();
        }

        var files = LogFiles();
        Assert.True(files.Count >= 3, $"30 entries of about 80 bytes must not fit in one {TinyCapBytes}-byte file; got {files.Count}");
        Assert.Contains(files, f => Path.GetFileName(f).EndsWith(".1.log", StringComparison.Ordinal));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= TinyCapBytes, $"{f} exceeds the cap"));

        var written = files
            .OrderBy(OverflowIndex)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Contains("entry-", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("entry-", StringComparison.Ordinal))..])
            .ToList();
        Assert.Equal(messages, written);
    }

    [Fact]
    public void ARestartOnTheSameDay_DoesNotAppendToAFileAlreadyAtItsCap()
    {
        FileLogger.Initialize(_dir, NeverFlushMs, TinyCapBytes, RetentionDays);
        FileLogger.Info("first " + new string('a', TinyCapBytes));
        FileLogger.Flush();
        var baseFile = Assert.Single(LogFiles());
        var lengthBefore = new FileInfo(baseFile).Length;

        FileLogger.Initialize(_dir, NeverFlushMs, TinyCapBytes, RetentionDays);
        FileLogger.Info("second " + new string('b', 40));
        FileLogger.Flush();

        Assert.Equal(lengthBefore, new FileInfo(baseFile).Length);
        Assert.Equal(2, LogFiles().Count);
    }

    [Fact]
    public void Initialize_DeletesLogsOlderThanTheRetention_AndKeepsEverythingElse()
    {
        var old = DateTime.UtcNow.AddDays(-(RetentionDays + 10));
        var recent = DateTime.UtcNow.AddDays(-1);
        var expiredDay = Seed("heimdall_20200101.log", old);
        var expiredOverflow = Seed("heimdall_20200101.2.log", old);
        var recentDay = Seed("heimdall_20260101.log", recent);

        var transcript = Seed("ssh_srv01_20200101_101010.log", old);
        var notALogOfOurs = Seed("heimdall_notes.log", old);
        var lookalike = Seed("heimdall_session_x.log", old);
        var eventLog = Seed("session-events.log", old);
        var inSubfolder = Seed(Path.Combine("sessions", "heimdall_20200101.log"), old);

        FileLogger.Initialize(_dir, NeverFlushMs, TinyCapBytes, RetentionDays);

        Assert.False(File.Exists(expiredDay));
        Assert.False(File.Exists(expiredOverflow));
        Assert.True(File.Exists(recentDay));
        Assert.True(File.Exists(transcript), "a session transcript is not a diagnostic log");
        Assert.True(File.Exists(notALogOfOurs), "on Windows the search pattern alone matches this name");
        Assert.True(File.Exists(lookalike), "eight characters after the prefix are not enough, they must be a day");
        Assert.True(File.Exists(eventLog));
        Assert.True(File.Exists(inSubfolder), "sub-folders are not searched");
    }

    [Fact]
    public void PurgeExpiredLogs_NeverDeletesTheActiveFile_WhateverItsAge()
    {
        var active = Seed("heimdall_20200101.log", DateTime.UtcNow.AddDays(-400));

        var deleted = FileLogger.PurgeExpiredLogs(_dir, RetentionDays, DateTime.UtcNow, active);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(active));
    }

    private string Seed(string relativePath, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private List<string> LogFiles() =>
        [.. Directory.GetFiles(_dir, "heimdall_*.log", SearchOption.TopDirectoryOnly)];

    /// <summary>0 for the day's own file, N for its <c>.N</c> continuation.</summary>
    private static int OverflowIndex(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        return parts.Length == 1 ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}
