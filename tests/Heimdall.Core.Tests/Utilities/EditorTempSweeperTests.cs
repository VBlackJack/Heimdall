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
using Heimdall.Core.Utilities;

namespace Heimdall.Core.Tests;

/// <summary>
/// What the editor temp sweeper removes, and - more importantly - what it leaves.
/// </summary>
public sealed class EditorTempSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "heimdall-bl0089-sweeper",
        Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sweep_DirectoryOlderThanTheThreshold_IsRemoved()
    {
        string stale = Aged("stale", EditorTempSweeper.MinimumAge + TimeSpan.FromHours(1));

        int removed = EditorTempSweeper.Sweep(_root, Now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void Sweep_RecentDirectory_IsLeftAlone()
    {
        string recent = Aged("recent", EditorTempSweeper.MinimumAge - TimeSpan.FromHours(1));

        int removed = EditorTempSweeper.Sweep(_root, Now);

        // There is no single-instance lock in this application, so a young directory may
        // belong to a second window open right now. Deleting it would take a file out
        // from under a live editor - a worse defect than the leak this fixes.
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Sweep_RemovesTheDirectoryContentsToo()
    {
        string stale = Aged("stale", EditorTempSweeper.MinimumAge + TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(stale, "notes.conf"), "edited but never uploaded");
        Directory.SetLastWriteTimeUtc(
            stale,
            (Now - EditorTempSweeper.MinimumAge - TimeSpan.FromHours(1)).UtcDateTime);

        Assert.Equal(1, EditorTempSweeper.Sweep(_root, Now));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void Sweep_MissingRoot_IsNotAnError()
    {
        // The ordinary case on a machine that has never opened a remote file.
        Assert.Equal(0, EditorTempSweeper.Sweep(Path.Combine(_root, "never-created"), Now));
    }

    [Fact]
    public void Sweep_LeavesFilesThatSitDirectlyInTheRoot()
    {
        Directory.CreateDirectory(_root);
        string stray = Path.Combine(_root, "stray.txt");
        File.WriteAllText(stray, "not ours to judge");

        Assert.Equal(0, EditorTempSweeper.Sweep(_root, Now));
        Assert.True(File.Exists(stray), "the sweeper only ever considers directories");
    }

    private string Aged(string name, TimeSpan age)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
