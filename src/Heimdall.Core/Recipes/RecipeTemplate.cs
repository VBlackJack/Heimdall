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

using System.Text;

namespace Heimdall.Core.Recipes;

/// <summary>What one piece of a token is.</summary>
internal enum TemplateSegmentKind
{
    /// <summary>Fixed text, with doubled braces already unescaped.</summary>
    Literal,

    /// <summary>A <c>{in:name}</c> placeholder; <see cref="TemplateSegment.Text"/> is the input name.</summary>
    Input,

    /// <summary>The <c>{Host}</c> session placeholder.</summary>
    Host,

    /// <summary>The <c>{Port}</c> session placeholder.</summary>
    Port,
}

/// <summary>One piece of a token.</summary>
internal sealed record TemplateSegment(TemplateSegmentKind Kind, string Text);

/// <summary>How a token failed to parse.</summary>
internal enum TemplateFailure
{
    /// <summary>A brace that opens or closes nothing.</summary>
    Malformed,

    /// <summary>A <c>{...}</c> that is not one of the three placeholder forms.</summary>
    Unknown,
}

/// <summary>The result of splitting a token into literal text and placeholders.</summary>
internal sealed class TemplateParse
{
    private TemplateParse(IReadOnlyList<TemplateSegment> segments, TemplateFailure? failure, string fragment)
    {
        Segments = segments;
        Failure = failure;
        Fragment = fragment;
    }

    /// <summary>The pieces, in order. Empty when parsing failed.</summary>
    public IReadOnlyList<TemplateSegment> Segments { get; }

    /// <summary>The failure, or null when the token is well formed.</summary>
    public TemplateFailure? Failure { get; }

    /// <summary>The offending text of a failure; empty otherwise.</summary>
    public string Fragment { get; }

    public static TemplateParse Success(IReadOnlyList<TemplateSegment> segments) => new(segments, null, string.Empty);

    public static TemplateParse Fail(TemplateFailure failure, string fragment) => new([], failure, fragment);
}

/// <summary>
/// Splits a token into literal text and placeholders. The syntax is closed: <c>{in:name}</c>,
/// <c>{Host}</c> and <c>{Port}</c> (case-sensitive, exact), <c>{{</c> and <c>}}</c> for literal
/// braces; any other brace use is an error. No environment variable is ever expanded.
/// </summary>
internal static class RecipeTemplate
{
    public static TemplateParse Parse(string token)
    {
        List<TemplateSegment> segments = [];
        StringBuilder literal = new();
        int index = 0;

        while (index < token.Length)
        {
            char c = token[index];
            if (c == RecipeFormat.PlaceholderOpen)
            {
                if (index + 1 < token.Length && token[index + 1] == RecipeFormat.PlaceholderOpen)
                {
                    literal.Append(RecipeFormat.PlaceholderOpen);
                    index += 2;
                    continue;
                }

                int close = token.IndexOf(RecipeFormat.PlaceholderClose, index + 1);
                if (close < 0)
                    return TemplateParse.Fail(TemplateFailure.Malformed, token[index..]);

                string inner = token[(index + 1)..close];
                if (inner.Contains(RecipeFormat.PlaceholderOpen, StringComparison.Ordinal))
                    return TemplateParse.Fail(TemplateFailure.Malformed, token[index..(close + 1)]);

                TemplateSegment? placeholder = ReadPlaceholder(inner);
                if (placeholder is null)
                    return TemplateParse.Fail(TemplateFailure.Unknown, token[index..(close + 1)]);

                FlushLiteral(segments, literal);
                segments.Add(placeholder);
                index = close + 1;
            }
            else if (c == RecipeFormat.PlaceholderClose)
            {
                if (index + 1 < token.Length && token[index + 1] == RecipeFormat.PlaceholderClose)
                {
                    literal.Append(RecipeFormat.PlaceholderClose);
                    index += 2;
                    continue;
                }

                return TemplateParse.Fail(TemplateFailure.Malformed, token[index..]);
            }
            else
            {
                literal.Append(c);
                index++;
            }
        }

        FlushLiteral(segments, literal);
        return TemplateParse.Success(segments);
    }

    private static TemplateSegment? ReadPlaceholder(string inner)
    {
        if (string.Equals(inner, RecipeFormat.HostPlaceholder, StringComparison.Ordinal))
            return new TemplateSegment(TemplateSegmentKind.Host, inner);

        if (string.Equals(inner, RecipeFormat.PortPlaceholder, StringComparison.Ordinal))
            return new TemplateSegment(TemplateSegmentKind.Port, inner);

        if (inner.StartsWith(RecipeFormat.InputPlaceholderPrefix, StringComparison.Ordinal))
        {
            string name = inner[RecipeFormat.InputPlaceholderPrefix.Length..];
            if (RecipeFormat.IsInputName(name))
                return new TemplateSegment(TemplateSegmentKind.Input, name);
        }

        return null;
    }

    private static void FlushLiteral(List<TemplateSegment> segments, StringBuilder literal)
    {
        if (literal.Length == 0)
            return;

        segments.Add(new TemplateSegment(TemplateSegmentKind.Literal, literal.ToString()));
        literal.Clear();
    }
}
