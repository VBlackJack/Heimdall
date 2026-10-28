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
using System.Security.AccessControl;
using Heimdall.Core.Utilities;

namespace Heimdall.Core.Tests;

/// <summary>
/// The startup sweep of the cross-server paste staging folder, and the way the folder is created.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CrossServerPasteStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "heimdall-crosspaste-staging",
        Guid.NewGuid().ToString("N"),
        "cross-server-paste");

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sweep_FileOlderThanTheLimit_IsRemoved_AndAFreshOneIsKept()
    {
        string stale = Aged("stale", CrossServerPasteStaging.MaxAge + TimeSpan.FromHours(1));
        string fresh = Aged("fresh", CrossServerPasteStaging.MaxAge - TimeSpan.FromHours(1));

        int removed = CrossServerPasteStaging.Sweep(_root, Now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Sweep_MissingFolder_IsNotAnError()
    {
        Assert.Equal(0, CrossServerPasteStaging.Sweep(Path.Combine(_root, "never-created"), Now));
    }

    [Fact]
    public void Sweep_StaleFileHeldOpen_IsLeftAloneWithoutThrowing()
    {
        string held = Aged("held", CrossServerPasteStaging.MaxAge + TimeSpan.FromHours(1));
        using var open = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        int removed = CrossServerPasteStaging.Sweep(_root, Now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(held));
    }

    [Fact]
    public void CreateTempPath_CreatesTheFolderWithoutInheritedAccess()
    {
        string path = CrossServerPasteStaging.CreateTempPath(_root);

        Assert.True(Directory.Exists(_root));
        Assert.Equal(_root, Path.GetDirectoryName(path));
        DirectorySecurity security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "the staging folder must not inherit the temp folder's ACL");
    }

    [Fact]
    public void CreateTempPath_FolderAlreadyThere_IsReused()
    {
        string first = CrossServerPasteStaging.CreateTempPath(_root);
        string second = CrossServerPasteStaging.CreateTempPath(_root);

        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
    }

    private string Aged(string name, TimeSpan age)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "staged remote file content");
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
