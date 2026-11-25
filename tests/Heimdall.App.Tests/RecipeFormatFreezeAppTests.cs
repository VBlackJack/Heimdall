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

using System.Data;
using System.Globalization;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The application side of the last rules of the version 1 format: what the import preview says about each new
/// property, how the replace preview compares them, the words of a value outside its range, the hint under a
/// field, and the separator of a table.
/// </summary>
public sealed class RecipeFormatFreezeAppTests
{
    private static readonly RecipeStoreEntry ExistingEntry = new(
        "acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);

    private static async Task<LocalizationManager> LocalizerAsync(string locale = "en")
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);
        return localizer;
    }

    private static RecipeImportPlan PlanOf(byte[] bytes, RecipeStoreEntry? existing = null, Recipe? previous = null)
    {
        Recipe recipe = RecipeParser.Parse(bytes).Recipe ?? throw new InvalidOperationException("The test recipe is not valid.");
        return new RecipeImportPlan(recipe, bytes, @"C:\Windows\System32\ping.exe", RecipeSessionUse.Of(recipe), existing, previous);
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    private static string Field(string name, string type, string more = "")
        => $"{{\"name\":\"{name}\",\"type\":\"{type}\",\"label\":\"Label {name}\"{more}}}";

    private static async Task<string[]> PreviewLinesAsync(byte[] bytes, string locale = "en")
        => Lines(RecipeImportPreview.Compose(await LocalizerAsync(locale), PlanOf(bytes)));

    // ---- the preview shows every new property ----

    [Fact]
    public async Task ASwitchSaysNeitherRequiredNorOptional()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:flag}\", \"--n={in:n}\"]",
            inputsJson: $"[{Field("flag", "bool", ",\"whenTrue\":[\"-v\"]")},{Field("n", "int", ",\"default\":\"4\"")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        string flag = Assert.Single(lines, line => line.StartsWith("  flag:", StringComparison.Ordinal));
        Assert.DoesNotContain("required", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("optional", flag, StringComparison.Ordinal);
        string number = Assert.Single(lines, line => line.StartsWith("  n:", StringComparison.Ordinal));
        Assert.Contains("always sent, default: 4", number, StringComparison.Ordinal);
        Assert.DoesNotContain("optional", number, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "required", "optional", "always sent")]
    [InlineData("fr", "required", "optional", "toujours envoy\u00e9")]
    [InlineData("es", "required", "optional", "siempre enviado")]
    public async Task ThreeKindsOfFieldHaveThreeWords(string locale, string required, string optional, string always)
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:r}\", [\"-o\", \"{in:o}\"], \"{in:d}\"]",
            inputsJson: $"[{Field("r", "string", ",\"required\":true")},{Field("o", "string")},{Field("d", "string", ",\"default\":\"x\"")}]");

        string[] lines = await PreviewLinesAsync(bytes, locale);

        string r = Assert.Single(lines, line => line.Contains("Label r", StringComparison.Ordinal));
        string o = Assert.Single(lines, line => line.Contains("Label o", StringComparison.Ordinal));
        string d = Assert.Single(lines, line => line.Contains("Label d", StringComparison.Ordinal));
        string[] words = locale == "en" ? [required, optional, always] : [string.Empty, string.Empty, always];
        Assert.Contains(words[2], d, StringComparison.Ordinal);
        Assert.DoesNotContain(words[2], r, StringComparison.Ordinal);
        Assert.DoesNotContain(words[2], o, StringComparison.Ordinal);
        if (locale == "en")
        {
            Assert.Contains("required", r, StringComparison.Ordinal);
            Assert.Contains("optional", o, StringComparison.Ordinal);
            Assert.DoesNotContain("optional", d, StringComparison.Ordinal);
            Assert.DoesNotContain("required", d, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(",\"min\":1,\"max\":100", "range: 1 to 100")]
    [InlineData(",\"min\":1", "minimum: 1")]
    [InlineData(",\"max\":\"100\"", "maximum: 100")]
    public async Task ARangeIsShownAsWritten(string bounds, string expected)
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"--n={in:n}\"]",
            inputsJson: $"[{Field("n", "int", bounds + ",\"default\":\"5\"")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        Assert.Contains(lines, line => line.StartsWith("  n:", StringComparison.Ordinal) && line.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANumberRangeKeepsTheLexemesOfTheFile()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"--r={in:r}\"]",
            inputsJson: $"[{Field("r", "number", ",\"min\":\"0.50\",\"max\":2.5,\"default\":\"1\"")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        Assert.Contains(lines, line => line.Contains("range: 0.50 to 2.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHintIsShownUnderItsField()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[[\"-s\", \"{in:s}\"]]",
            inputsJson: $"[{Field("s", "string", ",\"hint\":\"Only letters.\"")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        int field = Array.FindIndex(lines, line => line.StartsWith("  s:", StringComparison.Ordinal));
        Assert.True(field >= 0);
        Assert.Equal("    Hint: Only letters.", lines[field + 1]);
    }

    [Fact]
    public async Task BothListsOfASwitchAreShown()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:flag}\"]",
            inputsJson: $"[{Field("flag", "bool", ",\"whenTrue\":[\"--x\"],\"whenFalse\":[\"--no-x\",\"-q\"]")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        int whenOn = Array.IndexOf(lines, "    When on, it adds these arguments:");
        int whenOff = Array.IndexOf(lines, "    When off, it adds these arguments:");
        Assert.True(whenOn >= 0 && whenOff > whenOn);
        Assert.Equal("      [--x]", lines[whenOn + 1]);
        Assert.Equal("      [--no-x]", lines[whenOff + 1]);
        Assert.Equal("      [-q]", lines[whenOff + 2]);
    }

    [Fact]
    public async Task ASwitchWithOnlyWhenFalseShowsOnlyThatList()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:flag}\"]",
            inputsJson: $"[{Field("flag", "bool", ",\"whenFalse\":[\"-q\"]")}]");

        string[] lines = await PreviewLinesAsync(bytes);

        Assert.DoesNotContain("    When on, it adds these arguments:", lines);
        Assert.Contains("    When off, it adds these arguments:", lines);
    }

    [Theory]
    [InlineData("comma", "commas")]
    [InlineData("semicolon", "semicolons")]
    [InlineData("tab", "tabs")]
    public async Task ATableFromCsvSaysWhatSeparatesItsCells(string separator, string words)
    {
        byte[] bytes = RecipeImportFiles.Bytes(extraProperties: $"\"output\":\"csv\",\"csvSeparator\":\"{separator}\"");

        string[] lines = await PreviewLinesAsync(bytes);

        Assert.Contains($"The output is shown as: a table (from CSV), cells separated by {words}", lines);
    }

    [Fact]
    public async Task APlainTextOutputSaysNothingAboutASeparator()
    {
        string[] lines = await PreviewLinesAsync(RecipeImportFiles.Bytes());

        Assert.Contains("The output is shown as: plain text", lines);
    }

    [Theory]
    [InlineData("ansi", "The output is read as: the ANSI code page of the system")]
    [InlineData("utf16le", "The output is read as: UTF-16 little endian")]
    [InlineData("oem", "The output is read as: the OEM code page of the console")]
    public async Task EveryEncodingHasItsWords(string encoding, string expected)
    {
        string[] lines = await PreviewLinesAsync(RecipeImportFiles.Bytes(extraProperties: $"\"outputEncoding\":\"{encoding}\""));

        Assert.Contains(expected, lines);
    }

    [Fact]
    public async Task AnIconKeyThisVersionDoesNotKnowIsSaidSo_AndAKnownOneIsNot()
    {
        string[] unknown = await PreviewLinesAsync(RecipeImportFiles.Bytes(extraProperties: "\"icon\":\"sparkles-2\""));
        string[] known = await PreviewLinesAsync(RecipeImportFiles.Bytes(extraProperties: "\"icon\":\"network\""));
        string[] none = await PreviewLinesAsync(RecipeImportFiles.Bytes());

        Assert.Contains("Icon: sparkles-2 (unknown to this version of Heimdall; the default icon is shown)", unknown);
        Assert.Contains("Icon: network", known);
        Assert.Contains("Icon: (none)", none);
    }

    [Fact]
    public async Task EveryTranslatedHintIsListed()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[[\"-s\", \"{in:s}\"]]",
            inputsJson: $"[{Field("s", "string")}]",
            labelsJson: "{\"fr\":{\"fieldLabels\":{\"s\":\"Texte\"},\"fieldHints\":{\"s\":\"Seulement des lettres.\"}}}");

        string[] lines = await PreviewLinesAsync(bytes);

        Assert.Contains("    Field s: Texte", lines);
        Assert.Contains("    Hint for field s: Seulement des lettres.", lines);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task EveryNewLineIsWordedInTheOtherLanguages(string locale)
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:flag}\", \"--n={in:n}\"]",
            inputsJson: $"[{Field("flag", "bool", ",\"whenTrue\":[\"--x\"],\"whenFalse\":[\"--no-x\"],\"hint\":\"Aide\"")},{Field("n", "int", ",\"min\":1,\"max\":9,\"default\":\"2\"")}]",
            labelsJson: "{\"fr\":{\"fieldHints\":{\"flag\":\"Aide\"},\"fieldLabels\":{\"flag\":\"Drapeau\"}}}",
            extraProperties: "\"output\":\"csv\",\"csvSeparator\":\"tab\",\"outputEncoding\":\"utf16le\",\"icon\":\"unknown-key\"");

        string text = string.Join(Environment.NewLine, await PreviewLinesAsync(bytes, locale));

        Assert.DoesNotContain("RecipeImport", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", text, StringComparison.Ordinal);
    }

    // ---- the replace preview compares every new property ----

    private static async Task<string[]> ReplaceLinesAsync(byte[] before, byte[] after)
        => Lines(RecipeImportPreview.Compose(await LocalizerAsync(), PlanOf(after, ExistingEntry, PlanOf(before).Recipe)));

    private static byte[] WithInput(string more, string type = "int", string arguments = "[\"--n={in:n}\"]", string? extra = null, string? labels = null)
        => RecipeImportFiles.Bytes(
            argumentsJson: arguments,
            inputsJson: $"[{Field("n", type, more)}]",
            extraProperties: extra,
            labelsJson: labels);

    [Theory]
    [InlineData(",\"default\":\"5\"", ",\"default\":\"5\",\"min\":1")]
    [InlineData(",\"default\":\"5\",\"min\":1", ",\"default\":\"5\",\"min\":2")]
    [InlineData(",\"default\":\"5\"", ",\"default\":\"5\",\"max\":9")]
    [InlineData(",\"default\":\"5\",\"max\":9", ",\"default\":\"5\",\"max\":\"9\"")]
    [InlineData(",\"default\":\"5\",\"max\":9", ",\"default\":\"5\",\"max\":10")]
    [InlineData(",\"default\":\"5\"", ",\"default\":\"5\",\"hint\":\"Help.\"")]
    [InlineData(",\"default\":\"5\",\"hint\":\"Help.\"", ",\"default\":\"5\",\"hint\":\"Other help.\"")]
    public async Task APropertyOfAFieldThatChangesMakesTheFieldsLineSayChanged(string before, string after)
    {
        string[] lines = await ReplaceLinesAsync(WithInput(before), WithInput(after));

        if (before.Replace("\"9\"", "9", StringComparison.Ordinal) == after.Replace("\"9\"", "9", StringComparison.Ordinal))
        {
            // The same bound written as a string or as a number is the same parsed text.
            Assert.Contains("  Fields: unchanged", lines);
            return;
        }

        Assert.Contains("  Fields: changed, read them below", lines);
        Assert.DoesNotContain("  Fields: unchanged", lines);
    }

    [Theory]
    [InlineData(",\"whenTrue\":[\"-v\"]", ",\"whenTrue\":[\"-v\"],\"whenFalse\":[\"-q\"]")]
    [InlineData(",\"whenFalse\":[\"-q\"]", ",\"whenFalse\":[\"-q\",\"-z\"]")]
    [InlineData(",\"whenFalse\":[\"-q\"]", ",\"whenFalse\":[\"-r\"]")]
    [InlineData(",\"whenTrue\":[\"-v\"],\"whenFalse\":[\"-q\"]", ",\"whenTrue\":[\"-q\"],\"whenFalse\":[\"-v\"]")]
    public async Task AChangedWhenFalseListMakesTheFieldsLineSayChanged(string before, string after)
    {
        string[] lines = await ReplaceLinesAsync(WithInput(before, "bool", "[\"{in:n}\"]"), WithInput(after, "bool", "[\"{in:n}\"]"));

        Assert.Contains("  Fields: changed, read them below", lines);
    }

    [Fact]
    public async Task ASameFieldWithBoundsAndAHintSaysUnchanged()
    {
        const string more = ",\"default\":\"5\",\"min\":1,\"max\":9,\"hint\":\"Help.\"";

        string[] lines = await ReplaceLinesAsync(WithInput(more), WithInput(more));

        Assert.Contains("  Fields: unchanged", lines);
    }

    [Theory]
    [InlineData("\"output\":\"csv\"", "\"output\":\"csv\",\"csvSeparator\":\"semicolon\"")]
    [InlineData("\"output\":\"csv\",\"csvSeparator\":\"semicolon\"", "\"output\":\"csv\",\"csvSeparator\":\"tab\"")]
    [InlineData("\"outputEncoding\":\"oem\"", "\"outputEncoding\":\"ansi\"")]
    [InlineData("\"outputEncoding\":\"utf8\"", "\"outputEncoding\":\"utf16le\"")]
    public async Task AChangedSeparatorOrEncodingMakesTheOutputLineSayChanged(string before, string after)
    {
        string[] lines = await ReplaceLinesAsync(RecipeImportFiles.Bytes(extraProperties: before), RecipeImportFiles.Bytes(extraProperties: after));

        Assert.Contains("  Output and encoding: changed", lines);
    }

    [Fact]
    public async Task TheCommaWrittenOutLoudIsTheDefaultAndSaysUnchanged()
    {
        string[] lines = await ReplaceLinesAsync(
            RecipeImportFiles.Bytes(extraProperties: "\"output\":\"csv\""),
            RecipeImportFiles.Bytes(extraProperties: "\"output\":\"csv\",\"csvSeparator\":\"comma\""));

        Assert.Contains("  Output and encoding: unchanged", lines);
    }

    [Fact]
    public async Task AChangedFieldHintTranslationMakesTheTranslationsLineSayChanged()
    {
        byte[] Bytes(string hint) => WithInput(
            ",\"default\":\"5\"",
            labels: $"{{\"fr\":{{\"fieldLabels\":{{\"n\":\"Nombre\"}},\"fieldHints\":{{\"n\":\"{hint}\"}}}}}}");

        string[] changed = await ReplaceLinesAsync(Bytes("Aide"), Bytes("Autre aide"));
        string[] same = await ReplaceLinesAsync(Bytes("Aide"), Bytes("Aide"));

        Assert.Contains("  Translations: changed", changed);
        Assert.Contains("  Translations: unchanged", same);
    }

    [Fact]
    public async Task ATranslationThatGainsAHintMakesTheTranslationsLineSayChanged()
    {
        byte[] before = WithInput(",\"default\":\"5\"", labels: "{\"fr\":{\"fieldLabels\":{\"n\":\"Nombre\"}}}");
        byte[] after = WithInput(",\"default\":\"5\"", labels: "{\"fr\":{\"fieldLabels\":{\"n\":\"Nombre\"},\"fieldHints\":{\"n\":\"Aide\"}}}");

        string[] lines = await ReplaceLinesAsync(before, after);

        Assert.Contains("  Translations: changed", lines);
    }

    // ---- the words of a value outside its range ----

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task AValueOutsideItsRangeNamesTheBoundsInTheThreeLanguages(string locale)
    {
        LocalizationManager localizer = await LocalizerAsync(locale);
        RecipeInput both = new("n", RecipeInputType.Int, "N", false, null, null, null, null, null) { Min = "1", Max = "100" };
        RecipeInput low = both with { Max = null };
        RecipeInput high = both with { Min = null };

        string between = RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, both);
        string atLeast = RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, low);
        string atMost = RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, high);

        Assert.Contains("1", between, StringComparison.Ordinal);
        Assert.Contains("100", between, StringComparison.Ordinal);
        Assert.Contains("1", atLeast, StringComparison.Ordinal);
        Assert.DoesNotContain("100", atLeast, StringComparison.Ordinal);
        Assert.Contains("100", atMost, StringComparison.Ordinal);
        Assert.DoesNotContain("{", between + atLeast + atMost, StringComparison.Ordinal);
        Assert.Equal(3, new[] { between, atLeast, atMost }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheEnglishWordsOfAValueOutsideItsRange()
    {
        LocalizationManager localizer = await LocalizerAsync();
        RecipeInput both = new("n", RecipeInputType.Int, "N", false, null, null, null, null, null) { Min = "1", Max = "100" };

        Assert.Equal("Enter a value between 1 and 100.", RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, both));
        Assert.Equal("Enter a value of at least 1.", RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, both with { Max = null }));
        Assert.Equal("Enter a value of at most 100.", RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, both with { Min = null }));
    }

    [Fact]
    public async Task TheBoundsAreShownAsWrittenWhateverTheCultureOfTheInterface()
    {
        LocalizationManager localizer = await LocalizerAsync("fr");
        RecipeInput number = new("r", RecipeInputType.Number, "R", false, null, null, null, null, null) { Min = "0.5", Max = "2.5" };
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            string words = RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, number);

            Assert.Contains("0.5", words, StringComparison.Ordinal);
            Assert.Contains("2.5", words, StringComparison.Ordinal);
            Assert.DoesNotContain("0,5", words, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task AnotherRefusalReadsTheSameWithOrWithoutTheInput()
    {
        LocalizationManager localizer = await LocalizerAsync();
        RecipeInput input = new("n", RecipeInputType.Int, "N", false, null, null, null, null, null) { Min = "1" };

        Assert.Equal(
            RecipeRunText.Describe(localizer, RecipeValueRefusal.NotAnInteger),
            RecipeRunText.Describe(localizer, RecipeValueRefusal.NotAnInteger, input));
        Assert.Equal(
            RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange),
            RecipeRunText.Describe(localizer, RecipeValueRefusal.OutOfRange, null));
    }

    // ---- the view model: a value outside its range, and the hint ----

    private static Recipe RangeRecipe() => RecipeBuilders.Recipe(
        inputs: [RecipeBuilders.Input("n", RecipeInputType.Int, defaultValue: "5") with { Min = "1", Max = "9" }],
        arguments: [RecipeArgument.Bare("--n={in:n}")]);

    [Fact]
    public async Task AValueOutsideItsRangeIsShownOnItsFieldWithTheBounds()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(RangeRecipe());
        viewModel.Fields[0].Text = "12";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.Fields[0].HasError);
        Assert.Equal("Enter a value between 1 and 9.", viewModel.Fields[0].Error);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public async Task AValueInsideItsRangeRuns()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(RangeRecipe());
        viewModel.Fields[0].Text = "9";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(["--n=9"], harness.Runner.Calls.Single().Arguments);
    }

    private static Recipe HintedRecipe() => RecipeBuilders.Recipe(
        inputs: [RecipeBuilders.Input("t", RecipeInputType.Hostname, label: "Target") with { Hint = "A host name." }],
        arguments: [RecipeArgument.Bare("{in:t}")],
        labels: new Dictionary<string, RecipeTranslation>
        {
            ["fr"] = new(null, null, new Dictionary<string, string>())
            {
                FieldHints = new Dictionary<string, string> { ["t"] = "Un nom d'hote." },
            },
        });

    [Fact]
    public async Task AFieldShowsTheHintOfTheRecipe()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("en");

        RecipeToolViewModel viewModel = harness.Create(HintedRecipe());

        Assert.True(viewModel.Fields[0].HasHint);
        Assert.Equal("A host name.", viewModel.Fields[0].Hint);
    }

    [Fact]
    public async Task AFieldShowsTheTranslatedHintInTheLanguageOfTheInterface()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("fr");

        RecipeToolViewModel viewModel = harness.Create(HintedRecipe());

        Assert.Equal("Un nom d'hote.", viewModel.Fields[0].Hint);
    }

    [Fact]
    public async Task AFieldWithoutAHintHasNoneToShow()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();

        RecipeToolViewModel viewModel = harness.Create(
            RecipeBuilders.Recipe(inputs: [RecipeBuilders.Input("t", RecipeInputType.Hostname)], arguments: [RecipeArgument.Bare("{in:t}")]));

        Assert.False(viewModel.Fields[0].HasHint);
        Assert.Equal(string.Empty, viewModel.Fields[0].Hint);
    }

    [Fact]
    public void TheHintFallsBackToTheRecipeWhenTheLanguageHasNone()
    {
        Recipe recipe = HintedRecipe();

        Assert.Equal("Un nom d'hote.", RecipeToolText.Resolve(recipe, "fr").FieldHints["t"]);
        Assert.Equal("A host name.", RecipeToolText.Resolve(recipe, "es").FieldHints["t"]);
        Assert.Equal("A host name.", RecipeToolText.Resolve(recipe, "en").FieldHints["t"]);
    }

    [Fact]
    public void AnInputWithoutAHintHasNoEntryInTheTexts()
    {
        Recipe recipe = RecipeBuilders.Recipe(inputs: [RecipeBuilders.Input("t", RecipeInputType.Hostname)]);

        Assert.Empty(RecipeToolText.Resolve(recipe, "fr").FieldHints);
    }

    // ---- the words of the new errors ----

    [Theory]
    [InlineData("en", "The field \"unused\" is not used by any argument.")]
    [InlineData("fr", "Le champ \"unused\" n'est utilis\u00e9 par aucun argument.")]
    [InlineData("es", "Ning\u00fan argumento usa el campo \"unused\".")]
    public async Task AnInputNoArgumentUsesIsDescribedInTheThreeLanguages(string locale, string expected)
    {
        LocalizationManager localizer = await LocalizerAsync(locale);

        string words = RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.UnreferencedInput, Path: null, Name: "unused"));

        Assert.Equal(expected, words);
    }

    [Fact]
    public async Task TheBoundsOfAnInvalidRangeAreNamedInTheMessages()
    {
        LocalizationManager localizer = await LocalizerAsync();

        Assert.Equal(
            "The minimum 5 is above the maximum 4.",
            RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.MinAboveMax, Name: "4", Fragment: "5")));
        Assert.Equal(
            "The default \"7\" of the field \"n\" is outside the minimum and maximum of that field.",
            RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.DefaultOutOfBounds, Name: "n", Fragment: "7")));
        Assert.Equal(
            "The exit code 1 is listed twice.",
            RecipeImportText.Describe(localizer, new RecipeError(RecipeErrorCode.DuplicateExitCode, Actual: 1)));
    }

    [Fact]
    public void EveryNewErrorCodeHasItsOwnKey()
    {
        RecipeErrorCode[] added =
        [
            RecipeErrorCode.InvalidBound,
            RecipeErrorCode.MinAboveMax,
            RecipeErrorCode.DefaultOutOfBounds,
            RecipeErrorCode.RequiredWithDefault,
            RecipeErrorCode.OptionalInputInSwitchArguments,
            RecipeErrorCode.StderrMergeWithStructuredOutput,
            RecipeErrorCode.CsvSeparatorWithoutCsvOutput,
            RecipeErrorCode.DuplicateLanguageTag,
            RecipeErrorCode.DuplicateExitCode,
            RecipeErrorCode.UnknownTranslationField,
            RecipeErrorCode.InvalidIconKey,
            RecipeErrorCode.MissingSwitchArguments,
            RecipeErrorCode.UnreferencedInput,
        ];

        Assert.Equal(added.Length, added.Select(RecipeImportText.KeyFor).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("RecipeErrorUnreferencedInput", RecipeImportText.KeyFor(RecipeErrorCode.UnreferencedInput));
        Assert.Equal("RecipeErrorUnknownTranslationField", RecipeImportText.KeyFor(RecipeErrorCode.UnknownTranslationField));
    }

    // ---- the table: the separator ----

    [Theory]
    [InlineData(RecipeCsvSeparator.Comma, "a,b\r\n1,2\r\n")]
    [InlineData(RecipeCsvSeparator.Semicolon, "a;b\r\n1;2\r\n")]
    [InlineData(RecipeCsvSeparator.Tab, "a\tb\r\n1\t2\r\n")]
    public void EverySeparatorCutsTheCellsTheSameWay(RecipeCsvSeparator separator, string text)
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv(text, separator).Table);

        Assert.Equal(["a", "b"], table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal("2", table.Rows[0][1]);
    }

    [Theory]
    [InlineData(RecipeCsvSeparator.Comma, "Name,Note\r\n\"Smith, J\",\"said \"\"hi\"\"\"\r\n")]
    [InlineData(RecipeCsvSeparator.Semicolon, "Name;Note\r\n\"Smith; J\";\"said \"\"hi\"\"\"\r\n")]
    [InlineData(RecipeCsvSeparator.Tab, "Name\tNote\r\n\"Smith\t J\"\t\"said \"\"hi\"\"\"\r\n")]
    public void AQuotedCellKeepsItsSeparatorAndItsQuotesWhateverTheSeparator(RecipeCsvSeparator separator, string text)
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv(text, separator).Table);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("said \"hi\"", table.Rows[0][1]);
        Assert.StartsWith("Smith", (string)table.Rows[0][0], StringComparison.Ordinal);
        Assert.Contains((separator == RecipeCsvSeparator.Comma ? "," : separator == RecipeCsvSeparator.Semicolon ? ";" : "\t"), (string)table.Rows[0][0], StringComparison.Ordinal);
    }

    [Fact]
    public void ACommaIsJustTextWhenTheSeparatorIsASemicolon()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv("a;b\r\n1,5;2\r\n", RecipeCsvSeparator.Semicolon).Table);

        Assert.Equal("1,5", table.Rows[0][0]);
    }

    [Fact]
    public void TheDefaultSeparatorIsTheComma_SoTheExternalToolWrapperIsUnchanged()
    {
        DataTable table = Assert.IsType<DataTable>(TabularOutput.FromCsv("a,b\r\n1,2\r\n").Table);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("1", table.Rows[0][0]);
    }

    [Fact]
    public async Task TheViewModelReadsATableWithTheSeparatorOfTheRecipe()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = RecipeBuilders.Recipe(output: RecipeOutputFormat.Csv) with { CsvSeparator = RecipeCsvSeparator.Semicolon };
        RecipeToolViewModel viewModel = harness.Create(recipe);
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, output: "a;b\r\n1;2\r\n"));

        await viewModel.RunCommand.ExecuteAsync(null);

        DataTable table = Assert.IsType<DataTable>(viewModel.OutputTable);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("2", table.Rows[0][1]);
    }
}
