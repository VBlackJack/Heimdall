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

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes what a folder creation request resolves to, including the two answers the
/// creation path used to be unable to tell apart.
/// </summary>
/// <remarks>
/// Creating a folder whose name was taken did nothing at all: the dialog closed, no entry
/// was written, and nothing was said. The outcome exists so the caller can say which of the
/// two happened, and the collision rule is the rename path's own predicate rather than a
/// second one written here.
/// </remarks>
public sealed class MainWindowFolderCreationTests
{
    [Fact]
    public void NameHeldByAnEmptyFolder_IsADuplicate()
    {
        (MainWindow.FolderCreationOutcome outcome, string path) =
            MainWindow.ResolveFolderCreation("Prod", ["Prod"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Duplicate, outcome);
        Assert.Equal("Prod", path);
    }

    // The stored empty-folder list holds empty folders only. A folder that exists because
    // sessions carry its path is absent from it, so an equality test against that list alone
    // calls the name free and appends a second entry for a folder already on screen.
    [Fact]
    public void NameHeldOnlyByASessionPath_IsADuplicate()
    {
        (MainWindow.FolderCreationOutcome outcome, _) =
            MainWindow.ResolveFolderCreation("Prod", ["Prod/Web"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Duplicate, outcome);
    }

    // Segment boundaries decide the answer: "Production" is a different folder, not a
    // descendant, so a prefix test would refuse a name that is free.
    [Fact]
    public void NameSharingAPrefixWithAnotherFolder_IsCreated()
    {
        (MainWindow.FolderCreationOutcome outcome, _) =
            MainWindow.ResolveFolderCreation("Prod", ["Production", "Prod2"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Created, outcome);
    }

    [Fact]
    public void NameDifferingOnlyByCase_IsADuplicate()
    {
        (MainWindow.FolderCreationOutcome outcome, _) =
            MainWindow.ResolveFolderCreation("PROD", ["prod"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Duplicate, outcome);
    }

    [Fact]
    public void FreeName_IsCreatedTrimmed()
    {
        (MainWindow.FolderCreationOutcome outcome, string path) =
            MainWindow.ResolveFolderCreation("  Staging  ", ["Prod"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Created, outcome);
        Assert.Equal("Staging", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNameEntered_CreatesNothing(string? name)
    {
        (MainWindow.FolderCreationOutcome outcome, string path) =
            MainWindow.ResolveFolderCreation(name, ["Prod"]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Cancelled, outcome);
        Assert.Equal(string.Empty, path);
    }

    // Blank entries reach the resolver from server profiles that carry no folder at all.
    [Fact]
    public void BlankExistingPaths_AreNotFolders()
    {
        (MainWindow.FolderCreationOutcome outcome, _) =
            MainWindow.ResolveFolderCreation("Prod", [null, string.Empty, "   "]);

        Assert.Equal(MainWindow.FolderCreationOutcome.Created, outcome);
    }
}
