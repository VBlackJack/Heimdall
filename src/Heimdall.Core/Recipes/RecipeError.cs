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

namespace Heimdall.Core.Recipes;

/// <summary>What is wrong with a recipe. A later layer maps each code to localized text.</summary>
public enum RecipeErrorCode
{
    /// <summary>The input is larger than <see cref="RecipeLimits.MaxFileBytes"/>; it was not parsed.</summary>
    FileTooLarge,

    /// <summary>The input is empty.</summary>
    EmptyDocument,

    /// <summary>The input is not valid JSON (or not valid UTF-8, or nested too deep).</summary>
    SyntaxError,

    /// <summary>The file was written for a newer format: <c>Actual</c> is its version, <c>Limit</c> the supported one.</summary>
    UnsupportedSchemaVersion,

    /// <summary>A property the format does not define.</summary>
    UnknownProperty,

    /// <summary>The same property appears twice in one object.</summary>
    DuplicateProperty,

    /// <summary>A mandatory property is absent.</summary>
    MissingProperty,

    /// <summary>A value has the wrong JSON type.</summary>
    WrongType,

    /// <summary>A text names a value that is not in the closed set for that property.</summary>
    InvalidEnumValue,

    /// <summary>A number is outside its bounds: <c>Actual</c> is the value, <c>Limit</c> the violated bound.</summary>
    ValueOutOfRange,

    /// <summary>A text or list is longer than allowed: <c>Actual</c> is its size, <c>Limit</c> the cap.</summary>
    TooLong,

    /// <summary>A list holds more entries than allowed: <c>Actual</c> is its size, <c>Limit</c> the cap.</summary>
    TooMany,

    /// <summary>A text or list that must not be empty is empty.</summary>
    EmptyValue,

    /// <summary>A text holds a control, format or separator character.</summary>
    ControlCharacter,

    /// <summary>The id is not <c>author.name</c> of lowercase slugs.</summary>
    InvalidIdShape,

    /// <summary>The version is not a semantic version.</summary>
    InvalidVersion,

    /// <summary>An input name is not a legal name.</summary>
    InvalidInputName,

    /// <summary>Two inputs share a name.</summary>
    DuplicateInputName,

    /// <summary>A language tag is not well formed.</summary>
    InvalidLanguageTag,

    /// <summary>A labels entry names an input that does not exist.</summary>
    UnknownLabelField,

    /// <summary>The icon key is not in the closed set.</summary>
    UnknownIcon,

    /// <summary>A property is not allowed for the type of its input: <c>Fragment</c> names the property.</summary>
    PropertyNotAllowedForType,

    /// <summary>A <c>choice</c> input has no list, or an empty one.</summary>
    MissingChoices,

    /// <summary>Two entries of a choice list are equal.</summary>
    DuplicateChoice,

    /// <summary>The default of a <c>choice</c> input is not in its list.</summary>
    InvalidChoiceDefault,

    /// <summary>The default is not valid for the type of its input; <c>Fragment</c> names the value rule that refused it.</summary>
    InvalidDefault,

    /// <summary>A <c>pattern</c> is not a valid regular expression.</summary>
    InvalidPattern,

    /// <summary>A <c>bool</c> input has no <c>whenTrue</c> tokens.</summary>
    MissingWhenTrue,

    /// <summary>A <c>bool</c> input is never placed in the argument list.</summary>
    UnreferencedBoolInput,

    /// <summary>A token is empty.</summary>
    EmptyToken,

    /// <summary>A group has no token.</summary>
    EmptyGroup,

    /// <summary>A <c>{...}</c> that is neither <c>{Host}</c>, <c>{Port}</c> nor <c>{in:name}</c>; <c>Fragment</c> holds it.</summary>
    UnknownPlaceholder,

    /// <summary>A brace that opens or closes nothing (an unclosed <c>{</c>, a lone <c>}</c>, a nested <c>{</c>).</summary>
    MalformedPlaceholder,

    /// <summary><c>{in:name}</c> names no declared input; <c>Fragment</c> holds the name.</summary>
    UnknownInput,

    /// <summary>A bare token uses an input that can be empty, which would leave the argument list ambiguous; use a group.</summary>
    OptionalInputInBareToken,

