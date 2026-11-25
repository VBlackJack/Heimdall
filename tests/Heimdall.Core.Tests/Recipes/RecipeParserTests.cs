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

/// <summary>The strict reader: shapes, refusals, positions and the size cap.</summary>
public sealed class RecipeParserTests
{
    [Fact]
    public void AMinimalRecipe_ParsesWithTheDocumentedDefaults()
    {
        Recipe recipe = RecipeTestData.ParseValid(RecipeTestData.MinimalJson);

        Assert.Equal("acme.ping", recipe.Id);
        Assert.Equal("ping.exe", recipe.Program);
        Assert.Equal(RecipeOutputFormat.Text, recipe.Output);
        Assert.Equal(RecipeOutputEncoding.Utf8, recipe.OutputEncoding);
        Assert.Equal(RecipeStderrMode.Separate, recipe.Stderr);
        Assert.Equal([0], recipe.SuccessExitCodes);
        Assert.Equal(RecipeLimits.DefaultTimeoutSeconds, recipe.TimeoutSeconds);
        Assert.Null(recipe.Icon);
        Assert.Empty(recipe.Inputs);
        Assert.Empty(recipe.Translations);
        RecipeArgument only = Assert.Single(recipe.Arguments);
        Assert.False(only.IsGroup);
        Assert.Equal(["{Host}"], only.Tokens);
    }

    [Fact]
    public void AFullRecipe_ParsesEveryProperty()
    {
        Recipe recipe = RecipeTestData.ParseValid(RecipeTestData.FullJson);

        Assert.Equal("1.2.3-beta.1+build.5", recipe.Version);
        Assert.Equal(RecipeOutputFormat.Text, recipe.Output);
        Assert.Equal(RecipeOutputEncoding.Oem, recipe.OutputEncoding);
        Assert.Equal(RecipeStderrMode.Merge, recipe.Stderr);
        Assert.Equal([0, 1], recipe.SuccessExitCodes);
        Assert.Equal(30, recipe.TimeoutSeconds);
        Assert.Equal("network", recipe.Icon);
        Assert.Equal(@"C:\Windows\System32\ping.exe", recipe.Program);

        Assert.Equal(9, recipe.Arguments.Count);
        Assert.True(recipe.Arguments[0].IsGroup);
        Assert.Equal(["-n", "{in:count}"], recipe.Arguments[0].Tokens);
        Assert.False(recipe.Arguments[1].IsGroup);

        Assert.Equal(8, recipe.Inputs.Count);
        RecipeInput count = recipe.FindInput("count")!;
        Assert.Equal("4", count.Default);
        RecipeInput verbose = recipe.FindInput("verbose")!;
        Assert.Equal("false", verbose.Default);
        Assert.Equal(["-v", "-d"], verbose.WhenTrue);
        RecipeInput mode = recipe.FindInput("mode")!;
        Assert.Equal(["fast", "slow"], mode.Choices);
        RecipeInput extra = recipe.FindInput("extra")!;
        Assert.Equal("[a-z]+", extra.Pattern);
        Assert.Equal(20, extra.MaxLength);
        Assert.True(recipe.FindInput("target")!.Required);

        RecipeTranslation french = recipe.Translations["fr"];
        Assert.Equal("Analyse", french.Name);
        Assert.Equal("Cible", french.FieldLabels["target"]);
    }

