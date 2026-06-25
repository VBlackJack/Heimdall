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
using FluentAssertions;
using Heimdall.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="SessionLogService"/>: header/footer, no-op safety, UTF-8/ANSI
/// handling across chunk boundaries, size rollover, cross-session concurrency, and disposal.
/// </summary>
public sealed class SessionLogServiceTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    private static string Localize(string key) => key switch
    {
        "SessionLogHeader" => "HEADER ts={0} proto={1} host={2} title={3}",
        "SessionLogFooter" => "FOOTER ts={0} dur={1}",
        "LogSessionLogRollover" => "ROLLOVER {0}",
        "LogSessionLogWriteError" => "WRITEERR {0}: {1}",
        _ => key
    };

    private string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HeimdallSessionLogTests",
            Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private SessionLogService CreateService(string rootDirectory, SessionLogOptions options)
    {
        return new SessionLogService(
            rootDirectory,
            options,
            NullLogger<SessionLogService>.Instance,
            Localize);
    }

    private static SessionLogContext Context(string sessionKey, DateTime startedUtc)
    {
        return new SessionLogContext(sessionKey, "SSH", "host.example", "host.example (admin)", startedUtc);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // Reads a transcript even while its writer still holds the file open (live tailing): the writer
    // keeps a write handle, so a reader must allow FileShare.ReadWrite to coexist with it.
    private static string ReadAllSharedText(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void StartSession_CreatesFileWithHeader_AndStopWritesFooterWithDuration()
    {
        string root = NewTempDirectory();
        using SessionLogService service = CreateService(root, SessionLogOptions.CreateDefault());

        DateTime started = DateTime.UtcNow.AddSeconds(-3);
        string? path = service.StartSession(Context("s1", started));

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();

        string afterStart = ReadAllSharedText(path!);
        afterStart.Should().Contain("HEADER ts=");
        afterStart.Should().Contain("proto=SSH");
        afterStart.Should().Contain("host=host.example");
        afterStart.Should().Contain("title=host.example (admin)");

        service.StopSession("s1");
        service.IsSessionActive("s1").Should().BeFalse();

        string afterStop = ReadAllSharedText(path!);
        afterStop.Should().Contain("FOOTER ts=");
        afterStop.Should().Contain("dur=");
    }

    [Fact]
    public void StartSession_RestartingSameKey_FootersThePreviousFile()
    {
        string root = NewTempDirectory();
        using SessionLogService service = CreateService(root, SessionLogOptions.CreateDefault());

        string? first = service.StartSession(Context("s1", DateTime.UtcNow.AddSeconds(-2)));
        first.Should().NotBeNull();
        service.WriteOutput("s1", Encoding.UTF8.GetBytes("first segment output"));

        // Restart semantics: a second StartSession for the same key (e.g. a reconnect) must close
        // the previous segment with a footer rather than abandoning it header-only.
        string? second = service.StartSession(Context("s1", DateTime.UtcNow));
        second.Should().NotBeNull();
        second.Should().NotBe(first);

        string firstContent = ReadAllSharedText(first!);
        firstContent.Should().Contain("HEADER ts=");
        firstContent.Should().Contain("FOOTER ts=");
        firstContent.Should().Contain("dur=");

        service.StopSession("s1");
    }

    [Fact]
    public void WriteOutput_BeforeStartOrUnknownKey_IsNoOp()
    {
        string root = NewTempDirectory();
        using SessionLogService service = CreateService(root, SessionLogOptions.CreateDefault());

        byte[] data = Encoding.UTF8.GetBytes("orphan output");

        Action act = () => service.WriteOutput("never-started", data);

        act.Should().NotThrow();
        service.IsSessionActive("never-started").Should().BeFalse();

        bool anyFiles = Directory.Exists(root) && Directory.EnumerateFiles(root).Any();
        anyFiles.Should().BeFalse();
    }

    [Fact]
    public void WriteOutput_DecodesUtf8AndStripsAnsi_AcrossSplitChunks()
    {
        string root = NewTempDirectory();
        using SessionLogService service = CreateService(root, SessionLogOptions.CreateDefault());

        string? path = service.StartSession(Context("s1", DateTime.UtcNow));
        path.Should().NotBeNull();

        // Logical stream: 'A' + ESC[31m (color, must be stripped) + 'é' (0xC3 0xA9) + 'B'.
        // Split so both the CSI escape and the multi-byte character straddle chunk boundaries.
        service.WriteOutput("s1", new byte[] { 0x41, 0x1B, 0x5B, 0x33 }); // 'A' ESC '[' '3'
        service.WriteOutput("s1", new byte[] { 0x31, 0x6D, 0xC3 });       // '1' 'm' + first é byte
        service.WriteOutput("s1", new byte[] { 0xA9, 0x42 });             // second é byte + 'B'

        service.StopSession("s1");

        string content = ReadAllSharedText(path!);
        content.Should().Contain("AéB");
        content.Should().NotContain("");
        content.Should().NotContain("[31m");
    }

    [Fact]
    public void WriteOutput_ExceedingSizeCap_RollsOverToContinuationFile()
    {
        string root = NewTempDirectory();
        // Tiny cap forces rollover after the header plus a few small chunks.
        using SessionLogService service = CreateService(root, new SessionLogOptions(MaxFileBytes: 64, FlushIntervalMs: 50));

        string? path = service.StartSession(Context("s1", DateTime.UtcNow));
        path.Should().NotBeNull();

        for (int i = 0; i < 20; i++)
        {
            service.WriteOutput("s1", Encoding.UTF8.GetBytes("0123456789"));
        }

        service.StopSession("s1");

        // The first overflow opens a ".1.log" continuation; a 64-byte cap spreads the chunks across
        // several files. Each chunk is written atomically (rollover only happens between entries), so
        // every "0123456789" payload stays intact and the total over all files must be exactly 20.
        string continuation = path![..^4] + ".1.log";
        File.Exists(continuation).Should().BeTrue();

        int payloadCount = Directory.GetFiles(root)
            .Sum(file => CountOccurrences(ReadAllSharedText(file), "0123456789"));
        payloadCount.Should().Be(20);
    }

    [Fact]
    public void WriteOutput_ParallelAcrossSessions_KeepsEachFileIsolated()
    {
        string root = NewTempDirectory();
        using SessionLogService service = CreateService(root, new SessionLogOptions(MaxFileBytes: 1024 * 1024, FlushIntervalMs: 50));

        string[] markers = ["AAAA", "BBBB", "CCCC", "DDDD"];
        Dictionary<int, string> paths = [];
        for (int i = 0; i < markers.Length; i++)
        {
            string? path = service.StartSession(Context($"s{i}", DateTime.UtcNow));
            path.Should().NotBeNull();
            paths[i] = path!;
        }

        Parallel.For(0, markers.Length, i =>
        {
            byte[] payload = Encoding.UTF8.GetBytes(markers[i]);
            for (int n = 0; n < 500; n++)
            {
                service.WriteOutput($"s{i}", payload);
            }
        });

        for (int i = 0; i < markers.Length; i++)
        {
            service.StopSession($"s{i}");
        }

        for (int i = 0; i < markers.Length; i++)
        {
            string content = ReadAllSharedText(paths[i]);
            content.Should().Contain(markers[i]);
            for (int j = 0; j < markers.Length; j++)
            {
                if (j != i)
                {
                    content.Should().NotContain(markers[j]);
                }
            }
        }
    }

    [Fact]
    public void Dispose_FlushesFootersAndReleasesAllFiles()
    {
        string root = NewTempDirectory();
        SessionLogService service = CreateService(root, new SessionLogOptions(MaxFileBytes: 1024 * 1024, FlushIntervalMs: 1000));

        string? path1 = service.StartSession(Context("s1", DateTime.UtcNow));
        string? path2 = service.StartSession(Context("s2", DateTime.UtcNow));
        path1.Should().NotBeNull();
        path2.Should().NotBeNull();

        service.WriteOutput("s1", Encoding.UTF8.GetBytes("session one body"));
        service.WriteOutput("s2", Encoding.UTF8.GetBytes("session two body"));

        service.Dispose();

        foreach (string path in new[] { path1!, path2! })
        {
            string content = ReadAllSharedText(path);
            content.Should().Contain("FOOTER ts=");

            // No open handle must remain: an exclusive open must succeed.
            Action exclusiveOpen = () =>
            {
                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            };
            exclusiveOpen.Should().NotThrow();
        }
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