    /// <summary>A <c>bool</c> input is used anywhere other than as a whole bare token.</summary>
    BoolInputInTokenText,

    /// <summary>A literal text holds <c>\\</c> (a network path).</summary>
    ForbiddenBackslashPair,

    /// <summary>A literal text holds <c>//</c> (a network path).</summary>
    ForbiddenDoubleSlash,

    /// <summary>A literal text holds <c>://</c> (a URL).</summary>
    ForbiddenUrl,

    /// <summary>A literal text holds <c>%</c> (an environment variable).</summary>
    ForbiddenPercent,

    /// <summary>A literal text starts with <c>@</c> (a response file).</summary>
    ForbiddenLeadingAt,

    /// <summary>
    /// A choice or a default the author fixed would always be refused at run time, because it starts
    /// with <c>-</c>, <c>/</c> or <c>@</c> where its placeholder starts the argument: <c>Name</c> is
    /// the input, <c>Fragment</c> the value, <c>Path</c> the token that uses the input.
    /// </summary>
    FixedValueStartsWithOptionCharacter,

    /// <summary>A literal text holds a backslash and a slash in a row, in either order (a network path as Windows reads it).</summary>
    ForbiddenMixedSeparators,

    /// <summary>A literal text holds the NT object prefix: a separator, <c>??</c> and a separator.</summary>
    ForbiddenNtPathPrefix,

    /// <summary>
    /// A text that becomes an argument holds a character that a program reading its command line in
    /// an ANSI or OEM code page would be handed as an ASCII one (a full-width hyphen, a typographic quote).
    /// </summary>
    ForbiddenLookAlikeCharacter,

    /// <summary>
    /// A choice or a default the author fixed would always be refused at run time, at the place its
    /// placeholder stands, for a reason other than an option character: it would start with a
    /// separator or a space there, or join the text around it into a network path or a URL.
    /// <c>Name</c> is the input, <c>Fragment</c> the value, <c>Path</c> the token that uses the input.
    /// </summary>
    FixedValueRefusedAtRun,
}

/// <summary>A place in the source text, one-based.</summary>
/// <param name="Line">The line.</param>
/// <param name="Column">The column, counted in characters.</param>
public readonly record struct RecipePosition(int Line, int Column);

/// <summary>
/// One problem found in a recipe. Errors are data: there is deliberately no sentence here.
/// </summary>
/// <param name="Code">What is wrong.</param>
/// <param name="Position">Where in the file, when the reader could tell.</param>
/// <param name="Path">The entry at fault, as a structural path such as <c>arguments[2][0]</c> or <c>inputs[1].default</c>.</param>
/// <param name="Name">The input name, property name or placeholder name involved, when one is.</param>
/// <param name="Fragment">The offending text, already made safe to display (see <see cref="RecipeText.MakeSafe"/>).</param>
/// <param name="Actual">The offending size or number, when the code carries one.</param>
/// <param name="Limit">The bound that was violated, when the code carries one.</param>
public sealed record RecipeError(
    RecipeErrorCode Code,
    RecipePosition? Position = null,
    string? Path = null,
    string? Name = null,
    string? Fragment = null,
    int? Actual = null,
    int? Limit = null);

/// <summary>The outcome of parsing a recipe: the recipe, or the errors, never both.</summary>
public sealed class RecipeParseResult
{
    private RecipeParseResult(Recipe? recipe, IReadOnlyList<RecipeError> errors)
    {
        Recipe = recipe;
        Errors = errors;
    }

    /// <summary>The recipe when parsing and validation both succeeded; otherwise null.</summary>
    public Recipe? Recipe { get; }

    /// <summary>The errors; empty exactly when <see cref="Recipe"/> is set.</summary>
    public IReadOnlyList<RecipeError> Errors { get; }

    /// <summary>True when <see cref="Recipe"/> is set.</summary>
    public bool Succeeded => Recipe is not null;

    internal static RecipeParseResult Success(Recipe recipe) => new(recipe, []);

    internal static RecipeParseResult Failure(IReadOnlyList<RecipeError> errors)
    {
        if (errors.Count == 0)
            throw new ArgumentException("A failure needs at least one error.", nameof(errors));

        return new RecipeParseResult(null, errors);
    }
}
