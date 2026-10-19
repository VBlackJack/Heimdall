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

using System.Globalization;

namespace Heimdall.Core.Redaction;

/// <summary>
/// The mask token written in place of a secret. It is a language-neutral technical format,
/// <c>[masked:&lt;rule-id&gt;]</c>, not user-facing text. No catalogue rule may match it, which is
/// what makes redaction idempotent.
/// </summary>
public static class RedactionMask
{
    public const string Prefix = "[masked:";

    public const string Suffix = "]";

    public const string Format = Prefix + "{0}" + Suffix;

    public static string Token(string ruleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleId);
        return string.Format(CultureInfo.InvariantCulture, Format, ruleId);
    }

    /// <summary>True when the whole span is one mask token written by <see cref="Token"/>.</summary>
    public static bool IsMaskToken(ReadOnlySpan<char> value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>True when the span starts with a mask token prefix (a token possibly followed by more text).</summary>
    internal static bool StartsWithMask(ReadOnlySpan<char> value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);
}
