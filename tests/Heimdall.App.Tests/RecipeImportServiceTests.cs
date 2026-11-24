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
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The first half of an import reads, checks and resolves a recipe file and writes nothing; the
/// second half stores what the user confirmed.
/// </summary>
public sealed class RecipeImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-recipe-import-service-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeRecipeProgramLocator _locator = new();
    private readonly FakeToolAvailability _availability = new();
    private readonly RecipeStore _store;
    private readonly RecipeImportService _service;

    public RecipeImportServiceTests()
    {
        Directory.CreateDirectory(_root);
        _store = new RecipeStore(Path.Combine(_root, "data"));
        _service = new RecipeImportService(_store, _locator, _availability);
    }

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

    [Fact]
    public void AValidFileGivesAPlanAndStoresNothing()
    {
        string path = RecipeImportFiles.Write(_root, RecipeImportFiles.Bytes());

        RecipeImportOutcome outcome = _service.PrepareFile(path, []);

        Assert.True(outcome.Succeeded);
        RecipeImportPlan plan = Assert.IsType<RecipeImportPlan>(outcome.Plan);
        Assert.Equal("acme.ping", plan.Recipe.Id);
        Assert.Equal(FakeRecipeProgramLocator.DefaultPath, plan.ResolvedProgramPath);
        Assert.False(plan.IsReplace);
        Assert.Empty(_store.List());
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "recipes")));
    }

    [Fact]
    public void ThePlanKeepsTheExactBytesOfTheFile()
    {
        byte[] bytes = RecipeImportFiles.Bytes();
        string path = RecipeImportFiles.Write(_root, bytes);

        RecipeImportPlan plan = _service.PrepareFile(path, []).Plan!;

        Assert.Equal(bytes, plan.Bytes);
    }

    [Fact]
    public void TheProgramIsResolvedAgainstTheFoldersTheUserAllowed()
    {
        string[] folders = [@"D:\Tools"];

        _service.Prepare(RecipeImportFiles.Bytes(), folders);

        Assert.Equal(folders, Assert.Single(_locator.FolderCalls));
    }

    [Fact]
    public void AFileThatIsNotARecipeIsRefusedWithItsProblemsAndStoresNothing()
    {
        RecipeImportOutcome outcome = _service.Prepare(System.Text.Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1 }"), []);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecipeImportFailure.InvalidRecipe, outcome.Failure);
        Assert.NotEmpty(outcome.Errors);
        Assert.Null(outcome.Plan);
        Assert.Empty(_store.List());
        Assert.Empty(_locator.FolderCalls);
    }

    [Fact]
    public void ARecipeThatBreaksAnImportRuleIsRefused()
    {
        byte[] bytes = RecipeImportFiles.Bytes(argumentsJson: "[\"https://example.org/x\"]");

        RecipeImportOutcome outcome = _service.Prepare(bytes, []);

        Assert.Equal(RecipeImportFailure.InvalidRecipe, outcome.Failure);
        Assert.Contains(outcome.Errors, error => error.Code == RecipeErrorCode.ForbiddenUrl);
    }

    [Theory]
    [InlineData(RecipeProgramRefusal.ShellOrInterpreter)]
    [InlineData(RecipeProgramRefusal.OutsideAllowedRoots)]
    [InlineData(RecipeProgramRefusal.NotFound)]
    [InlineData(RecipeProgramRefusal.LinkLeavesAllowedRoots)]
    public void AProgramThatMayNotRunRefusesTheImport(RecipeProgramRefusal refusal)
    {
        _locator.Refuse(refusal, "tool.exe");

        RecipeImportOutcome outcome = _service.Prepare(RecipeImportFiles.Bytes(), []);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecipeImportFailure.ProgramRefused, outcome.Failure);
        Assert.Equal(refusal, outcome.Verdict!.Refusal);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefusedWithoutBeingRead()
    {
        string path = Path.Combine(_root, "big.heimdall-recipe.json");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(RecipeLimits.MaxFileBytes + 1);
        }

        RecipeImportOutcome outcome = _service.PrepareFile(path, []);

        Assert.Equal(RecipeImportFailure.InvalidRecipe, outcome.Failure);
        RecipeError error = Assert.Single(outcome.Errors);
        Assert.Equal(RecipeErrorCode.FileTooLarge, error.Code);
        Assert.Equal(RecipeLimits.MaxFileBytes + 1, error.Actual);
    }

    [Fact]
    public void AFileThatCannotBeReadIsRefused()
    {
        RecipeImportOutcome outcome = _service.PrepareFile(Path.Combine(_root, "missing.heimdall-recipe.json"), []);

        Assert.Equal(RecipeImportFailure.FileUnreadable, outcome.Failure);
        Assert.Empty(_store.List());
    }

    [Fact]
    public void WhileThePolicyForbidsRecipesNothingIsPrepared()
    {
        _availability.PackForbidden = true;
        string path = RecipeImportFiles.Write(_root, RecipeImportFiles.Bytes());

        RecipeImportOutcome outcome = _service.PrepareFile(path, []);

        Assert.Equal(RecipeImportFailure.PackForbiddenByPolicy, outcome.Failure);
        Assert.True(_service.IsForbiddenByPolicy);
        Assert.Empty(_locator.FolderCalls);
    }

    [Fact]
    public void ADisabledPackIsNotForbiddenAndStillAllowsAnImport()
    {
        _availability.PackEnabled = false;

        RecipeImportOutcome outcome = _service.Prepare(RecipeImportFiles.Bytes(), []);

        Assert.True(outcome.Succeeded);
        Assert.False(_service.IsForbiddenByPolicy);
        Assert.False(_service.IsPackEnabled);
    }

    [Fact]
    public void CommitStoresTheExactBytesAndTheResolvedPath()
    {
        byte[] bytes = RecipeImportFiles.Bytes();
        RecipeImportPlan plan = _service.Prepare(bytes, []).Plan!;

        RecipeStoreResult result = _service.Commit(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(bytes, _store.ReadOriginalBytes("acme.ping"));
        RecipeStoreEntry entry = Assert.Single(_store.List());
        Assert.Equal(FakeRecipeProgramLocator.DefaultPath, entry.ResolvedProgramPath);
    }

    [Fact]
    public void ARecipeWithAnImportedIdIsAReplaceThatShowsTheOldRecipe()
    {
        _service.Commit(_service.Prepare(RecipeImportFiles.Bytes(version: "1.0.0"), []).Plan!);
        _locator.ResolveTo(@"C:\Windows\System32\tracert.exe");

        RecipeImportPlan plan = _service.Prepare(RecipeImportFiles.Bytes(version: "2.0.0"), []).Plan!;

        Assert.True(plan.IsReplace);
        Assert.Equal("1.0.0", plan.Previous!.Version);
        Assert.Equal(FakeRecipeProgramLocator.DefaultPath, plan.Existing!.ResolvedProgramPath);
        Assert.Equal(@"C:\Windows\System32\tracert.exe", plan.ResolvedProgramPath);
    }

    [Fact]
    public void CommitOfAReplaceKeepsOneRecipeWithTheNewBytesAndPath()
    {
        _service.Commit(_service.Prepare(RecipeImportFiles.Bytes(version: "1.0.0"), []).Plan!);
        _locator.ResolveTo(@"C:\Windows\System32\tracert.exe");
        byte[] newer = RecipeImportFiles.Bytes(version: "2.0.0");

        RecipeStoreResult result = _service.Commit(_service.Prepare(newer, []).Plan!);

        Assert.True(result.Succeeded);
        RecipeStoreEntry entry = Assert.Single(_store.List());
        Assert.Equal(@"C:\Windows\System32\tracert.exe", entry.ResolvedProgramPath);
        Assert.Equal(newer, _store.ReadOriginalBytes("acme.ping"));
    }

    [Fact]
    public void ReimportingARecipeWhoseProgramMovedReRecordsTheResolvedPath()
    {
        byte[] bytes = RecipeImportFiles.Bytes();
        _service.Commit(_service.Prepare(bytes, []).Plan!);
        _locator.ResolveTo(@"C:\Windows\SysWOW64\ping.exe");

        RecipeImportPlan plan = _service.Prepare(bytes, []).Plan!;
        _service.Commit(plan);

        Assert.True(plan.IsReplace);
        Assert.Equal(@"C:\Windows\SysWOW64\ping.exe", Assert.Single(_store.List()).ResolvedProgramPath);
    }

    [Fact]
    public void ThePlanOfAReplaceWhoseStoredFileChangedHasNoPreviousRecipe()
    {
        _service.Commit(_service.Prepare(RecipeImportFiles.Bytes(), []).Plan!);
        File.WriteAllText(Path.Combine(_root, "data", "recipes", RecipeStore.FileNameFor("acme.ping")), "changed");

        RecipeImportPlan plan = _service.Prepare(RecipeImportFiles.Bytes(version: "2.0.0"), []).Plan!;

        Assert.True(plan.IsReplace);
        Assert.Null(plan.Previous);
    }
}
