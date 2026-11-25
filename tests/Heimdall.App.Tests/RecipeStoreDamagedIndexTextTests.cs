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
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Tests;

/// <summary>The words for a damaged index name the index file from its constant, in the three languages.</summary>
public sealed class RecipeStoreDamagedIndexTextTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheTextNamesTheIndexFileFromTheConstantAndHasNoLeftOverPlaceholder(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);
        RecipeStoreResult result = new RecipeStoreResultFactory().Damaged();

        string text = RecipeImportText.Describe(localizer, result);

        Assert.Contains(RecipeStore.IndexFileName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
        Assert.NotEqual("RecipeStoreFailureIndexDamaged", text);
        Assert.NotEqual(localizer["RecipeStoreFailureStorageError"], text);
    }

    // A store given a damaged index answers with the failure, so the text is the one a user would read.
    private sealed class RecipeStoreResultFactory
    {
        public RecipeStoreResult Damaged()
        {
            string root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-damaged-text", Guid.NewGuid().ToString("N"));
            try
            {
                RecipeStore store = new(root);
                store.Add(RecipeTestFiles.Bytes("acme.ping"), RecipeTestFiles.ResolvedProgram);
                string index = Path.Combine(root, "recipes", RecipeStore.IndexFileName);
                File.WriteAllText(index, "{ " + (char)34 + "schemaVersion" + (char)34 + ": 1, " + (char)34 + "recipes" + (char)34 + ": [ { " + (char)34 + "id" + (char)34 + ": " + (char)34 + "Not Plain" + (char)34 + " } ] }");
                return store.Remove("acme.ping");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

/// <summary>What the store writes to the diagnostic log never carries the path of a file.</summary>
[Collection("ExternalToolLaunchFileLogger")]
public sealed class RecipeStoreLogTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "Heimdall.RecipeStoreLog.Tests." + Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-store-log-tests", Guid.NewGuid().ToString("N"));
    private readonly bool _previousLoggingEnabled = FileLogger.IsEnabled;

    public RecipeStoreLogTests()
    {
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_root);
        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_logDirectory, flushIntervalMs: 60000);
    }

    public void Dispose()
    {
        FileLogger.SetEnabled(_previousLoggingEnabled);
        foreach (string folder in new[] { _logDirectory, _root })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temporary folder.
            }
        }
    }

    [Fact]
    public void ALockedIndexIsLoggedByTheTypeOfTheErrorAndNeverByItsPath()
    {
        RecipeStore store = new(_root);
        Assert.True(store.Add(RecipeTestFiles.Bytes("acme.ping"), RecipeTestFiles.ResolvedProgram).Succeeded);
        string index = Path.Combine(_root, "recipes", RecipeStore.IndexFileName);

        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(RecipeStoreFailure.StorageError, store.Add(RecipeTestFiles.Bytes("acme.other"), RecipeTestFiles.ResolvedProgram).Failure);
        }

        FileLogger.Flush();
        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(_logDirectory, "heimdall_*.log")));
        Assert.Contains("the index could not be read (IOException)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetDirectoryName(index)!, log, StringComparison.OrdinalIgnoreCase);
    }
}