    [Fact]
    public void AnUnknownProperty_IsRefusedWithItsPosition()
    {
        string json = RecipeTestData.MinimalJson.Replace(
            "\"program\"",
            "\"colour\": \"red\",\n  \"program\"",
            StringComparison.Ordinal);

        IReadOnlyList<RecipeError> errors = RecipeTestData.ParseInvalid(json);

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.UnknownProperty, error.Code);
        Assert.Equal("colour", error.Name);
        Assert.Equal(8, error.Position!.Value.Line);
        Assert.Equal(3, error.Position.Value.Column);
    }

    [Fact]
    public void AnUnknownPropertyInAnInput_IsRefusedAndNamesTheInput()
    {
        string json = RecipeTestData.MinimalJson.Replace(
            "\"arguments\"",
            "\"inputs\": [{ \"name\": \"a\", \"type\": \"string\", \"label\": \"A\", \"extra\": 1 }], \"arguments\"",
            StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.UnknownProperty, error.Code);
        Assert.Equal("inputs[0]", error.Path);
    }

    [Fact]
    public void ADuplicateProperty_IsRefusedAtTheSecondOne()
    {
        string json = RecipeTestData.MinimalJson.Replace(
            "\"id\": \"acme.ping\",",
            "\"id\": \"acme.ping\",\n  \"id\": \"acme.other\",",
            StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.DuplicateProperty, error.Code);
        Assert.Equal("id", error.Name);
        Assert.Equal(4, error.Position!.Value.Line);
    }

    [Theory]
    [InlineData("\"name\": \"Ping\"", "\"name\": 5", "name")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": \"1\"", "schemaVersion")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 1.5", "schemaVersion")]
    [InlineData("\"arguments\": [\"{Host}\"]", "\"arguments\": \"{Host}\"", "arguments")]
    [InlineData("\"arguments\": [\"{Host}\"]", "\"arguments\": [5]", "arguments[0]")]
    [InlineData("\"arguments\": [\"{Host}\"]", "\"arguments\": [[\"a\", 5]]", "arguments[0][1]")]
    [InlineData("\"arguments\": [\"{Host}\"]", "\"arguments\": [null]", "arguments[0]")]
    public void AWrongType_IsRefusedWithItsPath(string from, string to, string path)
    {
        string json = RecipeTestData.MinimalJson.Replace(from, to, StringComparison.Ordinal);

        IReadOnlyList<RecipeError> errors = RecipeTestData.ParseInvalid(json);

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.WrongType && error.Path == path);
        Assert.All(errors, error => Assert.NotNull(error.Position));
    }

    [Fact]
    public void ADefaultOfTheWrongJsonTypeForItsInput_IsRefused()
    {
        string json = RecipeTestData.MinimalJson.Replace(
            "\"arguments\"",
            "\"inputs\": [{ \"name\": \"a\", \"type\": \"string\", \"label\": \"A\", \"default\": 5 }], \"arguments\"",
            StringComparison.Ordinal);

        IReadOnlyList<RecipeError> errors = RecipeTestData.ParseInvalid(json);

        Assert.Contains(errors, error => error.Code == RecipeErrorCode.WrongType && error.Path == "inputs[0].default");
    }

    [Fact]
    public void AMissingMandatoryProperty_IsRefused()
    {
        string json = RecipeTestData.MinimalJson.Replace("\"author\": \"Acme\",", string.Empty, StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.MissingProperty, error.Code);
        Assert.Equal("author", error.Name);
    }

    [Fact]
    public void AnUnknownEnumValue_IsRefused()
    {
        string json = RecipeTestData.MinimalJson.Replace(
            "\"program\"",
            "\"output\": \"xml\", \"program\"",
            StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.InvalidEnumValue, error.Code);
        Assert.Equal("xml", error.Fragment);
    }

    [Fact]
    public void AnOversizeText_IsRefusedBeforeItIsParsed()
    {
        // Not JSON at all: a parse attempt would report a syntax error, never a size error.
        string text = new('x', RecipeLimits.MaxFileBytes + 1);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(text));

        Assert.Equal(RecipeErrorCode.FileTooLarge, error.Code);
        Assert.Equal(RecipeLimits.MaxFileBytes + 1, error.Actual);
        Assert.Equal(RecipeLimits.MaxFileBytes, error.Limit);
        Assert.Null(error.Position);
    }

    [Fact]
    public void AnOversizeByteArray_IsRefusedBeforeItIsParsed()
    {
        byte[] bytes = new byte[RecipeLimits.MaxFileBytes + 1];
        Array.Fill(bytes, (byte)'{');

        RecipeParseResult result = RecipeParser.Parse(bytes);

        RecipeError error = Assert.Single(result.Errors);
        Assert.Equal(RecipeErrorCode.FileTooLarge, error.Code);
    }

    [Fact]
    public void AFileAtExactlyTheCap_IsNotRefusedForItsSize()
    {
        string padding = new(' ', RecipeLimits.MaxFileBytes - RecipeTestData.MinimalJson.Length);

        RecipeParseResult result = RecipeParser.Parse(RecipeTestData.MinimalJson + padding);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ANewerSchemaVersion_IsRefusedAloneWithBothNumbers()
    {
        string json = RecipeTestData.MinimalJson
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 7, \"tomorrow\": true", StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.UnsupportedSchemaVersion, error.Code);
        Assert.Equal(7, error.Actual);
        Assert.Equal(RecipeFormat.SupportedSchemaVersion, error.Limit);
        Assert.Equal(2, error.Position!.Value.Line);
    }

    [Fact]
    public void ASyntaxError_ReportsItsLineAndColumn()
    {
        const string json = "{\n  \"id\": ,\n  \"name\": \"x\"\n}";

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.SyntaxError, error.Code);
        Assert.Equal(2, error.Position!.Value.Line);
        Assert.InRange(error.Position.Value.Column, 1, 20);
    }

    [Fact]
    public void ATrailingValue_IsASyntaxError()
    {
        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(RecipeTestData.MinimalJson + "\n{}"));

        Assert.Equal(RecipeErrorCode.SyntaxError, error.Code);
        Assert.Equal(11, error.Position!.Value.Line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyDocument_IsRefused(string text)
    {
        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(text));

        Assert.True(error.Code is RecipeErrorCode.EmptyDocument or RecipeErrorCode.SyntaxError);
    }

    [Fact]
    public void ABadlyEncodedFile_NeverThrows()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("{\"id\": \""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}")];

        RecipeParseResult result = RecipeParser.Parse(bytes);

        Assert.Equal(RecipeErrorCode.SyntaxError, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AnUnpairedSurrogateEscape_NeverThrows()
    {
        string json = RecipeTestData.MinimalJson.Replace("\"Acme\"", "\"\\ud800\"", StringComparison.Ordinal);

        RecipeParseResult result = RecipeParser.Parse(json);

        Assert.Equal(RecipeErrorCode.SyntaxError, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void TooDeepANesting_IsASyntaxError()
    {
        string json = new string('[', RecipeLimits.MaxJsonDepth + 2) + new string(']', RecipeLimits.MaxJsonDepth + 2);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.SyntaxError, error.Code);
    }

    [Fact]
    public void ARootThatIsNotAnObject_IsAWrongType()
    {
        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid("[1, 2]"));

        Assert.Equal(RecipeErrorCode.WrongType, error.Code);
        Assert.Equal(1, error.Position!.Value.Line);
    }

    [Fact]
    public void AByteOrderMark_IsTolerated()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(RecipeTestData.MinimalJson)];

        Assert.True(RecipeParser.Parse(bytes).Succeeded);
    }

    [Fact]
    public void AColumn_CountsCharactersNotBytes()
    {
        // "e acute" is two bytes in UTF-8; the unknown property after it must still be at column 16.
        const string json = "{ \"name\": \"\u00E9\", \"zz\": 1 }";

        RecipeParseResult result = RecipeParser.Parse(json);

        RecipeError zz = Assert.Single(result.Errors, error => error.Code == RecipeErrorCode.UnknownProperty);
        Assert.Equal(new RecipePosition(1, 16), zz.Position);
    }

    [Fact]
    public void AValidationError_CarriesThePositionOfItsEntry()
    {
        string json = RecipeTestData.MinimalJson.Replace("acme.ping", "Acme Ping", StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.InvalidIdShape, error.Code);
        Assert.Equal("id", error.Path);
        Assert.Equal(3, error.Position!.Value.Line);
    }

    [Fact]
    public void AValidationErrorInAToken_CarriesThePositionOfThatToken()
    {
        string json = RecipeTestData.MinimalJson.Replace("{Host}", "{host}", StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.UnknownPlaceholder, error.Code);
        Assert.Equal("arguments[0]", error.Path);
        Assert.Equal(9, error.Position!.Value.Line);
    }

    [Fact]
    public void ARightToLeftOverrideInAText_IsRefusedAndTheFragmentIsEscaped()
    {
        string json = RecipeTestData.MinimalJson.Replace("Ping the selected host.", "Ping \\u202E host", StringComparison.Ordinal);

        RecipeError error = Assert.Single(RecipeTestData.ParseInvalid(json));

        Assert.Equal(RecipeErrorCode.ControlCharacter, error.Code);
        Assert.Equal("description", error.Path);
        Assert.Equal("Ping \\u202E host", error.Fragment);
        Assert.DoesNotContain('\u202E', error.Fragment!);
    }

    [Fact]
    public void TheParserNeverReportsMoreErrorsThanTheCap()
    {
        string many = string.Join(",", Enumerable.Range(0, 200).Select(index => $"\"p{index}\": 1"));
        string json = "{" + many + "}";

        RecipeParseResult result = RecipeParser.Parse(json);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeLimits.MaxReportedErrors, result.Errors.Count);
    }

    [Fact]
    public void ATextAndItsBytes_GiveTheSameResult()
    {
        RecipeParseResult fromText = RecipeParser.Parse(RecipeTestData.FullJson);
        RecipeParseResult fromBytes = RecipeParser.Parse(Encoding.UTF8.GetBytes(RecipeTestData.FullJson));

        Assert.True(fromText.Succeeded);
        Assert.True(fromBytes.Succeeded);
        Assert.Equal(fromText.Recipe!.Id, fromBytes.Recipe!.Id);
    }

    [Fact]
    public void TheExtensionIsTheDocumentedOne()
        => Assert.Equal(".heimdall-recipe.json", RecipeFormat.FileExtension);
}
