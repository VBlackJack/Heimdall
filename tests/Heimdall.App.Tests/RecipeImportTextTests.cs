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
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The reasons a recipe cannot be imported are worded in one place, and every member of every enum
/// that reports one has its text, with the same placeholders, in all three languages.
/// </summary>
public sealed partial class RecipeImportTextTests
{
    private static readonly string[] Locales = ["en", "fr", "es"];

    private static Dictionary<string, string> ReadLocale(string locale)
    {
        string path = Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales", locale + ".json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    public static TheoryData<string> LocaleNames()
    {
        TheoryData<string> data = [];
        foreach (string locale in Locales)
        {
            data.Add(locale);
        }

        return data;
    }

    /// <summary>Every key the presenter and the preview can ask for, from every enum member.</summary>
    private static IEnumerable<string> EnumKeys()
    {
        foreach (RecipeErrorCode code in Enum.GetValues<RecipeErrorCode>())
        {
            yield return RecipeImportText.KeyFor(code);
        }

        foreach (RecipeProgramRefusal refusal in Enum.GetValues<RecipeProgramRefusal>())
        {
            if (RecipeImportText.KeyFor(refusal) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeImportFailure failure in Enum.GetValues<RecipeImportFailure>())
        {
            if (RecipeImportText.KeyFor(failure) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeStoreFailure failure in Enum.GetValues<RecipeStoreFailure>())
        {
            if (RecipeImportText.KeyFor(failure) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeFolderRefusal refusal in Enum.GetValues<RecipeFolderRefusal>())
        {
            if (RecipeImportText.KeyFor(refusal) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeInputType type in Enum.GetValues<RecipeInputType>())
        {
            yield return RecipeImportPreview.KeyFor(type);
        }

        foreach (RecipeOutputFormat format in Enum.GetValues<RecipeOutputFormat>())
        {
            yield return RecipeImportPreview.KeyFor(format);
        }

        foreach (RecipeOutputEncoding encoding in Enum.GetValues<RecipeOutputEncoding>())
        {
            yield return RecipeImportPreview.KeyFor(encoding);
        }

        foreach (RecipeStderrMode mode in Enum.GetValues<RecipeStderrMode>())
        {
            yield return RecipeImportPreview.KeyFor(mode);
        }
    }

    [Theory]
    [MemberData(nameof(LocaleNames))]
    public void EveryMemberOfEveryEnumHasItsTextInTheLanguage(string locale)
    {
        Dictionary<string, string> strings = ReadLocale(locale);

        List<string> missing = [.. EnumKeys().Distinct().Where(key => !strings.TryGetValue(key, out string? text) || string.IsNullOrWhiteSpace(text))];

        Assert.True(missing.Count == 0, $"{locale}.json lacks: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(LocaleNames))]
    public void EveryTextOfTheImportTakesTheSamePlaceholdersAsTheEnglishOne(string locale)
    {
        Dictionary<string, string> english = ReadLocale("en");
        Dictionary<string, string> strings = ReadLocale(locale);

        List<string> drifted = [];
        foreach (string key in english.Keys.Where(IsImportKey))
        {
            if (!strings.TryGetValue(key, out string? text) || Placeholders(english[key]) != Placeholders(text))
            {
                drifted.Add(key);
            }
        }

        Assert.True(drifted.Count == 0, $"{locale}.json drifts from en.json on: {string.Join(", ", drifted)}");
    }

    [Fact]
    public void EveryErrorTextOfTheImportHasTheSameFiveArgumentsToChooseFrom()
    {
        Dictionary<string, string> english = ReadLocale("en");

        foreach (RecipeErrorCode code in Enum.GetValues<RecipeErrorCode>())
        {
            string text = english[RecipeImportText.KeyFor(code)];
            foreach (Match match in PlaceholderPattern().Matches(text))
            {
                Assert.InRange(int.Parse(match.Value.Trim('{', '}'), System.Globalization.CultureInfo.InvariantCulture), 0, 4);
            }
        }
    }

    [Fact]
    public void EveryMemberThatMeansSomethingHasAKeyAndOnlyTheNeutralOnesHaveNone()
    {
        foreach (RecipeProgramRefusal refusal in Enum.GetValues<RecipeProgramRefusal>())
        {
            Assert.Equal(refusal == RecipeProgramRefusal.None, RecipeImportText.KeyFor(refusal) is null);
        }

        foreach (RecipeImportFailure failure in Enum.GetValues<RecipeImportFailure>())
        {
            Assert.Equal(failure == RecipeImportFailure.None, RecipeImportText.KeyFor(failure) is null);
        }

        foreach (RecipeStoreFailure failure in Enum.GetValues<RecipeStoreFailure>())
        {
            Assert.Equal(failure == RecipeStoreFailure.None, RecipeImportText.KeyFor(failure) is null);
        }

        foreach (RecipeFolderRefusal refusal in Enum.GetValues<RecipeFolderRefusal>())
        {
            Assert.Equal(refusal == RecipeFolderRefusal.None, RecipeImportText.KeyFor(refusal) is null);
        }
    }

    [Fact]
    public void NoTwoMembersOfAnEnumShareAKey()
    {
        string[] errors = [.. Enum.GetValues<RecipeErrorCode>().Select(RecipeImportText.KeyFor)];
        string[] programs = [.. Enum.GetValues<RecipeProgramRefusal>().Select(RecipeImportText.KeyFor).OfType<string>()];
        string[] stores = [.. Enum.GetValues<RecipeStoreFailure>().Select(RecipeImportText.KeyFor).OfType<string>()];
        string[] folders = [.. Enum.GetValues<RecipeFolderRefusal>().Select(RecipeImportText.KeyFor).OfType<string>()];

        Assert.Equal(errors.Length, errors.Distinct().Count());
        Assert.Equal(programs.Length, programs.Distinct().Count());
        Assert.Equal(stores.Length, stores.Distinct().Count());
        Assert.Equal(folders.Length, folders.Distinct().Count());
    }

    [Fact]
    public void AnUnnamedMemberIsAnErrorRatherThanAnEmptyText()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportText.KeyFor((RecipeErrorCode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportText.KeyFor((RecipeProgramRefusal)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportText.KeyFor((RecipeImportFailure)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportText.KeyFor((RecipeStoreFailure)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportText.KeyFor((RecipeFolderRefusal)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportPreview.KeyFor((RecipeInputType)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeImportPreview.KeyFor((RecipeOutputFormat)999));
    }

    [Fact]
    public async Task AProblemIsWordedWithWhereItIsAndWhatIsAtFault()
    {
        LocalizationManager localizer = await EnglishAsync();
        RecipeError error = new(
            RecipeErrorCode.UnknownProperty,
            new RecipePosition(7, 3),
            "inputs[1]",
            Name: "colour");

        string text = RecipeImportText.Describe(localizer, error);

        Assert.Equal("Unknown property \"colour\". The recipe format does not define it. (in inputs[1], line 7, column 3)", text);
    }

    [Fact]
    public async Task AProblemWithOnlyAPositionOrOnlyAPathSaysOnlyThat()
    {
        LocalizationManager localizer = await EnglishAsync();

        string positionOnly = RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.SyntaxError, new RecipePosition(2, 9)));
        string pathOnly = RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.EmptyToken, Path: "arguments[0]"));
        string neither = RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.EmptyDocument));

        Assert.Equal("The file is not valid JSON. (line 2, column 9)", positionOnly);
        Assert.Equal("An argument is empty. (in arguments[0])", pathOnly);
        Assert.Equal("The file is empty.", neither);
    }

    [Fact]
    public async Task AFixedValueThatStartsWithADashTellsTheAuthorToKeepTheDashInTheArgument()
    {
        LocalizationManager localizer = await EnglishAsync();
        RecipeError error = new(
            RecipeErrorCode.FixedValueStartsWithOptionCharacter,
            Path: "arguments[0]",
            Name: "record",
            Fragment: "-type=A");

        string text = RecipeImportText.Describe(localizer, error);

        Assert.Contains("\"-type=A\"", text, StringComparison.Ordinal);
        Assert.Contains("\"record\"", text, StringComparison.Ordinal);
        Assert.Contains("Keep that character in the fixed text of the argument, for example \"-type={in:record}\"", text, StringComparison.Ordinal);
        Assert.Contains("(in arguments[0])", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASizeRefusalNamesTheSizeAndTheLimit()
    {
        LocalizationManager localizer = await EnglishAsync();

        string text = RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.FileTooLarge, Actual: 70000, Limit: 65536));

        Assert.Contains("70", text, StringComparison.Ordinal);
        Assert.Contains("65", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithManyProblemsListsTheFirstOnesAndCountsTheRest()
    {
        LocalizationManager localizer = await EnglishAsync();
        RecipeImportOutcome outcome = RecipeImportOutcomeWithManyProblems(RecipeImportText.MaxListedProblems + 3);

        string text = RecipeImportText.Describe(localizer, outcome);

        string[] lines = text.Split(Environment.NewLine);
        int rest = outcome.Errors.Count - RecipeImportText.MaxListedProblems;
        Assert.Equal("This file cannot be imported:", lines[0]);
        Assert.Equal(RecipeImportText.MaxListedProblems, lines.Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Equal($"...and {rest} more problems.", lines[^1]);
    }

    [Fact]
    public async Task AProgramRefusalQuotesTheProgramAsWritten()
    {
        LocalizationManager localizer = await EnglishAsync();

        string text = RecipeImportText.Describe(localizer, new RecipeProgramVerdict(RecipeProgramRefusal.ShellOrInterpreter, null, "cmd.exe"));

        Assert.Equal("The program \"cmd.exe\" is a shell or an interpreter, which recipes may not run.", text);
    }

    [Fact]
    public async Task AFolderRefusalNamesTheLimitOfTheListWhenItIsFull()
    {
        LocalizationManager localizer = await EnglishAsync();

        string text = RecipeImportText.Describe(localizer, RecipeFolderRefusal.ListFull, 32);

        Assert.Contains("32", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, RecipeImportText.Describe(localizer, RecipeFolderRefusal.None, 32));
    }

    private static bool IsImportKey(string key)
        => key.StartsWith("Recipe", StringComparison.Ordinal)
            || key.StartsWith("SettingsRecipe", StringComparison.Ordinal)
            || key.StartsWith("A11yRecipe", StringComparison.Ordinal);

    private static string Placeholders(string text)
        => string.Join(",", PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal));

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    private static async Task<LocalizationManager> EnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        return localizer;
    }

    private static RecipeImportOutcome RecipeImportOutcomeWithManyProblems(int tokenCount)
    {
        // The outcome is built by the service from a real refusal; a file full of empty tokens gives many problems.
        string tokens = string.Join(",", Enumerable.Repeat("\"\"", tokenCount));
        byte[] bytes = RecipeImportFiles.Bytes(argumentsJson: $"[{tokens}]");
        FakeRecipeProgramLocator locator = new();
        RecipeImportService service = new(new FakeRecipeStore(), locator, new FakeToolAvailability());
        RecipeImportOutcome outcome = service.Prepare(bytes, []);
        Assert.Equal(RecipeImportFailure.InvalidRecipe, outcome.Failure);
        Assert.True(outcome.Errors.Count > RecipeImportText.MaxListedProblems);
        return outcome;
    }
}
