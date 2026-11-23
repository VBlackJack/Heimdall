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
using System.Security.Cryptography;
using System.Text;
using Heimdall.App.Services;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The recipe store: importing is the trust step, so a file that was only dropped in the folder,
/// or changed after the import, is never loaded.
/// </summary>
public sealed class RecipeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-recipe-store-tests", Guid.NewGuid().ToString("N"));

    public RecipeStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    private string RecipesDirectory => Path.Combine(_root, "recipes");

    private RecipeStore NewStore() => new(_root);

    private string FileOf(string id) => Path.Combine(RecipesDirectory, RecipeStore.FileNameFor(id));

    [Fact]
    public void AStoreWhoseFolderDoesNotExistIsEmptyAndDoesNotCreateIt()
    {
        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Empty(result.Issues);
        Assert.False(Directory.Exists(RecipesDirectory));
        Assert.Empty(NewStore().List());
    }

    [Fact]
    public void TheFolderLivesUnderTheDataRootItWasGiven()
    {
        RecipeStoreResult added = NewStore().Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);

        Assert.True(added.Succeeded);
        Assert.True(File.Exists(FileOf("acme.ping")));
        Assert.True(File.Exists(Path.Combine(RecipesDirectory, RecipeStore.IndexFileName)));
        Assert.Equal(RecipesDirectory, Path.GetDirectoryName(FileOf("acme.ping")));
    }

    [Fact]
    public void AnImportedRecipeLoadsWithTheIndexEntryOfItsExactBytes()
    {
        byte[] bytes = RecipeTestFiles.Bytes();
        RecipeStore store = NewStore();

        RecipeStoreResult added = store.Add(bytes, RecipeTestFiles.ResolvedProgram);
        RecipeLoadResult loaded = store.LoadAll();

        Assert.True(added.Succeeded);
        LoadedRecipe recipe = Assert.Single(loaded.Recipes);
        Assert.Empty(loaded.Issues);
        Assert.Equal("acme.ping", recipe.Recipe.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), recipe.Entry.Sha256);
        Assert.Equal(RecipeTestFiles.ResolvedProgram, recipe.Entry.ResolvedProgramPath);
        Assert.Equal(RecipeStore.FileNameFor("acme.ping"), recipe.Entry.FileName);
        Assert.Equal(TimeSpan.Zero, recipe.Entry.ImportedAtUtc.Offset);
        Assert.InRange(recipe.Entry.ImportedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void ARecipeFileDroppedInTheFolderIsNotLoadedAndIsReported()
    {
        Directory.CreateDirectory(RecipesDirectory);
        string dropped = Path.Combine(RecipesDirectory, "dropped" + RecipeFormat.FileExtension);
        File.WriteAllBytes(dropped, RecipeTestFiles.Bytes("evil.tool"));

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        RecipeLoadIssue issue = Assert.Single(result.Issues);
        Assert.Equal(RecipeLoadIssueCode.NotIndexed, issue.Code);
        Assert.Equal("dropped" + RecipeFormat.FileExtension, issue.FileName);
    }

    [Fact]
    public void ADroppedFileIsNotLoadedEvenWhenAnotherRecipeWasImported()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        File.WriteAllBytes(Path.Combine(RecipesDirectory, "other" + RecipeFormat.FileExtension), RecipeTestFiles.Bytes("acme.other"));

        RecipeLoadResult result = store.LoadAll();

        Assert.Equal(["acme.ping"], result.Recipes.Select(item => item.Recipe.Id));
        Assert.Contains(result.Issues, issue => issue.Code == RecipeLoadIssueCode.NotIndexed && issue.FileName == "other" + RecipeFormat.FileExtension);
    }

    [Fact]
    public void AFileChangedAfterTheImportIsNotLoadedAndIsReported()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        File.WriteAllBytes(FileOf("acme.ping"), RecipeTestFiles.Bytes(description: "Now it does something else."));

        RecipeLoadResult result = store.LoadAll();

        Assert.Empty(result.Recipes);
        RecipeLoadIssue issue = Assert.Single(result.Issues);
        Assert.Equal(RecipeLoadIssueCode.HashMismatch, issue.Code);
        Assert.Equal(RecipeStore.FileNameFor("acme.ping"), issue.FileName);
        Assert.Equal("acme.ping", issue.RecipeId);
        Assert.Null(store.ReadOriginalBytes("acme.ping"));
    }

    [Fact]
    public void AnIndexedFileThatIsGoneIsReported()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        File.Delete(FileOf("acme.ping"));

        RecipeLoadResult result = store.LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Equal(RecipeLoadIssueCode.FileMissing, Assert.Single(result.Issues).Code);
        Assert.Single(store.List());
    }

    [Fact]
    public void AnOversizeFileIsReportedWithoutBeingTrusted()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        File.WriteAllBytes(FileOf("acme.ping"), new byte[RecipeLimits.MaxFileBytes + 1]);

        RecipeLoadResult result = store.LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Equal(RecipeLoadIssueCode.HashMismatch, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void TheExportedBytesAreTheImportedBytes()
    {
        // A byte order mark and a final newline are part of what was imported.
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. RecipeTestFiles.Bytes(), (byte)'\r', (byte)'\n'];
        RecipeStore store = NewStore();

        Assert.True(store.Add(bytes, RecipeTestFiles.ResolvedProgram).Succeeded);

        Assert.Equal(bytes, store.ReadOriginalBytes("acme.ping"));
        Assert.Equal(bytes, File.ReadAllBytes(FileOf("acme.ping")));
    }

    [Fact]
    public void TheCallersBytesAreNotKeptByReference()
    {
        byte[] bytes = RecipeTestFiles.Bytes();
        byte[] copy = [.. bytes];
        RecipeStore store = NewStore();
        store.Add(bytes, RecipeTestFiles.ResolvedProgram);

        bytes[0] = (byte)'X';

        Assert.Equal(copy, store.ReadOriginalBytes("acme.ping"));
    }

    [Fact]
    public void AnUnknownRecipeHasNoBytesToExport()
        => Assert.Null(NewStore().ReadOriginalBytes("acme.nothing"));

    [Fact]
    public void AddingTheSameIdTwiceIsRefusedAndKeepsTheFirst()
    {
        RecipeStore store = NewStore();
        byte[] first = RecipeTestFiles.Bytes();
        store.Add(first, RecipeTestFiles.ResolvedProgram);

        RecipeStoreResult second = store.Add(RecipeTestFiles.Bytes(description: "Different."), RecipeTestFiles.ResolvedProgram);

        Assert.Equal(RecipeStoreFailure.AlreadyExists, second.Failure);
        Assert.Equal(first, store.ReadOriginalBytes("acme.ping"));
    }

    [Fact]
    public void ReplacingSwapsTheBytesTheHashAndThePath()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        byte[] second = RecipeTestFiles.Bytes(description: "Second version.");
        const string OtherProgram = @"C:\Windows\System32\tracert.exe";

        RecipeStoreResult replaced = store.Replace(second, OtherProgram);
        LoadedRecipe loaded = Assert.Single(store.LoadAll().Recipes);

        Assert.True(replaced.Succeeded);
        Assert.Equal(second, store.ReadOriginalBytes("acme.ping"));
        Assert.Equal("Second version.", loaded.Recipe.Description);
        Assert.Equal(OtherProgram, loaded.Entry.ResolvedProgramPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(second)), loaded.Entry.Sha256);
        Assert.Single(store.List());
    }

    [Fact]
    public void ReplacingARecipeThatWasNeverImportedIsRefused()
    {
        RecipeStoreResult result = NewStore().Replace(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);

        Assert.Equal(RecipeStoreFailure.NotFound, result.Failure);
        Assert.False(File.Exists(FileOf("acme.ping")));
    }

    [Fact]
    public void RemovingDeletesTheFileAndTheEntry()
    {
        RecipeStore store = NewStore();
        store.Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        store.Add(RecipeTestFiles.Bytes("acme.other"), RecipeTestFiles.ResolvedProgram);

        RecipeStoreResult removed = store.Remove("acme.ping");

        Assert.True(removed.Succeeded);
        Assert.False(File.Exists(FileOf("acme.ping")));
        Assert.Equal(["acme.other"], store.LoadAll().Recipes.Select(item => item.Recipe.Id));
        Assert.Equal(["acme.other"], store.List().Select(entry => entry.Id));
        Assert.Equal(RecipeStoreFailure.NotFound, store.Remove("acme.ping").Failure);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"schemaVersion\": 1, \"id\": \"acme.ping\", \"surprise\": true }")]
    [InlineData("{ \"schemaVersion\": 99, \"id\": \"acme.ping\" }")]
    [InlineData("")]
    public void BytesThatAreNotAValidRecipeAreRefusedAndNothingIsWritten(string text)
    {
        RecipeStore store = NewStore();

        RecipeStoreResult added = store.Add(Encoding.UTF8.GetBytes(text), RecipeTestFiles.ResolvedProgram);
        RecipeStoreResult replaced = store.Replace(Encoding.UTF8.GetBytes(text), RecipeTestFiles.ResolvedProgram);

        Assert.Equal(RecipeStoreFailure.InvalidRecipe, added.Failure);
        Assert.NotEmpty(added.Errors);
        Assert.Equal(RecipeStoreFailure.InvalidRecipe, replaced.Failure);
        Assert.False(Directory.Exists(RecipesDirectory));
    }

    [Fact]
    public void AnOversizeRecipeIsRefused()
    {
        RecipeStoreResult result = NewStore().Add(new byte[RecipeLimits.MaxFileBytes + 1], RecipeTestFiles.ResolvedProgram);

        Assert.Equal(RecipeStoreFailure.InvalidRecipe, result.Failure);
        Assert.Contains(result.Errors, error => error.Code == RecipeErrorCode.FileTooLarge);
    }

    [Theory]
    [InlineData("ping.exe")]
    [InlineData(@"..\ping.exe")]
    [InlineData("")]
    public void ARelativeProgramPathIsRefused(string path)
    {
        RecipeStoreResult result = NewStore().Add(RecipeTestFiles.Bytes(), path);

        Assert.Equal(RecipeStoreFailure.InvalidProgramPath, result.Failure);
        Assert.False(Directory.Exists(RecipesDirectory));
    }

    [Fact]
    public void TheFileNameComesFromTheValidatedIdAndCarriesAFixedPrefix()
    {
        RecipeStore store = NewStore();

        // "con" and "nul" are Windows device names: a file named after the id alone would open the device.
        RecipeStoreResult added = store.Add(RecipeTestFiles.Bytes("con.nul"), RecipeTestFiles.ResolvedProgram);

        Assert.True(added.Succeeded);
        Assert.Equal("rcp-con.nul" + RecipeFormat.FileExtension, added.Entry!.FileName);
        Assert.Equal("con.nul", Assert.Single(store.LoadAll().Recipes).Recipe.Id);
        Assert.Equal(
            new[] { RecipeStore.IndexFileName, "rcp-con.nul" + RecipeFormat.FileExtension }.Order(StringComparer.Ordinal),
            Directory.GetFiles(RecipesDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheIndexSurvivesAReload()
    {
        NewStore().Add(RecipeTestFiles.Bytes(), RecipeTestFiles.ResolvedProgram);
        NewStore().Add(RecipeTestFiles.Bytes("acme.other"), RecipeTestFiles.ResolvedProgram);

        RecipeStore reopened = NewStore();
        RecipeLoadResult result = reopened.LoadAll();

        Assert.Equal(["acme.other", "acme.ping"], result.Recipes.Select(item => item.Recipe.Id).Order(StringComparer.Ordinal));
        Assert.Empty(result.Issues);
        Assert.Equal(2, reopened.List().Count);
    }

    [Fact]
    public void AnIndexEntryThatNamesAnotherFileIsRefusedAndTheFileIsNotRead()
    {
        Directory.CreateDirectory(RecipesDirectory);
        byte[] bytes = RecipeTestFiles.Bytes();
        string outside = Path.Combine(_root, "outside" + RecipeFormat.FileExtension);
        File.WriteAllBytes(outside, bytes);
        WriteIndex(
            "acme.ping",
            @"..\outside" + RecipeFormat.FileExtension,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Contains(result.Issues, issue => issue.Code == RecipeLoadIssueCode.IndexEntryInvalid);
    }

    [Fact]
    public void AFileWhoseIdIsNotTheOneIndexedIsRefused()
    {
        Directory.CreateDirectory(RecipesDirectory);
        byte[] bytes = RecipeTestFiles.Bytes("acme.b");
        File.WriteAllBytes(FileOf("acme.a"), bytes);
        WriteIndex("acme.a", RecipeStore.FileNameFor("acme.a"), Convert.ToHexStringLower(SHA256.HashData(bytes)));

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Equal(RecipeLoadIssueCode.Invalid, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void AnUnreadableIndexLoadsNothingAndDoesNotThrow()
    {
        Directory.CreateDirectory(RecipesDirectory);
        File.WriteAllText(Path.Combine(RecipesDirectory, RecipeStore.IndexFileName), "{ not json");
        File.WriteAllBytes(FileOf("acme.ping"), RecipeTestFiles.Bytes());

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Contains(result.Issues, issue => issue.Code == RecipeLoadIssueCode.IndexUnreadable);
        Assert.Contains(result.Issues, issue => issue.Code == RecipeLoadIssueCode.NotIndexed);
    }

    [Fact]
    public void AnIndexWrittenByANewerFormatLoadsNothing()
    {
        Directory.CreateDirectory(RecipesDirectory);
        File.WriteAllText(Path.Combine(RecipesDirectory, RecipeStore.IndexFileName), "{ \"schemaVersion\": 99, \"recipes\": [] }");

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Equal(RecipeLoadIssueCode.IndexUnreadable, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void AnIdThatIsNotPlainIdTextNeverBuildsAPath()
    {
        // The id decides the file name, and an id that is not plain id text never builds a path.
        Directory.CreateDirectory(RecipesDirectory);
        WriteIndex(@"..\..\evil", @"rcp-..\..\evil" + RecipeFormat.FileExtension, new string('a', 64));

        RecipeLoadResult result = NewStore().LoadAll();

        Assert.Empty(result.Recipes);
        Assert.Contains(result.Issues, issue => issue.Code == RecipeLoadIssueCode.IndexEntryInvalid);
    }

    private void WriteIndex(string id, string fileName, string sha256)
    {
        string json = $$"""
            {
              "schemaVersion": 1,
              "recipes": [
                {
                  "id": "{{id.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "fileName": "{{fileName.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "sha256": "{{sha256}}",
                  "resolvedProgramPath": "C:\\Windows\\System32\\ping.exe",
                  "importedAtUtc": "2026-11-23T10:00:00+00:00"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(RecipesDirectory, RecipeStore.IndexFileName), json);
    }
}
