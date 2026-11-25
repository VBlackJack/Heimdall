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

/// <summary>
/// Windows reads a backslash and a slash as the same separator, so a network path, an NT path or a
/// URL marker can be written with either, in any mix. One shared rule refuses every form, in the
/// fixed text at import and in the final argument at run time.
/// </summary>
public sealed class RecipeSeparatorShapeTests
{
    private static IRecipeCharacterConverter Plain => FakeCharacterConverter.Plain;

    private static RecipeInput Text(string name = "v")
        => RecipeTestData.Input(name, RecipeInputType.String, required: true) with { MaxLength = RecipeLimits.MaxStringValueLength };

    private static Recipe WithTokens(params string[] tokens)
        => RecipeTestData.Minimal() with { Arguments = [.. tokens.Select(RecipeArgument.Bare)] };

    private static IReadOnlyList<RecipeErrorCode> Codes(Recipe recipe)
        => [.. RecipeValidator.Validate(recipe, Plain).Select(error => error.Code)];

    private static RecipeExpansion Expand(Recipe recipe, params (string Name, string Value)[] values)
        => RecipeExpander.Expand(
            recipe,
            values.ToDictionary(entry => entry.Name, entry => (string?)entry.Value),
            null,
            null,
            Plain);

    private static RecipeExpansion ExpandOne(string token, string value)
    {
        Recipe recipe = RecipeTestData.With([Text()], RecipeArgument.Bare(token));
        Assert.Empty(RecipeValidator.Validate(recipe, Plain));
        return Expand(recipe, ("v", value));
    }

    // ---- the shared helper

