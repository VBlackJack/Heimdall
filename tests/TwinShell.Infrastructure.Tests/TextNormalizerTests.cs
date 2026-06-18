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

using FluentAssertions;
using TwinShell.Core.Helpers;

namespace TwinShell.Infrastructure.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeForSearch_NullOrWhitespace_ReturnsEmpty(string? text)
    {
        string actual = TextNormalizer.NormalizeForSearch(text);

        actual.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeForSearch_StripsAccentsLowercasesSeparatorsAndCollapsesSpaces()
    {
        string actual = TextNormalizer.NormalizeForSearch("  Café-résumé_naïve.Étapes   Multi   Space  ");

        actual.Should().Be("cafe resume naive etapes multi space");
    }

    [Theory]
    [InlineData("Procedure", "Procedure")]
    [InlineData("Procédure résumé naïve Étapes façade garçon", "Procedure resume naive Etapes facade garcon")]
    [InlineData("niño jalapeño São", "nino jalapeno Sao")]
    public void RemoveDiacritics_StripsAccentMarksAndPreservesBaseCharacters(string text, string expected)
    {
        string actual = TextNormalizer.RemoveDiacritics(text);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("     ")]
    public void GetSearchTokens_Whitespace_ReturnsEmptyArray(string normalizedText)
    {
        string[] actual = TextNormalizer.GetSearchTokens(normalizedText);

        actual.Should().BeEmpty();
    }

    [Fact]
    public void GetSearchTokens_SplitsOnSpacesAndRemovesEmptyEntries()
    {
        string[] actual = TextNormalizer.GetSearchTokens("get  active directory   user");

        actual.Should().Equal("get", "active", "directory", "user");
    }

    [Fact]
    public void ContainsAllTokens_NullOrEmptyTokenSet_ReturnsTrueWhenSearchableTextExists()
    {
        TextNormalizer.ContainsAllTokens("active directory", null!).Should().BeTrue();
        TextNormalizer.ContainsAllTokens("active directory", []).Should().BeTrue();
    }

    [Fact]
    public void ContainsAllTokens_RequiresEveryTokenButNotOrder()
    {
        TextNormalizer.ContainsAllTokens("active directory user management", ["user", "directory"]).Should().BeTrue();
        TextNormalizer.ContainsAllTokens("active directory", ["user", "directory"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsAllTokens_EmptySearchableText_ReturnsFalseEvenForEmptyTokenSet()
    {
        TextNormalizer.ContainsAllTokens("", []).Should().BeFalse();
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("", "user", 4)]
    [InlineData("user", "", 4)]
    [InlineData("network", "network", 0)]
    [InlineData("user", "usr", 1)]
    [InlineData("service", "serviec", 2)]
    public void LevenshteinDistance_ReturnsExpectedEditDistance(string source, string target, int expected)
    {
        int actual = TextNormalizer.LevenshteinDistance(source, target);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "user")]
    [InlineData("service", null)]
    [InlineData("", "user")]
    [InlineData("service", "")]
    public void GetFuzzyMatchScore_NullOrEmptyInputs_ReturnsZero(string? searchableText, string? searchToken)
    {
        double actual = TextNormalizer.GetFuzzyMatchScore(searchableText!, searchToken!);

        actual.Should().Be(0.0);
    }

    [Fact]
    public void GetFuzzyMatchScore_ExactContainedToken_ReturnsPerfectScore()
    {
        double actual = TextNormalizer.GetFuzzyMatchScore("get service status", "service");

        actual.Should().Be(1.0);
    }

    [Fact]
    public void GetFuzzyMatchScore_ReturnsBestWordSimilarityWithinThreshold()
    {
        double actual = TextNormalizer.GetFuzzyMatchScore("get service status", "serviec");

        actual.Should().BeApproximately(1.0 - (2.0 / 7.0), 0.0001);
    }

    [Theory]
    [InlineData("abcdefghij", "abcxxxghij", 0.7)]
    [InlineData("abcdefghij", "abcxxxxhij", 0.0)]
    public void GetFuzzyMatchScore_ThirtyPercentBoundaryIsIncludedOnlyAtOrBelowThreshold(
        string searchableText,
        string searchToken,
        double expected)
    {
        double actual = TextNormalizer.GetFuzzyMatchScore(searchableText, searchToken);

        actual.Should().BeApproximately(expected, 0.0001);
    }
}
