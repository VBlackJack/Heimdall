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

using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for searching actions with advanced text normalization, relevance scoring, and fuzzy matching.
/// Features:
/// - Case-insensitive, accent-insensitive, and punctuation-normalized search
/// - Multi-word queries with AND logic
/// - Relevance scoring (Title > Tags > Description > Category > Template > Notes)
/// - Fuzzy matching with Levenshtein distance (tolerates typos up to 30%)
/// </summary>
public sealed class SearchService : ISearchService
{
    // Relevance score weights
    private const double TITLE_SCORE_WEIGHT = 100.0;
    private const double TAGS_SCORE_WEIGHT = 70.0;
    private const double DESCRIPTION_SCORE_WEIGHT = 50.0;
    private const double CATEGORY_SCORE_WEIGHT = 40.0;
    private const double TEMPLATE_SCORE_WEIGHT = 30.0;
    private const double NOTES_SCORE_WEIGHT = 20.0;
    private const double FUZZY_MATCH_BONUS_MAX = 20.0;
    /// <summary>
    /// Searches actions based on normalized text matching.
    /// Search fields: Title, Description, Category, Tags, Notes, Command Templates (Name, Pattern).
    ///
    /// Normalization includes:
    /// - Case-insensitive matching
    /// - Accent/diacritic removal (café = cafe, réseau = reseau)
    /// - Punctuation normalization (Get-Service = Get Service = GetService)
    /// - Multi-word search with AND logic (all words must match)
    ///
    /// Results are sorted by relevance score (title matches first, then tags, then description, etc.)
    /// </summary>
    /// <param name="actions">Actions to search</param>
    /// <param name="searchTerm">Search query (can be multi-word)</param>
    /// <returns>Matching actions sorted by relevance</returns>
    public async Task<IEnumerable<ActionModel>> SearchAsync(
        IEnumerable<ActionModel> actions,
        string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return actions;
        }

