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
/// The last rules of the version 1 format, read from files: what a file may say about required
/// switches, unused fields, bounds, switch arguments, hints, translations, icons, the separator of a
/// table, standard error and the strict form of a number.
/// </summary>
public sealed class RecipeFormatFreezeTests
{
    private static RecipeParseResult Parse(
        string inputs = "[]",
        string arguments = "[\"{Host}\"]",
        string extra = "")
        => RecipeParser.Parse(Json(inputs, arguments, extra), FakeCharacterConverter.Plain);

    private static string Json(string inputs, string arguments, string extra)
        => $$"""
            {
              "schemaVersion": 1,
              "id": "acme.ping",
              "version": "1.0.0",
              "author": "Acme",
              "name": "Ping",
              "description": "Ping the selected host.",
              "program": "ping.exe",
              "arguments": {{arguments}},
              "inputs": {{inputs}}{{extra}}
            }
            """;

    private static Recipe Valid(string inputs = "[]", string arguments = "[\"{Host}\"]", string extra = "")
    {
        RecipeParseResult result = Parse(inputs, arguments, extra);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code + "@" + error.Path)));
        return result.Recipe!;
    }

    private static IReadOnlyList<RecipeError> Refused(string inputs = "[]", string arguments = "[\"{Host}\"]", string extra = "")
    {
        RecipeParseResult result = Parse(inputs, arguments, extra);
        Assert.False(result.Succeeded);
        return result.Errors;
    }

    private static string Input(string name, string type, string more = "")
        => $"{{\"name\":\"{name}\",\"type\":\"{type}\",\"label\":\"L {name}\"{more}}}";

    private static RecipeExpansion Expand(Recipe recipe, params (string Name, string? Value)[] values)
        => RecipeExpander.Expand(
            recipe,
            values.ToDictionary(entry => entry.Name, entry => entry.Value),
            "h",
            "22",
            FakeCharacterConverter.Plain);

    // ---- A: the strict numeric forms ----

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0", true)]
    [InlineData("7", true)]
    [InlineData("-12", true)]
    [InlineData("2147483647", true)]
    [InlineData("-2147483648", true)]
    [InlineData("2147483648", false)]
    [InlineData("+5", false)]
    [InlineData("05", false)]
    [InlineData("00", false)]
    [InlineData("-05", false)]
    [InlineData("1e2", false)]
    [InlineData("1E2", false)]
    [InlineData("1.0", false)]
    [InlineData("5.", false)]
    [InlineData(".5", false)]
    [InlineData("", false)]
    [InlineData("-", false)]
    [InlineData(" 5", false)]
    public void AnIntegerValueHasTheStrictForm(string value, bool valid)
    {
        RecipeInput input = RecipeTestData.Input("n", RecipeInputType.Int);

        Assert.Equal(valid, RecipeValueRule.Check(input, value, placeholderStartsToken: false, FakeCharacterConverter.Plain) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-0", true)]
    [InlineData("1.5", true)]
    [InlineData("-0.25", true)]
    [InlineData("10.50", true)]
    [InlineData("0.0", true)]
    [InlineData("123456789012345678", true)]
    [InlineData("+1", false)]
    [InlineData("01", false)]
    [InlineData("01.5", false)]
    [InlineData("1e3", false)]
    [InlineData("1E3", false)]
    [InlineData("1.5e3", false)]
    [InlineData(".5", false)]
    [InlineData("-.5", false)]
    [InlineData("5.", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1,5", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    public void ANumberValueHasTheStrictForm(string value, bool valid)
    {
        RecipeInput input = RecipeTestData.Input("n", RecipeInputType.Number);

        Assert.Equal(valid, RecipeValue(input, value));
    }

    private static bool RecipeValue(RecipeInput input, string value)
        => RecipeValueRule.Check(input, value, placeholderStartsToken: false, FakeCharacterConverter.Plain) == RecipeValueRefusal.None;

    [Theory]
    [InlineData("int", "5", "5")]
    [InlineData("int", "\"5\"", "5")]
    [InlineData("number", "1.50", "1.50")]
    [InlineData("number", "\"1.50\"", "1.50")]
    public void ADefaultMayBeAJsonNumberOrAString_AndKeepsItsLexeme(string type, string written, string expected)
    {
        Recipe recipe = Valid($"[{Input("n", type, ",\"default\":" + written)}]", "[\"-x\", \"{in:n}\"]");

        Assert.Equal(expected, recipe.Inputs[0].Default);
    }

    [Theory]
    [InlineData("int", "\"+5\"")]
    [InlineData("int", "\"05\"")]
    [InlineData("int", "1e2")]
    [InlineData("int", "1.5")]
    [InlineData("number", "\"1e3\"")]
    [InlineData("number", "1e3")]
    [InlineData("number", "\".5\"")]
    [InlineData("number", "\"+1.5\"")]
    public void ADefaultOutsideTheStrictFormIsRefused(string type, string written)
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("n", type, ",\"default\":" + written)}]", "[\"-x\", \"{in:n}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.InvalidDefault, error.Code);
        Assert.Equal("inputs[0].default", error.Path);
    }

    // ---- 1: required on a bool ----

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void RequiredOnABoolIsRefusedWhateverItsValue(string value)
    {
        IReadOnlyList<RecipeError> errors = Refused(
            $"[{Input("flag", "bool", ",\"whenTrue\":[\"-v\"],\"required\":" + value)}]",
            "[\"{in:flag}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code);
        Assert.Equal("inputs[0].required", error.Path);
        Assert.Equal("required", error.Fragment);
        Assert.NotNull(error.Position);
    }

    [Fact]
    public void RequiredOnAnotherTypeIsStillAllowed()
    {
        Recipe recipe = Valid($"[{Input("name", "string", ",\"required\":true")}]", "[\"{in:name}\"]");

        Assert.True(recipe.Inputs[0].Required);
    }

    [Fact]
    public void ARequiredBoolBuiltByHandIsRefusedByTheValidator()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool, required: true) with { WhenTrue = ["-v"] };

        RecipeError error = Assert.Single(RecipeValidator.Validate(RecipeTestData.Using(flag)));

        Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code);
        Assert.Equal("inputs[0].required", error.Path);
    }

    // ---- 2: an input that no argument uses ----

    [Fact]
    public void AnInputNoArgumentUsesIsRefused()
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("unused", "string")}]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.UnreferencedInput, error.Code);
        Assert.Equal("inputs[0]", error.Path);
        Assert.Equal("unused", error.Name);
    }

    [Theory]
    [InlineData("[\"-x\", \"{in:a}\"]")]
    [InlineData("[\"--a={in:a}\"]")]
    [InlineData("[[\"--a\", \"{in:a}\"]]")]
    public void AnInputUsedInABareTokenAnInsideAGroupOrBesideLiteralTextIsUsed(string arguments)
    {
        Recipe recipe = Valid($"[{Input("a", "string", ",\"default\":\"x\"")}]", arguments);

        Assert.Single(recipe.Inputs);
    }

    [Theory]
    [InlineData("whenTrue")]
    [InlineData("whenFalse")]
    public void AnInputUsedOnlyInTheTokensOfASwitchIsUsed(string list)
    {
        string inputs = $"[{Input("tag", "string", ",\"default\":\"t\"")},{Input("flag", "bool", $",\"{list}\":[\"--tag\",\"{{in:tag}}\"]")}]";

        Recipe recipe = Valid(inputs, "[\"{in:flag}\"]");

        Assert.Equal(2, recipe.Inputs.Count);
    }

    [Fact]
    public void ASwitchMustStillBePlacedAsAWholeBareToken()
    {
        string inputs = $"[{Input("flag", "bool", ",\"whenTrue\":[\"-v\"]")}]";

        IReadOnlyList<RecipeError> errors = Refused(inputs, "[[\"--x\", \"{in:flag}\"]]");

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.UnreferencedInput && error.Path == "inputs[0]");
    }

    [Fact]
    public void EveryUnusedInputIsReportedOnItsOwn()
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("a", "int")},{Input("b", "path")},{Input("c", "choice", ",\"choices\":[\"x\"]")}]");

        Assert.Equal(["inputs[0]", "inputs[1]", "inputs[2]"], errors.Where(error => error.Code == RecipeErrorCode.UnreferencedInput).Select(error => error.Path));
    }

    // ---- E: required together with a default ----

    [Theory]
    [InlineData("string", "\"x\"")]
    [InlineData("int", "5")]
    public void RequiredTogetherWithADefaultIsRefused(string type, string defaultValue)
    {
        IReadOnlyList<RecipeError> errors = Refused(
            $"[{Input("a", type, ",\"required\":true,\"default\":" + defaultValue)}]",
            "[\"-x\", \"{in:a}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.RequiredWithDefault, error.Code);
        Assert.Equal("inputs[0].required", error.Path);
    }

    [Fact]
    public void ADefaultWithoutRequired_AndRequiredWithoutADefault_AreAccepted()
    {
        Valid($"[{Input("a", "string", ",\"default\":\"x\"")}]", "[\"{in:a}\"]");
        Valid($"[{Input("a", "string", ",\"required\":true")}]", "[\"{in:a}\"]");
        Valid($"[{Input("a", "string", ",\"required\":false,\"default\":\"x\"")}]", "[\"{in:a}\"]");
    }

    [Fact]
    public void AClearedFieldWithADefaultUsesTheDefault()
    {
        Recipe recipe = Valid($"[{Input("a", "string", ",\"default\":\"x\"")}]", "[\"--a\", \"{in:a}\"]");

        Assert.Equal(["--a", "x"], Expand(recipe, ("a", string.Empty)).Arguments);
        Assert.Equal(["--a", "x"], Expand(recipe, ("a", "   ")).Arguments);
        Assert.Equal(["--a", "y"], Expand(recipe, ("a", "y")).Arguments);
    }

    // ---- C: an input inside the arguments of a switch ----

    [Theory]
    [InlineData("whenTrue")]
    [InlineData("whenFalse")]
    public void AnOptionalInputWithoutADefaultInsideSwitchArgumentsIsRefused(string list)
    {
        string inputs = $"[{Input("tag", "string")},{Input("flag", "bool", $",\"{list}\":[\"--tag\",\"{{in:tag}}\"]")}]";

        IReadOnlyList<RecipeError> errors = Refused(inputs, "[\"{in:flag}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.OptionalInputInSwitchArguments, error.Code);
        Assert.Equal($"inputs[1].{list}[1]", error.Path);
        Assert.Equal("tag", error.Name);
    }

    [Theory]
    [InlineData(",\"required\":true")]
    [InlineData(",\"default\":\"t\"")]
    public void ARequiredInputOrOneWithADefaultInsideSwitchArgumentsIsAccepted(string more)
    {
        string inputs = $"[{Input("tag", "string", more)},{Input("flag", "bool", ",\"whenTrue\":[\"--tag\",\"{in:tag}\"]")}]";

        Recipe recipe = Valid(inputs, "[\"{in:flag}\"]");

        Assert.Equal(["--tag", "t"], Expand(recipe, ("flag", "true"), ("tag", "t")).Arguments);
    }

    // ---- B: standard error merged into a table ----

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void MergingStandardErrorIntoATableOutputIsRefused(string output)
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: $",\"output\":\"{output}\",\"stderr\":\"merge\"");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.StderrMergeWithStructuredOutput, error.Code);
        Assert.Equal("stderr", error.Path);
    }

    [Theory]
    [InlineData("text", "merge")]
    [InlineData("csv", "separate")]
    [InlineData("json", "separate")]
    public void OtherCombinationsOfOutputAndStandardErrorAreAccepted(string output, string stderr)
    {
        Valid(extra: $",\"output\":\"{output}\",\"stderr\":\"{stderr}\"");
    }

    // ---- 6: min and max ----

    [Fact]
    public void BoundsOnAnIntAreReadAsNumbersOrStrings_AndKeptAsWritten()
    {
        Recipe recipe = Valid($"[{Input("n", "int", ",\"min\":1,\"max\":\"100\",\"default\":4")}]", "[\"-n\", \"{in:n}\"]");

        Assert.Equal("1", recipe.Inputs[0].Min);
        Assert.Equal("100", recipe.Inputs[0].Max);
    }

    [Fact]
    public void BoundsOnANumberKeepTheirLexeme()
    {
        Recipe recipe = Valid($"[{Input("r", "number", ",\"min\":\"0.50\",\"max\":2.5")}]", "[[\"-r\", \"{in:r}\"]]");

        Assert.Equal("0.50", recipe.Inputs[0].Min);
        Assert.Equal("2.5", recipe.Inputs[0].Max);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("bool")]
    [InlineData("hostname")]
    [InlineData("ipaddress")]
    [InlineData("path")]
    public void BoundsAreRefusedOnAnyOtherType(string type)
    {
        string more = type == "bool" ? ",\"whenTrue\":[\"-v\"],\"min\":1,\"max\":2" : ",\"min\":1,\"max\":2";
        string arguments = type == "bool" ? "[\"{in:x}\"]" : "[[\"--x\", \"{in:x}\"]]";

        IReadOnlyList<RecipeError> errors = Refused($"[{Input("x", type, more)}]", arguments);

        Assert.Equal(
            ["inputs[0].min", "inputs[0].max"],
            errors.Where(error => error.Code == RecipeErrorCode.PropertyNotAllowedForType).Select(error => error.Path));
    }

    [Theory]
    [InlineData("int", "1.5")]
    [InlineData("int", "\"abc\"")]
    [InlineData("int", "\"+1\"")]
    [InlineData("int", "\"\"")]
    [InlineData("int", "3000000000")]
    [InlineData("number", "\"1e3\"")]
    [InlineData("number", "\".5\"")]
    [InlineData("number", "\"x\"")]
    public void AnInvalidBoundIsRefused(string type, string bound)
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("n", type, ",\"min\":" + bound)}]", "[[\"-n\", \"{in:n}\"]]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.InvalidBound, error.Code);
        Assert.Equal("inputs[0].min", error.Path);
    }

    [Fact]
    public void ABoundOfTheWrongJsonTypeIsRefused()
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("n", "int", ",\"max\":true")}]", "[[\"-n\", \"{in:n}\"]]");

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.WrongType && error.Path == "inputs[0].max");
    }

    [Theory]
    [InlineData("int", "5", "4")]
    [InlineData("int", "-1", "-2")]
    [InlineData("number", "\"2.5\"", "\"2.4\"")]
    public void AMinimumAboveTheMaximumIsRefused(string type, string min, string max)
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("n", type, $",\"min\":{min},\"max\":{max}")}]", "[[\"-n\", \"{in:n}\"]]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.MinAboveMax, error.Code);
        Assert.Equal("inputs[0].min", error.Path);
    }

    [Theory]
    [InlineData("int", "5", "5")]
    [InlineData("number", "\"2.5\"", "\"2.5\"")]
    public void EqualBoundsAreAccepted(string type, string min, string max)
    {
        Valid($"[{Input("n", type, $",\"min\":{min},\"max\":{max}")}]", "[[\"-n\", \"{in:n}\"]]");
    }

    [Theory]
    [InlineData("int", "0", "100", "0", true)]
    [InlineData("int", "1", "100", "1", true)]
    [InlineData("int", "1", "100", "100", true)]
    [InlineData("int", "1", "100", "0", false)]
    [InlineData("int", "1", "100", "101", false)]
    [InlineData("number", "\"0.5\"", "\"2.5\"", "0.5", true)]
    [InlineData("number", "\"0.5\"", "\"2.5\"", "2.5", true)]
    [InlineData("number", "\"0.5\"", "\"2.5\"", "2.50001", false)]
    [InlineData("number", "\"0.5\"", "\"2.5\"", "0.49", false)]
    public void ADefaultOutsideTheBoundsIsRefusedAtImport(string type, string min, string max, string defaultValue, bool accepted)
    {
        string inputs = $"[{Input("n", type, $",\"min\":{min},\"max\":{max},\"default\":\"{defaultValue}\"")}]";

        RecipeParseResult result = Parse(inputs, "[\"-n\", \"{in:n}\"]");

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            RecipeError error = Assert.Single(result.Errors);
            Assert.Equal(RecipeErrorCode.DefaultOutOfBounds, error.Code);
            Assert.Equal("inputs[0].default", error.Path);
            Assert.Equal("n", error.Name);
            Assert.Equal(defaultValue, error.Fragment);
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("50", true)]
    [InlineData("100", true)]
    [InlineData("101", false)]
    [InlineData("-5", false)]
    public void AnIntOutsideItsBoundsIsRefusedWhenTheToolRuns(string value, bool accepted)
    {
        Recipe recipe = Valid($"[{Input("n", "int", ",\"min\":1,\"max\":100,\"default\":4")}]", "[\"--n={in:n}\"]");

        RecipeExpansion expansion = Expand(recipe, ("n", value));

        Assert.Equal(accepted, expansion.Succeeded);
        if (!accepted)
        {
            RecipeRefusal refusal = Assert.Single(expansion.Refusals);
            Assert.Equal(RecipeValueRefusal.OutOfRange, refusal.Reason);
            Assert.Equal("n", refusal.Subject);
        }
        else
        {
            Assert.Equal(["--n=" + value], expansion.Arguments);
        }
    }

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.10000000000000001", true)]
    [InlineData("0.30000000000000004", false)]
    [InlineData("0.3", true)]
    [InlineData("0.09", false)]
    public void ANumberIsComparedAsADoublePrecisionNumber(string value, bool accepted)
    {
        // 0.10000000000000001 and 0.1 are the same double, so the lower bound lets both through.
        Recipe recipe = Valid($"[{Input("r", "number", ",\"min\":\"0.1\",\"max\":\"0.3\",\"default\":\"0.2\"")}]", "[\"-r\", \"{in:r}\"]");

        Assert.Equal(accepted, Expand(recipe, ("r", value)).Succeeded);
    }

    [Fact]
    public void OnlyTheBoundThatIsWrittenIsChecked()
    {
        Recipe low = Valid($"[{Input("n", "int", ",\"min\":10,\"default\":10")}]", "[\"--n={in:n}\"]");
        Recipe high = Valid($"[{Input("n", "int", ",\"max\":10,\"default\":10")}]", "[\"--n={in:n}\"]");

        Assert.True(Expand(low, ("n", "2147483647")).Succeeded);
        Assert.False(Expand(low, ("n", "9")).Succeeded);
        Assert.True(Expand(high, ("n", "-2147483648")).Succeeded);
        Assert.False(Expand(high, ("n", "11")).Succeeded);
    }

    // ---- 7: whenFalse ----

    private const string SwitchWithWhenFalseOnly = "[{\"name\":\"flag\",\"type\":\"bool\",\"label\":\"Flag\",\"whenFalse\":[\"--no-x\",\"-q\"]}]";

    private const string SwitchWithBoth = "[{\"name\":\"flag\",\"type\":\"bool\",\"label\":\"Flag\",\"whenTrue\":[\"--x\"],\"whenFalse\":[\"--no-x\",\"-q\"]}]";

    [Fact]
    public void ASwitchWithOnlyWhenFalseAddsItsTokensWhenOff()
    {
        Recipe recipe = Valid(SwitchWithWhenFalseOnly, "[\"-a\", \"{in:flag}\", \"-z\"]");

        Assert.Equal(["-a", "--no-x", "-q", "-z"], Expand(recipe, ("flag", "false")).Arguments);
        Assert.Equal(["-a", "--no-x", "-q", "-z"], Expand(recipe).Arguments);
        Assert.Equal(["-a", "-z"], Expand(recipe, ("flag", "true")).Arguments);
    }

    [Fact]
    public void ASwitchWithBothListsAddsTheListOfItsState()
    {
        Recipe recipe = Valid(SwitchWithBoth, "[\"{in:flag}\"]");

        Assert.Equal(["--x"], Expand(recipe, ("flag", "true")).Arguments);
        Assert.Equal(["--no-x", "-q"], Expand(recipe, ("flag", "false")).Arguments);
    }

    [Fact]
    public void ASwitchWithOnlyWhenTrueStillAddsNothingWhenOff()
    {
        Recipe recipe = Valid("[{\"name\":\"flag\",\"type\":\"bool\",\"label\":\"Flag\",\"whenTrue\":[\"-v\"]}]", "[\"{in:flag}\"]");

        Assert.Empty(Expand(recipe, ("flag", "false")).Arguments);
        Assert.Equal(["-v"], Expand(recipe, ("flag", "true")).Arguments);
    }

    [Fact]
    public void ASwitchTokenKeepsTheGroupSemanticsForAnInputWithADefault()
    {
        string inputs = $"[{Input("tag", "string", ",\"default\":\"t\"")},{Input("flag", "bool", ",\"whenFalse\":[\"--tag\",\"{in:tag}\"]")}]";
        Recipe recipe = Valid(inputs, "[\"{in:flag}\"]");

        Assert.Equal(["--tag", "t"], Expand(recipe, ("flag", "false")).Arguments);
        Assert.Equal(["--tag", "u"], Expand(recipe, ("flag", "false"), ("tag", "u")).Arguments);
    }

    [Fact]
    public void ASwitchWithNeitherListIsRefused()
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("flag", "bool")}]", "[\"{in:flag}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.MissingSwitchArguments, error.Code);
    }

    [Theory]
    [InlineData(",\"whenFalse\":[]", "inputs[0].whenFalse")]
    [InlineData(",\"whenTrue\":[],\"whenFalse\":[\"-q\"]", "inputs[0].whenTrue")]
    [InlineData(",\"whenTrue\":[\"-v\"],\"whenFalse\":[]", "inputs[0].whenFalse")]
    public void AListThatIsWrittenMustNotBeEmpty(string more, string path)
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("flag", "bool", more)}]", "[\"{in:flag}\"]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.MissingSwitchArguments, error.Code);
        Assert.Equal(path, error.Path);
    }

    [Fact]
    public void WhenFalseIsRefusedOnAnyOtherType()
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("a", "string", ",\"whenFalse\":[\"-q\"]")}]", "[[\"--a\", \"{in:a}\"]]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.PropertyNotAllowedForType, error.Code);
        Assert.Equal("inputs[0].whenFalse", error.Path);
        Assert.Equal("whenFalse", error.Fragment);
    }

    [Theory]
    [InlineData("\\\\\\\\srv", RecipeErrorCode.ForbiddenBackslashPair)]
    [InlineData("a://b", RecipeErrorCode.ForbiddenUrl)]
    [InlineData("{in:ghost}", RecipeErrorCode.UnknownInput)]
    [InlineData("{broken", RecipeErrorCode.MalformedPlaceholder)]
    [InlineData("\\u2014x", RecipeErrorCode.ForbiddenLookAlikeCharacter)]
    public void WhenFalseTokensAreCheckedLikeWhenTrueTokens(string token, RecipeErrorCode expected)
    {
        Recipe viaFalse = ParseWith("whenFalse", token);
        Recipe viaTrue = ParseWith("whenTrue", token);

        Assert.Contains(expected, RecipeTestData.Codes(viaFalse));
        Assert.Equal(RecipeTestData.Codes(viaTrue), RecipeTestData.Codes(viaFalse));
        Assert.Contains(RecipeValidator.Validate(viaFalse, FakeCharacterConverter.Plain), error => error.Path == "inputs[0].whenFalse[0]");
    }

    private static Recipe ParseWith(string list, string token)
    {
        // Built by hand from the text of the token, so that a refused token still gives a recipe to validate.
        string decoded = System.Text.RegularExpressions.Regex.Unescape(token);
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with
        {
            WhenTrue = list == "whenTrue" ? [decoded] : ["-v"],
            WhenFalse = list == "whenFalse" ? [decoded] : null,
        };
        return RecipeTestData.Using(flag);
    }

    [Fact]
    public void ASwitchPlacedOnceCountsTheLargerOfItsTwoLists()
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenTrue = ["-a"], WhenFalse = ["-b", "-c"] };
        RecipeArgument[] filler(int count) => [.. Enumerable.Range(0, count).Select(_ => RecipeArgument.Bare("x"))];

        Recipe atCap = RecipeTestData.Minimal() with { Inputs = [flag], Arguments = [.. filler(RecipeLimits.MaxTokens - 2), RecipeArgument.Bare("{in:flag}")] };
        Recipe over = RecipeTestData.Minimal() with { Inputs = [flag], Arguments = [.. filler(RecipeLimits.MaxTokens - 1), RecipeArgument.Bare("{in:flag}")] };

        Assert.Empty(RecipeValidator.Validate(atCap, FakeCharacterConverter.Plain));
        RecipeError error = Assert.Single(RecipeValidator.Validate(over, FakeCharacterConverter.Plain));
        Assert.Equal(RecipeErrorCode.TooMany, error.Code);
        Assert.Equal(RecipeLimits.MaxTokens + 1, error.Actual);
    }

    [Theory]
    [InlineData("{Host}", true, false)]
    [InlineData("{Port}", false, true)]
    [InlineData("--p={Host}:{Port}", true, true)]
    public void TheHostAndThePortAreFoundInWhenFalseToo(string token, bool host, bool port)
    {
        RecipeInput flag = RecipeTestData.Input("flag", RecipeInputType.Bool) with { WhenFalse = [token] };
        Recipe recipe = RecipeTestData.Minimal() with { Inputs = [flag], Arguments = [RecipeArgument.Bare("{in:flag}")] };

        RecipeSessionUse use = RecipeSessionUse.Of(recipe);

        Assert.Equal(host, use.UsesHost);
        Assert.Equal(port, use.UsesPort);
    }

    [Fact]
    public void TheHostOfAWhenFalseTokenIsFilledFromTheSession()
    {
        Recipe recipe = Valid(
            "[{\"name\":\"flag\",\"type\":\"bool\",\"label\":\"Flag\",\"whenFalse\":[\"--to={Host}:{Port}\"]}]",
            "[\"{in:flag}\"]");

        Assert.Equal(["--to=h:22"], Expand(recipe, ("flag", "false")).Arguments);
    }

    // ---- 8: hint ----

    [Fact]
    public void AHintIsKeptOnAnyTypeOfInput()
    {
        Recipe recipe = Valid(
            $"[{Input("a", "string", ",\"hint\":\"Anything.\"")},{Input("flag", "bool", ",\"whenTrue\":[\"-v\"],\"hint\":\"Be loud.\"")}]",
            "[[\"--a\", \"{in:a}\"], \"{in:flag}\"]");

        Assert.Equal("Anything.", recipe.Inputs[0].Hint);
        Assert.Equal("Be loud.", recipe.Inputs[1].Hint);
    }

    [Fact]
    public void AHintAtItsCapIsAcceptedAndOneOverIsRefused()
    {
        Valid($"[{Input("a", "string", ",\"hint\":\"" + new string('h', RecipeLimits.MaxHintLength) + "\"")}]", "[[\"--a\", \"{in:a}\"]]");

        IReadOnlyList<RecipeError> errors = Refused(
            $"[{Input("a", "string", ",\"hint\":\"" + new string('h', RecipeLimits.MaxHintLength + 1) + "\"")}]",
            "[[\"--a\", \"{in:a}\"]]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.TooLong, error.Code);
        Assert.Equal("inputs[0].hint", error.Path);
        Assert.Equal(RecipeLimits.MaxHintLength + 1, error.Actual);
        Assert.Equal(RecipeLimits.MaxHintLength, error.Limit);
    }

    [Theory]
    [InlineData("two\\nlines")]
    [InlineData("tab\\there")]
    [InlineData("bell\\u0007")]
    [InlineData("flip\\u202Eit")]
    [InlineData("sep\\u2028x")]
    [InlineData("")]
    public void AHintWithAControlFormatOrLineCharacterIsRefused(string hint)
    {
        IReadOnlyList<RecipeError> errors = Refused($"[{Input("a", "string", ",\"hint\":\"" + hint + "\"")}]", "[[\"--a\", \"{in:a}\"]]");

        RecipeError error = Assert.Single(errors);
        Assert.Contains(error.Code, new[] { RecipeErrorCode.ControlCharacter, RecipeErrorCode.EmptyValue });
        Assert.Equal("inputs[0].hint", error.Path);
    }

    [Fact]
    public void AHintNeverReachesTheCommandLine_SoALookAlikeCharacterIsAllowedInIt()
    {
        // A typographic quote would be refused in an argument, but a hint is only shown.
        Recipe recipe = Valid(
            $"[{Input("a", "string", ",\"hint\":\"Use the \\u201Cname\\u201D \\u2014 not the id.\"")}]",
            "[[\"--a\", \"{in:a}\"]]",
            string.Empty);

        Assert.Contains('\u2014', recipe.Inputs[0].Hint!);
    }

    // ---- 4: the icon is a shape ----

    [Theory]
    [InlineData("tool", true)]
    [InlineData("a", true)]
    [InlineData("a1", true)]
    [InlineData("network-card", true)]
    [InlineData("a-b-c", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("", false)]
    [InlineData("Tool", false)]
    [InlineData("1a", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("a--b", false)]
    [InlineData("a_b", false)]
    [InlineData("a b", false)]
    [InlineData("caf\u00E9", false)]
    public void AnIconKeyMustHaveTheShapeOfAKey(string key, bool valid)
        => Assert.Equal(valid, RecipeIcons.IsKeyShape(key));

    [Fact]
    public void AKeyOfTheRightShapeIsAcceptedEvenWhenThisVersionDoesNotKnowIt()
    {
        Recipe recipe = Valid(extra: ",\"icon\":\"sparkles-2\"");

        Assert.Equal("sparkles-2", recipe.Icon);
        Assert.False(RecipeIcons.IsKnown("sparkles-2"));
        Assert.True(RecipeIcons.IsKnown("network"));
    }

    [Theory]
    [InlineData("Network")]
    [InlineData("a--b")]
    [InlineData("a-")]
    [InlineData("1x")]
    public void AnIconKeyOfAnotherShapeIsRefused(string key)
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: $",\"icon\":\"{key}\"");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.InvalidIconKey, error.Code);
        Assert.Equal("icon", error.Path);
    }

    // ---- D: translations ----

    private const string TwoFieldsInputs = "[{\"name\":\"a\",\"type\":\"string\",\"label\":\"A\"}]";

    private const string TwoFieldsArguments = "[[\"--a\", \"{in:a}\"]]";

    [Fact]
    public void TranslationsCarryLabelsAndHintsPerField()
    {
        Recipe recipe = Valid(
            TwoFieldsInputs,
            TwoFieldsArguments,
            ",\"translations\":{\"fr\":{\"name\":\"N\",\"description\":\"D\",\"fieldLabels\":{\"a\":\"Un\"},\"fieldHints\":{\"a\":\"Aide\"}}}");

        RecipeTranslation french = recipe.Translations["fr"];
        Assert.Equal("N", french.Name);
        Assert.Equal("D", french.Description);
        Assert.Equal("Un", french.FieldLabels["a"]);
        Assert.Equal("Aide", french.FieldHints["a"]);
    }

    [Fact]
    public void ATranslationWithoutHintsHasNone()
    {
        Recipe recipe = Valid(TwoFieldsInputs, TwoFieldsArguments, ",\"translations\":{\"fr\":{\"name\":\"N\"}}");

        Assert.Empty(recipe.Translations["fr"].FieldHints);
        Assert.Empty(recipe.Translations["fr"].FieldLabels);
    }

    [Fact]
    public void TheOldNamesOfTheTranslationBlocksAreUnknownProperties()
    {
        IReadOnlyList<RecipeError> labels = Refused(extra: ",\"labels\":{\"fr\":{\"name\":\"N\"}}");
        IReadOnlyList<RecipeError> fields = Refused(
            TwoFieldsInputs,
            TwoFieldsArguments,
            ",\"translations\":{\"fr\":{\"fields\":{\"a\":\"Un\"}}}");

        Assert.Contains(labels, error => error.Code == RecipeErrorCode.UnknownProperty && error.Name == "labels");
        Assert.Contains(fields, error => error.Code == RecipeErrorCode.UnknownProperty && error.Name == "fields");
    }

    [Theory]
    [InlineData("fieldLabels")]
    [InlineData("fieldHints")]
    public void AFieldThatDoesNotExistIsRefusedInBothMaps(string map)
    {
        IReadOnlyList<RecipeError> errors = Refused(
            TwoFieldsInputs,
            TwoFieldsArguments,
            $",\"translations\":{{\"fr\":{{\"{map}\":{{\"ghost\":\"x\"}}}}}}");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.UnknownTranslationField, error.Code);
        Assert.Equal($"translations.fr.{map}.ghost", error.Path);
        Assert.Equal("ghost", error.Name);
    }

    [Fact]
    public void ATranslatedHintHasTheCapOfAHint_AndATranslatedLabelTheCapOfALabel()
    {
        string longHint = new('h', RecipeLimits.MaxHintLength);
        Valid(TwoFieldsInputs, TwoFieldsArguments, $",\"translations\":{{\"fr\":{{\"fieldHints\":{{\"a\":\"{longHint}\"}}}}}}");

        IReadOnlyList<RecipeError> errors = Refused(
            TwoFieldsInputs,
            TwoFieldsArguments,
            $",\"translations\":{{\"fr\":{{\"fieldHints\":{{\"a\":\"{longHint}x\"}},\"fieldLabels\":{{\"a\":\"{new string('l', RecipeLimits.MaxLabelLength + 1)}\"}}}}}}");

        string[] paths = [.. errors.Where(error => error.Code == RecipeErrorCode.TooLong).Select(error => error.Path!).Order(StringComparer.Ordinal)];
        Assert.Equal(["translations.fr.fieldHints.a", "translations.fr.fieldLabels.a"], paths);
    }

    [Fact]
    public void TwoLanguageTagsThatDifferOnlyByCaseAreRefused()
    {
        IReadOnlyList<RecipeError> errors = Refused(
            extra: ",\"translations\":{\"fr\":{\"name\":\"A\"},\"FR\":{\"name\":\"B\"}}");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.DuplicateLanguageTag, error.Code);
    }

    [Fact]
    public void RegionalVariantsAreNotDuplicates()
    {
        Recipe recipe = Valid(extra: ",\"translations\":{\"fr\":{\"name\":\"A\"},\"fr-CA\":{\"name\":\"B\"},\"fr-ca2\":{\"name\":\"C\"}}");

        Assert.Equal(3, recipe.Translations.Count);
    }

    // ---- successExitCodes ----

    [Fact]
    public void ADuplicateSuccessExitCodeIsRefused()
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: ",\"successExitCodes\":[0,1,0]");

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.DuplicateExitCode, error.Code);
        Assert.Equal("successExitCodes[2]", error.Path);
        Assert.Equal(0, error.Actual);
    }

    [Fact]
    public void DistinctSuccessExitCodesAreAccepted()
    {
        Assert.Equal([0, 1, -1], Valid(extra: ",\"successExitCodes\":[0,1,-1]").SuccessExitCodes);
    }

    // ---- 9: csvSeparator ----

    [Theory]
    [InlineData("comma", RecipeCsvSeparator.Comma)]
    [InlineData("semicolon", RecipeCsvSeparator.Semicolon)]
    [InlineData("tab", RecipeCsvSeparator.Tab)]
    public void ACsvSeparatorIsReadForACsvOutput(string written, RecipeCsvSeparator expected)
    {
        Recipe recipe = Valid(extra: $",\"output\":\"csv\",\"csvSeparator\":\"{written}\"");

        Assert.Equal(expected, recipe.CsvSeparator);
    }

    [Fact]
    public void TheCsvSeparatorDefaultsToTheComma()
    {
        Assert.Equal(RecipeCsvSeparator.Comma, Valid(extra: ",\"output\":\"csv\"").CsvSeparator);
        Assert.Equal(RecipeCsvSeparator.Comma, Valid().CsvSeparator);
    }

    [Theory]
    [InlineData(",\"output\":\"text\",\"csvSeparator\":\"semicolon\"")]
    [InlineData(",\"output\":\"json\",\"csvSeparator\":\"tab\"")]
    [InlineData(",\"csvSeparator\":\"semicolon\"")]
    [InlineData(",\"output\":\"text\",\"csvSeparator\":\"comma\"")]
    [InlineData(",\"csvSeparator\":\"comma\"")]
    public void ACsvSeparatorWithoutACsvOutputIsRefusedEvenForTheComma(string extra)
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: extra);

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.CsvSeparatorWithoutCsvOutput, error.Code);
        Assert.Equal("csvSeparator", error.Path);
    }

    [Fact]
    public void AnUnknownSeparatorIsAnInvalidValue()
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: ",\"output\":\"csv\",\"csvSeparator\":\"pipe\"");

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.InvalidEnumValue && error.Path == "csvSeparator");
    }

    [Fact]
    public void ASeparatorBuiltByHandWithoutACsvOutputIsRefusedByTheValidator()
    {
        Recipe recipe = RecipeTestData.Minimal() with { CsvSeparator = RecipeCsvSeparator.Tab };

        Assert.Contains(RecipeErrorCode.CsvSeparatorWithoutCsvOutput, RecipeTestData.Codes(recipe));
        Assert.Empty(RecipeTestData.Codes(recipe with { Output = RecipeOutputFormat.Csv }));
    }

    // ---- 5: the encodings are names ----

    [Theory]
    [InlineData("utf8", RecipeOutputEncoding.Utf8)]
    [InlineData("oem", RecipeOutputEncoding.Oem)]
    [InlineData("ansi", RecipeOutputEncoding.Ansi)]
    [InlineData("utf16le", RecipeOutputEncoding.Utf16Le)]
    public void EveryOutputEncodingIsReadByName(string written, RecipeOutputEncoding expected)
    {
        Assert.Equal(expected, Valid(extra: $",\"outputEncoding\":\"{written}\"").OutputEncoding);
    }

    [Theory]
    [InlineData("utf16be")]
    [InlineData("utf16")]
    [InlineData("UTF8")]
    [InlineData("default")]
    public void AnotherEncodingNameIsRefused(string written)
    {
        IReadOnlyList<RecipeError> errors = Refused(extra: $",\"outputEncoding\":\"{written}\"");

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.InvalidEnumValue && error.Path == "outputEncoding");
    }
}
