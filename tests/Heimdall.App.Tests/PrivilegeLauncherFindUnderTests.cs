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
using Heimdall.App.Views.Tools;

namespace Heimdall.App.Tests;

public sealed class PrivilegeLauncherFindUnderTests : IDisposable
{
    private const string SubDir = "PowerShell";
    private const string FileName = "pwsh.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "heimdall-findunder-" + Guid.NewGuid().ToString("N"));

    public PrivilegeLauncherFindUnderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void FindUnder_FindsFileOneFolderBelowTheSubDirectory()
    {
        var expected = Touch(SubDir, "7", FileName);

        Assert.Equal(expected, PrivilegeLauncherView.FindUnder(_root, SubDir, FileName));
    }

    [Fact]
    public void FindUnder_IgnoresFileTwoFoldersBelowTheSubDirectory()
    {
        Touch(SubDir, "a", "b", FileName);

        Assert.Null(PrivilegeLauncherView.FindUnder(_root, SubDir, FileName));
    }

    [Fact]
    public void FindUnder_ReturnsNullForMissingRoot()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Null(PrivilegeLauncherView.FindUnder(missing, SubDir, FileName));
    }
}