        // Use scoring search and extract just the actions
        var searchResults = await SearchWithScoringAsync(actions, searchTerm).ConfigureAwait(false);
        return searchResults.Select(r => r.Action);
    }

    /// <summary>
    /// Searches actions with relevance scoring and fuzzy matching.
    /// Returns SearchResult objects with detailed scoring information.
    /// Results are sorted by relevance score (highest first).
    /// </summary>
    /// <param name="actions">Actions to search</param>
    /// <param name="searchTerm">Search query (can be multi-word)</param>
    /// <param name="enableFuzzySearch">Enable fuzzy matching for typo tolerance (default: true)</param>
    /// <returns>Search results with scoring, sorted by relevance</returns>
    public async Task<IEnumerable<SearchResult>> SearchWithScoringAsync(
        IEnumerable<ActionModel> actions,
        string searchTerm,
        bool enableFuzzySearch = true)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            // Return all actions with zero score
            return actions.Select(a => new SearchResult
            {
                Action = a,
                Score = 0,
                Breakdown = new SearchScoreBreakdown(),
                IsExactMatch = true
            });
        }

        // Normalize search term and extract tokens for multi-word search
        var normalizedSearchTerm = TextNormalizer.NormalizeForSearch(searchTerm);
        var searchTokens = TextNormalizer.GetSearchTokens(normalizedSearchTerm);

        // If no valid tokens after normalization, return all actions
        if (searchTokens.Length == 0)
        {
            return actions.Select(a => new SearchResult
            {
                Action = a,
                Score = 0,
                Breakdown = new SearchScoreBreakdown(),
                IsExactMatch = true
            });
        }

        // Score each action
        var searchResults = new List<SearchResult>();

        foreach (var action in actions)
        {
            var scoreBreakdown = CalculateRelevanceScore(action, normalizedSearchTerm, searchTokens, enableFuzzySearch);

            // Only include actions with score > 0 (i.e., they match the search)
            if (scoreBreakdown.TotalScore > 0)
            {
                searchResults.Add(new SearchResult
                {
                    Action = action,
                    Score = scoreBreakdown.TotalScore,
                    Breakdown = scoreBreakdown,
                    IsExactMatch = scoreBreakdown.FuzzyMatchBonus == 0
                });
            }
        }

        // Sort by score descending (highest relevance first)
        var sortedResults = searchResults.OrderByDescending(r => r.Score);

        return await Task.FromResult(sortedResults).ConfigureAwait(false);
    }

    /// <summary>
    /// Calculates the relevance score for an action based on search terms.
    /// Scores different fields with different weights:
    /// - Title: 100 points (highest priority)
    /// - Tags: 70 points
    /// - Description: 50 points
    /// - Category: 40 points
    /// - Templates: 30 points
    /// - Notes: 20 points
    /// - Fuzzy match bonus: up to 20 points
    /// </summary>
    private SearchScoreBreakdown CalculateRelevanceScore(
        ActionModel action,
        string normalizedSearchTerm,
        string[] searchTokens,
        bool enableFuzzySearch)
    {
        var breakdown = new SearchScoreBreakdown();

        // Normalize all fields
        var normalizedTitle = TextNormalizer.NormalizeForSearch(action.Title);
        var normalizedDescription = TextNormalizer.NormalizeForSearch(action.Description);
        var normalizedCategory = TextNormalizer.NormalizeForSearch(action.Category);
        var normalizedNotes = TextNormalizer.NormalizeForSearch(action.Notes);

        // Check for exact token matches in each field
        bool titleMatches = TextNormalizer.ContainsAllTokens(normalizedTitle, searchTokens);
        bool descriptionMatches = TextNormalizer.ContainsAllTokens(normalizedDescription, searchTokens);
        bool categoryMatches = TextNormalizer.ContainsAllTokens(normalizedCategory, searchTokens);
        bool notesMatches = !string.IsNullOrWhiteSpace(normalizedNotes) &&
                           TextNormalizer.ContainsAllTokens(normalizedNotes, searchTokens);

        // Check tags
        bool tagsMatch = false;
        if (action.Tags != null && action.Tags.Count > 0)
        {
            var normalizedTags = action.Tags.Select(TextNormalizer.NormalizeForSearch).ToList();
            var allTagsText = string.Join(" ", normalizedTags);
            tagsMatch = TextNormalizer.ContainsAllTokens(allTagsText, searchTokens);
        }

        // Check templates
        bool templateMatches = false;
        if (action.WindowsCommandTemplate != null)
        {
            var winTemplateName = TextNormalizer.NormalizeForSearch(action.WindowsCommandTemplate.Name);
            var winTemplatePattern = TextNormalizer.NormalizeForSearch(action.WindowsCommandTemplate.CommandPattern);
            var winTemplateText = $"{winTemplateName} {winTemplatePattern}";
            templateMatches = TextNormalizer.ContainsAllTokens(winTemplateText, searchTokens);
        }

        if (!templateMatches && action.LinuxCommandTemplate != null)
        {
            var linuxTemplateName = TextNormalizer.NormalizeForSearch(action.LinuxCommandTemplate.Name);
            var linuxTemplatePattern = TextNormalizer.NormalizeForSearch(action.LinuxCommandTemplate.CommandPattern);
            var linuxTemplateText = $"{linuxTemplateName} {linuxTemplatePattern}";
            templateMatches = TextNormalizer.ContainsAllTokens(linuxTemplateText, searchTokens);
        }

        // Calculate exact match scores
        if (titleMatches) breakdown.TitleScore = TITLE_SCORE_WEIGHT;
        if (tagsMatch) breakdown.TagsScore = TAGS_SCORE_WEIGHT;
        if (descriptionMatches) breakdown.DescriptionScore = DESCRIPTION_SCORE_WEIGHT;
        if (categoryMatches) breakdown.CategoryScore = CATEGORY_SCORE_WEIGHT;
        if (templateMatches) breakdown.TemplateScore = TEMPLATE_SCORE_WEIGHT;
        if (notesMatches) breakdown.NotesScore = NOTES_SCORE_WEIGHT;

        // If no exact matches and fuzzy search is enabled, try fuzzy matching
        if (breakdown.TotalScore == 0 && enableFuzzySearch)
        {
            double fuzzyScore = CalculateFuzzyMatchScore(action, searchTokens);
            breakdown.FuzzyMatchBonus = fuzzyScore;
        }

        return breakdown;
    }

    /// <summary>
    /// Calculates fuzzy match score for an action when no exact matches are found.
    /// Uses Levenshtein distance to tolerate typos (up to 30% character difference).
    /// </summary>
    private double CalculateFuzzyMatchScore(ActionModel action, string[] searchTokens)
    {
        double totalFuzzyScore = 0;

        // Build searchable text from all fields
        var searchableText = BuildNormalizedSearchableText(action);

        // Check fuzzy match for each search token
        foreach (var token in searchTokens)
        {
            double tokenFuzzyScore = TextNormalizer.GetFuzzyMatchScore(searchableText, token);

            // If no fuzzy match found for this token, action doesn't match at all
            if (tokenFuzzyScore == 0)
            {
                return 0; // All tokens must match (AND logic)
            }

            totalFuzzyScore += tokenFuzzyScore;
        }

        // Average fuzzy score across all tokens, then scale to max bonus
        double averageFuzzyScore = totalFuzzyScore / searchTokens.Length;
        return averageFuzzyScore * FUZZY_MATCH_BONUS_MAX;
    }

    /// <summary>
    /// Builds a single normalized searchable string from all action fields.
    /// Includes: Title, Description, Category, Tags, Notes, Template Names, Template Patterns.
    /// </summary>
    private string BuildNormalizedSearchableText(ActionModel action)
    {
        var parts = new List<string>
        {
            TextNormalizer.NormalizeForSearch(action.Title),
            TextNormalizer.NormalizeForSearch(action.Description),
            TextNormalizer.NormalizeForSearch(action.Category)
        };

        // Add all tags
        if (action.Tags != null && action.Tags.Count > 0)
        {
            parts.AddRange(action.Tags.Select(TextNormalizer.NormalizeForSearch));
        }

        // Add notes if present
        if (!string.IsNullOrWhiteSpace(action.Notes))
        {
            parts.Add(TextNormalizer.NormalizeForSearch(action.Notes));
        }

        // Add Windows command template fields
        if (action.WindowsCommandTemplate != null)
        {
            if (!string.IsNullOrWhiteSpace(action.WindowsCommandTemplate.Name))
            {
                parts.Add(TextNormalizer.NormalizeForSearch(action.WindowsCommandTemplate.Name));
            }
            if (!string.IsNullOrWhiteSpace(action.WindowsCommandTemplate.CommandPattern))
            {
                parts.Add(TextNormalizer.NormalizeForSearch(action.WindowsCommandTemplate.CommandPattern));
            }
        }

        // Add Linux command template fields
        if (action.LinuxCommandTemplate != null)
        {
            if (!string.IsNullOrWhiteSpace(action.LinuxCommandTemplate.Name))
            {
                parts.Add(TextNormalizer.NormalizeForSearch(action.LinuxCommandTemplate.Name));
            }
            if (!string.IsNullOrWhiteSpace(action.LinuxCommandTemplate.CommandPattern))
            {
                parts.Add(TextNormalizer.NormalizeForSearch(action.LinuxCommandTemplate.CommandPattern));
            }
        }

        // Join all parts with space separator
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}

