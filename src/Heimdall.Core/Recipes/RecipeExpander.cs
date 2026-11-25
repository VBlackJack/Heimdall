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

/// <summary>One value that was refused during expansion.</summary>
/// <param name="Reason">Why it was refused.</param>
/// <param name="Subject">The input name, or <c>Host</c> or <c>Port</c> for a session placeholder.</param>
/// <param name="Path">The entry that holds the placeholder, for example <c>arguments[2][0]</c>; empty when the value is not tied to a token.</param>
public sealed record RecipeRefusal(RecipeValueRefusal Reason, string Subject, string Path);

/// <summary>The outcome of expanding a recipe: the argument list, or the refusals.</summary>
public sealed class RecipeExpansion
{
    internal RecipeExpansion(IReadOnlyList<string> arguments, IReadOnlyList<RecipeRefusal> refusals)
    {
        Arguments = arguments;
        Refusals = refusals;
    }

    /// <summary>
    /// The final arguments, one per process argument, to put into <c>ProcessStartInfo.ArgumentList</c>
    /// as they are. Empty when <see cref="Refusals"/> is not.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Every value that was refused; empty when the expansion succeeded.</summary>
    public IReadOnlyList<RecipeRefusal> Refusals { get; }

    /// <summary>True when there is no refusal.</summary>
    public bool Succeeded => Refusals.Count == 0;
}

/// <summary>
/// Builds the final argument list of a validated recipe from the values the user typed and the
/// selected session. It is a pure function: it quotes nothing, splits nothing and joins nothing, so
/// a value stays exactly one argument, byte for byte.
/// </summary>
public static class RecipeExpander
{
    /// <summary>Expands a recipe.</summary>
    /// <param name="recipe">A recipe that passed <see cref="RecipeValidator"/>.</param>
    /// <param name="values">The typed values keyed by input name; a missing or blank entry means "no value" (a bool is <c>true</c> or <c>false</c>).</param>
    /// <param name="host">The host of the selected session, or null when there is none.</param>
    /// <param name="port">The port of the selected session as text, or null when there is none.</param>
    /// <param name="converter">What the machine does to a character; the real machine when null.</param>
    /// <exception cref="InvalidOperationException">A token is not well formed, which a validated recipe cannot be.</exception>
    public static RecipeExpansion Expand(
        Recipe recipe,
        IReadOnlyDictionary<string, string?> values,
        string? host = null,
        string? port = null,
        IRecipeCharacterConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(values);

        Expansion run = new(recipe, host, port, converter);
        run.ResolveInputs(values);
        run.ExpandArguments();
        return run.Finish();
    }

    private sealed class Expansion
    {
        private readonly IRecipeCharacterConverter? _converter;

        private readonly Recipe _recipe;

        private readonly string? _host;

        private readonly string? _port;

        private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

        private readonly List<string> _arguments = [];

        private readonly List<RecipeRefusal> _refusals = [];

        public Expansion(Recipe recipe, string? host, string? port, IRecipeCharacterConverter? converter)
        {
            _converter = converter;
            _recipe = recipe;
            _host = host;
            _port = port;
        }

        public void ResolveInputs(IReadOnlyDictionary<string, string?> values)
        {
            for (int index = 0; index < _recipe.Inputs.Count; index++)
            {
                RecipeInput input = _recipe.Inputs[index];
                string path = RecipePath.Index(RecipePath.Inputs, index);

                values.TryGetValue(input.Name, out string? typed);
                string value = !string.IsNullOrWhiteSpace(typed) ? typed : input.Default ?? string.Empty;

                if (input.Type == RecipeInputType.Bool)
                {
                    ResolveBool(input, value, path);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    value = string.Empty;
                    if (input.Required)
                        _refusals.Add(new RecipeRefusal(RecipeValueRefusal.RequiredMissing, input.Name, path));
                }

                _resolved[input.Name] = value;
            }
        }

        public void ExpandArguments()
        {
            for (int index = 0; index < _recipe.Arguments.Count; index++)
            {
                RecipeArgument argument = _recipe.Arguments[index];
                string path = RecipePath.Index(RecipePath.Arguments, index);

                if (!argument.IsGroup)
                {
                    ExpandBare(argument.Tokens[0], path);
                    continue;
                }

                ExpandTokens(argument.Tokens, path, indexed: true);
            }
        }

        public RecipeExpansion Finish()
            => _refusals.Count == 0
                ? new RecipeExpansion(_arguments, [])
                : new RecipeExpansion([], _refusals);

        private void ResolveBool(RecipeInput input, string value, string path)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _resolved[input.Name] = RecipeFormat.FalseText;
                return;
            }

            if (RecipeValueRule.Check(input, value, placeholderStartsToken: false, _converter) != RecipeValueRefusal.None)
            {
                _refusals.Add(new RecipeRefusal(RecipeValueRefusal.NotABoolean, input.Name, path));
                _resolved[input.Name] = RecipeFormat.FalseText;
                return;
            }

