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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class AtomicLocalFileTests : IDisposable
{
    private readonly string _testDirectory;

    public AtomicLocalFileTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Heimdall",
            "AtomicLocalFileTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    /// <remarks>
    /// On Windows an antivirus scanner, the search indexer or a preview handler holds a
    /// freshly written file, or the one about to be replaced, for a moment; a single
    /// unretried move turned that moment into a discarded download.
    /// </remarks>
    [Fact]
    public async Task Commit_RetriesWhileTheDestinationIsBrieflyHeld()
    {
        string finalPath = Path.Combine(_testDirectory, "held.txt");
        string tempPath = AtomicLocalFile.CreateTempPath(finalPath);
        File.WriteAllText(finalPath, "original");
        File.WriteAllText(tempPath, "downloaded");

        using (FileStream hold = new(finalPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Task commit = Task.Run(() => AtomicLocalFile.Commit(tempPath, finalPath));
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            hold.Dispose();
            await commit;
        }

        Assert.Equal("downloaded", File.ReadAllText(finalPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void Commit_ReplacesExistingFinalFile_WithTempContent()
    {
        string finalPath = Path.Combine(_testDirectory, "download.txt");
        string tempPath = AtomicLocalFile.CreateTempPath(finalPath);
        File.WriteAllText(finalPath, "original");
        File.WriteAllText(tempPath, "replacement");

        AtomicLocalFile.Commit(tempPath, finalPath);

        Assert.Equal("replacement", File.ReadAllText(finalPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void Rollback_PreservesExistingFinalFileAndRemovesTempFile()
    {
        string finalPath = Path.Combine(_testDirectory, "download.txt");
        string tempPath = AtomicLocalFile.CreateTempPath(finalPath);
        File.WriteAllText(finalPath, "original");
        File.WriteAllText(tempPath, "partial");

        AtomicLocalFile.Rollback(tempPath);

        Assert.Equal("original", File.ReadAllText(finalPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void CreateTempPath_ReturnsPathInSameDirectoryAsFinalPath()
    {
        string finalPath = Path.Combine(_testDirectory, "download.txt");

        string tempPath = AtomicLocalFile.CreateTempPath(finalPath);

        Assert.Equal(
            Path.GetFullPath(_testDirectory),
            Path.GetFullPath(Path.GetDirectoryName(tempPath) ?? string.Empty));
    }

    [Fact]
    public void Rollback_MissingTempPath_DoesNotThrow()
    {
        string tempPath = Path.Combine(_testDirectory, "missing.part");

        Exception? exception = Record.Exception(() => AtomicLocalFile.Rollback(tempPath));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch
        {
            // Test cleanup should not mask assertions.
        }
    }
}
