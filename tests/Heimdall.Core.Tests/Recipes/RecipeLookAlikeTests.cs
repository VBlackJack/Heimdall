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
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// A program that reads its command line in the ANSI code page is handed some non-ASCII characters
/// as ASCII ones (a full-width hyphen as a hyphen, a full-width quote as a double quote that can
/// split a token). Every character that Windows would fold onto ASCII is refused wherever it
/// becomes part of an argument, while ordinary accented letters stay usable.
/// </summary>
public sealed class RecipeLookAlikeTests
{
    private static IRecipeCharacterConverter Plain => FakeCharacterConverter.Plain;

    /// <summary>Code points that are refused whatever the code page of the machine is.</summary>
    public static TheoryData<int> AlwaysRefused => new()
    {
        0xFF0D, 0xFF0F, 0xFF20, 0xFF3C, 0xFF02, 0xFF07, 0xFF21, 0xFF5E, 0xFFE5, 0xFFEF, 0xFF00,
        0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015,
        0x2212, 0x2215, 0x2044,
        0xFE50, 0xFE58, 0xFE63, 0xFE68, 0xFE6F,
        0x00A5, 0x20A9,
        0x2018, 0x2019, 0x201A, 0x201B, 0x201C, 0x201D, 0x201E, 0x201F,
        0x2032, 0x2033, 0x2034, 0x2035, 0x2036,
        0x02B9, 0x02BA, 0x02BC, 0x02C8, 0x02CA, 0x02DC,
        0x00A0, 0x1680, 0x2000, 0x2002, 0x2009, 0x200A, 0x202F, 0x205F, 0x3000,
    };

