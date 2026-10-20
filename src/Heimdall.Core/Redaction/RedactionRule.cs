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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Redaction;

/// <summary>
/// One masking rule of the catalogue. Rules are applied in catalogue order, each on the output of the
/// previous one, so a span already masked is never counted again by a later rule.
/// </summary>
/// <remarks>
/// Input lines are logical lines separated by <c>\n</c>. A secret wrapped over several terminal rows is
/// not this type's problem: the terminal bridge joins wrapped rows before calling the redactor.
/// <para>
/// Timeout design: in .NET the match timeout applies to one Regex operation, and a single scan over a
/// 5 MB input is one operation. Every pattern therefore declares <see cref="MatchTimeout"/> (a hang
/// fails closed, see <see cref="TextRedactor"/>) and single-line rules are run chunk by chunk
/// (<see cref="RedactionApply.Replace"/>) so the budget is per chunk, not per buffer. The timeout is
/// kept backtracking-capable because the patterns need lookarounds, which NonBacktracking does not
/// support; every quantifier is bounded instead.
/// </para>
/// </remarks>
public sealed class RedactionRule
{
    /// <summary>Per-operation match timeout shared by every pattern, in milliseconds.</summary>
    public const int MatchTimeoutMilliseconds = 2000;

    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds);

    private readonly Func<string, RedactionRun, string> _apply;

    internal RedactionRule(
        string id,
        IReadOnlyList<Regex> patterns,
        Func<string, RedactionRun, string> apply,
        bool requiresPseudonymization = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(apply);
        Id = id;
        Patterns = patterns;
        _apply = apply;
        RequiresPseudonymization = requiresPseudonymization;
    }

    /// <summary>Stable kebab-case identifier, also written inside the mask token.</summary>
    public string Id { get; }

    /// <summary>True for rules that run only when <see cref="RedactionOptions.PseudonymizeHosts"/> is set.</summary>
    public bool RequiresPseudonymization { get; }

    /// <summary>Every regular expression the rule runs, so a guard can assert they all declare a timeout.</summary>
    internal IReadOnlyList<Regex> Patterns { get; }

    internal string Apply(string text, RedactionRun run) => _apply(text, run);
}
