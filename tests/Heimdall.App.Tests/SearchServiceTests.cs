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
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

// Producer: SearchService.SearchWithScoringAsync (SearchService.cs:64) and SearchAsync (line 41).
// Field weights: Title 100 > Tags 70 > Description 50 > Category 40 > Template 30 > Notes 20;
// fuzzy bonus up to 20. SearchService depends only on the static TextNormalizer, so no mocks.
public sealed class SearchServiceTests
{
    // 1) Field-weight ranking: a title match (100) ranks above a description-only match (50).
    [Fact]
    public async Task SearchWithScoring_TitleMatchRanksAboveDescriptionMatch()
    {
        SearchService service = new SearchService();
        ActionModel titleHit = MakeAction("title-hit", title: "alpha");
        ActionModel descriptionHit = MakeAction("desc-hit", title: "beta", description: "alpha details");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([titleHit, descriptionHit], "alpha")).ToList();

        results.Should().HaveCount(2);
        results[0].Action.Id.Should().Be("title-hit");
        results[0].Score.Should().Be(100);
        results[1].Action.Id.Should().Be("desc-hit");
        results[1].Score.Should().Be(50);
    }

    // 2) Cumulative scoring: a Title + Tags match (170) outranks a Title-only match (100).
    [Fact]
    public async Task SearchWithScoring_TitleAndTagsMatch_SumsScores()
    {
        SearchService service = new SearchService();
        ActionModel both = MakeAction("both", title: "user mgmt", tags: ["user"]);
        ActionModel titleOnly = MakeAction("title-only", title: "user list");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([titleOnly, both], "user")).ToList();

        results[0].Action.Id.Should().Be("both");
        results[0].Score.Should().Be(170);
        results[1].Action.Id.Should().Be("title-only");
        results[1].Score.Should().Be(100);
    }

    // 3) Multi-word AND logic: only actions whose field contains BOTH tokens match.
    [Fact]
    public async Task SearchWithScoring_MultiWordQuery_RequiresAllTokens()
    {
        SearchService service = new SearchService();
        ActionModel both = MakeAction("both", title: "add user to group");
        ActionModel partial = MakeAction("partial", title: "add role");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([both, partial], "add user")).ToList();

        results.Should().ContainSingle();
        results[0].Action.Id.Should().Be("both");
    }

    // 4) score>0 filter: non-matching actions are absent from results.
    [Fact]
    public async Task SearchWithScoring_NonMatching_AreExcluded()
    {
        SearchService service = new SearchService();
        ActionModel match = MakeAction("match", title: "network ping");
        ActionModel noMatch = MakeAction("no-match", title: "disk usage");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([match, noMatch], "network", enableFuzzySearch: false)).ToList();

        results.Should().ContainSingle();
        results[0].Action.Id.Should().Be("match");
    }

    // 5) Passthrough on empty/whitespace term.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_EmptyTerm_ReturnsAllActionsUnchanged(string term)
    {
        SearchService service = new SearchService();
        ActionModel a = MakeAction("a", title: "alpha");
        ActionModel b = MakeAction("b", title: "beta");

        List<ActionModel> results = (await service.SearchAsync([a, b], term)).ToList();

        results.Should().Equal(a, b);
    }

    [Fact]
    public async Task SearchWithScoring_EmptyTerm_ReturnsAllWithZeroScore()
    {
        SearchService service = new SearchService();
        ActionModel a = MakeAction("a", title: "alpha");
        ActionModel b = MakeAction("b", title: "beta");

        List<SearchResult> results = (await service.SearchWithScoringAsync([a, b], "")).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Score == 0);
    }

    // 6) A punctuation-only query normalises to zero tokens -> returns all with Score 0.
    [Fact]
    public async Task SearchWithScoring_PunctuationOnlyQuery_ReturnsAllWithZeroScore()
    {
        SearchService service = new SearchService();
        ActionModel a = MakeAction("a", title: "alpha");
        ActionModel b = MakeAction("b", title: "beta");

        List<SearchResult> results = (await service.SearchWithScoringAsync([a, b], "---")).ToList();

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Score == 0);
    }

    // 7) Normalisation integration through SearchService (accents + punctuation).
    [Fact]
    public async Task SearchWithScoring_AccentInsensitive_MatchesNormalizedTitle()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("reseau", title: "Réseau");

        List<SearchResult> results = (await service.SearchWithScoringAsync([action], "reseau")).ToList();

        results.Should().ContainSingle();
        results[0].Score.Should().Be(100);
    }

    [Fact]
    public async Task SearchWithScoring_PunctuationInsensitive_MatchesHyphenatedTitle()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("get-service", title: "Get-Service");

        List<SearchResult> results = (await service.SearchWithScoringAsync([action], "Get Service")).ToList();

        results.Should().ContainSingle();
        results[0].Score.Should().Be(100);
    }

    // 8) Fuzzy matching: a single-typo query with no exact match yields a fuzzy result.
    [Fact]
    public async Task SearchWithScoring_FuzzyEnabled_SingleTypo_YieldsFuzzyResult()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("svc", title: "service");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([action], "serviec", enableFuzzySearch: true)).ToList();

        results.Should().ContainSingle();
        results[0].Breakdown.FuzzyMatchBonus.Should().BeGreaterThan(0);
        results[0].IsExactMatch.Should().BeFalse();
    }

    [Fact]
    public async Task SearchWithScoring_FuzzyEnabled_UnrelatedQuery_YieldsNoResults()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("svc", title: "service");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([action], "zzzzzz", enableFuzzySearch: true)).ToList();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchWithScoring_FuzzyEnabled_MultiTokenWithOneNoMatch_YieldsNoResults()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("svc", title: "service");

        // "serviec" fuzzy-matches "service", but "zzzzzz" matches nothing -> AND logic fails.
        List<SearchResult> results =
            (await service.SearchWithScoringAsync([action], "serviec zzzzzz", enableFuzzySearch: true)).ToList();

        results.Should().BeEmpty();
    }

    // 9) With fuzzy disabled, the same typo query yields nothing.
    [Fact]
    public async Task SearchWithScoring_FuzzyDisabled_SingleTypo_YieldsNoResults()
    {
        SearchService service = new SearchService();
        ActionModel action = MakeAction("svc", title: "service");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([action], "serviec", enableFuzzySearch: false)).ToList();

        results.Should().BeEmpty();
    }

    // 10) Tie-break = stable input order. OrderByDescending is stable and there is no secondary
    // sort, so two equally-scored title matches keep their original input order. This test
    // documents the current behavior (no deterministic secondary tie-break key).
    [Fact]
    public async Task SearchWithScoring_EqualScores_PreserveInputOrder()
    {
        SearchService service = new SearchService();
        ActionModel first = MakeAction("first", title: "alpha one");
        ActionModel second = MakeAction("second", title: "alpha two");

        List<SearchResult> results =
            (await service.SearchWithScoringAsync([first, second], "alpha")).ToList();

        results.Should().HaveCount(2);
        results[0].Score.Should().Be(100);
        results[1].Score.Should().Be(100);
        results[0].Action.Id.Should().Be("first");
        results[1].Action.Id.Should().Be("second");
    }

    private static ActionModel MakeAction(
        string id,
        string title = "",
        string description = "",
        string category = "",
        List<string>? tags = null,
        string? notes = null,
        CommandTemplate? windowsTemplate = null)
    {
        return new ActionModel
        {
            Id = id,
            Title = title,
            Description = description,
            Category = category,
            Platform = Platform.Both,
            Level = CriticalityLevel.Info,
            Tags = tags ?? new List<string>(),
            Notes = notes,
            WindowsCommandTemplate = windowsTemplate
        };
    }
}
