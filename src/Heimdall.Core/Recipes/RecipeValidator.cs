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
using System.Text.RegularExpressions;

namespace Heimdall.Core.Recipes;

/// <summary>
/// Checks a parsed recipe against every rule of the format: shapes, caps, placeholder syntax,
/// the import refusals for network paths, URLs, environment variables and response files, and the
/// consistency of inputs with the argument list. It reports every problem it finds.
/// </summary>
public static class RecipeValidator
{
    private const string SemverPattern =
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z";

    private static readonly Regex Semver = new(SemverPattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Stands for the value of a placeholder that is not the one being examined.</summary>
    private const string NeutralValue = "x";

    private static readonly int MaxIdLength = (RecipeLimits.MaxIdPartLength * 2) + 1;

    /// <summary>Validates a recipe. The errors carry no position, since no source text is known.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <param name="converter">What the machine does to a character; the real machine when null.</param>
    public static IReadOnlyList<RecipeError> Validate(Recipe recipe, IRecipeCharacterConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return Validate(recipe, null, converter);
    }

    /// <summary>Validates a recipe and attaches the position of each entry when the source is known.</summary>
    internal static IReadOnlyList<RecipeError> Validate(
        Recipe recipe,
        Func<string, RecipePosition?>? positionOf,
        IRecipeCharacterConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new Run(recipe, positionOf, converter).Execute();
    }

    private enum TokenContext
    {
        Bare,
        Group,
        Switch,
    }

    private sealed class Run
    {
        private readonly Recipe _recipe;

        private readonly Func<string, RecipePosition?>? _positionOf;

        private readonly IRecipeCharacterConverter? _converter;

        private readonly List<RecipeError> _errors = [];

        private readonly Dictionary<string, RecipeInput> _inputs = new(StringComparer.Ordinal);

        private readonly HashSet<string> _placedBools = new(StringComparer.Ordinal);

        private readonly HashSet<string> _mentioned = new(StringComparer.Ordinal);

        private int _tokenCount;

        public Run(Recipe recipe, Func<string, RecipePosition?>? positionOf, IRecipeCharacterConverter? converter)
        {
            _recipe = recipe;
            _positionOf = positionOf;
            _converter = converter;
        }

        public IReadOnlyList<RecipeError> Execute()
        {
            CheckSchemaVersion();
            CheckId();
            CheckVersion();
            CheckText(RecipePath.Author, _recipe.Author, RecipeLimits.MaxAuthorLength, forbidden: false);
            CheckText(RecipePath.Name, _recipe.Name, RecipeLimits.MaxNameLength, forbidden: false);
            CheckText(RecipePath.Description, _recipe.Description, RecipeLimits.MaxDescriptionLength, forbidden: false);
            CheckText(RecipePath.Program, _recipe.Program, RecipeLimits.MaxProgramLength, forbidden: true, onCommandLine: true);
            CheckInputs();
            CheckArguments();
            CheckTranslations();
            CheckRunSettings();
            return _errors;
        }

        private void Add(
            RecipeErrorCode code,
            string path,
            string? name = null,
            string? fragment = null,
            int? actual = null,
            int? limit = null)
            => _errors.Add(new RecipeError(code, _positionOf?.Invoke(path), path, name, fragment, actual, limit));

        private void CheckSchemaVersion()
        {
            if (_recipe.SchemaVersion > RecipeFormat.SupportedSchemaVersion)
            {
                Add(
                    RecipeErrorCode.UnsupportedSchemaVersion,
                    RecipePath.SchemaVersion,
                    actual: _recipe.SchemaVersion,
                    limit: RecipeFormat.SupportedSchemaVersion);
            }
            else if (_recipe.SchemaVersion < 1)
            {
                Add(RecipeErrorCode.ValueOutOfRange, RecipePath.SchemaVersion, actual: _recipe.SchemaVersion, limit: 1);
            }
        }

        private void CheckId()
        {
            string id = _recipe.Id ?? string.Empty;
            if (id.Length > MaxIdLength)
            {
                Add(RecipeErrorCode.TooLong, RecipePath.Id, actual: id.Length, limit: MaxIdLength);
                return;
            }

            string[] parts = id.Split(RecipeFormat.IdSeparator);
            if (parts.Length != 2 || !RecipeFormat.IsIdPart(parts[0]) || !RecipeFormat.IsIdPart(parts[1]))
            {
                Add(RecipeErrorCode.InvalidIdShape, RecipePath.Id, fragment: RecipeText.MakeSafe(id));
                return;
            }

            foreach (string part in parts)
            {
                if (part.Length > RecipeLimits.MaxIdPartLength)
                    Add(RecipeErrorCode.TooLong, RecipePath.Id, actual: part.Length, limit: RecipeLimits.MaxIdPartLength);
            }
        }

        private void CheckVersion()
        {
            string version = _recipe.Version ?? string.Empty;
            if (version.Length > RecipeLimits.MaxVersionLength)
            {
                Add(RecipeErrorCode.TooLong, RecipePath.Version, actual: version.Length, limit: RecipeLimits.MaxVersionLength);
            }
            else if (!Semver.IsMatch(version))
            {
                Add(RecipeErrorCode.InvalidVersion, RecipePath.Version, fragment: RecipeText.MakeSafe(version));
            }
        }

        /// <summary>
        /// Checks a displayed or fixed text; returns true when it raised no error. A text that reaches
        /// the command line (<paramref name="onCommandLine"/>: the program, a choice) is also refused when it holds a
        /// look-alike character.
        /// </summary>
        private bool CheckText(string path, string? text, int maxLength, bool forbidden, bool onCommandLine = false)
        {
            int before = _errors.Count;
            if (string.IsNullOrWhiteSpace(text))
            {
                Add(RecipeErrorCode.EmptyValue, path);
                return false;
            }

            if (text.Length > maxLength)
            {
                Add(RecipeErrorCode.TooLong, path, actual: text.Length, limit: maxLength);
                return false;
            }

            if (RecipeText.ContainsRefusedCharacter(text))
            {
                Add(RecipeErrorCode.ControlCharacter, path, fragment: RecipeText.MakeSafe(text));
                return false;
            }

            if (onCommandLine && RecipeText.ContainsLookAlikeCharacter(text, _converter))
            {
                Add(RecipeErrorCode.ForbiddenLookAlikeCharacter, path, fragment: LookAlikeFragment(text));
                return false;
            }

            if (forbidden)
                CheckForbiddenFragments(path, text);

            return _errors.Count == before;
        }

        /// <summary>The text with every character outside printable ASCII written as an escape, so that the look-alike can be told from the ASCII one.</summary>
        private static string LookAlikeFragment(string text)
            => RecipeText.MakeSafe(RecipeText.Escape(text, escapeNonAscii: true));

        private void CheckForbiddenFragments(string path, string text)
        {
            foreach (RecipeErrorCode code in RecipeText.FindForbiddenFragments(text))
                Add(code, path, fragment: RecipeText.MakeSafe(text));
        }

        private void CheckInputs()
        {
            if (_recipe.Inputs.Count > RecipeLimits.MaxInputs)
                Add(RecipeErrorCode.TooMany, RecipePath.Inputs, actual: _recipe.Inputs.Count, limit: RecipeLimits.MaxInputs);

            // Names first: tokens may refer to an input declared after them.
            foreach (RecipeInput input in _recipe.Inputs)
                _inputs.TryAdd(input.Name ?? string.Empty, input);

            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int index = 0; index < _recipe.Inputs.Count; index++)
            {
                RecipeInput input = _recipe.Inputs[index];
                string path = RecipePath.Index(RecipePath.Inputs, index);
                string namePath = RecipePath.Property(path, RecipePath.Name);

                if (!RecipeFormat.IsInputName(input.Name))
                {
                    Add(RecipeErrorCode.InvalidInputName, namePath, fragment: RecipeText.MakeSafe(input.Name));
                }
                else if (!seen.Add(input.Name))
                {
                    Add(RecipeErrorCode.DuplicateInputName, namePath, name: input.Name);
                }

                CheckText(RecipePath.Property(path, RecipePath.Label), input.Label, RecipeLimits.MaxLabelLength, forbidden: false);
                CheckHint(input, path);
                CheckRequired(input, path);
                CheckStringProperties(input, path);
                CheckChoices(input, path);
                CheckSwitchArguments(input, path);
                CheckBounds(input, path);
                CheckDefault(input, path);
            }
        }

        private void CheckHint(RecipeInput input, string path)
        {
            if (input.Hint is not null)
                CheckText(RecipePath.Property(path, RecipePath.Hint), input.Hint, RecipeLimits.MaxHintLength, forbidden: false);
        }

        private void CheckRequired(RecipeInput input, string path)
        {
            string requiredPath = RecipePath.Property(path, RecipePath.Required);
            if (input.Type == RecipeInputType.Bool)
            {
                // A switch is never required (the file reader reports the property itself; this covers a recipe built by hand).
                if (input.Required)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, requiredPath, fragment: RecipePath.Required);

                return;
            }

            // A field with a default is never omitted: when it is cleared, the default is used. Requiring it says
            // nothing (the file reader refuses even "required": false next to a default; a recipe built by hand is
            // held to the same rule through the flag).
            if (input.Required && input.Default is not null)
                Add(RecipeErrorCode.RequiredWithDefault, requiredPath, name: RecipeText.MakeSafe(input.Name));
        }

