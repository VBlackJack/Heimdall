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

namespace Heimdall.Core.Redaction;

/// <summary>
/// Outcome of one redaction. When <see cref="Refused"/> is true a rule hit its match timeout:
/// <see cref="Text"/> is empty and the caller must not copy anything (fail closed).
/// </summary>
public sealed class RedactionResult
{
    private RedactionResult(string text, IReadOnlyDictionary<string, int> counts, bool refused, string? refusedRuleId)
    {
        Text = text;
        Counts = counts;
        Refused = refused;
        RefusedRuleId = refusedRuleId;
    }

    /// <summary>The masked text, or an empty string when <see cref="Refused"/>.</summary>
    public string Text { get; }

    /// <summary>Rule id to number of masked occurrences. Every catalogue rule is present, zero allowed.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }

    public bool Refused { get; }

    /// <summary>The id of the rule that hit its match timeout, or null when not refused.</summary>
    public string? RefusedRuleId { get; }

    internal static RedactionResult Success(string text, IReadOnlyDictionary<string, int> counts) =>
        new(text, counts, refused: false, refusedRuleId: null);

    internal static RedactionResult Refusal(string ruleId, IReadOnlyDictionary<string, int> counts) =>
        new(string.Empty, counts, refused: true, refusedRuleId: ruleId);
}
