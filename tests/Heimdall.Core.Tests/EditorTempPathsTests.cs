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
/// One factory for both editors' working directories. The inline editor used to create its own
/// directory without the restrictive ACL the external editor applied, so a root-owned file read
/// through sudo was staged in a directory every account able to read the temporary folder could
/// read.
/// </summary>
public sealed class EditorTempPathsTests
{
    [Fact]
    public void CreateWorkingDirectory_CreatesAUniqueDirectoryUnderTheRoot()
    {
        string first = EditorTempPaths.CreateWorkingDirectory();
        string second = EditorTempPaths.CreateWorkingDirectory();

        try
        {
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            Assert.NotEqual(first, second);
            Assert.Equal(EditorTempPaths.Root, Path.GetDirectoryName(first));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void CreateWorkingDirectory_OnWindows_ProtectsTheDirectoryFromInheritedAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = EditorTempPaths.CreateWorkingDirectory();
        try
        {
            DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl();

            Assert.True(security.AreAccessRulesProtected, "the directory must not inherit the temp folder's DACL");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
