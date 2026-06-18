/*
 * Copyright 2025 Julien Bombled
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

using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Models;

/// <summary>
/// Represents a search result with relevance scoring.
/// Used to sort search results by relevance to the search query.
/// </summary>
public sealed class SearchResult
{
    /// <summary>
    /// The action that matched the search query
    /// </summary>
    public required ActionModel Action { get; init; }

    /// <summary>
    /// Relevance score (higher = more relevant)
    /// Scoring:
    /// - Title match: 100 points
    /// - Tags match: 70 points
    /// - Description match: 50 points
    /// - Category match: 40 points
    /// - Template match: 30 points
    /// - Notes match: 20 points
    /// - Fuzzy match bonus: up to 20 additional points
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Breakdown of the score for debugging/display purposes
    /// </summary>
    public SearchScoreBreakdown Breakdown { get; init; } = new();

    /// <summary>
    /// Whether the match was exact (no fuzzy matching needed)
    /// </summary>
    public bool IsExactMatch { get; init; }
}

/// <summary>
/// Detailed breakdown of how the relevance score was calculated
/// </summary>
public sealed class SearchScoreBreakdown
{
    public double TitleScore { get; set; }
    public double TagsScore { get; set; }
    public double DescriptionScore { get; set; }
    public double CategoryScore { get; set; }
    public double TemplateScore { get; set; }
    public double NotesScore { get; set; }
    public double FuzzyMatchBonus { get; set; }

    public double TotalScore => TitleScore + TagsScore + DescriptionScore +
                                CategoryScore + TemplateScore + NotesScore +
                                FuzzyMatchBonus;
}
