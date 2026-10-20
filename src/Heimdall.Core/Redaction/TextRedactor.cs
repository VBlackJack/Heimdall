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
/// Pure, offline redaction of terminal output: secrets are replaced by <c>[masked:&lt;rule-id&gt;]</c>
/// and counted per rule. The result is idempotent: redacting the output again changes nothing and counts zero.
/// </summary>
/// <remarks>
/// Fail closed: when a rule hits its match timeout the result is <see cref="RedactionResult.Refused"/>
/// with an empty text, never a partially masked one. The input is a sequence of logical lines separated by
/// <c>\n</c>; joining rows that the terminal wrapped is the caller's job.
/// </remarks>
public static class TextRedactor
{
    public static RedactionResult Redact(string text) => Redact(text, new RedactionOptions());

    public static RedactionResult Redact(string text, RedactionOptions options) =>
        Redact(RedactionRuleCatalog.Default, text, options);

    internal static RedactionResult Redact(IReadOnlyList<RedactionRule> rules, string text, RedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        var run = new RedactionRun(rules);
        string current = text;
        foreach (RedactionRule rule in rules)
        {
            if (rule.RequiresPseudonymization && !options.PseudonymizeHosts)
            {
                continue;
            }

            try
            {
                current = rule.Apply(current, run);
            }
            catch (RegexMatchTimeoutException)
            {
                return RedactionResult.Refusal(rule.Id, run.ZeroCounts());
            }
        }

        return RedactionResult.Success(current, run.SnapshotCounts());
    }
}
