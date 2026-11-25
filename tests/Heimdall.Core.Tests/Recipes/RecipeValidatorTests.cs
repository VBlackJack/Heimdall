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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>Validation of a parsed recipe: shapes, caps, placeholders, groups, import refusals.</summary>
public sealed class RecipeValidatorTests
{
    private static Recipe WithToken(string token)
        => RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare(token)] };

    private static Recipe WithInput(RecipeInput input, params RecipeArgument[] arguments)
        => RecipeTestData.With([input], arguments);

    [Fact]
    public void TheMinimalRecipe_HasNoError()
        => Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal()));

    [Fact]
    public void TheFullRecipe_HasNoError()
        => Assert.Empty(RecipeValidator.Validate(RecipeTestData.ParseValid(RecipeTestData.FullJson)));

    [Theory]
    [InlineData("acme.ping", true)]
    [InlineData("a.b", true)]
    [InlineData("acme-labs.net-tools", true)]
    [InlineData("a1.b2", true)]
    [InlineData("acme", false)]
    [InlineData("acme.ping.extra", false)]
    [InlineData("Acme.ping", false)]
    [InlineData("acme.Ping", false)]
    [InlineData("acme.pi_ng", false)]
    [InlineData(".ping", false)]
    [InlineData("acme.", false)]
    [InlineData("-acme.ping", false)]
    [InlineData("acme.ping-", false)]
    [InlineData("acme .ping", false)]
    [InlineData("", false)]
    public void TheIdMustBeTwoLowercaseSlugs(string id, bool valid)
    {
        IReadOnlyList<RecipeErrorCode> codes = RecipeTestData.Codes(RecipeTestData.Minimal() with { Id = id });

        Assert.Equal(valid, codes.Count == 0);
        if (!valid)
            Assert.Contains(RecipeErrorCode.InvalidIdShape, codes);
    }

    [Fact]
    public void AnIdPartLongerThanTheCap_IsRefused()
    {
        string part = new('a', RecipeLimits.MaxIdPartLength + 1);

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(RecipeTestData.Minimal() with { Id = part + ".ping" });

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.TooLong, error.Code);
        Assert.Equal(RecipeLimits.MaxIdPartLength + 1, error.Actual);
        Assert.Equal(RecipeLimits.MaxIdPartLength, error.Limit);
    }

    [Fact]
    public void AnIdPartAtTheCap_IsAccepted()
    {
        string id = new string('a', RecipeLimits.MaxIdPartLength) + "." + new string('b', RecipeLimits.MaxIdPartLength);

        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal() with { Id = id }));
    }

    [Fact]
    public void AVeryLongId_IsRefusedWithoutBeingTakenApart()
    {
        string id = new('a', 10_000);

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Id = id }));

        Assert.Equal(RecipeErrorCode.TooLong, error.Code);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.0.1", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0.0-alpha", true)]
    [InlineData("1.0.0-alpha.1", true)]
    [InlineData("1.0.0+build.7", true)]
    [InlineData("1.0.0-rc.1+build.7", true)]
    [InlineData("1.0", false)]
    [InlineData("1", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-", false)]
    [InlineData("1.0.0 ", false)]
    [InlineData("1.0.0\n", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("", false)]
    public void TheVersionMustBeSemver(string version, bool valid)
    {
        IReadOnlyList<RecipeErrorCode> codes = RecipeTestData.Codes(RecipeTestData.Minimal() with { Version = version });

        Assert.Equal(valid, codes.Count == 0);
    }

    [Fact]
    public void AVersionLongerThanTheCap_IsRefused()
    {
        string version = "1.0.0-" + new string('a', RecipeLimits.MaxVersionLength);

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Version = version }));

        Assert.Equal(RecipeErrorCode.TooLong, error.Code);
    }

    [Fact]
    public void ADisplayedTextAtItsCap_IsAcceptedAndOneOverIsRefused()
    {
        Recipe atCap = RecipeTestData.Minimal() with
        {
            Name = new string('n', RecipeLimits.MaxNameLength),
            Author = new string('a', RecipeLimits.MaxAuthorLength),
            Description = new string('d', RecipeLimits.MaxDescriptionLength),
        };
        Assert.Empty(RecipeValidator.Validate(atCap));

        Assert.Equal(RecipeErrorCode.TooLong, Assert.Single(RecipeValidator.Validate(atCap with { Name = atCap.Name + "n" })).Code);
        Assert.Equal(RecipeErrorCode.TooLong, Assert.Single(RecipeValidator.Validate(atCap with { Author = atCap.Author + "a" })).Code);
        Assert.Equal(RecipeErrorCode.TooLong, Assert.Single(RecipeValidator.Validate(atCap with { Description = atCap.Description + "d" })).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMandatoryTextCannotBeBlank(string blank)
    {
        Assert.Contains(RecipeErrorCode.EmptyValue, RecipeTestData.Codes(RecipeTestData.Minimal() with { Name = blank }));
        Assert.Contains(RecipeErrorCode.EmptyValue, RecipeTestData.Codes(RecipeTestData.Minimal() with { Description = blank }));
        Assert.Contains(RecipeErrorCode.EmptyValue, RecipeTestData.Codes(RecipeTestData.Minimal() with { Author = blank }));
        Assert.Contains(RecipeErrorCode.EmptyValue, RecipeTestData.Codes(RecipeTestData.Minimal() with { Program = blank }));
    }

    [Fact]
    public void TheProgramHasItsOwnCap()
    {
        Recipe atCap = RecipeTestData.Minimal() with { Program = new string('p', RecipeLimits.MaxProgramLength) };
        Assert.Empty(RecipeValidator.Validate(atCap));

        Assert.Equal(RecipeErrorCode.TooLong, Assert.Single(RecipeValidator.Validate(atCap with { Program = atCap.Program + "p" })).Code);
    }

    [Fact]
    public void TheTokenCount_IsCappedAcrossBareTokensGroupsAndWhenTrue()
    {
        RecipeArgument[] arguments = [.. Enumerable.Range(0, RecipeLimits.MaxTokens).Select(_ => RecipeArgument.Bare("x"))];
        Recipe atCap = RecipeTestData.Minimal() with { Arguments = arguments };
        Assert.Empty(RecipeValidator.Validate(atCap));

        Recipe over = atCap with { Arguments = [.. arguments, RecipeArgument.Group(["y"])] };
        RecipeError error = Assert.Single(RecipeValidator.Validate(over));

        Assert.Equal(RecipeErrorCode.TooMany, error.Code);
        Assert.Equal(RecipeLimits.MaxTokens + 1, error.Actual);
        Assert.Equal(RecipeLimits.MaxTokens, error.Limit);
    }

    [Fact]
    public void AWhenTrueTokenCountsTowardTheTokenCap()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-a", "-b"] };
        RecipeArgument[] filler = [.. Enumerable.Range(0, RecipeLimits.MaxTokens - 1).Select(_ => RecipeArgument.Bare("x"))];
        Recipe recipe = RecipeTestData.Minimal() with { Inputs = [flag], Arguments = [.. filler, RecipeArgument.Bare("{in:flag}")] };

        Assert.Contains(RecipeErrorCode.TooMany, RecipeTestData.Codes(recipe));
    }

    [Fact]
    public void ATokenLength_IsCapped()
    {
        Assert.Empty(RecipeValidator.Validate(WithToken(new string('x', RecipeLimits.MaxTokenLength))));

        RecipeError error = Assert.Single(RecipeValidator.Validate(WithToken(new string('x', RecipeLimits.MaxTokenLength + 1))));
        Assert.Equal(RecipeErrorCode.TooLong, error.Code);
        Assert.Equal("arguments[0]", error.Path);
    }

    [Fact]
    public void TheInputCount_IsCapped()
    {
        RecipeInput[] atCap = [.. Enumerable.Range(0, RecipeLimits.MaxInputs).Select(index => RecipeTestData.Input("i" + index, RecipeInputType.String))];
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(atCap)));

        RecipeInput[] over = [.. atCap, RecipeTestData.Input("extra", RecipeInputType.String)];
        Assert.Contains(RecipeErrorCode.TooMany, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = over }));
    }

    [Fact]
    public void AnInputLabel_IsCapped()
    {
        RecipeInput input = RecipeTestData.Input("a", RecipeInputType.String) with { Label = new string('l', RecipeLimits.MaxLabelLength + 1) };

        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [input] }));
    }

    [Fact]
    public void TheChoiceList_AndEachChoice_AreCapped()
    {
        RecipeInput atCap = RecipeTestData.Input("c", RecipeInputType.Choice) with
        {
            Choices = [.. Enumerable.Range(0, RecipeLimits.MaxChoices).Select(index => "v" + index)],
        };
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(atCap)));

        RecipeInput tooMany = atCap with { Choices = [.. atCap.Choices!, "extra"] };
        Assert.Contains(RecipeErrorCode.TooMany, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [tooMany] }));

        RecipeInput tooLong = atCap with { Choices = [new string('c', RecipeLimits.MaxChoiceLength + 1)] };
        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [tooLong] }));
    }

    [Fact]
    public void ADefaultLength_IsCapped()
    {
        RecipeInput input = RecipeTestData.Input("a", RecipeInputType.String, defaultValue: new string('d', RecipeLimits.MaxTokenLength + 1)) with
        {
            MaxLength = RecipeLimits.MaxStringValueLength,
        };

        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [input] }));
    }

    [Fact]
    public void ThePatternLength_AndTheMaxLength_AreBounded()
    {
        RecipeInput longPattern = RecipeTestData.Input("a", RecipeInputType.String) with { Pattern = new string('a', RecipeLimits.MaxPatternLength + 1) };
        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [longPattern] }));

        RecipeInput zero = RecipeTestData.Input("a", RecipeInputType.String) with { MaxLength = 0 };
        Assert.Contains(RecipeErrorCode.ValueOutOfRange, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [zero] }));

        RecipeInput huge = RecipeTestData.Input("a", RecipeInputType.String) with { MaxLength = RecipeLimits.MaxStringValueLength + 1 };
        Assert.Contains(RecipeErrorCode.ValueOutOfRange, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [huge] }));

        RecipeInput max = RecipeTestData.Input("a", RecipeInputType.String) with { MaxLength = RecipeLimits.MaxStringValueLength };
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(max)));
    }

    [Fact]
    public void TheNumberOfLanguages_AndTheirTexts_AreCapped()
    {
        Dictionary<string, RecipeTranslation> languages = Enumerable.Range(0, RecipeLimits.MaxTranslationLanguages + 1)
            .ToDictionary(index => "x" + (char)('a' + index), _ => new RecipeTranslation("n", null, new Dictionary<string, string>()));
        Assert.Contains(RecipeErrorCode.TooMany, RecipeTestData.Codes(RecipeTestData.Minimal() with { Translations = languages }));

        Dictionary<string, RecipeTranslation> longName = new() { ["fr"] = new RecipeTranslation(new string('n', RecipeLimits.MaxNameLength + 1), null, new Dictionary<string, string>()) };
        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Translations = longName }));

        Dictionary<string, RecipeTranslation> longDescription = new() { ["fr"] = new RecipeTranslation(null, new string('d', RecipeLimits.MaxDescriptionLength + 1), new Dictionary<string, string>()) };
        Assert.Contains(RecipeErrorCode.TooLong, RecipeTestData.Codes(RecipeTestData.Minimal() with { Translations = longDescription }));
    }

    [Theory]
    [InlineData("fr", true)]
    [InlineData("en-GB", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("f", false)]
    [InlineData("fr_FR", false)]
    [InlineData("", false)]
    [InlineData("fr-", false)]
    [InlineData("12", false)]
    public void ALanguageTagMustBeWellFormed(string tag, bool valid)
    {
        Dictionary<string, RecipeTranslation> labels = new() { [tag] = new RecipeTranslation("n", null, new Dictionary<string, string>()) };

        Assert.Equal(valid, RecipeValidator.Validate(RecipeTestData.Minimal() with { Translations = labels }).Count == 0);
    }

    [Fact]
    public void ALabelForAnInputThatDoesNotExist_IsRefused()
    {
        Dictionary<string, RecipeTranslation> labels = new() { ["fr"] = new RecipeTranslation(null, null, new Dictionary<string, string> { ["ghost"] = "Fantome" }) };

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Translations = labels }));

        Assert.Equal(RecipeErrorCode.UnknownTranslationField, error.Code);
        Assert.Equal("ghost", error.Name);
    }

    [Fact]
    public void TheRunSettings_AreBounded()
    {
        Assert.Contains(RecipeErrorCode.ValueOutOfRange, RecipeTestData.Codes(RecipeTestData.Minimal() with { TimeoutSeconds = 0 }));
        Assert.Contains(RecipeErrorCode.ValueOutOfRange, RecipeTestData.Codes(RecipeTestData.Minimal() with { TimeoutSeconds = RecipeLimits.MaxTimeoutSeconds + 1 }));
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal() with { TimeoutSeconds = RecipeLimits.MaxTimeoutSeconds }));
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal() with { TimeoutSeconds = RecipeLimits.MinTimeoutSeconds }));

        Assert.Contains(RecipeErrorCode.EmptyValue, RecipeTestData.Codes(RecipeTestData.Minimal() with { SuccessExitCodes = [] }));
        int[] tooManyCodes = [.. Enumerable.Range(0, RecipeLimits.MaxSuccessExitCodes + 1)];
        Assert.Contains(RecipeErrorCode.TooMany, RecipeTestData.Codes(RecipeTestData.Minimal() with { SuccessExitCodes = tooManyCodes }));
    }

    [Fact]
    public void AnIcon_MustHaveTheShapeOfAKey_NotBeInAClosedSet()
    {
        Assert.All(RecipeIcons.Keys, key => Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal() with { Icon = key })));

        // Reversed by the format freeze: a key this version has no glyph for is accepted (it shows the default icon).
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Minimal() with { Icon = "skull" }));

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Icon = "Skull" }));
        Assert.Equal(RecipeErrorCode.InvalidIconKey, error.Code);
    }

    [Fact]
    public void ASchemaVersionOutOfRange_IsRefused()
    {
        Assert.Contains(RecipeErrorCode.ValueOutOfRange, RecipeTestData.Codes(RecipeTestData.Minimal() with { SchemaVersion = 0 }));

        RecipeError newer = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { SchemaVersion = 2 }));
        Assert.Equal(RecipeErrorCode.UnsupportedSchemaVersion, newer.Code);
        Assert.Equal(2, newer.Actual);
        Assert.Equal(1, newer.Limit);
    }

    // ---- placeholders ----

    [Theory]
    [InlineData("{Host}")]
    [InlineData("{Port}")]
    [InlineData("--host={Host}:{Port}")]
    [InlineData("{{literal}}")]
    [InlineData("{{{Host}}}")]
    [InlineData("{{{Host}")]
    [InlineData("a{{b")]
    [InlineData("b}}a")]
    [InlineData("-n")]
    [InlineData("/all")]
    public void AWellFormedToken_IsAccepted(string token)
        => Assert.Empty(RecipeValidator.Validate(WithToken(token)));

    [Theory]
    [InlineData("{foo}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{host}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{HOST}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{port}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{Host }", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{ Host}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{IN:x}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{in:}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{in:1x}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{in:x y}", RecipeErrorCode.UnknownPlaceholder)]
    [InlineData("{Host", RecipeErrorCode.MalformedPlaceholder)]
    [InlineData("Host}", RecipeErrorCode.MalformedPlaceholder)]
    [InlineData("{{Host}", RecipeErrorCode.MalformedPlaceholder)]
    [InlineData("{a{Host}}", RecipeErrorCode.MalformedPlaceholder)]
    public void ABrokenPlaceholder_IsRefused(string token, RecipeErrorCode expected)
    {
        RecipeError error = Assert.Single(RecipeValidator.Validate(WithToken(token)));

        Assert.Equal(expected, error.Code);
        Assert.Equal("arguments[0]", error.Path);
    }

    [Fact]
    public void AnInputPlaceholderThatNamesNoInput_IsRefusedWithTheName()
    {
        RecipeError error = Assert.Single(RecipeValidator.Validate(WithToken("{in:missing}")));

        Assert.Equal(RecipeErrorCode.UnknownInput, error.Code);
        Assert.Equal("missing", error.Name);
    }

    [Fact]
    public void AnInputPlaceholder_IsCaseSensitive()
    {
        Recipe recipe = WithInput(RecipeTestData.Input("target", RecipeInputType.String, required: true), RecipeArgument.Bare("{in:Target}"));

        Assert.Contains(RecipeErrorCode.UnknownInput, RecipeTestData.Codes(recipe));
    }

    [Fact]
    public void AnEscapedBracePair_IsNotAPlaceholder()
    {
        // "{{in:ghost}}" is the literal text {in:ghost}, so no input is needed.
        Assert.Empty(RecipeValidator.Validate(WithToken("{{in:ghost}}")));
    }

    // ---- groups and optional inputs ----

    [Fact]
    public void AnOptionalInput_IsAllowedInAGroup()
    {
        Recipe recipe = WithInput(
            RecipeTestData.Input("extra", RecipeInputType.String),
            RecipeArgument.Group(["--extra", "{in:extra}"]));

        Assert.Empty(RecipeValidator.Validate(recipe));
    }

    [Fact]
    public void AnOptionalInput_IsRefusedInABareToken()
    {
        Recipe recipe = WithInput(
            RecipeTestData.Input("extra", RecipeInputType.String),
            RecipeArgument.Bare("{in:extra}"));

        RecipeError error = Assert.Single(RecipeValidator.Validate(recipe));

        Assert.Equal(RecipeErrorCode.OptionalInputInBareToken, error.Code);
        Assert.Equal("extra", error.Name);
    }

    [Fact]
    public void ARequiredInput_AndAnInputWithADefault_AreAllowedInABareToken()
    {
        Recipe required = WithInput(RecipeTestData.Input("a", RecipeInputType.String, required: true), RecipeArgument.Bare("{in:a}"));
        Recipe defaulted = WithInput(RecipeTestData.Input("a", RecipeInputType.String, defaultValue: "x"), RecipeArgument.Bare("{in:a}"));

        Assert.Empty(RecipeValidator.Validate(required));
        Assert.Empty(RecipeValidator.Validate(defaulted));
    }

    [Fact]
    public void AnEmptyGroup_AndAnEmptyToken_AreRefused()
    {
        Assert.Contains(RecipeErrorCode.EmptyGroup, RecipeTestData.Codes(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Group([])] }));
        Assert.Contains(RecipeErrorCode.EmptyToken, RecipeTestData.Codes(WithToken(string.Empty)));
        Assert.Contains(RecipeErrorCode.EmptyToken, RecipeTestData.Codes(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Group([""])] }));
    }

    // ---- bool inputs ----

    [Fact]
    public void ABoolInput_IsPlacedAsAWholeBareToken()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v"] };

        Assert.Empty(RecipeValidator.Validate(WithInput(flag, RecipeArgument.Bare("{in:flag}"))));
    }

    [Theory]
    [InlineData("--x={in:flag}")]
    [InlineData("{in:flag}{in:flag}")]
    public void ABoolInput_CannotBeUsedInsideTokenText(string token)
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v"] };

        Assert.Contains(RecipeErrorCode.BoolInputInTokenText, RecipeTestData.Codes(WithInput(flag, RecipeArgument.Bare(token))));
    }

    [Fact]
    public void ABoolInput_CannotBeUsedInAGroup()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v"] };

        Assert.Contains(RecipeErrorCode.BoolInputInTokenText, RecipeTestData.Codes(WithInput(flag, RecipeArgument.Group(["--x", "{in:flag}"]))));
    }

    [Fact]
    public void ABoolInput_NeverPlaced_IsRefused()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v"] };

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Inputs = [flag] }));

        Assert.Equal(RecipeErrorCode.UnreferencedInput, error.Code);
        Assert.Equal("inputs[0]", error.Path);
    }

    [Fact]
    public void ABoolInput_NeedsItsWhenTrueTokens()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool);

        Assert.Contains(RecipeErrorCode.MissingSwitchArguments, RecipeTestData.Codes(WithInput(flag, RecipeArgument.Bare("{in:flag}"))));
        Assert.Contains(RecipeErrorCode.MissingSwitchArguments, RecipeTestData.Codes(WithInput(flag with { WhenTrue = [] }, RecipeArgument.Bare("{in:flag}"))));
    }

    [Fact]
    public void WhenTrue_IsOnlyAllowedOnABoolInput()
    {
        RecipeInput text = RecipeTestData.Input("a", RecipeInputType.String) with { WhenTrue = ["-v"] };

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Using(text)));

        Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code);
        Assert.Equal("whenTrue", error.Fragment);
    }

    [Fact]
    public void AWhenTrueToken_IsCheckedLikeAnyToken()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["{nope}", @"\\srv"] };

        IReadOnlyList<RecipeErrorCode> codes = RecipeTestData.Codes(WithInput(flag, RecipeArgument.Bare("{in:flag}")));

        Assert.Contains(RecipeErrorCode.UnknownPlaceholder, codes);
        Assert.Contains(RecipeErrorCode.ForbiddenBackslashPair, codes);
    }

    // ---- inputs ----

    [Fact]
    public void TwoInputsWithTheSameName_AreRefused()
    {
        RecipeInput a = RecipeTestData.Input("a", RecipeInputType.String, required: true);

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Using(a, a)));

        Assert.Equal(RecipeErrorCode.DuplicateInputName, error.Code);
        Assert.Equal("inputs[1].name", error.Path);
    }

    [Theory]
    [InlineData("1a")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("a:b")]
    public void AnInputName_MustBeAnIdentifier(string name)
    {
        RecipeInput input = RecipeTestData.Input(name, RecipeInputType.String);

        Assert.Contains(RecipeErrorCode.InvalidInputName, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [input] }));
    }

    [Fact]
    public void APatternAndAMaxLength_AreOnlyAllowedOnAString()
    {
        RecipeInput number = RecipeTestData.Input("n", RecipeInputType.Int) with { Pattern = "[0-9]+", MaxLength = 5 };

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(RecipeTestData.Using(number));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code));
    }

    [Theory]
    [InlineData("([")]
    [InlineData("a)|(b")]
    [InlineData("*")]
    [InlineData("(?<n>")]
    public void AnInvalidPattern_IsRefused(string pattern)
    {
        RecipeInput input = RecipeTestData.Input("a", RecipeInputType.String) with { Pattern = pattern };

        Assert.Contains(RecipeErrorCode.InvalidPattern, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [input] }));
    }

    [Fact]
    public void AValidPattern_IsAccepted()
    {
        RecipeInput input = RecipeTestData.Input("a", RecipeInputType.String) with { Pattern = "[a-z]{1,8}(-[0-9]+)?" };

        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(input)));
    }

    [Fact]
    public void AChoiceInput_NeedsAList_WithoutDuplicates_AndADefaultFromIt()
    {
        RecipeInput choice = RecipeTestData.Input("c", RecipeInputType.Choice);
        Assert.Contains(RecipeErrorCode.MissingChoices, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [choice] }));
        Assert.Contains(RecipeErrorCode.MissingChoices, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [choice with { Choices = [] }] }));

        RecipeInput duplicated = choice with { Choices = ["a", "b", "a"] };
        Assert.Contains(RecipeErrorCode.DuplicateChoice, RecipeTestData.Codes(RecipeTestData.Minimal() with { Inputs = [duplicated] }));

        RecipeInput badDefault = choice with { Choices = ["a", "b"], Default = "c" };
        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Using(badDefault)));
        Assert.Equal(RecipeErrorCode.InvalidChoiceDefault, error.Code);

        RecipeInput good = choice with { Choices = ["a", "b"], Default = "b" };
        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(good)));
    }

    [Fact]
    public void Choices_AreOnlyAllowedOnAChoiceInput()
    {
        RecipeInput text = RecipeTestData.Input("a", RecipeInputType.String) with { Choices = ["x"] };

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Using(text)));

        Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code);
        Assert.Equal("choices", error.Fragment);
    }

    [Theory]
    [InlineData(RecipeInputType.Int, "abc")]
    [InlineData(RecipeInputType.Int, "1.5")]
    [InlineData(RecipeInputType.Number, "NaN")]
    [InlineData(RecipeInputType.Bool, "yes")]
    [InlineData(RecipeInputType.Hostname, "bad host")]
    [InlineData(RecipeInputType.IpAddress, "999.1.1.1")]
    [InlineData(RecipeInputType.Path, "CON")]
    public void ADefaultThatTheTypeRefuses_IsRefused(RecipeInputType type, string defaultValue)
    {
        RecipeInput input = RecipeTestData.Input("a", type, defaultValue: defaultValue);
        if (type == RecipeInputType.Bool)
            input = input with { WhenTrue = ["-v"] };

        Recipe recipe = RecipeTestData.Minimal() with
        {
            Inputs = [input],
            Arguments = type == RecipeInputType.Bool ? [RecipeArgument.Bare("{in:a}")] : [RecipeArgument.Group(["--a={in:a}"])],
        };

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(recipe);

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.InvalidDefault, error.Code);
        Assert.Equal("inputs[0].default", error.Path);
    }

    [Fact]
    public void ADefaultLeadingWithADash_IsNotRefusedAsItDoesNotStartAToken()
    {
        RecipeInput input = RecipeTestData.Input("n", RecipeInputType.Int, defaultValue: "-5");

        Assert.Empty(RecipeValidator.Validate(RecipeTestData.Using(input)));
    }

    // ---- import refusals: network paths, URLs, environment variables, response files ----

    public static TheoryData<string, RecipeErrorCode> ForbiddenTexts => new()
    {
        { @"\\server\share", RecipeErrorCode.ForbiddenBackslashPair },
        { @"--out=\\server", RecipeErrorCode.ForbiddenBackslashPair },
        { "//server/share", RecipeErrorCode.ForbiddenDoubleSlash },
        { "--out=//host", RecipeErrorCode.ForbiddenDoubleSlash },
        { "http://example.com", RecipeErrorCode.ForbiddenUrl },
        { "--url=ftp://example.com/x", RecipeErrorCode.ForbiddenUrl },
        { "%PATH%", RecipeErrorCode.ForbiddenPercent },
        { "50%", RecipeErrorCode.ForbiddenPercent },
        { "@response.txt", RecipeErrorCode.ForbiddenLeadingAt },
        { "@{Host}", RecipeErrorCode.ForbiddenLeadingAt },
    };

    public static TheoryData<string> AllowedTexts => new()
    {
        { "-n" },
        { "/all" },
        { "user@host" },
        { "a/b" },
        { @"C:\Windows\System32" },
        { "a:b" },
        { "key=value" },
        { "1.2.3" },
    };

    [Theory]
    [MemberData(nameof(ForbiddenTexts))]
    public void ALiteralTokenWithAForbiddenText_IsRefusedAndTheEntryIsNamed(string text, RecipeErrorCode expected)
    {
        Recipe recipe = RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("{Host}"), RecipeArgument.Group([text])] };

        RecipeError error = Assert.Single(RecipeValidator.Validate(recipe), e => e.Code == expected);

        Assert.Equal("arguments[1][0]", error.Path);
    }

    [Theory]
    [MemberData(nameof(ForbiddenTexts))]
    public void ADefaultValueWithAForbiddenText_IsRefusedAndTheEntryIsNamed(string text, RecipeErrorCode expected)
    {
        RecipeInput input = RecipeTestData.Input("a", RecipeInputType.String, defaultValue: text);

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(RecipeTestData.Minimal() with { Inputs = [input] });

        RecipeError error = Assert.Single(errors, e => e.Code == expected);
        Assert.Equal("inputs[0].default", error.Path);
    }

    [Theory]
    [MemberData(nameof(ForbiddenTexts))]
    public void AChoiceValueWithAForbiddenText_IsRefusedAndTheEntryIsNamed(string text, RecipeErrorCode expected)
    {
        RecipeInput input = RecipeTestData.Input("c", RecipeInputType.Choice) with { Choices = ["fine", text] };

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(RecipeTestData.Minimal() with { Inputs = [input] });

        RecipeError error = Assert.Single(errors, e => e.Code == expected);
        Assert.Equal("inputs[0].choices[1]", error.Path);
    }

    [Theory]
    [MemberData(nameof(ForbiddenTexts))]
    public void TheProgramWithAForbiddenText_IsRefused(string text, RecipeErrorCode expected)
        => Assert.Contains(expected, RecipeTestData.Codes(RecipeTestData.Minimal() with { Program = text }));

    [Theory]
    [MemberData(nameof(AllowedTexts))]
    public void OrdinaryTokenText_IsNotRefused(string text)
        => Assert.Empty(RecipeValidator.Validate(WithToken(text)));

    [Fact]
    public void AUrl_IsReportedOnceAsAUrl()
    {
        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(WithToken("http://example.com"));

        Assert.Equal(RecipeErrorCode.ForbiddenUrl, Assert.Single(errors).Code);
    }

    [Fact]
    public void ALiteralTokenWithAControlOrFormatCharacter_IsRefusedAndMadeSafe()
    {
        string token = "-x\u202Ey\u0007z";

        RecipeError error = Assert.Single(RecipeValidator.Validate(WithToken(token)));

        Assert.Equal(RecipeErrorCode.ControlCharacter, error.Code);
        Assert.Equal("-x\\u202Ey\\u0007z", error.Fragment);
    }

    [Fact]
    public void EveryErrorFragment_ContainsNoControlOrFormatCharacter()
    {
        Recipe recipe = RecipeTestData.Minimal() with { Id = "bad\u202Eid", Version = "v\u0001", Icon = "x\u200B" };

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(recipe);

        Assert.NotEmpty(errors);
        Assert.All(errors.Where(error => error.Fragment is not null), error => Assert.False(RecipeText.ContainsRefusedCharacter(error.Fragment!)));
    }

    [Fact]
    public void AFragment_IsCutToTheCapWithAnAsciiEllipsis()
    {
        string id = new('A', (RecipeLimits.MaxIdPartLength * 2) + 1);

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Minimal() with { Id = id }));

        Assert.Equal(RecipeErrorCode.InvalidIdShape, error.Code);
        Assert.Equal(new string('A', RecipeLimits.MaxFragmentLength) + "...", error.Fragment);
    }

    [Fact]
    public void EveryProblemIsReported_NotOnlyTheFirst()
    {
        Recipe recipe = RecipeTestData.Minimal() with { Id = "Bad", Version = "x", Arguments = [RecipeArgument.Bare("{nope}")] };

        IReadOnlyList<RecipeErrorCode> codes = RecipeTestData.Codes(recipe);

        Assert.Contains(RecipeErrorCode.InvalidIdShape, codes);
        Assert.Contains(RecipeErrorCode.InvalidVersion, codes);
        Assert.Contains(RecipeErrorCode.UnknownPlaceholder, codes);
    }
}
