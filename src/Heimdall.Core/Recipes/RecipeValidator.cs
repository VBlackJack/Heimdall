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

    private static readonly int MaxIdLength = (RecipeLimits.MaxIdPartLength * 2) + 1;

    /// <summary>Validates a recipe. The errors carry no position, since no source text is known.</summary>
    public static IReadOnlyList<RecipeError> Validate(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return Validate(recipe, null);
    }

    /// <summary>Validates a recipe and attaches the position of each entry when the source is known.</summary>
    internal static IReadOnlyList<RecipeError> Validate(Recipe recipe, Func<string, RecipePosition?>? positionOf)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new Run(recipe, positionOf).Execute();
    }

    private enum TokenContext
    {
        Bare,
        Group,
        WhenTrue,
    }

    private sealed class Run
    {
        private readonly Recipe _recipe;

        private readonly Func<string, RecipePosition?>? _positionOf;

        private readonly List<RecipeError> _errors = [];

        private readonly Dictionary<string, RecipeInput> _inputs = new(StringComparer.Ordinal);

        private readonly HashSet<string> _placedBools = new(StringComparer.Ordinal);

        private int _tokenCount;

        public Run(Recipe recipe, Func<string, RecipePosition?>? positionOf)
        {
            _recipe = recipe;
            _positionOf = positionOf;
        }

        public IReadOnlyList<RecipeError> Execute()
        {
            CheckSchemaVersion();
            CheckId();
            CheckVersion();
            CheckText(RecipePath.Author, _recipe.Author, RecipeLimits.MaxAuthorLength, forbidden: false);
            CheckText(RecipePath.Name, _recipe.Name, RecipeLimits.MaxNameLength, forbidden: false);
            CheckText(RecipePath.Description, _recipe.Description, RecipeLimits.MaxDescriptionLength, forbidden: false);
            CheckText(RecipePath.Program, _recipe.Program, RecipeLimits.MaxProgramLength, forbidden: true);
            CheckInputs();
            CheckArguments();
            CheckLabels();
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

        /// <summary>Checks a displayed or fixed text; returns true when it raised no error.</summary>
        private bool CheckText(string path, string? text, int maxLength, bool forbidden)
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

            if (forbidden)
                CheckForbiddenFragments(path, text);

            return _errors.Count == before;
        }

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
                CheckStringProperties(input, path);
                CheckChoices(input, path);
                CheckWhenTrue(input, path);
                CheckDefault(input, path);
            }
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

            if (input.Pattern is not null)
            {
                if (input.Pattern.Length == 0)
                {
                    Add(RecipeErrorCode.EmptyValue, patternPath);
                }
                else if (input.Pattern.Length > RecipeLimits.MaxPatternLength)
                {
                    Add(RecipeErrorCode.TooLong, patternPath, actual: input.Pattern.Length, limit: RecipeLimits.MaxPatternLength);
                }
                else if (!RecipePatterns.IsValid(input.Pattern))
                {
                    Add(RecipeErrorCode.InvalidPattern, patternPath, fragment: RecipeText.MakeSafe(input.Pattern));
                }
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
                if (CheckText(valuePath, value, RecipeLimits.MaxChoiceLength, forbidden: true) && !seen.Add(value))
                    Add(RecipeErrorCode.DuplicateChoice, valuePath, fragment: RecipeText.MakeSafe(value));
            }
        }

        private void CheckWhenTrue(RecipeInput input, string path)
        {
            string whenPath = RecipePath.Property(path, RecipePath.WhenTrue);
            if (input.Type != RecipeInputType.Bool)
            {
                if (input.WhenTrue is not null)
                    Add(RecipeErrorCode.PropertyNotAllowedForType, whenPath, fragment: RecipePath.WhenTrue);

                return;
            }

            if (input.WhenTrue is null || input.WhenTrue.Count == 0)
            {
                Add(RecipeErrorCode.MissingWhenTrue, whenPath);
                return;
            }

            for (int index = 0; index < input.WhenTrue.Count; index++)
                CheckToken(RecipePath.Index(whenPath, index), input.WhenTrue[index], TokenContext.WhenTrue);
        }

        private void CheckDefault(RecipeInput input, string path)
        {
            if (input.Default is null)
                return;

            string defaultPath = RecipePath.Property(path, RecipePath.Default);
            string value = input.Default;
            if (value.Length == 0)
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

            int before = _errors.Count;
            CheckForbiddenFragments(defaultPath, value);
            if (_errors.Count > before)
                return;

            if (input.Type == RecipeInputType.Choice && (input.Choices is null || input.Choices.Count == 0))
                return;

            RecipeValueRefusal refusal = RecipeValueRule.Check(input, value, placeholderStartsToken: false);
            if (refusal == RecipeValueRefusal.None)
                return;

            if (input.Type == RecipeInputType.Choice)
                Add(RecipeErrorCode.InvalidChoiceDefault, defaultPath, fragment: RecipeText.MakeSafe(value));
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

            for (int index = 0; index < _recipe.Inputs.Count; index++)
            {
                RecipeInput input = _recipe.Inputs[index];
                if (input.Type == RecipeInputType.Bool && !_placedBools.Contains(input.Name))
                {
                    Add(
                        RecipeErrorCode.UnreferencedBoolInput,
                        RecipePath.Index(RecipePath.Inputs, index),
                        name: RecipeText.MakeSafe(input.Name));
                }
            }
        }

        private void CheckToken(string path, string? token, TokenContext context)
        {
            _tokenCount++;

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
                    CheckInputReference(path, segment.Text, context, wholeToken, startsToken: index == 0);
            }
        }

        private void CheckInputReference(string path, string name, TokenContext context, bool wholeToken, bool startsToken)
        {
            if (!_inputs.TryGetValue(name, out RecipeInput? input))
            {
                Add(RecipeErrorCode.UnknownInput, path, name: RecipeText.MakeSafe(name), fragment: RecipeText.MakeSafe(name));
                return;
            }

            if (input.Type == RecipeInputType.Bool)
            {
                // A switch is placed as a whole bare token, and expands to its whenTrue tokens.
                if (context == TokenContext.Bare && wholeToken)
                    _placedBools.Add(name);
                else
                    Add(RecipeErrorCode.BoolInputInTokenText, path, name: RecipeText.MakeSafe(name));

                return;
            }

            if (context == TokenContext.Bare && input.IsOptional)
                Add(RecipeErrorCode.OptionalInputInBareToken, path, name: RecipeText.MakeSafe(name));

            if (startsToken)
                CheckFixedValues(path, input);
        }

        /// <summary>
        /// A value the author fixed (a choice, a default) is checked with the rule the expander applies
        /// at run time, for the position where the placeholder starts its token, so that a recipe which
        /// validates cannot carry a choice or a default that is always refused.
        /// </summary>
        private void CheckFixedValues(string path, RecipeInput input)
        {
            IEnumerable<string> fixedValues = input.Type == RecipeInputType.Choice
                ? (input.Choices ?? []).Concat(input.Default is null ? [] : [input.Default])
                : input.Default is null ? [] : [input.Default];

            foreach (string value in fixedValues.Distinct(StringComparer.Ordinal))
            {
                RecipeValueRefusal refusal = RecipeValueRule.Check(input, value, placeholderStartsToken: true);
                if (refusal is RecipeValueRefusal.LeadingDash or RecipeValueRefusal.LeadingSlash or RecipeValueRefusal.LeadingAt)
                    Add(RecipeErrorCode.FixedValueStartsWithOptionCharacter, path, name: RecipeText.MakeSafe(input.Name), fragment: RecipeText.MakeSafe(value));
            }
        }

        private void CheckLabels()
        {
            if (_recipe.Labels.Count > RecipeLimits.MaxLabelLanguages)
                Add(RecipeErrorCode.TooMany, RecipePath.Labels, actual: _recipe.Labels.Count, limit: RecipeLimits.MaxLabelLanguages);

            foreach ((string language, RecipeLabels labels) in _recipe.Labels)
            {
                string path = RecipePath.Property(RecipePath.Labels, language);
                if (!RecipeFormat.IsLanguageTag(language))
                    Add(RecipeErrorCode.InvalidLanguageTag, path, fragment: RecipeText.MakeSafe(language));

                if (labels.Name is not null)
                    CheckText(RecipePath.Property(path, RecipePath.Name), labels.Name, RecipeLimits.MaxNameLength, forbidden: false);

                if (labels.Description is not null)
                    CheckText(RecipePath.Property(path, RecipePath.Description), labels.Description, RecipeLimits.MaxDescriptionLength, forbidden: false);

                string fieldsPath = RecipePath.Property(path, RecipePath.Fields);
                foreach ((string field, string text) in labels.Fields)
                {
                    string fieldPath = RecipePath.Property(fieldsPath, field);
                    if (!_inputs.ContainsKey(field))
                        Add(RecipeErrorCode.UnknownLabelField, fieldPath, name: RecipeText.MakeSafe(field));

                    CheckText(fieldPath, text, RecipeLimits.MaxLabelLength, forbidden: false);
                }
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

            if (_recipe.Icon is not null && !RecipeIcons.Keys.Contains(_recipe.Icon))
                Add(RecipeErrorCode.UnknownIcon, RecipePath.Icon, fragment: RecipeText.MakeSafe(_recipe.Icon));
        }
    }
}
