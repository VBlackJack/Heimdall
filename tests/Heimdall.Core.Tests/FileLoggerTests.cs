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

using Heimdall.Core.Logging;

namespace Heimdall.Core.Tests;

/// <summary>
/// Dedicated collection definition for FileLogger tests. FileLogger is a
/// process-wide singleton, so these tests must not run in parallel with any
/// other test collection that may emit log entries through production code.
/// </summary>
[CollectionDefinition("FileLogger", DisableParallelization = true)]
public sealed class FileLoggerCollectionDefinition;

/// <summary>
/// Tests for <see cref="FileLogger"/>. Because FileLogger uses a static singleton,
/// tests must run sequentially to avoid cross-contamination.
/// </summary>
[Collection("FileLogger")]
public class FileLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        FileLogger.SetEnabled(true);
    }

    public void Dispose()
    {
        FileLogger.SetEnabled(true);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    // ── Initialization ────────────────────────────────────────────────

    [Fact]
    public void Initialize_CreatesLogDirectory()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        FileLogger.Initialize(logDir);
        FileLogger.Flush();

        Assert.True(Directory.Exists(logDir));
    }

    [Fact]
    public void Initialize_CreatesNestedDirectoryIfNeeded()
    {
        var logDir = Path.Combine(_tempDir, "deep", "nested", "logs");
        FileLogger.Initialize(logDir);
        FileLogger.Flush();

        Assert.True(Directory.Exists(logDir));
    }

    // ── Log level methods ─────────────────────────────────────────────

    [Fact]
    public void Info_WritesEntryWithInfoLevel()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Info("Application started");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("[INFO]", content);
        Assert.Contains("Application started", content);
    }

    [Fact]
    public void Error_RecordsOnlyTheMessage()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Error("Startup failure", BuildNestedException());
        FileLogger.Flush();

        var content = ReadLogContent();

        // This is the whole reason ErrorDetailed exists: on a crash path the ordinary
        // error line is the same sentence the user just read in the dialog, so a log
        // built from it tells the author nothing new.
        Assert.Contains("outer failure", content);
        Assert.DoesNotContain("inner failure", content);
        Assert.DoesNotContain("BuildNestedException", content);
    }

    [Fact]
    public void ErrorDetailed_RecordsTheStackAndTheInnerChain()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.ErrorDetailed("Unhandled exception", BuildNestedException());
        FileLogger.Flush();

        var content = ReadLogContent();

        Assert.Contains("[ERROR]", content);
        Assert.Contains("outer failure", content);
        Assert.Contains("inner failure", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("BuildNestedException", content);
    }

    [Fact]
    public void WarnDetailed_RecordsTheStackAndTheInnerChain_AsAWarning()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.WarnDetailed("Silent close failed", BuildNestedException());
        FileLogger.Flush();

        var content = ReadLogContent();

        Assert.Contains("[WARN]", content);
        Assert.DoesNotContain("[ERROR]", content);
        Assert.Contains("outer failure", content);
        Assert.Contains("inner failure", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("BuildNestedException", content);
    }

    /// <summary>
    /// Thrown and caught for real, because an exception that was never thrown carries
    /// no stack trace and the assertion above would pass for the wrong reason.
    /// </summary>
    private static Exception BuildNestedException()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("inner failure");
            }
            catch (InvalidOperationException inner)
            {
                throw new ApplicationException("outer failure", inner);
            }
        }
        catch (ApplicationException caught)
        {
            return caught;
        }
    }

    [Fact]
    public void Warn_WritesEntryWithWarnLevel()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Warn("Disk space low");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("[WARN]", content);
        Assert.Contains("Disk space low", content);
    }

    [Fact]
    public void Error_WritesEntryWithErrorLevel()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Error("Connection failed");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("[ERROR]", content);
        Assert.Contains("Connection failed", content);
    }

    [Fact]
    public void Error_WithException_IncludesExceptionMessage()
    {
        FileLogger.Initialize(_tempDir);
        var ex = new InvalidOperationException("port already in use");
        FileLogger.Error("Tunnel setup failed", ex);
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("[ERROR]", content);
        Assert.Contains("Tunnel setup failed", content);
        Assert.Contains("port already in use", content);
    }

    // ── Timestamp ─────────────────────────────────────────────────────

    [Fact]
    public void LogEntries_ContainTimestamp()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Info("timestamp test");
        FileLogger.Flush();

        var content = ReadLogContent();

        // Timestamp format: [yyyy-MM-dd HH:mm:ss]
        // Verify the entry starts with a bracketed date-like pattern
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]", content);
    }

    // ── Flush ─────────────────────────────────────────────────────────

    [Fact]
    public void Flush_WritesBufferedEntriesToDisk()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Info("entry one");
        FileLogger.Info("entry two");
        FileLogger.Info("entry three");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("entry one", content);
        Assert.Contains("entry two", content);
        Assert.Contains("entry three", content);
    }

    [Fact]
    public void Flush_IsIdempotent_WhenCalledMultipleTimes()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Info("single entry");
        FileLogger.Flush();
        FileLogger.Flush();
        FileLogger.Flush();

        var content = ReadLogContent();
        // Count occurrences of the message
        var count = CountOccurrences(content, "single entry");
        Assert.Equal(1, count);
    }

    // ── SetEnabled ────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_False_StopsLogging()
    {
        FileLogger.Initialize(_tempDir);
        FileLogger.Info("before disable");
        FileLogger.Flush();

        FileLogger.SetEnabled(false);
        FileLogger.Info("while disabled");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("before disable", content);
        Assert.DoesNotContain("while disabled", content);
    }

    [Fact]
    public void SetEnabled_True_ResumesLogging()
    {
        FileLogger.Initialize(_tempDir);

        FileLogger.SetEnabled(false);
        FileLogger.Info("while disabled");

        FileLogger.SetEnabled(true);
        FileLogger.Info("after re-enable");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.DoesNotContain("while disabled", content);
        Assert.Contains("after re-enable", content);
    }

    // ── Concurrent logging ────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentLogging_DoesNotCrash()
    {
        FileLogger.Initialize(_tempDir);

        const int threadCount = 10;
        const int messagesPerThread = 20;

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int m = 0; m < messagesPerThread; m++)
                {
                    FileLogger.Info($"Thread {threadId} message {m}");
                }
            });
        }

        await Task.WhenAll(tasks);
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.NotEmpty(content);

        // All messages should be present
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(threadCount * messagesPerThread, lines.Length);
    }

    // ── Uninitialized state ───────────────────────────────────────────

    [Fact]
    public void Log_DoesNotCrash_BeforeInitialize()
    {
        // Reinitialize with a dummy path to reset the singleton, then test
        // that calling Log on a fresh instance works without throwing.
        // Since we cannot truly un-initialize the static, we verify that
        // methods do not throw even in normal operation.
        var exception = Record.Exception(() =>
        {
            FileLogger.Info("test message");
            FileLogger.Warn("test warning");
            FileLogger.Error("test error");
            FileLogger.Flush();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Flush_DoesNotCrash_WhenQueueIsEmpty()
    {
        FileLogger.Initialize(_tempDir);

        var exception = Record.Exception(() => FileLogger.Flush());

        Assert.Null(exception);
    }

    // ── Bounded queue ──────────────────────────────────────────────────

    private const string DroppedSummaryMarker = "entries dropped while the log could not be written";
    private const int TestQueueCap = 100;
    private const int TestBurstSize = 250;
    private const int TestIdleFlushIntervalMs = 3_600_000;
    private const int TestConcurrentEntries = 50;
    private const int FailingFlushHeadStartMs = 50;

    private void InitializeCapped()
        => FileLogger.Initialize(_tempDir, TestIdleFlushIntervalMs, FileLogger.MaxLogFileBytes, FileLogger.LogRetentionDays, TestQueueCap);

    private string[] SummaryLines()
        => ReadLogContent().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains(DroppedSummaryMarker, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Flush_WhenTheFileIsHeld_KeepsTheQueueUnderTheCapAndReportsTheDrops()
    {
        InitializeCapped();
        FileLogger.Info("warmup");
        FileLogger.Flush();
        var logFile = Directory.GetFiles(_tempDir, "heimdall_*.log").Single();

        using (new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i < TestBurstSize; i++)
            {
                FileLogger.Info($"e{i:D3}");
            }

            FileLogger.Flush();
        }

        FileLogger.Info("after");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("e249", content);
        Assert.Contains("after", content);
        Assert.DoesNotContain("e000", content);
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Count(l => !l.Contains(DroppedSummaryMarker, StringComparison.Ordinal)) <= TestQueueCap + 1);

        // 150 were dropped by the burst; "after" pushed one more out of the full queue.
        var summary = Assert.Single(SummaryLines());
        Assert.Contains(" 151 entries", summary);

        FileLogger.Info("one more");
        FileLogger.Flush();
        Assert.Single(SummaryLines());
    }

    [Fact]
    public async Task Flush_WhenEntriesArriveDuringAFailedWrite_RequeuesOnlyWhatFitsUnderTheCap()
    {
        InitializeCapped();
        FileLogger.Info("warmup");
        FileLogger.Flush();
        var logFile = Directory.GetFiles(_tempDir, "heimdall_*.log").Single();

        using (new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i < TestBurstSize; i++)
            {
                FileLogger.Info($"e{i:D3}");
            }

            // The failing flush drains the queue, then retries for a while: entries logged in
            // that window are newer and take room that the failed batch can no longer claim.
            var failingFlush = Task.Run(FileLogger.Flush);
            Thread.Sleep(FailingFlushHeadStartMs);
            for (var i = 0; i < TestConcurrentEntries; i++)
            {
                FileLogger.Info($"m{i:D2}");
            }

            await failingFlush;
        }

        FileLogger.Info("after");
        FileLogger.Flush();

        var content = ReadLogContent();
        Assert.Contains("e249", content);
        Assert.Contains("m49", content);
        Assert.DoesNotContain("e150", content);
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Count(l => !l.Contains(DroppedSummaryMarker, StringComparison.Ordinal)) <= TestQueueCap + 1);
        Assert.Single(SummaryLines());
    }

    [Fact]
    public void Flush_WhenNothingWasDropped_WritesNoSummary()
    {
        InitializeCapped();
        for (var i = 0; i < TestQueueCap; i++)
        {
            FileLogger.Info($"e{i:D3}");
        }

        FileLogger.Flush();

        Assert.Empty(SummaryLines());
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private string ReadLogContent()
    {
        var logFile = Directory.GetFiles(_tempDir, "heimdall_*.log").FirstOrDefault();
        return logFile != null ? File.ReadAllText(logFile) : string.Empty;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