        private void CheckStringProperties(RecipeInput input, string path)
        {
            string patternPath = RecipePath.Property(path, RecipePath.Pattern);
            string lengthPath = RecipePath.Property(path, RecipePath.MaxLength);

            if (input.Type != RecipeInputType.String)
            {
                if (input.Pattern is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, patternPath, fragment: RecipePath.Pattern);

                if (input.MaxLength is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, lengthPath, fragment: RecipePath.MaxLength);

                return;
            }

            if (input.Pattern is not null
                && CheckText(patternPath, input.Pattern, RecipeLimits.MaxPatternLength, forbidden: false)
                && !RecipePatterns.IsValid(input.Pattern))
            {
                Add(RecipeErrorCode.InvalidPattern, patternPath, fragment: RecipeText.MakeSafe(input.Pattern));
            }

            if (input.MaxLength is int maxLength)
            {
                if (maxLength < 1)
                    Add(RecipeErrorCode.ValueOutOfRange, lengthPath, actual: maxLength, limit: 1);
                else if (maxLength > RecipeLimits.MaxStringValueLength)
                    Add(RecipeErrorCode.ValueOutOfRange, lengthPath, actual: maxLength, limit: RecipeLimits.MaxStringValueLength);
            }
        }

        private void CheckChoices(RecipeInput input, string path)
        {
            string choicesPath = RecipePath.Property(path, RecipePath.Choices);
            if (input.Type != RecipeInputType.Choice)
            {
                if (input.Choices is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, choicesPath, fragment: RecipePath.Choices);

                return;
            }

            if (input.Choices is null || input.Choices.Count == 0)
            {
                Add(RecipeErrorCode.MissingChoices, choicesPath);
                return;
            }

            if (input.Choices.Count > RecipeLimits.MaxChoices)
                Add(RecipeErrorCode.TooMany, choicesPath, actual: input.Choices.Count, limit: RecipeLimits.MaxChoices);

            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int index = 0; index < input.Choices.Count; index++)
            {
                string value = input.Choices[index];
                string valuePath = RecipePath.Index(choicesPath, index);
                if (CheckText(valuePath, value, RecipeLimits.MaxChoiceLength, forbidden: true, onCommandLine: true) && !seen.Add(value))
                    Add(RecipeErrorCode.DuplicateChoice, valuePath, fragment: RecipeText.MakeSafe(value));
            }
        }

        private void CheckSwitchArguments(RecipeInput input, string path)
        {
            string whenTruePath = RecipePath.Property(path, RecipePath.WhenTrue);
            string whenFalsePath = RecipePath.Property(path, RecipePath.WhenFalse);
            if (input.Type != RecipeInputType.Bool)
            {
                if (input.WhenTrue is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, whenTruePath, fragment: RecipePath.WhenTrue);

                if (input.WhenFalse is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, whenFalsePath, fragment: RecipePath.WhenFalse);

                return;
            }

            if (input.WhenTrue is null && input.WhenFalse is null)
            {
                Add(RecipeErrorCode.MissingSwitchArguments, whenTruePath);
                return;
            }

            CheckSwitchList(input.WhenTrue, whenTruePath);
            CheckSwitchList(input.WhenFalse, whenFalsePath);
        }

        private void CheckSwitchList(IReadOnlyList<string>? tokens, string listPath)
        {
            if (tokens is null)
                return;

            if (tokens.Count == 0)
            {
                Add(RecipeErrorCode.MissingSwitchArguments, listPath);
                return;
            }

            for (int index = 0; index < tokens.Count; index++)
                CheckToken(RecipePath.Index(listPath, index), tokens[index], TokenContext.Switch);
        }

        /// <summary>The <c>min</c> and <c>max</c> of a number: allowed on <c>int</c> and <c>number</c> only, valid, and in order.</summary>
        private void CheckBounds(RecipeInput input, string path)
        {
            string minPath = RecipePath.Property(path, RecipePath.Min);
            string maxPath = RecipePath.Property(path, RecipePath.Max);
            if (input.Type is not (RecipeInputType.Int or RecipeInputType.Number))
            {
                if (input.Min is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, minPath, fragment: RecipePath.Min);

                if (input.Max is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, maxPath, fragment: RecipePath.Max);

                return;
            }

            bool minValid = CheckBound(input, input.Min, minPath, out _);
            bool maxValid = CheckBound(input, input.Max, maxPath, out _);
            if (minValid && maxValid && input.Min is not null && input.Max is not null && RecipeNumbers.CompareDecimal(input.Min, input.Max) > 0)
                Add(RecipeErrorCode.MinAboveMax, minPath, fragment: RecipeText.MakeSafe(input.Min), name: RecipeText.MakeSafe(input.Max));
        }

        /// <summary>True when the bound is present and valid; an absent bound raises nothing and is not valid.</summary>
        private bool CheckBound(RecipeInput input, string? text, string path, out double value)
        {
            value = 0;
            if (text is null)
                return false;

            bool ok = input.Type == RecipeInputType.Int
                ? TryParseIntBound(text, out value)
                : RecipeNumbers.TryParseNumber(text, out value);
            if (!ok)
                Add(RecipeErrorCode.InvalidBound, path, fragment: RecipeText.MakeSafe(text));

            return ok;
        }

        private static bool TryParseIntBound(string text, out double value)
        {
            bool ok = RecipeNumbers.TryParseInt(text, out int number);
            value = number;
            return ok;
        }

        private void CheckDefault(RecipeInput input, string path)
        {
            if (input.Default is null)
                return;

            string defaultPath = RecipePath.Property(path, RecipePath.Default);
            string value = input.Default;
            if (string.IsNullOrWhiteSpace(value))
            {
                Add(RecipeErrorCode.EmptyValue, defaultPath);
                return;
            }

            if (value.Length > RecipeLimits.MaxTokenLength)
            {
                Add(RecipeErrorCode.TooLong, defaultPath, actual: value.Length, limit: RecipeLimits.MaxTokenLength);
                return;
            }

            if (RecipeText.ContainsRefusedCharacter(value))
            {
                Add(RecipeErrorCode.ControlCharacter, defaultPath, fragment: RecipeText.MakeSafe(value));
                return;
            }

            if (RecipeText.ContainsLookAlikeCharacter(value, _converter))
            {
                Add(RecipeErrorCode.ForbiddenLookAlikeCharacter, defaultPath, fragment: LookAlikeFragment(value));
                return;
            }

            int before = _errors.Count;
            CheckForbiddenFragments(defaultPath, value);
            if (_errors.Count > before)
                return;

            if (input.Type == RecipeInputType.Choice && (input.Choices is null || input.Choices.Count == 0))
                return;

            RecipeValueRefusal refusal = RecipeValueRule.Check(input, value, placeholderStartsToken: false, _converter);
            if (refusal == RecipeValueRefusal.None)
                return;

            if (input.Type == RecipeInputType.Choice)
                Add(RecipeErrorCode.InvalidChoiceDefault, defaultPath, fragment: RecipeText.MakeSafe(value));
            else if (refusal == RecipeValueRefusal.OutOfRange)
                Add(RecipeErrorCode.DefaultOutOfBounds, defaultPath, name: RecipeText.MakeSafe(input.Name), fragment: RecipeText.MakeSafe(value));
            else
                Add(RecipeErrorCode.InvalidDefault, defaultPath, fragment: refusal.ToString());
        }

        private void CheckArguments()
        {
            for (int index = 0; index < _recipe.Arguments.Count; index++)
            {
                RecipeArgument argument = _recipe.Arguments[index];
                string path = RecipePath.Index(RecipePath.Arguments, index);

                if (!argument.IsGroup)
                {
                    if (argument.Tokens.Count == 1)
                        CheckToken(path, argument.Tokens[0], TokenContext.Bare);
                    else
                        Add(RecipeErrorCode.WrongType, path);

                    continue;
                }

                if (argument.Tokens.Count == 0)
                {
                    Add(RecipeErrorCode.EmptyGroup, path);
                    continue;
                }

                for (int token = 0; token < argument.Tokens.Count; token++)
                    CheckToken(RecipePath.Index(path, token), argument.Tokens[token], TokenContext.Group);
            }

            if (_tokenCount > RecipeLimits.MaxTokens)
                Add(RecipeErrorCode.TooMany, RecipePath.Arguments, actual: _tokenCount, limit: RecipeLimits.MaxTokens);

            // An input that no argument uses is refused. A switch must be placed as a whole bare token; any
            // other input is used as soon as a token, a group member or a switch token names it.
            for (int index = 0; index < _recipe.Inputs.Count; index++)
            {
                RecipeInput input = _recipe.Inputs[index];
                bool used = input.Type == RecipeInputType.Bool
                    ? _placedBools.Contains(input.Name)
                    : _mentioned.Contains(input.Name);
                if (!used)
                {
                    Add(
                        RecipeErrorCode.UnreferencedInput,
                        RecipePath.Index(RecipePath.Inputs, index),
                        name: RecipeText.MakeSafe(input.Name));
                }
            }
        }

        private void CheckToken(string path, string? token, TokenContext context)
        {
            // What the expander can produce: a switch placed as a bare token becomes its whenTrue or its
            // whenFalse tokens, once per placement, so the declared switch tokens themselves are not counted.
            if (context != TokenContext.Switch)
                _tokenCount += TokensProducedBy(token, context);

            NoteMentions(token);

            if (string.IsNullOrEmpty(token))
            {
                Add(RecipeErrorCode.EmptyToken, path);
                return;
            }

            if (token.Length > RecipeLimits.MaxTokenLength)
            {
                Add(RecipeErrorCode.TooLong, path, actual: token.Length, limit: RecipeLimits.MaxTokenLength);
                return;
            }

            if (RecipeText.ContainsRefusedCharacter(token))
            {
                Add(RecipeErrorCode.ControlCharacter, path, fragment: RecipeText.MakeSafe(token));
                return;
            }

            if (RecipeText.ContainsLookAlikeCharacter(token, _converter))
            {
                Add(RecipeErrorCode.ForbiddenLookAlikeCharacter, path, fragment: LookAlikeFragment(token));
                return;
            }

            CheckForbiddenFragments(path, token);

            TemplateParse parse = RecipeTemplate.Parse(token);
            if (parse.Failure is TemplateFailure failure)
            {
                RecipeErrorCode code = failure == TemplateFailure.Unknown
                    ? RecipeErrorCode.UnknownPlaceholder
                    : RecipeErrorCode.MalformedPlaceholder;
                Add(code, path, fragment: RecipeText.MakeSafe(parse.Fragment));
                return;
            }

            bool wholeToken = parse.Segments.Count == 1 && parse.Segments[0].Kind == TemplateSegmentKind.Input;
            for (int index = 0; index < parse.Segments.Count; index++)
            {
                TemplateSegment segment = parse.Segments[index];
                if (segment.Kind == TemplateSegmentKind.Input)
                    CheckInputReference(path, segment.Text, context, wholeToken, parse, index);
            }
        }

        /// <summary>Remembers every input a token names, whatever else is wrong with the token.</summary>
        private void NoteMentions(string? token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            foreach (TemplateSegment segment in RecipeTemplate.Parse(token).Segments)
            {
                if (segment.Kind == TemplateSegmentKind.Input)
                    _mentioned.Add(segment.Text);
            }
        }

        /// <summary>How many tokens one written token can become: its own count, or the larger list of a switch placed alone.</summary>
        private int TokensProducedBy(string? token, TokenContext context)
        {
            if (context != TokenContext.Bare || string.IsNullOrEmpty(token))
                return 1;

            TemplateParse parse = RecipeTemplate.Parse(token);
            if (parse.Failure is null
                && parse.Segments.Count == 1
                && parse.Segments[0].Kind == TemplateSegmentKind.Input
                && _inputs.TryGetValue(parse.Segments[0].Text, out RecipeInput? input)
                && input.Type == RecipeInputType.Bool)
            {
                return Math.Max(input.WhenTrue?.Count ?? 0, input.WhenFalse?.Count ?? 0);
            }

            return 1;
        }

        private void CheckInputReference(string path, string name, TokenContext context, bool wholeToken, TemplateParse parse, int segmentIndex)
        {
            if (!_inputs.TryGetValue(name, out RecipeInput? input))
            {
                Add(RecipeErrorCode.UnknownInput, path, name: RecipeText.MakeSafe(name), fragment: RecipeText.MakeSafe(name));
                return;
            }

            if (input.Type == RecipeInputType.Bool)
            {
                // A switch is placed as a whole bare token, and expands to its whenTrue or its whenFalse tokens.
                if (context == TokenContext.Bare && wholeToken)
                    _placedBools.Add(name);
                else
                    Add(RecipeErrorCode.BoolInputInTokenText, path, name: RecipeText.MakeSafe(name));

                return;
            }

            if (context == TokenContext.Bare && input.IsOptional)
                Add(RecipeErrorCode.OptionalInputInBareToken, path, name: RecipeText.MakeSafe(name));

            // The tokens of a switch have no group around them to be dropped: an input there must always have a value.
            if (context == TokenContext.Switch && input.IsOptional)
                Add(RecipeErrorCode.OptionalInputInSwitchArguments, path, name: RecipeText.MakeSafe(name));

            CheckFixedValues(path, input, parse, segmentIndex);
        }

        /// <summary>
        /// A value the author fixed (a choice, a default) is checked with what the expander does at run
        /// time at the very place the placeholder stands: the value rule for that position, the
        /// separator-join check against the literal text just before and after it, and the final-argument
        /// check on the whole token. A recipe that validates therefore cannot carry a choice or a default
        /// that is always refused.
        /// </summary>
        private void CheckFixedValues(string path, RecipeInput input, TemplateParse parse, int segmentIndex)
        {
            IEnumerable<string> fixedValues = input.Type == RecipeInputType.Choice
                ? (input.Choices ?? []).Concat(input.Default is null ? [] : [input.Default])
                : input.Default is null ? [] : [input.Default];

            bool startsToken = segmentIndex == 0;
            char? before = LiteralNeighbour(parse, segmentIndex - 1, last: true);
            char? after = LiteralNeighbour(parse, segmentIndex + 1, last: false);

            foreach (string value in fixedValues.Distinct(StringComparer.Ordinal))
            {
                // A value that is wrong for its own type is reported as an invalid default or choice.
                if (RecipeValueRule.Check(input, value, placeholderStartsToken: false, _converter) != RecipeValueRefusal.None)
                    continue;

                RecipeValueRefusal verdict = RecipeValueRule.Check(input, value, startsToken, _converter);
                if (verdict is RecipeValueRefusal.LeadingDash or RecipeValueRefusal.LeadingSlash or RecipeValueRefusal.LeadingAt)
                {
                    Add(RecipeErrorCode.FixedValueStartsWithOptionCharacter, path, name: RecipeText.MakeSafe(input.Name), fragment: RecipeText.MakeSafe(value));
                    continue;
                }

                bool refused = verdict != RecipeValueRefusal.None
                    || RecipeValueRule.CheckSeparatorJoin(value, before, after) != RecipeValueRefusal.None
                    || (RecipeText.ContainsNetworkPathShape(TokenWith(parse, segmentIndex, value))
                        && !RecipeText.ContainsNetworkPathShape(TokenWith(parse, segmentIndex, NeutralValue)));
                if (refused)
                    Add(RecipeErrorCode.FixedValueRefusedAtRun, path, name: RecipeText.MakeSafe(input.Name), fragment: RecipeText.MakeSafe(value));
            }
        }

        /// <summary>The first or last character of the literal text next to a placeholder; null when the neighbour is not literal text.</summary>
        private static char? LiteralNeighbour(TemplateParse parse, int index, bool last)
        {
            if (index < 0 || index >= parse.Segments.Count)
                return null;

            TemplateSegment segment = parse.Segments[index];
            if (segment.Kind != TemplateSegmentKind.Literal || segment.Text.Length == 0)
                return null;

            return last ? segment.Text[^1] : segment.Text[0];
        }

        /// <summary>The token as it would be after substitution: the given value at one place, a neutral letter for every other placeholder.</summary>
        private static string TokenWith(TemplateParse parse, int segmentIndex, string value)
        {
            StringBuilder token = new();
            for (int index = 0; index < parse.Segments.Count; index++)
            {
                TemplateSegment segment = parse.Segments[index];
                if (index == segmentIndex)
                    token.Append(value);
                else
                    token.Append(segment.Kind == TemplateSegmentKind.Literal ? segment.Text : NeutralValue);
            }

            return token.ToString();
        }

        private void CheckTranslations()
        {
            if (_recipe.Translations.Count > RecipeLimits.MaxTranslationLanguages)
                Add(RecipeErrorCode.TooMany, RecipePath.Translations, actual: _recipe.Translations.Count, limit: RecipeLimits.MaxTranslationLanguages);

            // Ordinal order, so that of two tags that differ only by case the same one is always the one reported.
            HashSet<string> seenTags = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string language, RecipeTranslation translation) in _recipe.Translations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string path = RecipePath.Property(RecipePath.Translations, language);
                if (!RecipeFormat.IsLanguageTag(language))
                    Add(RecipeErrorCode.InvalidLanguageTag, path, fragment: RecipeText.MakeSafe(language));
                else if (!seenTags.Add(language))
                    Add(RecipeErrorCode.DuplicateLanguageTag, path, fragment: RecipeText.MakeSafe(language));

                if (translation.Name is not null)
                    CheckText(RecipePath.Property(path, RecipePath.Name), translation.Name, RecipeLimits.MaxNameLength, forbidden: false);

                if (translation.Description is not null)
                    CheckText(RecipePath.Property(path, RecipePath.Description), translation.Description, RecipeLimits.MaxDescriptionLength, forbidden: false);

                CheckFieldTexts(RecipePath.Property(path, RecipePath.FieldLabels), translation.FieldLabels, RecipeLimits.MaxLabelLength);
                CheckFieldTexts(RecipePath.Property(path, RecipePath.FieldHints), translation.FieldHints, RecipeLimits.MaxHintLength);
            }
        }

        private void CheckFieldTexts(string mapPath, IReadOnlyDictionary<string, string> texts, int maxLength)
        {
            foreach ((string field, string text) in texts)
            {
                string fieldPath = RecipePath.Property(mapPath, field);
                if (!_inputs.ContainsKey(field))
                    Add(RecipeErrorCode.UnknownTranslationField, fieldPath, name: RecipeText.MakeSafe(field));

                CheckText(fieldPath, text, maxLength, forbidden: false);
            }
        }

        private void CheckRunSettings()
        {
            if (_recipe.TimeoutSeconds < RecipeLimits.MinTimeoutSeconds)
            {
                Add(RecipeErrorCode.ValueOutOfRange, RecipePath.TimeoutSeconds, actual: _recipe.TimeoutSeconds, limit: RecipeLimits.MinTimeoutSeconds);
            }
            else if (_recipe.TimeoutSeconds > RecipeLimits.MaxTimeoutSeconds)
            {
                Add(RecipeErrorCode.ValueOutOfRange, RecipePath.TimeoutSeconds, actual: _recipe.TimeoutSeconds, limit: RecipeLimits.MaxTimeoutSeconds);
            }

            if (_recipe.SuccessExitCodes.Count == 0)
                Add(RecipeErrorCode.EmptyValue, RecipePath.SuccessExitCodes);
            else if (_recipe.SuccessExitCodes.Count > RecipeLimits.MaxSuccessExitCodes)
                Add(RecipeErrorCode.TooMany, RecipePath.SuccessExitCodes, actual: _recipe.SuccessExitCodes.Count, limit: RecipeLimits.MaxSuccessExitCodes);

            HashSet<int> seenCodes = [];
            for (int index = 0; index < _recipe.SuccessExitCodes.Count; index++)
            {
                if (!seenCodes.Add(_recipe.SuccessExitCodes[index]))
                    Add(RecipeErrorCode.DuplicateExitCode, RecipePath.Index(RecipePath.SuccessExitCodes, index), actual: _recipe.SuccessExitCodes[index]);
            }

            // Any key of the right shape is accepted; a key this version has no glyph for shows the default one.
            if (_recipe.Icon is not null && !RecipeIcons.IsKeyShape(_recipe.Icon))
                Add(RecipeErrorCode.InvalidIconKey, RecipePath.Icon, fragment: RecipeText.MakeSafe(_recipe.Icon));

            // A merged stream is a mix of two outputs, which a grid cannot read.
            if (_recipe.Stderr == RecipeStderrMode.Merge && _recipe.Output != RecipeOutputFormat.Text)
                Add(RecipeErrorCode.StderrMergeWithStructuredOutput, RecipePath.Stderr);

            if (_recipe.CsvSeparator != RecipeCsvSeparator.Comma && _recipe.Output != RecipeOutputFormat.Csv)
                Add(RecipeErrorCode.CsvSeparatorWithoutCsvOutput, RecipePath.CsvSeparator);
        }
    }
}
