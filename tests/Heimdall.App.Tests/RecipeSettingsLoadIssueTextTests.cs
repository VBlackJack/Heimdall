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
using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Settings;

namespace Heimdall.App.Tests;

/// <summary>
/// Each reason a stored recipe did not load in Settings shows its own accurate wording rather than
/// attributing every failure to a changed file.
/// </summary>
public sealed class RecipeSettingsLoadIssueTextTests
{
    private static readonly string[] Locales = ["en", "fr", "es"];

    private static string StoredFilePath(RecipeImportHarness harness, string id = "acme.ping")
        => Path.Combine(harness.Root, "data", "recipes", RecipeStore.FileNameFor(id));

    private static string IndexFilePath(RecipeImportHarness harness)
        => Path.Combine(harness.Root, "data", "recipes", RecipeStore.IndexFileName);

    private static async Task ImportDefaultAsync(RecipeImportHarness harness)
    {
        harness.Offer(RecipeImportFiles.Bytes());
        harness.Dialogs.ConfirmResult = true;
        await harness.ViewModel.ImportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task HashMismatch_ShowsTheChangedFileText()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportDefaultAsync(harness);
        File.AppendAllText(StoredFilePath(harness), " ");

        harness.ViewModel.Refresh();

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.False(item.IsAvailable);
        Assert.Equal(harness.Localizer["SettingsRecipeItemUnavailable"], item.UnavailableText);
    }

    [Fact]
    public async Task InvalidRecipeWithMatchingHash_ShowsTheVersionRejectedText()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportDefaultAsync(harness);

        byte[] invalidBytes = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 99 }");
        File.WriteAllBytes(StoredFilePath(harness), invalidBytes);
        string updatedHash = Convert.ToHexStringLower(SHA256.HashData(invalidBytes));

        JsonNode index = JsonNode.Parse(File.ReadAllText(IndexFilePath(harness)))!;
        index["recipes"]![0]!["sha256"] = updatedHash;
        File.WriteAllText(IndexFilePath(harness), index.ToJsonString());

        harness.ViewModel.Refresh();

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.False(item.IsAvailable);
        Assert.Equal(harness.Localizer["SettingsRecipeItemInvalid"], item.UnavailableText);
        Assert.DoesNotContain("no longer matches what was imported", item.UnavailableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_ShowsTheMissingFileText()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportDefaultAsync(harness);
        File.Delete(StoredFilePath(harness));

        harness.ViewModel.Refresh();

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.False(item.IsAvailable);
        Assert.Equal(harness.Localizer["SettingsRecipeItemFileMissing"], item.UnavailableText);
    }

    [Fact]
    public async Task UnreadableFile_ShowsTheUnreadableFileText()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportDefaultAsync(harness);

        using FileStream exclusiveLock = new(
            StoredFilePath(harness),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        harness.ViewModel.Refresh();

        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.False(item.IsAvailable);
        Assert.Equal(harness.Localizer["SettingsRecipeItemUnreadable"], item.UnavailableText);
    }

    [Fact]
    public async Task InvalidRecipeRow_RewordsWhenLanguageChanges()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        await ImportDefaultAsync(harness);

        byte[] invalidBytes = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 99 }");
        File.WriteAllBytes(StoredFilePath(harness), invalidBytes);
        JsonNode index = JsonNode.Parse(File.ReadAllText(IndexFilePath(harness)))!;
        index["recipes"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(invalidBytes));
        File.WriteAllText(IndexFilePath(harness), index.ToJsonString());

        harness.ViewModel.Refresh();

        await harness.Localizer.SwitchLocaleAsync("fr");
        ImportedRecipeItemViewModel item = Assert.Single(harness.ViewModel.Items);
        Assert.Equal(harness.Localizer["SettingsRecipeItemInvalid"], item.UnavailableText);

        await harness.Localizer.SwitchLocaleAsync("es");
        Assert.Equal(harness.Localizer["SettingsRecipeItemInvalid"], item.UnavailableText);
    }

    [Fact]
    public void EveryPerEntryIssueCode_MapsToItsOwnKeyInAllThreeLocales()
    {
        RecipeLoadIssueCode[] codes =
        [
            RecipeLoadIssueCode.HashMismatch,
            RecipeLoadIssueCode.Invalid,
            RecipeLoadIssueCode.FileMissing,
            RecipeLoadIssueCode.Unreadable,
        ];

        string[] keys = [.. codes.Select(code => RecipeImportText.KeyForLoadIssue(code))];
        Assert.Equal(codes.Length, keys.Distinct(StringComparer.Ordinal).Count());
        string fallbackKey = RecipeImportText.KeyForLoadIssue(null);
        Assert.Equal(RecipeImportText.UnavailableFallbackKey, fallbackKey);
        Assert.DoesNotContain(fallbackKey, keys);

        foreach (string locale in Locales)
        {
            string path = Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales", locale + ".json");
            Dictionary<string, string> catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
            foreach (string key in keys.Append(fallbackKey))
            {
                Assert.True(
                    catalog.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value),
                    $"{locale}.json is missing or blank for key '{key}'.");
            }
        }
    }
}
