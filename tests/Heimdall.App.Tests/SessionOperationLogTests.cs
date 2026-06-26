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
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOperationLog"/> and <see cref="SessionOperationRecord"/>: NDJSON
/// shape per operation, byte/local-path coherence, rename destination, error and cancelled records,
/// "user@" host stripping, null-field omission, ts round-trip, and the shared
/// <see cref="NdjsonAppendLog{TRecord}"/> mechanics (lazy create, append, rollover, Dispose-flush)
/// exercised through the concrete operations log.
/// </summary>
public sealed class SessionOperationLogTests : IDisposable
{
    private const long LargeCap = 4L * 1024 * 1024;
    private const int FlushIntervalMs = 1000;

    private readonly List<string> _tempDirectories = [];

    private string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HeimdallSessionOperationLogTests",
            Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private static string OperationLogPath(string root) => Path.Combine(root, "session-operations.log");

    // Reads non-empty NDJSON lines from a file, tolerant of a writer still holding a handle.
    private static List<string> ReadLines(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        string content = reader.ReadToEnd();
        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static int CountLinesAcrossFiles(string root) =>
        Directory.GetFiles(root).Sum(file => ReadLines(file).Count);

    private static JsonElement SingleObject(string root) =>
        JsonDocument.Parse(ReadLines(OperationLogPath(root)).Single()).RootElement;

    [Fact]
    public void LogOperation_UploadSuccess_WritesBytesAndLocalPath()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Upload.Success(
                "SFTP", "host.example", "/srv/data/file.bin", @"C:\local\file.bin", bytes: 4096, durationMs: 1200));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("protocol").GetString().Should().Be("SFTP");
        obj.GetProperty("op").GetString().Should().Be("upload");
        obj.GetProperty("host").GetString().Should().Be("host.example");
        obj.GetProperty("remotePath").GetString().Should().Be("/srv/data/file.bin");
        obj.GetProperty("localPath").GetString().Should().Be(@"C:\local\file.bin");
        obj.GetProperty("bytes").GetInt64().Should().Be(4096);
        obj.GetProperty("durationMs").GetInt64().Should().Be(1200);
        obj.GetProperty("result").GetString().Should().Be("success");

        obj.TryGetProperty("remotePathTo", out _).Should().BeFalse();
        obj.TryGetProperty("errorCategory", out _).Should().BeFalse();

