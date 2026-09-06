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

using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

/// <summary>
/// What an update attempt leaves behind is swept at startup, on the same terms as
/// the editor working directories: by age, best effort, never throwing.
/// </summary>
public sealed class UpdateStagingSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "heimdall-update-sweeper",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SweepStaging_RemovesAgedDirectoriesAndKeepsRecentOnes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string aged = CreateStaging("Heimdall_2026.090601_aaaa", now - UpdateStagingSweeper.StagingMinimumAge - TimeSpan.FromMinutes(1));
        string agedToo = CreateStaging("Heimdall_2026.090602_bbbb", now - TimeSpan.FromDays(3));
        string fresh = CreateStaging("Heimdall_2026.090603_cccc", now - TimeSpan.FromMinutes(5));
        string unrelated = Path.Combine(_root, "not-a-staging-directory");
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(unrelated, (now - TimeSpan.FromDays(9)).UtcDateTime);

        int removed = UpdateStagingSweeper.SweepStaging(_root, now);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(aged));
        Assert.False(Directory.Exists(agedToo));
        Assert.True(Directory.Exists(fresh), "a directory younger than the margin may be a download in flight");
        Assert.True(Directory.Exists(unrelated), "only staging directories are swept");
    }

    [Fact]
    public void SweepStaging_MissingRoot_ReturnsZero()
    {
        Assert.Equal(0, UpdateStagingSweeper.SweepStaging(Path.Combine(_root, "absent"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SweepStaging_DirectoryStillHeld_IsLeftAlone()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string held = CreateStaging("Heimdall_2026.090601_held", now - TimeSpan.FromDays(3));
        string installer = Path.Combine(held, "installer.exe");
        File.WriteAllText(installer, "x");

        using (new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            int removed = UpdateStagingSweeper.SweepStaging(_root, now);
            Assert.Equal(0, removed);
        }

        Assert.True(File.Exists(installer), "a held installer is one a relauncher may be running");
    }

    [Fact]
    public void SweepRelaunchLogs_RemovesLogsPastRetentionOnly()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_root);
        string old = CreateFile("Heimdall_relaunch_20260101-000000.log", now - UpdateStagingSweeper.RelaunchLogRetention - TimeSpan.FromDays(1));
        string recent = CreateFile("Heimdall_relaunch_20260906-120000.log", now - TimeSpan.FromDays(1));
        string appLog = CreateFile("heimdall_20260101.log", now - TimeSpan.FromDays(400));

        int removed = UpdateStagingSweeper.SweepRelaunchLogs(_root, now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(appLog), "the application log is not the sweeper's to touch");
    }

    private string CreateStaging(string name, DateTimeOffset lastWrite)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Heimdall_Setup.exe"), "payload");
        Directory.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private string CreateFile(string name, DateTimeOffset lastWrite)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "log");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }
}
