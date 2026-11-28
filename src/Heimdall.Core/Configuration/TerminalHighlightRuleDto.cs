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

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Configurable regex-based log highlight rule rendered in the embedded SSH terminal.
/// </summary>
public sealed class TerminalHighlightRuleDto
{
    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    /// <summary>Color key: <c>red</c>, <c>yellow</c>, <c>green</c>, <c>cyan</c>, <c>magenta</c>, or <c>blue</c>.</summary>
    public string Color { get; set; } = "red";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Default rules, validation, and safe serialization for terminal log highlighting.
/// </summary>
public static class TerminalHighlightRuleDefaults
{
    public const int MaxPatternLength = 200;
    public const int MaxRuleCount = 32;

    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly IReadOnlyDictionary<string, string> ColorToAnsi =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = "\x1b[1;31m",
            ["yellow"] = "\x1b[33m",
            ["green"] = "\x1b[32m",
            ["cyan"] = "\x1b[36m",
            ["magenta"] = "\x1b[35m",
            ["blue"] = "\x1b[34m"
        };

    public static IReadOnlyList<string> SupportedColors { get; } = ["red", "yellow", "green", "cyan", "magenta", "blue"];

    public static List<TerminalHighlightRuleDto> CreateDefaultRules() =>
    [
        new()
        {
            Name = "Errors",
            Pattern = @"\b(?:FATAL|CRITICAL|ERROR|FAILED|FAIL|EXCEPTION)\b",
            Color = "red",
            Enabled = true
        },
        new()
        {
            Name = "Warnings",
            Pattern = @"\b(?:WARNING|WARN|DEPRECATED)\b",
            Color = "yellow",
            Enabled = true
        },
        new()
        {
            Name = "Success",
            Pattern = @"\b(?:SUCCESS|PASSED|READY|ACTIVE|OK)\b",
            Color = "green",
            Enabled = true
        },
        new()
        {
            Name = "IPv4",
            Pattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
            Color = "cyan",
            Enabled = true
        }
    ];

    public static string NormalizeColor(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color))
        {
            var trimmed = color.Trim().ToLowerInvariant();
            if (ColorToAnsi.ContainsKey(trimmed))
            {
                return trimmed;
            }
        }

        return "cyan";
    }

    public static string ToAnsiCode(string? color)
    {
        var key = NormalizeColor(color);
        return ColorToAnsi.TryGetValue(key, out var ansi) ? ansi : "\x1b[36m";
    }

    /// <summary>
    /// Validates a highlight rule's regex pattern syntax and length bounds.
    /// </summary>
    public static bool TryValidateRule(string? pattern, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            errorMessage = "Pattern is required.";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            errorMessage = $"Pattern must be at most {MaxPatternLength} characters.";
            return false;
        }

        if (HasUnsafeRegexConstruct(pattern))
        {
            errorMessage = "Pattern contains nested quantifiers or backreferences that may stall terminal rendering.";
            return false;
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.None, ValidationTimeout);
            _ = regex.IsMatch("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!");
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            errorMessage = "Pattern exceeded the validation timeout and may stall terminal rendering.";
            return false;
        }
        catch (ArgumentException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool HasUnsafeRegexConstruct(string pattern)
    {
        // Reject backreferences (\1..\9) and nested repeating quantifiers such as (a+)+, (a*)*, (\w+){2,}.
        var groupHasQuantifier = new Stack<bool>();
        bool escaped = false;
        bool inCharClass = false;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (escaped)
            {
                if (!inCharClass && c is >= '1' and <= '9')
                {
                    return true;
                }

                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '[' && !inCharClass)
            {
                inCharClass = true;
                continue;
            }

            if (c == ']' && inCharClass)
            {
                inCharClass = false;
                continue;
            }

            if (inCharClass)
            {
                continue;
            }

            if (c == '(')
            {
                groupHasQuantifier.Push(false);
                continue;
            }

            if (c == ')')
            {
                if (groupHasQuantifier.Count > 0)
                {
                    bool innerQuantified = groupHasQuantifier.Pop();
                    if (innerQuantified && IsOuterDangerousQuantifierAt(pattern, i + 1))
                    {
                        return true;
                    }

                    if (innerQuantified && groupHasQuantifier.Count > 0)
                    {
                        _ = groupHasQuantifier.Pop();
                        groupHasQuantifier.Push(true);
                    }
                }

                continue;
            }

            if (IsInnerUnboundedQuantifierAt(pattern, i) && groupHasQuantifier.Count > 0)
            {
                _ = groupHasQuantifier.Pop();
                groupHasQuantifier.Push(true);
            }
        }

        return false;
    }

    private static bool IsInnerUnboundedQuantifierAt(string pattern, int index)
    {
        if (index < 0 || index >= pattern.Length)
        {
            return false;
        }

        char c = pattern[index];
        if (c is '+' or '*')
        {
            return true;
        }

        if (c == '{')
        {
            int close = pattern.IndexOf('}', index + 1);
            if (close > index + 1)
            {
                string body = pattern[(index + 1)..close];
                int comma = body.IndexOf(',');
                if (comma >= 0)
                {
                    string upper = body[(comma + 1)..].Trim();
                    return upper.Length == 0 || (int.TryParse(upper, out int max) && max > 16);
                }
            }
        }

        return false;
    }

    private static bool IsOuterDangerousQuantifierAt(string pattern, int index)
    {
        if (index < 0 || index >= pattern.Length)
        {
            return false;
        }

        char c = pattern[index];
        if (c is '+' or '*')
        {
            return true;
        }

        if (c == '{')
        {
            int close = pattern.IndexOf('}', index + 1);
            if (close > index + 1)
            {
                string body = pattern[(index + 1)..close];
                int comma = body.IndexOf(',');
                if (comma >= 0)
                {
                    return true;
                }

                if (int.TryParse(body, out int exact) && exact > 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the effective list of valid, enabled rules, falling back to built-in defaults
    /// when no user rules have been configured.
    /// </summary>
    public static IReadOnlyList<TerminalHighlightRuleDto> ResolveEffectiveRules(
        IReadOnlyList<TerminalHighlightRuleDto>? configured)
    {
        var source = configured is { Count: > 0 } ? configured : CreateDefaultRules();
        var result = new List<TerminalHighlightRuleDto>(Math.Min(source.Count, MaxRuleCount));
        foreach (var rule in source)
        {
            if (result.Count >= MaxRuleCount)
            {
                break;
            }

            if (rule is null || !rule.Enabled || !TryValidateRule(rule.Pattern, out _))
            {
                continue;
            }

            result.Add(new TerminalHighlightRuleDto
            {
                Name = rule.Name ?? string.Empty,
                Pattern = rule.Pattern,
                Color = NormalizeColor(rule.Color),
                Enabled = true
            });
        }

        return result;
    }

    /// <summary>
    /// Serializes effective highlight rules as a JSON array safe for inline embedding into <c>terminal.html</c>.
    /// </summary>
    public static string SerializeForTerminalJs(IReadOnlyList<TerminalHighlightRuleDto>? configured)
    {
        var effective = ResolveEffectiveRules(configured);
        var payload = effective.Select(static r => new TerminalHighlightJsRule(r.Pattern, ToAnsiCode(r.Color))).ToArray();
        return JsonSerializer.Serialize(payload);
    }

    private sealed record TerminalHighlightJsRule(
        [property: System.Text.Json.Serialization.JsonPropertyName("pattern")] string Pattern,
        [property: System.Text.Json.Serialization.JsonPropertyName("ansi")] string Ansi);
}