    private static string Of(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static RecipeInput Text(string name = "v")
        => RecipeTestData.Input(name, RecipeInputType.String, required: true) with { MaxLength = RecipeLimits.MaxStringValueLength };

    private static IReadOnlyList<RecipeError> Validate(Recipe recipe, IRecipeCharacterConverter? converter = null)
        => RecipeValidator.Validate(recipe, converter ?? Plain);

    // ---- run time, every typed value

    [Theory]
    [MemberData(nameof(AlwaysRefused))]
    public void AFoldedCharacter_IsRefusedAtTheStartOfATokenAndInsideAValue(int codePoint)
    {
        RecipeInput field = Text();
        string character = Of(codePoint);

        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.Check(field, character + "x", placeholderStartsToken: true, Plain));
        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.Check(field, "a" + character + "b", placeholderStartsToken: false, Plain));
        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.Check(field, "ab" + character, placeholderStartsToken: false, Plain));
    }

    [Theory]
    [MemberData(nameof(AlwaysRefused))]
    public void AFoldedCharacter_IsRefusedForEveryInputTypeAndForTheHost(int codePoint)
    {
        string value = "a" + Of(codePoint) + "b";

        foreach (RecipeInputType type in Enum.GetValues<RecipeInputType>())
        {
            RecipeInput input = type == RecipeInputType.Choice
                ? RecipeTestData.Input("v", type) with { Choices = [value] }
                : RecipeTestData.Input("v", type);
            Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.Check(input, value, false, Plain));
        }

        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.CheckHost(value, false, Plain));
        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.CheckPort(value, false, Plain));
    }

    [Fact]
    public void AFullWidthQuote_InAStringValue_IsRefusedByTheExpander()
    {
        Recipe recipe = RecipeTestData.With([Text()], RecipeArgument.Bare("--name={in:v}"));

        RecipeExpansion result = RecipeExpander.Expand(
            recipe,
            new Dictionary<string, string?> { ["v"] = "a\uFF02 --evil \uFF02b" },
            null,
            null,
            Plain);

        Assert.False(result.Succeeded);
        RecipeRefusal refusal = Assert.Single(result.Refusals);
        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, refusal.Reason);
        Assert.Equal("v", refusal.Subject);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void TheSessionHost_WithAFullWidthCharacter_IsRefusedByTheExpander()
    {
        Recipe recipe = RecipeTestData.With([], RecipeArgument.Bare("--h={Host}"));

        RecipeExpansion result = RecipeExpander.Expand(recipe, new Dictionary<string, string?>(), "srv\uFF0Dx", "22", Plain);

        RecipeRefusal refusal = Assert.Single(result.Refusals);
        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, refusal.Reason);
        Assert.Equal("Host", refusal.Subject);
    }

    [Theory]
    [InlineData("caf\u00E9")]
    [InlineData("\u00E0\u00E2\u00E7\u00E8\u00E9\u00EA\u00EB\u00EE\u00EF\u00F4\u00F9\u00FB\u00FC\u00FF")]
    [InlineData("\u00F1\u00E1\u00ED\u00F3\u00FA")]
    [InlineData("\u00C9cole \u0153uvre")]
    public void AccentedLettersTheCodePageKeeps_AreAccepted(string value)
    {
        RecipeInput field = Text();

        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, value, placeholderStartsToken: true, Plain));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, "--x=" + value, placeholderStartsToken: false, Plain));
    }

    // ---- the general rule, through the machine description

    [Fact]
    public void ACharacterTheCodePageTurnsIntoAnAsciiOne_IsRefused()
    {
        FakeCharacterConverter machine = FakeCharacterConverter.Plain.Fold("\u0100");
        RecipeInput field = Text();

        Assert.Equal(RecipeValueRefusal.LookAlikeCharacter, RecipeValueRule.Check(field, "a\u0100b", false, machine));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, "a\u0100b", false, Plain));
    }

    [Fact]
    public void ACharacterTheCodePageCannotRepresent_IsAcceptedBecauseItOnlyBecomesAQuestionMark()
    {
        // The converter says "no ASCII form" (the character is replaced by a question mark): harmless.
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(Text(), "a\u4E2Db", false, Plain));
    }

    [Fact]
    public void OnlyNonAsciiCharactersAreEverPutToTheMachine()
    {
        FakeCharacterConverter machine = FakeCharacterConverter.Plain;

        RecipeValueRule.Check(Text(), "plain ascii only", false, machine);
        RecipeValueRule.Check(Text(), "caf\u00E9", false, machine);

        Assert.Equal(["\u00E9"], machine.Asked);
    }

    [Fact]
    public void ASurrogatePair_IsPutToTheMachineAsOneCharacter()
    {
        FakeCharacterConverter machine = FakeCharacterConverter.Plain;
        string pair = char.ConvertFromUtf32(0x1F600);

        Assert.False(RecipeText.ContainsLookAlikeCharacter("a" + pair + "b", machine));
        Assert.Equal([pair], machine.Asked);
        Assert.True(RecipeText.ContainsLookAlikeCharacter("a" + pair, FakeCharacterConverter.Plain.Fold(pair)));
    }

    [Fact]
    public void TheRealMachine_DoesNotRefuseAnIdeographThatHasNoAsciiForm()
    {
        // Machine independent: a code page either keeps this character or turns it into a question mark.
        Assert.False(RecipeCharacterConverter.Instance.BecomesAscii("\u4E2D"));
    }

    // Depends on the machine: the ANSI code page decides, so this only asserts on a 1252 machine.
    [Fact]
    public void OnAnAnsi1252Machine_TheRealConverterAcceptsLettersThatPage1252KeepsAndRefusesTheFoldedOnes()
    {
        const int WesternEuropeanAnsiCodePage = 1252;
        if (System.Globalization.CultureInfo.InstalledUICulture.TextInfo.ANSICodePage != WesternEuropeanAnsiCodePage)
        {
            return;
        }

        // A capital with a grave accent, a ligature and a superscript one reach the program as themselves.
        Assert.False(RecipeText.ContainsLookAlikeCharacter("\u00C0"));
        Assert.False(RecipeText.ContainsLookAlikeCharacter("\u0153"));
        Assert.False(RecipeText.ContainsLookAlikeCharacter("\u00B9"));
        Assert.True(RecipeText.ContainsLookAlikeCharacter("\uFF0D"));
        Assert.True(RecipeText.ContainsLookAlikeCharacter("\u201C"));
        Assert.True(RecipeText.ContainsLookAlikeCharacter("\u00A0"));
    }

    [Fact]
    public void TheRealMachine_StillRefusesTheFoldedBlocksWithoutAskingItsCodePage()
    {
        Assert.True(RecipeText.ContainsLookAlikeCharacter("a\uFF0Db"));
        Assert.True(RecipeText.ContainsLookAlikeCharacter("\u201Cquoted\u201D"));
        Assert.False(RecipeText.ContainsLookAlikeCharacter("plain-ascii/text"));
    }

    // ---- import: every text that becomes an argument

    [Theory]
    [MemberData(nameof(AlwaysRefused))]
    public void AFoldedCharacter_InAFixedTokenWhenTrueTokenDefaultOrChoice_IsRefusedAtImport(int codePoint)
    {
        string value = "a" + Of(codePoint) + "b";
        RecipeInput flag = RecipeTestData.Input("f", RecipeInputType.Bool) with { WhenTrue = [value] };
        RecipeInput text = Text("s") with { Default = value };
        RecipeInput choice = RecipeTestData.Input("c", RecipeInputType.Choice, required: true) with { Choices = ["ok", value] };

        Assert.Contains(RecipeErrorCode.ForbiddenLookAlikeCharacter, Validate(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare(value)] }).Select(error => error.Code));
        Assert.Contains(RecipeErrorCode.ForbiddenLookAlikeCharacter, Validate(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Group(["--x", value])] }).Select(error => error.Code));
        Assert.Contains(RecipeErrorCode.ForbiddenLookAlikeCharacter, Validate(RecipeTestData.With([flag], RecipeArgument.Bare("{in:f}"))).Select(error => error.Code));
        Assert.Contains(RecipeErrorCode.ForbiddenLookAlikeCharacter, Validate(RecipeTestData.With([text], RecipeArgument.Bare("--s={in:s}"))).Select(error => error.Code));
        Assert.Contains(RecipeErrorCode.ForbiddenLookAlikeCharacter, Validate(RecipeTestData.With([choice], RecipeArgument.Bare("--c={in:c}"))).Select(error => error.Code));
    }

    [Fact]
    public void TheImportError_ShowsTheLookAlikeAsAnEscapeSoItCannotPassForTheAsciiOne()
    {
        Recipe recipe = RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("a\uFF0Db")] };

        RecipeError error = Assert.Single(Validate(recipe));

        Assert.Equal(RecipeErrorCode.ForbiddenLookAlikeCharacter, error.Code);
        Assert.Equal("a\\uFF0Db", error.Fragment);
        Assert.Equal("arguments[0]", error.Path);
    }

    [Fact]
    public void AGeneralRuleCharacter_InAFixedToken_IsRefusedAtImportThroughTheMachine()
    {
        Recipe recipe = RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("a\u0100b")] };

        Assert.Empty(Validate(recipe, Plain));
        Assert.Contains(
            RecipeErrorCode.ForbiddenLookAlikeCharacter,
            Validate(recipe, FakeCharacterConverter.Plain.Fold("\u0100")).Select(error => error.Code));
    }

    [Fact]
    public void AccentedLettersInFixedText_AreAcceptedAtImport()
    {
        RecipeInput choice = RecipeTestData.Input("c", RecipeInputType.Choice, required: true) with { Choices = ["caf\u00E9", "th\u00E9"] };

        Assert.Empty(Validate(RecipeTestData.With([choice], RecipeArgument.Bare("--c={in:c}"), RecipeArgument.Bare("\u00E9t\u00E9"))));
    }

    [Fact]
    public void DisplayTextIsNotAnArgument_SoItMayHoldTypographicCharacters()
    {
        Recipe recipe = RecipeTestData.Minimal() with
        {
            Name = "Ping \u2013 once",
            Description = "Sends \u201Cone\u201D echo request.",
            Author = "Zo\u00EB\u00A0Labs",
            Inputs = [RecipeTestData.Input("v", RecipeInputType.String) with { Label = "L\u2019adresse" }],
            Arguments = [RecipeArgument.Group(["--v", "{in:v}"])],
            Labels = new Dictionary<string, RecipeLabels>
            {
                ["fr"] = new RecipeLabels("Analyse \u2014 rapide", "Cible \u00AB x \u00BB", new Dictionary<string, string> { ["v"] = "Cible\u00A0:" }),
            },
        };

        Assert.Empty(Validate(recipe));
    }

    [Fact]
    public void AFileWithALookAlikeInAToken_IsRefusedByTheParser()
    {
        string json = RecipeTestData.MinimalJson.Replace("\"{Host}\"", "\"--n\\uFF0D1\"", StringComparison.Ordinal);

        RecipeParseResult result = RecipeParser.Parse(Encoding.UTF8.GetBytes(json), Plain);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == RecipeErrorCode.ForbiddenLookAlikeCharacter);
    }
}
