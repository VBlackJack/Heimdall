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

/// <summary>Expansion of a validated recipe into the final argument list.</summary>
public sealed class RecipeExpanderTests
{
    private static readonly IReadOnlyDictionary<string, string?> NoValues = new Dictionary<string, string?>();

    private static IRecipeCharacterConverter Plain => FakeCharacterConverter.Plain;

    private static RecipeExpansion Expand(
        Recipe recipe,
        IReadOnlyDictionary<string, string?> values,
        string? host = null,
        string? port = null,
        IRecipeCharacterConverter? converter = null)
        => RecipeExpander.Expand(recipe, values, host, port, converter ?? Plain);

    private static IReadOnlyDictionary<string, string?> Values(params (string Name, string? Value)[] entries)
        => entries.ToDictionary(entry => entry.Name, entry => entry.Value);

    private static RecipeInput Text(string name, bool required = false, string? defaultValue = null)
        => RecipeTestData.Input(name, RecipeInputType.String, required, defaultValue) with { MaxLength = RecipeLimits.MaxStringValueLength };

    private static Recipe Checked(Recipe recipe)
    {
        Assert.Empty(RecipeValidator.Validate(recipe, Plain));
        return recipe;
    }

    public static TheoryData<string> AwkwardValues => new()
    {
        { "hello world" },
        { "a  b   c" },
        { "say \"hi\"" },
        { "\"" },
        { "\"\"" },
        { "a\\\"b" },
        { "x\\" },
        { "C:\\path with space\\" },
        { "caf\u00E9 \u00FC\u00F1" },
        { "a'b" },
        { "a;b|c&d" },
        { "a^b!c" },
        { "a$b`c" },
        { "(x)" },
        { "trailing " },
        { "*.txt" },
        { "a=b" },
    };

