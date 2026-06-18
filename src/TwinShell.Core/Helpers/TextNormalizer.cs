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

using System.Globalization;
using System.Text;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Provides text normalization utilities for search operations.
/// Ensures consistent matching regardless of accents, case, or punctuation variations.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Normalizes text for search by:
    /// - Converting to lowercase
    /// - Removing diacritics/accents (é→e, à→a, ñ→n, etc.)
    /// - Replacing hyphens and underscores with spaces
    /// - Collapsing multiple spaces into single spaces
    /// - Trimming leading/trailing whitespace
    /// </summary>
    /// <param name="text">Text to normalize</param>
    /// <returns>Normalized text suitable for search matching</returns>
    /// <example>
    /// NormalizeForSearch("Get-Service") → "get service"
    /// NormalizeForSearch("Café_réseau") → "cafe reseau"
    /// NormalizeForSearch("Multi   Space") → "multi space"
    /// </example>
    public static string NormalizeForSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Step 1: Remove diacritics (accents)
        var normalized = RemoveDiacritics(text);

        // Step 2: Convert to lowercase
        normalized = normalized.ToLowerInvariant();

        // Step 3: Replace hyphens, underscores, and dots with spaces
        // This allows "Get-Service", "Get_Service", "Get.Service", and "Get Service" to all match
        normalized = normalized.Replace('-', ' ')
                               .Replace('_', ' ')
                               .Replace('.', ' ');

        // Step 4: Collapse multiple spaces into single space
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }

        // Step 5: Trim whitespace
        return normalized.Trim();
    }

    /// <summary>
    /// Removes diacritics (accent marks) from text.
    /// Example: "café" → "cafe", "réseau" → "reseau", "niño" → "nino"
    /// </summary>
    /// <param name="text">Text containing diacritics</param>
    /// <returns>Text with diacritics removed</returns>
    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Normalize to FormD (decomposed form) where accents are separate characters
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(normalizedString.Length);

        // Keep only characters that are not "NonSpacingMark" (accents)
        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        // Normalize back to FormC (composed form)
        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Splits normalized text into individual search tokens (words).
    /// Useful for multi-word searches where all words should match.
    /// </summary>
    /// <param name="normalizedText">Already normalized text</param>
    /// <returns>Array of individual tokens</returns>
    /// <example>
    /// GetSearchTokens("get active directory user") → ["get", "active", "directory", "user"]
    /// </example>
    public static string[] GetSearchTokens(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return Array.Empty<string>();
        }

        return normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Checks if searchable text contains all tokens from the search query.
    /// All tokens must be present (AND logic), but order doesn't matter.
    /// </summary>
    /// <param name="searchableText">Normalized text to search in</param>
    /// <param name="searchTokens">Normalized search tokens</param>
    /// <returns>True if all tokens are found</returns>
    /// <example>
    /// ContainsAllTokens("active directory user management", ["user", "directory"]) → true
    /// ContainsAllTokens("active directory", ["user", "directory"]) → false (missing "user")
    /// </example>
    public static bool ContainsAllTokens(string searchableText, string[] searchTokens)
    {
        if (string.IsNullOrWhiteSpace(searchableText))
        {
            return false;
        }

        if (searchTokens == null || searchTokens.Length == 0)
        {
            return true;
        }

        // All tokens must be present (AND logic)
        return searchTokens.All(token => searchableText.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Calculates the Levenshtein distance between two strings.
    /// Lower distance = more similar strings.
    /// Used for fuzzy matching to tolerate typos.
    /// </summary>
    /// <param name="source">First string</param>
    /// <param name="target">Second string</param>
    /// <returns>Minimum number of edits (insertions, deletions, substitutions) needed to transform source into target</returns>
    /// <example>
    /// LevenshteinDistance("service", "serviec") → 2 (swap 'i' and 'e')
    /// LevenshteinDistance("user", "usr") → 1 (delete 'e')
    /// LevenshteinDistance("network", "network") → 0 (identical)
    /// </example>
    public static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.IsNullOrEmpty(target) ? 0 : target.Length;
        }

        if (string.IsNullOrEmpty(target))
        {
            return source.Length;
        }

        int sourceLength = source.Length;
        int targetLength = target.Length;

        // Create matrix (sourceLength + 1) x (targetLength + 1)
        int[,] distance = new int[sourceLength + 1, targetLength + 1];

        // Initialize first column and row
        for (int i = 0; i <= sourceLength; i++)
        {
            distance[i, 0] = i;
        }

        for (int j = 0; j <= targetLength; j++)
        {
            distance[0, j] = j;
        }

        // Calculate distances
        for (int i = 1; i <= sourceLength; i++)
        {
            for (int j = 1; j <= targetLength; j++)
            {
                int cost = (source[i - 1] == target[j - 1]) ? 0 : 1;

                distance[i, j] = Math.Min(
                    Math.Min(
                        distance[i - 1, j] + 1,      // deletion
                        distance[i, j - 1] + 1),     // insertion
                    distance[i - 1, j - 1] + cost);  // substitution
            }
        }

        return distance[sourceLength, targetLength];
    }

    /// <summary>
    /// Finds the best fuzzy match for a search token within a text.
    /// Returns the similarity score (0.0 = no match, 1.0 = perfect match).
    /// </summary>
    /// <param name="searchableText">Normalized text to search in</param>
    /// <param name="searchToken">Normalized search token</param>
    /// <returns>Similarity score between 0.0 and 1.0, or 0.0 if no fuzzy match found</returns>
    public static double GetFuzzyMatchScore(string searchableText, string searchToken)
    {
        if (string.IsNullOrWhiteSpace(searchableText) || string.IsNullOrWhiteSpace(searchToken))
        {
            return 0.0;
        }

        // If exact match exists, return perfect score
        if (searchableText.Contains(searchToken, StringComparison.Ordinal))
        {
            return 1.0;
        }

        // Split text into words and find best fuzzy match
        var words = searchableText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double bestScore = 0.0;

        foreach (var word in words)
        {
            int distance = LevenshteinDistance(searchToken, word);
            int maxLength = Math.Max(searchToken.Length, word.Length);

            // Only consider reasonable matches (< 30% different)
            double distanceRatio = (double)distance / maxLength;
            if (distanceRatio <= 0.3)
            {
                double similarity = 1.0 - distanceRatio;
                bestScore = Math.Max(bestScore, similarity);
            }
        }

        return bestScore;
    }
}
