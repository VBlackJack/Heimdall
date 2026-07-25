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
using Heimdall.App.Services;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Tests;

public sealed class PlinkPasswordFileJanitorTests
{
    private static readonly DateTime FixedUtcNow =
        new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Janitor_SweepsTunnelOrphan_WithUnifiedPrefix()
    {
        AssertDefaultSweepDeletes(PlinkPasswordFileNaming.Prefix);
    }

    [Fact]
    public void Janitor_SweepsLegacyPrefix_Too()
    {
        AssertDefaultSweepDeletes(PlinkPasswordFileNaming.LegacyTunnelPrefix);
    }

    [Fact]
    public void SweepStale_DeletesPasswordFileOlderThanMaxAge()
    {
        string stalePath = GetTestPasswordFilePath("stale");
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [stalePath] = FixedUtcNow.AddMinutes(-61)
        };
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = CreateJanitor(
            new string[] { stalePath },
            lastWriteTimes,
            deleted);

        int removed = janitor.SweepStale();

        Assert.Equal(1, removed);
        Assert.Equal(new string[] { stalePath }, deleted);
    }

    [Fact]
    public void SweepStale_KeepsPasswordFileNewerThanMaxAge()
    {
        string freshPath = GetTestPasswordFilePath("fresh");
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [freshPath] = FixedUtcNow.AddMinutes(-5)
        };
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = CreateJanitor(
            new string[] { freshPath },
            lastWriteTimes,
            deleted);

        int removed = janitor.SweepStale();

        Assert.Equal(0, removed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void SweepStale_ReturnsRemovedCount()
    {
        string stalePath1 = GetTestPasswordFilePath("stale_1");
        string stalePath2 = GetTestPasswordFilePath("stale_2");
        string freshPath = GetTestPasswordFilePath("fresh");
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [stalePath1] = FixedUtcNow.AddMinutes(-61),
            [stalePath2] = FixedUtcNow.AddHours(-2),
            [freshPath] = FixedUtcNow.AddMinutes(-10)
        };
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = CreateJanitor(
            new string[] { stalePath1, stalePath2, freshPath },
            lastWriteTimes,
            deleted);

        int removed = janitor.SweepStale();

        Assert.Equal(2, removed);
        Assert.Equal(new string[] { stalePath1, stalePath2 }, deleted);
    }

    [Fact]
    public void SweepStale_WhenEnumerateThrowsIOException_ReturnsZero()
    {
        PlinkPasswordFileJanitor janitor = new PlinkPasswordFileJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateFiles: _ => throw new IOException("enumerate failed"),
            getLastWriteTimeUtc: _ => FixedUtcNow.AddHours(-2),
            delete: _ => throw new InvalidOperationException("should not delete"),
            utcNow: () => FixedUtcNow,
            maxAge: TimeSpan.FromHours(1));

        Exception? exception = Record.Exception(() =>
        {
            int removed = janitor.SweepStale();
            Assert.Equal(0, removed);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void SweepStale_WhenDeleteThrowsIOException_ContinuesWithOtherFiles()
    {
        string failingPath = GetTestPasswordFilePath("locked");
        string deletedPath = GetTestPasswordFilePath("deleted");
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [failingPath] = FixedUtcNow.AddHours(-2),
            [deletedPath] = FixedUtcNow.AddHours(-2)
        };
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = CreateJanitor(
            new string[] { failingPath, deletedPath },
            lastWriteTimes,
            deleted,
            path =>
            {
                if (path == failingPath)
                {
                    throw new IOException("locked");
                }

                deleted.Add(path);
            });

        int removed = janitor.SweepStale();

        Assert.Equal(1, removed);
        Assert.Equal(new string[] { deletedPath }, deleted);
    }

    [Fact]
    public void SweepStale_WhenDirectoryIsEmpty_ReturnsZero()
    {
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>();
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = CreateJanitor(
            Array.Empty<string>(),
            lastWriteTimes,
            deleted);

        int removed = janitor.SweepStale();

        Assert.Equal(0, removed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void SweepStale_SkipsPasswordFileNotOwnedByCurrentUser()
    {
        string stalePath = GetTestPasswordFilePath("other_owner");
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [stalePath] = FixedUtcNow.AddHours(-2)
        };
        List<string> deleted = new List<string>();
        PlinkPasswordFileJanitor janitor = new PlinkPasswordFileJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateFiles: _ => new string[] { stalePath },
            getLastWriteTimeUtc: path => lastWriteTimes[path],
            isOwnedByCurrentUser: _ => false,
            delete: path => deleted.Add(path),
            utcNow: () => FixedUtcNow,
            maxAge: TimeSpan.FromHours(1));

        int removed = janitor.SweepStale();

        Assert.Equal(0, removed);
        Assert.Empty(deleted);
    }

    private static PlinkPasswordFileJanitor CreateJanitor(
        IEnumerable<string> candidates,
        IReadOnlyDictionary<string, DateTime> lastWriteTimes,
        List<string> deleted,
        Action<string>? delete = null)
    {
        return new PlinkPasswordFileJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateFiles: _ => candidates,
            getLastWriteTimeUtc: path => lastWriteTimes[path],
            isOwnedByCurrentUser: _ => true,
            delete: delete ?? (path => deleted.Add(path)),
            utcNow: () => FixedUtcNow,
            maxAge: TimeSpan.FromHours(1));
    }

    private static void AssertDefaultSweepDeletes(string prefix)
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall_plink_janitor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string orphanPath = Path.Combine(testDirectory, $"{prefix}{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(orphanPath, "secret");
            File.SetLastWriteTimeUtc(orphanPath, FixedUtcNow.AddHours(-2));
            // Ownership is stubbed so this test stays about enumeration, prefix
            // matching and deletion. The production ownership check compares the
            // file owner SID against the current user SID, and an elevated host
            // creates temp files owned by BUILTIN\Administrators, which would make
            // the sweep skip the orphan for reasons unrelated to what is asserted
            // here. SweepStale_SkipsPasswordFileNotOwnedByCurrentUser covers the
            // skip path.
            var janitor = new PlinkPasswordFileJanitor(
                tempDirectory: () => testDirectory,
                isOwnedByCurrentUser: _ => true,
                utcNow: () => FixedUtcNow,
                maxAge: TimeSpan.FromHours(1));

            int removed = janitor.SweepStale();

            Assert.Equal(1, removed);
            Assert.False(File.Exists(orphanPath));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static string GetTestPasswordFilePath(string suffix)
        => $@"C:\Temp\{PlinkPasswordFileNaming.Prefix}{suffix}";
}