        // ts must be ISO-8601 round-trip UTC.
        string ts = obj.GetProperty("ts").GetString()!;
        DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            .Should().BeTrue();
        ts.Should().EndWith("Z");
    }

    [Fact]
    public void LogOperation_DownloadSuccess_WritesDownloadOpWithBytes()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Download.Success(
                "FTP", "10.0.0.5", "/pub/readme.txt", @"C:\dl\readme.txt", bytes: 128, durationMs: 50));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("op").GetString().Should().Be("download");
        obj.GetProperty("protocol").GetString().Should().Be("FTP");
        obj.GetProperty("bytes").GetInt64().Should().Be(128);
        obj.GetProperty("localPath").GetString().Should().Be(@"C:\dl\readme.txt");
        obj.GetProperty("result").GetString().Should().Be("success");
    }

    [Fact]
    public void LogOperation_MkdirSuccess_OmitsBytesAndLocalPath()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Mkdir.Success(
                "SFTP", "host.example", "/srv/data/newdir", durationMs: 10));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("op").GetString().Should().Be("mkdir");
        obj.GetProperty("remotePath").GetString().Should().Be("/srv/data/newdir");
        obj.GetProperty("result").GetString().Should().Be("success");
        obj.TryGetProperty("bytes", out _).Should().BeFalse();
        obj.TryGetProperty("localPath", out _).Should().BeFalse();
        obj.TryGetProperty("remotePathTo", out _).Should().BeFalse();
    }

    [Fact]
    public void LogOperation_DeleteSuccess_WritesDeleteOpPathOnly()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Delete.Success(
                "SFTP", "host.example", "/srv/data/old.bin", durationMs: 5));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("op").GetString().Should().Be("delete");
        obj.GetProperty("remotePath").GetString().Should().Be("/srv/data/old.bin");
        obj.TryGetProperty("bytes", out _).Should().BeFalse();
        obj.TryGetProperty("localPath", out _).Should().BeFalse();
    }

    [Fact]
    public void LogOperation_RenameSuccess_WritesRemotePathTo()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Rename.Success(
                "SFTP", "host.example", "/srv/data/a.txt", "/srv/data/b.txt", durationMs: 8));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("op").GetString().Should().Be("rename");
        obj.GetProperty("remotePath").GetString().Should().Be("/srv/data/a.txt");
        obj.GetProperty("remotePathTo").GetString().Should().Be("/srv/data/b.txt");
        obj.TryGetProperty("bytes", out _).Should().BeFalse();
        obj.TryGetProperty("localPath", out _).Should().BeFalse();
    }

    [Fact]
    public void LogOperation_Error_WritesErrorResultAndCategory()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Upload.Error(
                "SFTP", "host.example", "/srv/data/file.bin", @"C:\local\file.bin",
                durationMs: 300, errorCategory: "permission"));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("result").GetString().Should().Be("error");
        obj.GetProperty("errorCategory").GetString().Should().Be("permission");
        // A failed transfer carries no byte count.
        obj.TryGetProperty("bytes", out _).Should().BeFalse();
        obj.GetProperty("localPath").GetString().Should().Be(@"C:\local\file.bin");
    }

    [Fact]
    public void LogOperation_Cancelled_WritesCancelledResultWithoutErrorCategory()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Download.Cancelled(
                "SFTP", "host.example", "/srv/data/file.bin", @"C:\dl\file.bin", durationMs: 700));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("result").GetString().Should().Be("cancelled");
        obj.TryGetProperty("errorCategory", out _).Should().BeFalse();
        obj.TryGetProperty("bytes", out _).Should().BeFalse();
    }

    [Fact]
    public void LogOperation_StripsUserPrefixFromHost()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Delete.Success(
                "SFTP", "admin@10.0.0.5", "/srv/data/old.bin", durationMs: 5));
        }

        SingleObject(root).GetProperty("host").GetString().Should().Be("10.0.0.5");
    }

    [Fact]
    public void LogOperation_Privileged_WritesPrivilegedTrue()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Mkdir.Success(
                "SFTP", "host.example", "/srv/data/newdir", durationMs: 10, privileged: true));
        }

        JsonElement obj = SingleObject(root);
        obj.GetProperty("privileged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void LogOperation_NonPrivileged_OmitsPrivilegedKey()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            // privileged defaults to false for the existing (decorator) callers.
            log.LogOperation(SessionOperationRecord.Mkdir.Success(
                "SFTP", "host.example", "/srv/data/newdir", durationMs: 10));
        }

        SingleObject(root).TryGetProperty("privileged", out _).Should().BeFalse();
    }

    [Fact]
    public void Record_PrivilegedDefault_IsFalse()
    {
        SessionOperationRecord record = SessionOperationRecord.Delete.Success(
            "SFTP", "host.example", "/srv/x", durationMs: 1);

        record.Privileged.Should().BeFalse();
    }

    [Fact]
    public void Record_NegativeDuration_ClampsToZero()
    {
        SessionOperationRecord record = SessionOperationRecord.Mkdir.Success(
            "SFTP", "host.example", "/srv/x", durationMs: -42);

        record.DurationMs.Should().Be(0);
    }

    [Fact]
    public void Record_ErrorFactory_RequiresNonEmptyCategory()
    {
        Action act = () => SessionOperationRecord.Delete.Error(
            "SFTP", "host.example", "/srv/x", durationMs: 1, errorCategory: "   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LogOperation_AppendsAcrossMultipleOperations()
    {
        string root = NewTempDirectory();
        using (SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs))
        {
            log.LogOperation(SessionOperationRecord.Mkdir.Success("SFTP", "h", "/a", 1));
            log.LogOperation(SessionOperationRecord.Upload.Success("SFTP", "h", "/a/f", @"C:\f", 10, 2));
            log.LogOperation(SessionOperationRecord.Delete.Success("SFTP", "h", "/a/f", 3));
        }

        List<string> lines = ReadLines(OperationLogPath(root));
        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(line => line.StartsWith("{") && line.EndsWith("}"));
    }

    [Fact]
    public void LogOperation_BeforeAnyWrite_CreatesNoFile()
    {
        string root = NewTempDirectory();
        // The sink is a dumb writer; when no caller logs, nothing is materialized on disk.
        using SessionOperationLog log = new SessionOperationLog(root, LargeCap, FlushIntervalMs);

        (Directory.Exists(root) && Directory.EnumerateFiles(root).Any()).Should().BeFalse();
    }

    [Fact]
    public void LogOperation_PastSizeCap_RollsOverToContinuationFile()
    {
        string root = NewTempDirectory();
        // Tiny cap forces rollover after a couple of operation lines.
        SessionOperationLog log = new SessionOperationLog(root, maxBytes: 64, flushIntervalMs: 50);

        const int operationCount = 20;
        for (int i = 0; i < operationCount; i++)
        {
            log.LogOperation(SessionOperationRecord.Mkdir.Success("SFTP", $"host-{i}", $"/dir-{i}", 1));
        }

        log.Dispose();

        string continuation = OperationLogPath(root)[..^4] + ".1.log";
        File.Exists(continuation).Should().BeTrue();

        // Every line is written atomically (rollover only happens between lines), so the total over
        // all files must equal the number of operations, with none lost or duplicated at the seam.
        CountLinesAcrossFiles(root).Should().Be(operationCount);
    }

    [Fact]
    public void Dispose_FlushesPendingOperations_NoLoss()
    {
        string root = NewTempDirectory();
        // A long flush interval guarantees the timer does not fire before Dispose: the only way these
        // operations reach disk is the synchronous drain inside Dispose.
        SessionOperationLog log = new SessionOperationLog(root, LargeCap, flushIntervalMs: 600_000);

        const int operationCount = 50;
        for (int i = 0; i < operationCount; i++)
        {
            log.LogOperation(SessionOperationRecord.Mkdir.Success("SFTP", $"host-{i}", $"/dir-{i}", 1));
        }

        log.Dispose();

        ReadLines(OperationLogPath(root)).Should().HaveCount(operationCount);

        // No open handle must remain after Dispose: an exclusive open must succeed.
        Action exclusiveOpen = () =>
        {
            using FileStream stream = new FileStream(
                OperationLogPath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };
        exclusiveOpen.Should().NotThrow();
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp artifacts.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of temp artifacts.
            }
        }
    }
}
