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

using Heimdall.Core.Localization;

namespace Heimdall.Core.Tests;

/// <summary>
/// Pins the count wording available to code that localizes through a bare key delegate: the
/// engines and tool helpers that hold a <see cref="Func{T, TResult}"/> and no
/// <see cref="LocalizationManager"/>.
/// </summary>
public sealed class CountLocalizerTests
{
    private static string Catalogue(string key) => key switch
    {
        "ThingsOne" => "{0} thing",
        "Things" => "{0} things",
        _ => key,
    };

    [Theory]
    [InlineData("en", 0, "0 things")]
    [InlineData("en", 1, "1 thing")]
    [InlineData("en", 2, "2 things")]
    [InlineData("es", 0, "0 things")]
    [InlineData("es", 1, "1 thing")]
    [InlineData("fr", 0, "0 thing")]
    [InlineData("fr", 1, "1 thing")]
    [InlineData("fr", 2, "2 things")]
    public void DelegateCountLocalizer_PicksTheKeyByTheLanguageRule(string locale, long count, string expected)
    {
        ICountLocalizer countLocalizer = new DelegateCountLocalizer(Catalogue, () => locale);

        Assert.Equal(expected, countLocalizer.FormatCount(count, "ThingsOne", "Things", count));
    }

    /// <summary>The language is read at each call, so a switch made after construction is followed.</summary>
    [Fact]
    public void DelegateCountLocalizer_FollowsALanguageReadAtEachCall()
    {
        string locale = "en";
        ICountLocalizer countLocalizer = new DelegateCountLocalizer(Catalogue, () => locale);
        Assert.Equal("0 things", countLocalizer.FormatCount(0, "ThingsOne", "Things", 0));

        locale = "fr";

        Assert.Equal("0 thing", countLocalizer.FormatCount(0, "ThingsOne", "Things", 0));
    }

    /// <summary>A template the arguments do not fit is returned as it stands, as Format does.</summary>
    [Fact]
    public void DelegateCountLocalizer_ReturnsAMalformedTemplateUnformatted()
    {
        ICountLocalizer countLocalizer = new DelegateCountLocalizer(key => "{1} broken", () => "en");

        Assert.Equal("{1} broken", countLocalizer.FormatCount(2, "ThingsOne", "Things", 2));
    }

    /// <summary>The shell's manager is a count localizer too, so engines can be handed it directly.</summary>
    [Fact]
    public void LocalizationManager_IsACountLocalizer()
    {
        ICountLocalizer countLocalizer = new LocalizationManager();

        Assert.Equal("Things", countLocalizer.FormatCount(2, "ThingsOne", "Things", 2));
    }

    [Theory]
    [InlineData(0, "0 files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    public void SelectEnglish_WordsAFallbackByItsNumber(long count, string expected)
    {
        Assert.Equal(expected, PluralRules.SelectEnglish(count, $"{count} file", $"{count} files"));
    }
}