    [Theory]
    [MemberData(nameof(AwkwardValues))]
    public void AValue_StaysExactlyOneArgument_ByteForByte(string value)
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("{in:v}")));

        RecipeExpansion result = Expand(recipe, Values(("v", value)));

        Assert.True(result.Succeeded);
        string argument = Assert.Single(result.Arguments);
        Assert.Equal(value, argument);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(value), System.Text.Encoding.UTF8.GetBytes(argument));
    }

    /// <summary>Values that are fine inside a token but cannot start one: a backslash or a space first.</summary>
    public static TheoryData<string> ValuesThatCannotStartAToken => new()
    {
        { "\\\"" },
        { " leading and trailing " },
        { "\\" },
    };

    [Theory]
    [MemberData(nameof(AwkwardValues))]
    [MemberData(nameof(ValuesThatCannotStartAToken))]
    public void AValueInsideAToken_StaysInsideThatOneArgument(string value)
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("--name={in:v}"), RecipeArgument.Bare("after")));

        RecipeExpansion result = Expand(recipe, Values(("v", value)));

        Assert.True(result.Succeeded);
        Assert.Equal(["--name=" + value, "after"], result.Arguments);
    }

    [Fact]
    public void LiteralTokens_ArePassedAsWritten_InOrder()
    {
        Recipe recipe = Checked(RecipeTestData.Minimal() with
        {
            Arguments = [RecipeArgument.Bare("-n"), RecipeArgument.Bare("/all"), RecipeArgument.Group(["--x", "two words"])],
        });

        RecipeExpansion result = Expand(recipe, NoValues);

        Assert.Equal(["-n", "/all", "--x", "two words"], result.Arguments);
    }

    [Fact]
    public void AGroup_IsDroppedAsAWhole_WhenItsOptionalInputIsEmpty()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [Text("extra")],
            RecipeArgument.Bare("first"),
            RecipeArgument.Group(["--extra", "{in:extra}"]),
            RecipeArgument.Bare("last")));

        RecipeExpansion dropped = Expand(recipe, NoValues);
        RecipeExpansion blank = Expand(recipe, Values(("extra", "   ")));
        RecipeExpansion nullValue = Expand(recipe, Values(("extra", null)));
        RecipeExpansion kept = Expand(recipe, Values(("extra", "value")));

        Assert.Equal(["first", "last"], dropped.Arguments);
        Assert.Equal(["first", "last"], blank.Arguments);
        Assert.Equal(["first", "last"], nullValue.Arguments);
        Assert.Equal(["first", "--extra", "value", "last"], kept.Arguments);
    }

    [Fact]
    public void AGroup_IsDropped_WhenAnyOneOfSeveralOptionalInputsIsEmpty()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [Text("a"), Text("b")],
            RecipeArgument.Group(["{in:a}", "{in:b}"])));

        Assert.Empty(Expand(recipe, Values(("a", "x"))).Arguments);
        Assert.Empty(Expand(recipe, Values(("b", "y"))).Arguments);
        Assert.Equal(["x", "y"], Expand(recipe, Values(("a", "x"), ("b", "y"))).Arguments);
    }

    [Fact]
    public void AGroupWithoutOptionalInputs_IsAlwaysKept()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [Text("req", required: true), Text("dflt", defaultValue: "d")],
            RecipeArgument.Group(["--a", "{in:req}", "--b={in:dflt}"])));

        RecipeExpansion result = Expand(recipe, Values(("req", "r")));

        Assert.Equal(["--a", "r", "--b=d"], result.Arguments);
    }

    [Fact]
    public void ADefault_IsUsed_WhenNoValueIsGiven_AndOverriddenWhenOneIs()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [RecipeTestData.Input("count", RecipeInputType.Int, defaultValue: "4")],
            RecipeArgument.Group(["-n", "{in:count}"])));

        Assert.Equal(["-n", "4"], Expand(recipe, NoValues).Arguments);
        Assert.Equal(["-n", "4"], Expand(recipe, Values(("count", " "))).Arguments);
        Assert.Equal(["-n", "9"], Expand(recipe, Values(("count", "9"))).Arguments);
    }

    [Fact]
    public void ARequiredInputWithoutAValue_IsRefused()
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("{in:v}")));

        RecipeExpansion result = Expand(recipe, NoValues);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Arguments);
        RecipeRefusal refusal = Assert.Single(result.Refusals);
        Assert.Equal(RecipeValueRefusal.RequiredMissing, refusal.Reason);
        Assert.Equal("v", refusal.Subject);
        Assert.Equal("inputs[0]", refusal.Path);
    }

    [Fact]
    public void ABoolInput_ContributesItsWhenTrueTokensAtItsPlace()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v", "--level", "3"] };
        Recipe recipe = Checked(RecipeTestData.With([flag], RecipeArgument.Bare("before"), RecipeArgument.Bare("{in:flag}"), RecipeArgument.Bare("after")));

        Assert.Equal(["before", "-v", "--level", "3", "after"], Expand(recipe, Values(("flag", "true"))).Arguments);
        Assert.Equal(["before", "-v", "--level", "3", "after"], Expand(recipe, Values(("flag", "TRUE"))).Arguments);
        Assert.Equal(["before", "after"], Expand(recipe, Values(("flag", "false"))).Arguments);
        Assert.Equal(["before", "after"], Expand(recipe, NoValues).Arguments);
    }

    [Fact]
    public void ABoolInput_UsesItsDefault()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool, defaultValue: "true") with { WhenTrue = ["-v"] };
        Recipe recipe = Checked(RecipeTestData.With([flag], RecipeArgument.Bare("{in:flag}")));

        Assert.Equal(["-v"], Expand(recipe, NoValues).Arguments);
        Assert.Empty(Expand(recipe, Values(("flag", "false"))).Arguments);
    }

    [Fact]
    public void ABoolValueThatIsNotABoolean_IsRefused()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-v"] };
        Recipe recipe = Checked(RecipeTestData.With([flag], RecipeArgument.Bare("{in:flag}")));

        RecipeExpansion result = Expand(recipe, Values(("flag", "maybe")));

        Assert.Equal(RecipeValueRefusal.NotABoolean, Assert.Single(result.Refusals).Reason);
    }

    [Fact]
    public void AWhenTrueGroup_UsingAnEmptyOptionalInput_IsDropped()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["--tag", "{in:tag}"] };
        Recipe recipe = Checked(RecipeTestData.With([flag, Text("tag")], RecipeArgument.Bare("{in:flag}")));

        Assert.Empty(Expand(recipe, Values(("flag", "true"))).Arguments);
        Assert.Equal(["--tag", "x"], Expand(recipe, Values(("flag", "true"), ("tag", "x"))).Arguments);
    }

    [Theory]
    [InlineData("{{literal}}", "{literal}")]
    [InlineData("a{{b", "a{b")]
    [InlineData("b}}a", "b}a")]
    [InlineData("{{{Host}}}", "{h}")]
    [InlineData("{{in:ghost}}", "{in:ghost}")]
    [InlineData("{{}}", "{}")]
    public void DoubledBraces_AreUnescapedToLiteralBraces(string token, string expected)
    {
        Recipe recipe = Checked(WithBare(token));

        RecipeExpansion result = Expand(recipe, NoValues, host: "h", port: "1");

        Assert.Equal([expected], result.Arguments);
    }

    private static Recipe WithBare(string token)
        => RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare(token)] };

    [Fact]
    public void TheSessionPlaceholders_AreSubstituted()
    {
        Recipe recipe = Checked(RecipeTestData.Minimal() with
        {
            Arguments = [RecipeArgument.Bare("{Host}"), RecipeArgument.Group(["-p", "{Port}"]), RecipeArgument.Bare("--addr={Host}:{Port}")],
        });

        RecipeExpansion result = Expand(recipe, NoValues, "server01.example.com", "2222");

        Assert.Equal(["server01.example.com", "-p", "2222", "--addr=server01.example.com:2222"], result.Arguments);
    }

    [Fact]
    public void AMissingSessionValue_IsRefused()
    {
        Recipe recipe = Checked(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("{Host}"), RecipeArgument.Bare("{Port}")] });

        RecipeExpansion result = Expand(recipe, NoValues);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Refusals.Count);
        Assert.All(result.Refusals, refusal => Assert.Equal(RecipeValueRefusal.SessionValueMissing, refusal.Reason));
        Assert.Equal(["Host", "Port"], result.Refusals.Select(refusal => refusal.Subject));
    }

    [Theory]
    [InlineData("-oProxyCommand=calc", "22", "Host", RecipeValueRefusal.LeadingDash)]
    [InlineData("host name", "22", "Host", RecipeValueRefusal.InvalidHostname)]
    [InlineData("server", "70000", "Port", RecipeValueRefusal.InvalidPort)]
    [InlineData("server", "-1", "Port", RecipeValueRefusal.LeadingDash)]
    public void AnInvalidSessionValue_IsRefusedByTheStrictHostAndPortValidation(string host, string port, string subject, RecipeValueRefusal expected)
    {
        Recipe recipe = Checked(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("{Host}"), RecipeArgument.Bare("{Port}")] });

        RecipeExpansion result = Expand(recipe, NoValues, host, port);

        Assert.False(result.Succeeded);
        RecipeRefusal refusal = Assert.Single(result.Refusals);
        Assert.Equal(expected, refusal.Reason);
        Assert.Equal(subject, refusal.Subject);
    }

    [Fact]
    public void ALeadingDash_IsRefusedWhereThePlaceholderStartsItsToken_AndAcceptedInsideAToken()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [Text("v", required: true)],
            RecipeArgument.Bare("{in:v}")));
        Recipe inside = Checked(RecipeTestData.With(
            [Text("v", required: true)],
            RecipeArgument.Bare("--value={in:v}")));

        RecipeExpansion atStart = Expand(recipe, Values(("v", "-rf")));
        RecipeExpansion embedded = Expand(inside, Values(("v", "-rf")));

        Assert.Equal(RecipeValueRefusal.LeadingDash, Assert.Single(atStart.Refusals).Reason);
        Assert.Equal("arguments[0]", atStart.Refusals[0].Path);
        Assert.Equal(["--value=-rf"], embedded.Arguments);
    }

    [Fact]
    public void ALiteralBeforeThePlaceholder_MeansItDoesNotStartTheToken()
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("{{{in:v}")));

        RecipeExpansion result = Expand(recipe, Values(("v", "-x")));

        Assert.Equal(["{-x"], result.Arguments);
    }

    [Fact]
    public void EveryRefusedValue_IsReported_WithItsSubjectAndEntry()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [Text("a", required: true), RecipeTestData.Input("n", RecipeInputType.Int, required: true)],
            RecipeArgument.Bare("{in:a}"),
            RecipeArgument.Group(["-n", "{in:n}"])));

        RecipeExpansion result = Expand(recipe, Values(("a", "/c"), ("n", "x")));

        Assert.Equal(2, result.Refusals.Count);
        Assert.Contains(result.Refusals, r => r is { Reason: RecipeValueRefusal.LeadingSlash, Subject: "a", Path: "arguments[0]" });
        Assert.Contains(result.Refusals, r => r is { Reason: RecipeValueRefusal.NotAnInteger, Subject: "n", Path: "arguments[1][1]" });
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void AControlCharacter_InAValue_IsRefused()
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("x{in:v}")));

        RecipeExpansion result = Expand(recipe, Values(("v", "a\u202Eb")));

        Assert.Equal(RecipeValueRefusal.ControlCharacter, Assert.Single(result.Refusals).Reason);
    }

    [Fact]
    public void APathInput_RefusesNetworkPaths()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [RecipeTestData.Input("p", RecipeInputType.Path, required: true)],
            RecipeArgument.Bare("--file={in:p}")));

        Assert.Equal(["--file=C:\\data\\x.txt"], Expand(recipe, Values(("p", "C:\\data\\x.txt"))).Arguments);
        Assert.Equal(RecipeValueRefusal.UncPath, Assert.Single(Expand(recipe, Values(("p", "\\\\srv\\share"))).Refusals).Reason);
    }

    [Fact]
    public void AChoiceInput_OnlyAcceptsItsOwnValues()
    {
        RecipeInput mode = RecipeTestData.Input("mode", RecipeInputType.Choice, defaultValue: "fast") with { Choices = ["fast", "slow"] };
        Recipe recipe = Checked(RecipeTestData.With([mode], RecipeArgument.Bare("--mode={in:mode}")));

        Assert.Equal(["--mode=fast"], Expand(recipe, NoValues).Arguments);
        Assert.Equal(["--mode=slow"], Expand(recipe, Values(("mode", "slow"))).Arguments);
        Assert.Equal(RecipeValueRefusal.NotInChoices, Assert.Single(Expand(recipe, Values(("mode", "other"))).Refusals).Reason);
    }

    [Fact]
    public void AnUnknownValueName_IsIgnored()
    {
        Recipe recipe = Checked(RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("x")] });

        Assert.Equal(["x"], Expand(recipe, Values(("ghost", "boo"))).Arguments);
    }

    [Fact]
    public void ExpandingTwice_GivesTheSameResult()
    {
        Recipe recipe = RecipeTestData.ParseValid(RecipeTestData.FullJson);
        IReadOnlyDictionary<string, string?> values = Values(("target", "server1"), ("verbose", "true"), ("extra", "abc"));

        RecipeExpansion first = Expand(recipe, values, "h", "22");
        RecipeExpansion second = Expand(recipe, values, "h", "22");

        Assert.True(first.Succeeded);
        Assert.Equal(first.Arguments, second.Arguments);
        Assert.Equal(["-n", "4", "-v", "-d", "--mode=fast", "--extra", "abc", "server1", "-p", "22"], first.Arguments);
    }

    [Fact]
    public void TheReportedExample_AUncValueAfterABackslash_IsRefused()
    {
        Recipe recipe = Checked(RecipeTestData.With(
            [RecipeTestData.Input("p", RecipeInputType.Path, required: true)],
            RecipeArgument.Bare(@"--file=\{in:p}")));

        RecipeExpansion result = Expand(recipe, Values(("p", @"\srv\share")));

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeValueRefusal.RootedPath, Assert.Single(result.Refusals).Reason);
    }

    [Theory]
    [InlineData(@"\{in:v}", @"\srv")]
    [InlineData("/{in:v}", "/etc")]
    [InlineData(@"/{in:v}", @"\x")]
    [InlineData(@"--p=C:\{in:v}", "/x")]
    public void AValueStartingWithASeparator_AfterLiteralTextEndingWithOne_IsRefused(string token, string value)
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare(token)));

        RecipeExpansion result = Expand(recipe, Values(("v", value)));

        Assert.Equal(RecipeValueRefusal.SeparatorJoin, Assert.Single(result.Refusals).Reason);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(@"{in:v}\x", @"C:\dir\")]
    [InlineData("{in:v}/x", "dir/")]
    [InlineData(@"{in:v}/x", @"dir\")]
    public void AValueEndingWithASeparator_BeforeLiteralTextStartingWithOne_IsRefused(string token, string value)
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare(token)));

        RecipeExpansion result = Expand(recipe, Values(("v", value)));

        Assert.Equal(RecipeValueRefusal.SeparatorJoin, Assert.Single(result.Refusals).Reason);
    }

    [Fact]
    public void TwoAdjacentPlaceholders_AreRefused_WhenTheLeftEndsAndTheRightStartsWithASeparator()
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("a", required: true), Text("b", required: true)], RecipeArgument.Bare("{in:a}{in:b}")));

        RecipeExpansion refused = Expand(recipe, Values(("a", "dir/"), ("b", "/file")));
        RecipeExpansion accepted = Expand(recipe, Values(("a", "dir/"), ("b", "file")));

        Assert.NotEmpty(refused.Refusals);
        Assert.All(refused.Refusals, refusal => Assert.Equal(RecipeValueRefusal.SeparatorJoin, refusal.Reason));
        Assert.Equal(["dir/file"], accepted.Arguments);
    }

    [Fact]
    public void ASingleSeparatorJoin_IsStillAccepted()
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("a", required: true), Text("b", required: true)], RecipeArgument.Bare(@"--p={in:a}\{in:b}")));

        RecipeExpansion result = Expand(recipe, Values(("a", "dir"), ("b", "file.txt")));

        Assert.Equal([@"--p=dir\file.txt"], result.Arguments);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("a//b")]
    [InlineData(@"a\\b")]
    [InlineData("x://y")]
    public void ADoubledSeparatorOrUrlMarkerInAFinalArgument_IsRefusedByTheLastLineCheck(string value)
    {
        Recipe recipe = Checked(RecipeTestData.With([Text("v", required: true)], RecipeArgument.Bare("--v={in:v}")));

        RecipeExpansion result = Expand(recipe, Values(("v", value)));

        Assert.False(result.Succeeded);
        RecipeRefusal refusal = Assert.Single(result.Refusals);
        Assert.Equal(RecipeValueRefusal.ForbiddenSequenceInArgument, refusal.Reason);
        Assert.Equal("arguments[0]", refusal.Path);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void ARecipeThatWasNotValidated_ThrowsAProgrammerError()
    {
        Recipe broken = RecipeTestData.Minimal() with { Arguments = [RecipeArgument.Bare("{oops")] };

        Assert.Throws<InvalidOperationException>(() => Expand(broken, NoValues));
    }
}
