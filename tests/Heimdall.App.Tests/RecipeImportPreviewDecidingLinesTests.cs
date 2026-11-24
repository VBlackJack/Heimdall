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
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>The preview shows what decides the behaviour: the program as written, the encoding, standard error, the exit codes and the icon.</summary>
public sealed class RecipeImportPreviewDecidingLinesTests
{
    private static async Task<LocalizationManager> EnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        return localizer;
    }

    private static RecipeImportPlan PlanOf(byte[] bytes, RecipeStoreEntry? existing = null, Recipe? previous = null)
    {
        Recipe recipe = RecipeParser.Parse(bytes).Recipe ?? throw new InvalidOperationException("The test recipe is not valid.");
        return new RecipeImportPlan(recipe, bytes, @"C:\Windows\System32\ping.exe", RecipeSessionUse.Of(recipe), existing, previous);
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    [Fact]
    public async Task TheProgramAsWrittenFollowsTheResolvedPath_WhichStaysFirst()
    {
        string[] lines = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(program: "ping"))));

        int heading = Array.FindIndex(lines, line => line.StartsWith("Program that will run", StringComparison.Ordinal));
        Assert.Equal(@"  C:\Windows\System32\ping.exe", lines[heading + 1]);
        Assert.Equal("  Program as written in the file: ping", lines[heading + 2]);
    }

    [Fact]
    public async Task TheDefaultsAreStatedInWords()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        Assert.Contains("The output is read as: UTF-8", text, StringComparison.Ordinal);
        Assert.Contains("Standard error: kept apart and shown as error output", text, StringComparison.Ordinal);
        Assert.Contains("Exit codes that count as success: 0", text, StringComparison.Ordinal);
        Assert.Contains("Icon: (none)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachChosenValueIsShown()
    {
        byte[] bytes = RecipeImportFiles.Bytes(extraProperties: "\"outputEncoding\": \"oem\", \"stderr\": \"merge\", \"successExitCodes\": [0, 3, 1], \"icon\": \"shield\"");

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes));

        Assert.Contains("The output is read as: the OEM code page of the console", text, StringComparison.Ordinal);
        Assert.Contains("Standard error: mixed into the output", text, StringComparison.Ordinal);
        Assert.Contains("Exit codes that count as success: 0, 3, 1", text, StringComparison.Ordinal);
        Assert.Contains("Icon: shield", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNewLinesComeBeforeTheTimeoutWhichStaysLast()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        int timeout = text.IndexOf("It is stopped after", StringComparison.Ordinal);
        foreach (string line in new[] { "The output is read as", "Standard error:", "Exit codes that count as success", "Icon:" })
        {
            Assert.True(text.IndexOf(line, StringComparison.Ordinal) is > 0 && text.IndexOf(line, StringComparison.Ordinal) < timeout, line);
        }

        Assert.EndsWith("It is stopped after 60 seconds.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedIconIsTheOnlyLineThatSaysChanged()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);
        Recipe previous = PlanOf(RecipeImportFiles.Bytes()).Recipe;

        string[] changed = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(extraProperties: "\"icon\": \"clock\""), existing, previous)));
        string[] same = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(), existing, previous)));

        Assert.Contains("  Icon: changed", changed);
        Assert.Contains("  Icon: unchanged", same);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheNewLinesAreWordedInTheOtherLanguages(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);

        string text = RecipeImportPreview.Compose(localizer, PlanOf(RecipeImportFiles.Bytes()));

        Assert.DoesNotContain("RecipeImportPreview", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RecipeImportEncoding", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RecipeImportStderr", text, StringComparison.Ordinal);
    }
}