    [Theory]
    [InlineData(@"a\\b", true)]
    [InlineData("a//b", true)]
    [InlineData(@"a\/b", true)]
    [InlineData(@"a/\b", true)]
    [InlineData(@"\??\C:", true)]
    [InlineData("/??/C:", true)]
    [InlineData(@"\??/C:", true)]
    [InlineData(@"/??\C:", true)]
    [InlineData(@"a:\\b", true)]
    [InlineData(@"a:\/b", true)]
    [InlineData(@"a:/\b", true)]
    [InlineData("a://b", true)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"a/b\c", false)]
    [InlineData(@"a\?\b", false)]
    [InlineData(@"a\??b", false)]
    [InlineData(@"a??\b", false)]
    [InlineData("a:/b", false)]
    [InlineData(@"a:\b", false)]
    [InlineData("what?", false)]
    [InlineData("", false)]
    public void TheSharedShapeRule_SeesEverySeparatorFormOfANetworkNtOrUrlShape(string text, bool expected)
        => Assert.Equal(expected, RecipeText.ContainsNetworkPathShape(text));

    // ---- import: fixed text

    [Theory]
    [InlineData(@"--p=\\server", RecipeErrorCode.ForbiddenBackslashPair)]
    [InlineData("--p=//server", RecipeErrorCode.ForbiddenDoubleSlash)]
    [InlineData("--p=http://server", RecipeErrorCode.ForbiddenUrl)]
    [InlineData(@"--p=\/server", RecipeErrorCode.ForbiddenMixedSeparators)]
    [InlineData(@"--p=/\server", RecipeErrorCode.ForbiddenMixedSeparators)]
    [InlineData(@"--p=\??\C:\x", RecipeErrorCode.ForbiddenNtPathPrefix)]
    [InlineData("--p=/??/C:/x", RecipeErrorCode.ForbiddenNtPathPrefix)]
    [InlineData(@"--p=\??/C:/x", RecipeErrorCode.ForbiddenNtPathPrefix)]
    [InlineData(@"--p=http:\/server", RecipeErrorCode.ForbiddenUrl)]
    [InlineData(@"--p=http:/\server", RecipeErrorCode.ForbiddenUrl)]
    public void AFixedTokenWithANetworkNtOrUrlShape_IsRefusedWithTheCodeThatTellsTheTruth(string token, RecipeErrorCode expected)
        => Assert.Contains(expected, Codes(WithTokens(token)));

    [Theory]
    [InlineData(@"\/x")]
    [InlineData(@"/\x")]
    [InlineData(@"\??\x")]
    public void TheSameShapesAreRefusedInAChoiceAndInADefault(string value)
    {
        RecipeInput choice = RecipeTestData.Input("c", RecipeInputType.Choice, required: true) with { Choices = ["a", value] };
        RecipeInput text = Text("s") with { Default = value };

        Assert.NotEmpty(Codes(RecipeTestData.With([choice], RecipeArgument.Bare("--c={in:c}"))));
        Assert.NotEmpty(Codes(RecipeTestData.With([text], RecipeArgument.Bare("--s={in:s}"))));
    }

    [Fact]
    public void TheSameShapesAreRefusedInAWhenTrueToken()
    {
        RecipeInput flag = RecipeTestData.Input("f", RecipeInputType.Bool) with { WhenTrue = [@"--p=\/x"] };

        Assert.Contains(RecipeErrorCode.ForbiddenMixedSeparators, Codes(RecipeTestData.With([flag], RecipeArgument.Bare("{in:f}"))));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("a/b/c")]
    [InlineData(@"--file=C:\dir\x")]
    [InlineData("--what?")]
    [InlineData("/all")]
    public void OrdinaryPathsAndOptions_AreStillAccepted(string token)
        => Assert.Empty(Codes(WithTokens(token)));

    // ---- run time: a typed string

    [Theory]
    [InlineData(@"\/x")]
    [InlineData(@"/\x")]
    [InlineData(@"\\x")]
    [InlineData("//x")]
    [InlineData(@"a\/b")]
    [InlineData(@"a/\b")]
    [InlineData(@"\??\C:")]
    [InlineData("a/??/C:")]
    [InlineData(@"x:\/y")]
    [InlineData(@"x:/\y")]
    public void AStringValueWithANetworkNtOrUrlShape_IsRefusedAfterALiteral(string value)
    {
        RecipeExpansion result = ExpandOne("--file={in:v}", value);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Refusals, refusal => refusal.Reason == RecipeValueRefusal.ForbiddenSequenceInArgument);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData(@"\/x", RecipeValueRefusal.LeadingBackslash)]
    [InlineData(@"\\x", RecipeValueRefusal.LeadingBackslash)]
    [InlineData(@"\??\C:", RecipeValueRefusal.LeadingBackslash)]
    [InlineData(@"/\x", RecipeValueRefusal.LeadingSlash)]
    [InlineData("//x", RecipeValueRefusal.LeadingSlash)]
    [InlineData(@"a\/b", RecipeValueRefusal.ForbiddenSequenceInArgument)]
    [InlineData(@"x:/\y", RecipeValueRefusal.ForbiddenSequenceInArgument)]
    [InlineData(@"a/??\b", RecipeValueRefusal.ForbiddenSequenceInArgument)]
    public void AStringValueWithANetworkNtOrUrlShape_IsRefusedAtTheStartOfATokenToo(string value, RecipeValueRefusal expected)
    {
        RecipeExpansion result = ExpandOne("{in:v}", value);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, Assert.Single(result.Refusals).Reason);
    }

    [Theory]
    [InlineData(RecipeInputType.String)]
    [InlineData(RecipeInputType.Int)]
    [InlineData(RecipeInputType.Number)]
    [InlineData(RecipeInputType.Bool)]
    [InlineData(RecipeInputType.Hostname)]
    [InlineData(RecipeInputType.IpAddress)]
    [InlineData(RecipeInputType.Path)]
    [InlineData(RecipeInputType.Choice)]
    public void ALeadingBackslash_IsRefusedForEveryInputTypeWhereThePlaceholderStartsItsToken(RecipeInputType type)
    {
        RecipeInput input = type == RecipeInputType.Choice
            ? RecipeTestData.Input("v", type) with { Choices = ["a"] }
            : RecipeTestData.Input("v", type);

        Assert.Equal(RecipeValueRefusal.LeadingBackslash, RecipeValueRule.Check(input, @"\x", placeholderStartsToken: true, Plain));
        Assert.NotEqual(RecipeValueRefusal.LeadingBackslash, RecipeValueRule.Check(input, @"\x", placeholderStartsToken: false, Plain));
    }

    [Fact]
    public void AValueThatJoinsALiteralIntoAMixedPair_IsRefusedAsASeparatorJoin()
    {
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin("/x", '\\', null));
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin(@"\x", '/', null));
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin("x/", null, '\\'));
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin(@"x\", null, '/'));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.CheckSeparatorJoin("x", '\\', '/'));
    }

    [Fact]
    public void ASecondPlaceholderThatJoinsTheFirstIntoAMixedPair_IsRefused()
    {
        Recipe recipe = RecipeTestData.With([Text("a"), Text("b")], RecipeArgument.Bare("{in:a}{in:b}"));
        Assert.Empty(RecipeValidator.Validate(recipe, Plain));

        RecipeExpansion result = Expand(recipe, ("a", @"x\"), ("b", "/y"));

        Assert.Contains(result.Refusals, refusal => refusal.Reason == RecipeValueRefusal.SeparatorJoin);
    }

    // ---- run time: a path and the session host

    [Theory]
    [InlineData(@"\??\C:\x", RecipeValueRefusal.DevicePath)]
    [InlineData(@"/??/C:/x", RecipeValueRefusal.DevicePath)]
    [InlineData(@"\\?\C:\x", RecipeValueRefusal.DevicePath)]
    [InlineData(@"\\server\share", RecipeValueRefusal.UncPath)]
    [InlineData(@"\/server\share", RecipeValueRefusal.UncPath)]
    [InlineData(@"/\server\share", RecipeValueRefusal.UncPath)]
    public void APathValue_WithAnNtOrNetworkPrefix_IsRefusedWhateverTheSeparators(string value, RecipeValueRefusal expected)
    {
        RecipeInput path = RecipeTestData.Input("v", RecipeInputType.Path);

        Assert.Equal(expected, RecipeValueRule.Check(path, value, placeholderStartsToken: false, Plain));
    }

    [Theory]
    [InlineData(@"\h", RecipeValueRefusal.LeadingBackslash)]
    [InlineData("/h", RecipeValueRefusal.LeadingSlash)]
    [InlineData(@"\/h", RecipeValueRefusal.LeadingBackslash)]
    [InlineData(@"\??\h", RecipeValueRefusal.LeadingBackslash)]
    public void TheSessionHost_IsHeldToTheLeadingSeparatorRule(string host, RecipeValueRefusal expected)
        => Assert.Equal(expected, RecipeValueRule.CheckHost(host, placeholderStartsToken: true, Plain));

    [Theory]
    [InlineData(@"a\/h")]
    [InlineData(@"a/\h")]
    [InlineData(@"a\\h")]
    [InlineData(@"a\??\h")]
    public void TheSessionHost_WithASeparatorShapeInside_IsRefusedAfterALiteralToo(string host)
    {
        Recipe recipe = RecipeTestData.With([], RecipeArgument.Bare("--h={Host}"));

        RecipeExpansion result = RecipeExpander.Expand(recipe, new Dictionary<string, string?>(), host, "22", Plain);

        Assert.False(result.Succeeded);
        Assert.Equal("Host", Assert.Single(result.Refusals).Subject);
    }

    // ---- the last line of defence: whatever its source, the final argument

    [Fact]
    public void TheFinalArgument_IsCheckedWholeEvenWhenNoValueTouchesASeparatorOfTheLiteralText()
    {
        Recipe single = RecipeTestData.With([Text("a")], RecipeArgument.Bare("{in:a}"));
        Recipe pair = RecipeTestData.With([Text("a"), Text("b")], RecipeArgument.Bare("{in:a}{in:b}"));

        Assert.True(Expand(pair, ("a", "x:"), ("b", "y")).Succeeded);
        Assert.Equal(
            RecipeValueRefusal.ForbiddenSequenceInArgument,
            Assert.Single(Expand(single, ("a", @"x:/\y")).Refusals).Reason);
    }
}
