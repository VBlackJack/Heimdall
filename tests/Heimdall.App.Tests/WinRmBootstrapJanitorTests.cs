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
using Heimdall.App.Services.WinRm;

namespace Heimdall.App.Tests;

public sealed class WinRmBootstrapJanitorTests
{
    private static readonly DateTime FixedUtcNow =
        new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SweepStale_DeletesScriptOlderThanMaxAge()
    {
        string stalePath = @"C:\Temp\heimdall_winrm_stale.ps1";
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [stalePath] = FixedUtcNow.AddMinutes(-61)
        };
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
            new string[] { stalePath },
            lastWriteTimes,
            deleted);

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(1, result.Removed);
        Assert.Null(result.NextEligibleUtc);
        Assert.Equal(new string[] { stalePath }, deleted);
    }

    [Fact]
    public void SweepStale_KeepsScriptNewerThanMaxAge()
    {
        string freshPath = @"C:\Temp\heimdall_winrm_fresh.ps1";
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [freshPath] = FixedUtcNow.AddMinutes(-5)
        };
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
            new string[] { freshPath },
            lastWriteTimes,
            deleted);

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(0, result.Removed);
        Assert.Equal(FixedUtcNow.AddMinutes(55), result.NextEligibleUtc);
        Assert.Empty(deleted);
    }

    [Fact]
    public void SweepStale_ReturnsRemovedCount()
    {
        string stalePath1 = @"C:\Temp\heimdall_winrm_stale_1.ps1";
        string stalePath2 = @"C:\Temp\heimdall_winrm_stale_2.ps1";
        string freshPath = @"C:\Temp\heimdall_winrm_fresh.ps1";
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [stalePath1] = FixedUtcNow.AddMinutes(-61),
            [stalePath2] = FixedUtcNow.AddHours(-2),
            [freshPath] = FixedUtcNow.AddMinutes(-10)
        };
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
            new string[] { stalePath1, stalePath2, freshPath },
            lastWriteTimes,
            deleted);

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(2, result.Removed);
        Assert.Equal(FixedUtcNow.AddMinutes(50), result.NextEligibleUtc);
        Assert.Equal(new string[] { stalePath1, stalePath2 }, deleted);
    }

    [Fact]
    public void SweepStale_UsesEarliestEligibilityAcrossYoungScripts()
    {
        const string olderFreshPath = @"C:\Temp\heimdall_winrm_older_fresh.ps1";
        const string newerFreshPath = @"C:\Temp\heimdall_winrm_newer_fresh.ps1";
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [olderFreshPath] = FixedUtcNow.AddMinutes(-15),
            [newerFreshPath] = FixedUtcNow.AddMinutes(-5)
        };
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
            new string[] { newerFreshPath, olderFreshPath },
            lastWriteTimes,
            deleted);

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(0, result.Removed);
        Assert.Equal(FixedUtcNow.AddMinutes(45), result.NextEligibleUtc);
        Assert.Empty(deleted);
    }

    [Fact]
    public void SweepStale_WhenEnumerateThrowsIOException_ReturnsZero()
    {
        WinRmBootstrapJanitor janitor = new WinRmBootstrapJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateScripts: _ => throw new IOException("enumerate failed"),
            getLastWriteTimeUtc: _ => FixedUtcNow.AddHours(-2),
            delete: _ => throw new InvalidOperationException("should not delete"),
            utcNow: () => FixedUtcNow,
            maxAge: TimeSpan.FromHours(1));

        Exception? exception = Record.Exception(() =>
        {
            SensitiveFileJanitorSweepResult result = janitor.SweepStale();
            Assert.Equal(0, result.Removed);
            Assert.Null(result.NextEligibleUtc);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void SweepStale_WhenDeleteThrowsIOException_ContinuesWithOtherFiles()
    {
        string failingPath = @"C:\Temp\heimdall_winrm_locked.ps1";
        string deletedPath = @"C:\Temp\heimdall_winrm_deleted.ps1";
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>
        {
            [failingPath] = FixedUtcNow.AddHours(-2),
            [deletedPath] = FixedUtcNow.AddHours(-2)
        };
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
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

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(1, result.Removed);
        Assert.Null(result.NextEligibleUtc);
        Assert.Equal(new string[] { deletedPath }, deleted);
    }

    [Fact]
    public void SweepStale_WhenDirectoryIsEmpty_ReturnsZero()
    {
        Dictionary<string, DateTime> lastWriteTimes = new Dictionary<string, DateTime>();
        List<string> deleted = new List<string>();
        WinRmBootstrapJanitor janitor = CreateJanitor(
            Array.Empty<string>(),
            lastWriteTimes,
            deleted);

        SensitiveFileJanitorSweepResult result = janitor.SweepStale();

        Assert.Equal(0, result.Removed);
        Assert.Null(result.NextEligibleUtc);
        Assert.Empty(deleted);
    }

    private static WinRmBootstrapJanitor CreateJanitor(
        IEnumerable<string> candidates,
        IReadOnlyDictionary<string, DateTime> lastWriteTimes,
        List<string> deleted,
        Action<string>? delete = null)
    {
        return new WinRmBootstrapJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateScripts: _ => candidates,
            getLastWriteTimeUtc: path => lastWriteTimes[path],
            delete: delete ?? (path => deleted.Add(path)),
            utcNow: () => FixedUtcNow,
            maxAge: TimeSpan.FromHours(1));
    }
}
