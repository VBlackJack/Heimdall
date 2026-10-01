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

using System.Text;
using Heimdall.Core.Localization;

namespace Heimdall.Core.Tests;

/// <summary>
/// Pins which wording of a count each shipped language picks.
/// </summary>
/// <remarks>
/// English and Spanish use the singular for 1 only. French uses it for 0 and 1: "0 session",
/// never "0 sessions". The catalogues here are written for the test so the rule is measured on
/// its own, apart from any wording in the shipped files.
/// </remarks>
public sealed class LocalizationPluralTests : IDisposable
{
    private readonly string _localesPath;

    public LocalizationPluralTests()
    {
        _localesPath = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.PluralTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localesPath);
        WriteCatalogue("en", "{0} thing", "{0} things");
        WriteCatalogue("fr", "{0} chose", "{0} choses");
        WriteCatalogue("es", "{0} cosa", "{0} cosas");
    }

    public void Dispose()
    {
        if (Directory.Exists(_localesPath))
        {
            Directory.Delete(_localesPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("en", 0, "0 things")]
    [InlineData("en", 1, "1 thing")]
    [InlineData("en", 2, "2 things")]
    [InlineData("es", 0, "0 cosas")]
    [InlineData("es", 1, "1 cosa")]
    [InlineData("es", 2, "2 cosas")]
    [InlineData("fr", 0, "0 chose")]
    [InlineData("fr", 1, "1 chose")]
    [InlineData("fr", 2, "2 choses")]
    public async Task FormatCount_PicksTheWordingTheLanguageGivesTheNumber(
        string locale,
        long count,
        string expected)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(_localesPath, locale);

        string text = manager.FormatCount(count, "ThingsOne", "Things", count);

        Assert.Equal(expected, text);
    }

    /// <summary>The rule follows a language switch rather than the language loaded first.</summary>
    [Fact]
    public async Task FormatCount_FollowsALanguageSwitch()
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(_localesPath, "en");
        Assert.Equal("0 things", manager.FormatCount(0, "ThingsOne", "Things", 0));

        await manager.SwitchLocaleAsync("fr");

        Assert.Equal("0 chose", manager.FormatCount(0, "ThingsOne", "Things", 0));
    }

    /// <summary>A region does not change the language's rule; a longer code is another language.</summary>
    [Theory]
    [InlineData("fr-CA", 0, true)]
    [InlineData("FR", 0, true)]
    [InlineData("en-GB", 0, false)]
    [InlineData("fry", 0, false)]
    [InlineData("fr", 2, false)]
    public void IsOne_ReadsTheLanguageOfTheLocale(string locale, long count, bool expected)
    {
        Assert.Equal(expected, PluralRules.IsOne(locale, count));
    }

    private void WriteCatalogue(string locale, string one, string other)
    {
        string json = "{ \"ThingsOne\": \"" + one + "\", \"Things\": \"" + other + "\" }";
        File.WriteAllText(Path.Combine(_localesPath, locale + ".json"), json, new UTF8Encoding(false));
    }
}
