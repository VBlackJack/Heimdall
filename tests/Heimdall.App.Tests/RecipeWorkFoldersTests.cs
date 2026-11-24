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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>The run folders: a fresh empty one per run under the runs root, which is outside the data directory, removed afterwards.</summary>
public sealed class RecipeWorkFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-recipe-workfolders-tests", Guid.NewGuid().ToString("N"));

    public RecipeWorkFoldersTests() => Directory.CreateDirectory(_root);

    private string RunsRoot => Path.Combine(_root, "runs");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    [Fact]
    public void EachFolderIsNewEmptyAbsoluteAndUnderTheRunsRoot()
    {
        RecipeWorkFolders folders = new(RunsRoot);

        string first = folders.Create();
        string second = folders.Create();

        Assert.NotEqual(first, second);
        Assert.True(Path.IsPathFullyQualified(first));
        Assert.True(Directory.Exists(first));
        Assert.Empty(Directory.EnumerateFileSystemEntries(first));
        Assert.Equal(Path.GetFullPath(RunsRoot), Path.GetDirectoryName(first));
    }

    [Fact]
    public void TheProductionRootIsUnderTheUserTemporaryFolderAndNotUnderTheDataDirectory()
    {
        string data = Path.Combine(_root, "data");

        string root = Path.GetFullPath(RecipeWorkFolders.DefaultRoot(data));

        string temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(Path.Combine(temp, AppConstants.RecipeRunsTempDirectoryName) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.StartsWith(Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoDataDirectoriesGiveTwoRootsAndTheSameOneGivesTheSameRootInAnyCase()
    {
        string one = RecipeWorkFolders.DefaultRoot(Path.Combine(_root, "dataone"));
        string two = RecipeWorkFolders.DefaultRoot(Path.Combine(_root, "datatwo"));

        Assert.NotEqual(one, two);
        Assert.Equal(one, RecipeWorkFolders.DefaultRoot(Path.Combine(_root, "dataone")));
        Assert.Equal(one, RecipeWorkFolders.DefaultRoot(Path.Combine(_root, "DATAONE")));
        Assert.Equal(one, RecipeWorkFolders.DefaultRoot(Path.Combine(_root, "dataone") + Path.DirectorySeparatorChar));
        Assert.Equal(16, Path.GetFileName(one).Length);
    }

    [Fact]
    public void CleaningTheStaleFoldersOfOneRootLeavesALiveFolderOfAnotherRoot()
    {
        RecipeWorkFolders mine = new(Path.Combine(_root, "mine"));
        RecipeWorkFolders other = new(Path.Combine(_root, "other"));
        string live = other.Create();
        mine.Create();

        mine.CleanStale();

        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public void DeleteRemovesAFolderWithItsContent()
    {
        RecipeWorkFolders folders = new(RunsRoot);
        string folder = folders.Create();
        File.WriteAllText(Path.Combine(folder, "left.txt"), "x");

        folders.Delete(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteNeverTouchesAnythingOutsideTheRunsFolder()
    {
        RecipeWorkFolders folders = new(RunsRoot);
        string outside = Path.Combine(_root, "important");
        Directory.CreateDirectory(outside);
        string deeper = Path.Combine(folders.Create(), "sub");
        Directory.CreateDirectory(deeper);

        folders.Delete(outside);
        folders.Delete(RunsRoot);
        folders.Delete(deeper);
        folders.Delete(string.Empty);
        folders.Delete(Path.Combine(_root, "recipe-runs", "..", "important"));

        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(deeper));
        Assert.True(Directory.Exists(RunsRoot));
    }

    [Fact]
    public void DeletingAFolderThatIsGoneIsNotAnError()
    {
        RecipeWorkFolders folders = new(RunsRoot);
        string folder = folders.Create();
        folders.Delete(folder);

        folders.Delete(folder);
    }

    [Fact]
    public void CleanStaleRemovesWhatACrashLeftBehindAndKeepsTheRunsFolder()
    {
        RecipeWorkFolders folders = new(RunsRoot);
        string one = folders.Create();
        string two = folders.Create();
        File.WriteAllText(Path.Combine(two, "x.txt"), "x");

        folders.CleanStale();

        Assert.False(Directory.Exists(one));
        Assert.False(Directory.Exists(two));
        Assert.True(Directory.Exists(RunsRoot));
    }

    [Fact]
    public void CleanStaleWithoutARunsFolderDoesNothing()
    {
        new RecipeWorkFolders(Path.Combine(_root, "never-used")).CleanStale();

        Assert.False(Directory.Exists(Path.Combine(_root, "never-used")));
    }
}