            _resolved[input.Name] = value.ToLowerInvariant();
        }

        private void ExpandBare(string token, string path)
        {
            TemplateParse parse = Parse(token);
            if (parse.Segments.Count == 1
                && parse.Segments[0].Kind == TemplateSegmentKind.Input
                && _recipe.FindInput(parse.Segments[0].Text) is { Type: RecipeInputType.Bool } flag)
            {
                // A switch placed in the argument list: its whenTrue tokens when on, its whenFalse tokens when
                // off, or nothing when the list of the state it is in was not written.
                bool on = string.Equals(_resolved[flag.Name], RecipeFormat.TrueText, StringComparison.Ordinal);
                ExpandTokens((on ? flag.WhenTrue : flag.WhenFalse) ?? [], path, indexed: false);
                return;
            }

            AppendToken(parse, path);
        }

        private void ExpandTokens(IReadOnlyList<string> tokens, string path, bool indexed)
        {
            List<TemplateParse> parses = [.. tokens.Select(Parse)];
            if (IsDropped(parses))
                return;

            for (int index = 0; index < parses.Count; index++)
            {
                string tokenPath = indexed ? RecipePath.Index(path, index) : path;
                AppendToken(parses[index], tokenPath);
            }
        }

        /// <summary>A group is dropped as a whole when any optional input it uses is empty.</summary>
        private bool IsDropped(List<TemplateParse> parses)
        {
            foreach (TemplateParse parse in parses)
            {
                foreach (TemplateSegment segment in parse.Segments)
                {
                    if (segment.Kind != TemplateSegmentKind.Input)
                        continue;

                    RecipeInput? input = _recipe.FindInput(segment.Text);
                    if (input is { IsOptional: true } && _resolved.TryGetValue(input.Name, out string? value) && value.Length == 0)
                        return true;
                }
            }

            return false;
        }

        private void AppendToken(TemplateParse parse, string path)
        {
            IReadOnlyList<TemplateSegment> segments = parse.Segments;
            string[] texts = new string[segments.Count];
            bool[] available = new bool[segments.Count];
            for (int index = 0; index < segments.Count; index++)
            {
                TemplateSegment segment = segments[index];
                available[index] = true;
                texts[index] = segment.Kind == TemplateSegmentKind.Literal
                    ? segment.Text
                    : ValueOf(segment, path, out available[index]);
            }

            StringBuilder token = new();
            bool refused = false;

            for (int index = 0; index < segments.Count; index++)
            {
                TemplateSegment segment = segments[index];
                if (segment.Kind != TemplateSegmentKind.Literal && available[index])
                {
                    bool startsToken = token.Length == 0;
                    RecipeValueRefusal verdict = Check(segment, texts[index], startsToken);
                    if (verdict == RecipeValueRefusal.None && texts[index].Length > 0)
                    {
                        verdict = RecipeValueRule.CheckSeparatorJoin(
                            texts[index],
                            LastCharBefore(texts, index),
                            FirstCharAfter(texts, index));
                    }

                    if (verdict != RecipeValueRefusal.None)
                    {
                        _refusals.Add(new RecipeRefusal(verdict, SubjectOf(segment), path));
                        refused = true;
                    }
                }

                token.Append(texts[index]);
            }

            if (refused)
                return;

            // The last line of defence: whatever its source, a final argument must not read as a network
            // path, an NT or device path or a URL, with either separator and in any mix.
            string final = token.ToString();
            if (RecipeText.ContainsNetworkPathShape(final))
            {
                _refusals.Add(new RecipeRefusal(RecipeValueRefusal.ForbiddenSequenceInArgument, string.Empty, path));
                return;
            }

            _arguments.Add(final);
        }

        private static char? LastCharBefore(string[] texts, int index)
        {
            for (int previous = index - 1; previous >= 0; previous--)
            {
                if (texts[previous].Length > 0)
                    return texts[previous][^1];
            }

            return null;
        }

        private static char? FirstCharAfter(string[] texts, int index)
        {
            for (int next = index + 1; next < texts.Length; next++)
            {
                if (texts[next].Length > 0)
                    return texts[next][0];
            }

            return null;
        }

        private string ValueOf(TemplateSegment segment, string path, out bool available)
        {
            available = true;
            switch (segment.Kind)
            {
                case TemplateSegmentKind.Host:
                    return SessionValue(_host, segment, path, out available);
                case TemplateSegmentKind.Port:
                    return SessionValue(_port, segment, path, out available);
                default:
                    // An optional input left empty in a bare token cannot occur in a validated
                    // recipe; it would simply contribute nothing.
                    return _resolved.TryGetValue(segment.Text, out string? value) ? value : string.Empty;
            }
        }

        private string SessionValue(string? value, TemplateSegment segment, string path, out bool available)
        {
            available = !string.IsNullOrEmpty(value);
            if (!available)
                _refusals.Add(new RecipeRefusal(RecipeValueRefusal.SessionValueMissing, segment.Text, path));

            return value ?? string.Empty;
        }

        private RecipeValueRefusal Check(TemplateSegment segment, string value, bool startsToken)
        {
            switch (segment.Kind)
            {
                case TemplateSegmentKind.Host:
                    return RecipeValueRule.CheckHost(value, startsToken, _converter);
                case TemplateSegmentKind.Port:
                    return RecipeValueRule.CheckPort(value, startsToken, _converter);
                default:
                    RecipeInput? input = _recipe.FindInput(segment.Text);
                    if (input is null)
                        throw new InvalidOperationException("The recipe was not validated: a placeholder names no input.");

                    // An empty optional value (only possible outside a group) has nothing to check.
                    return value.Length == 0 ? RecipeValueRefusal.None : RecipeValueRule.Check(input, value, startsToken, _converter);
            }
        }

        private static string SubjectOf(TemplateSegment segment)
            => segment.Kind == TemplateSegmentKind.Input ? segment.Text : segment.Kind.ToString();

        private static TemplateParse Parse(string token)
        {
            TemplateParse parse = RecipeTemplate.Parse(token);
            if (parse.Failure is not null)
                throw new InvalidOperationException("The recipe was not validated: a token is not well formed.");

            return parse;
        }
    }
}
