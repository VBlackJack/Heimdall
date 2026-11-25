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
using Heimdall.App.Services;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The words of a recipe run live in one place, and every member of every enum that Core reports
/// has its text in all three languages: a member added to Core without a key fails here.
/// </summary>
public sealed class RecipeRunTextTests
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

    private static IEnumerable<string> AllKeys()
    {
        foreach (RecipeValueRefusal refusal in Enum.GetValues<RecipeValueRefusal>())
        {
            if (RecipeRunText.KeyFor(refusal) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeRunStatus status in Enum.GetValues<RecipeRunStatus>())
        {
            yield return RecipeRunText.KeyFor(status);
        }

        foreach (RecipeRunRefusal refusal in Enum.GetValues<RecipeRunRefusal>())
        {
            if (RecipeRunText.KeyFor(refusal) is { } key)
            {
                yield return key;
            }
        }

        foreach (RecipeProgramComparison comparison in Enum.GetValues<RecipeProgramComparison>())
        {
            if (RecipeRunText.KeyFor(comparison) is { } key)
            {
                yield return key;
            }
        }

        yield return RecipeRunText.TimedOutOneKey;
        yield return RecipeRunText.RefusedFrameKey;
        yield return RecipeRunText.UnknownStatusKey;
    }

    [Theory]
    [MemberData(nameof(LocaleNames))]
    public void EveryMemberOfEveryEnumHasItsTextInTheLanguage(string locale)
    {
        Dictionary<string, string> strings = ReadLocale(locale);

        List<string> missing = [.. AllKeys().Distinct().Where(key => !strings.TryGetValue(key, out string? text) || string.IsNullOrWhiteSpace(text))];

        Assert.True(missing.Count == 0, $"{locale}.json lacks: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryMemberThatMeansSomethingHasAKeyAndOnlyTheNeutralOnesHaveNone()
    {
        foreach (RecipeValueRefusal refusal in Enum.GetValues<RecipeValueRefusal>())
        {
            Assert.Equal(refusal == RecipeValueRefusal.None, RecipeRunText.KeyFor(refusal) is null);
        }

        foreach (RecipeRunRefusal refusal in Enum.GetValues<RecipeRunRefusal>())
        {
            Assert.Equal(refusal == RecipeRunRefusal.None, RecipeRunText.KeyFor(refusal) is null);
        }

        foreach (RecipeProgramComparison comparison in Enum.GetValues<RecipeProgramComparison>())
        {
            Assert.Equal(comparison == RecipeProgramComparison.Same, RecipeRunText.KeyFor(comparison) is null);
        }
    }

    [Fact]
    public void NoTwoMembersOfAnEnumShareAKey()
    {
        string[] refusals = [.. Enum.GetValues<RecipeValueRefusal>().Select(RecipeRunText.KeyFor).OfType<string>()];
        string[] statuses = [.. Enum.GetValues<RecipeRunStatus>().Select(RecipeRunText.KeyFor)];
        string[] runRefusals = [.. Enum.GetValues<RecipeRunRefusal>().Select(RecipeRunText.KeyFor).OfType<string>()];

        Assert.Equal(refusals.Length, refusals.Distinct().Count());
        Assert.Equal(statuses.Length, statuses.Distinct().Count());
        Assert.Equal(runRefusals.Length, runRefusals.Distinct().Count());
    }

    [Fact]
    public void AnUnnamedMemberIsAnErrorRatherThanAnEmptyText()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeRunText.KeyFor((RecipeValueRefusal)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeRunText.KeyFor((RecipeRunStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeRunText.KeyFor((RecipeRunRefusal)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeRunText.KeyFor((RecipeProgramComparison)999));
    }

    [Theory]
    [MemberData(nameof(LocaleNames))]
    public void TheFormatsOfEveryLanguageTakeTheSameArgumentsAsTheEnglishOnes(string locale)
    {
        Dictionary<string, string> english = ReadLocale("en");
        Dictionary<string, string> strings = ReadLocale(locale);
        string[] formatted =
        [
            "RecipeProvenanceFormat", "RecipeTabTitleFormat", "RecipeAccessibleNameFormat", "RecipeStatusFailedExitCode",
            "RecipeStatusTimedOut", "RecipeStatusTimedOutOne", "RecipeStatusRefused", "RecipeProgramDifferent",
            "RecipeProgramNoLongerResolves", "RecipeSessionTargetHostPort", "RecipeSessionTargetHost",
            "RecipeSessionTargetPort", "RecipeRunSessionValueRefused", "RecipeRunValuesRefused", "RecipeOutputRows",
            "RecipeOutputRowsOne", "A11yRecipeResultRow",
        ];

        foreach (string key in formatted)
        {
            Assert.Equal(Placeholders(english[key]), Placeholders(strings[key]));
        }
    }

    private static string Placeholders(string text)
        => string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order(StringComparer.Ordinal));
}
