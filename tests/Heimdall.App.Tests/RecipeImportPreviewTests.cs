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

using System.IO;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The import preview is the barrier of the feature: plain words, the resolved program on its own
/// line, one argument per line exactly as written, and nothing from the recipe taken for markup or
/// for a locale key.
/// </summary>
public sealed class RecipeImportPreviewTests
{
    private static async Task<LocalizationManager> EnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        return localizer;
    }

    private static RecipeImportPlan PlanOf(
        byte[] bytes,
        string resolved = @"C:\Windows\System32\ping.exe",
        RecipeStoreEntry? existing = null,
        Recipe? previous = null)
    {
        Recipe recipe = RecipeParser.Parse(bytes).Recipe ?? throw new InvalidOperationException("The test recipe is not valid.");
        return new RecipeImportPlan(recipe, bytes, resolved, RecipeSessionUse.Of(recipe), existing, previous);
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    [Fact]
    public async Task ThePreviewNamesTheRecipeItsAuthorAsUnverifiedItsIdAndItsVersion()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        Assert.Contains("Name: Ping", text, StringComparison.Ordinal);
        Assert.Contains("Author (unverified): Acme", text, StringComparison.Ordinal);
        Assert.Contains("Id: acme.ping", text, StringComparison.Ordinal);
        Assert.Contains("Version: 1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("Description: Ping the selected host.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreviewOpensWithTheStandingWarningThatTheRecipeIsUnverified()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        Assert.StartsWith("This recipe was written by someone else and Heimdall cannot verify it.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResolvedProgramIsOnALineOfItsOwn()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(), @"C:\Tools\probe.exe"));

        string[] lines = Lines(text);
        int heading = Array.FindIndex(lines, line => line.StartsWith("Program that will run", StringComparison.Ordinal));
        Assert.True(heading >= 0);
        Assert.Equal(@"  C:\Tools\probe.exe", lines[heading + 1]);
    }

    [Fact]
    public async Task EveryArgumentIsOnItsOwnLineExactlyAsWrittenWithItsPlaceholdersVisible()
    {
        byte[] bytes = RecipeImportFiles.Bytes(argumentsJson: "[\"-n\", \"--host={Host}\", \"a b\", \"-x\"]");

        string[] lines = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes)));

        Assert.Contains("  1. [-n]", lines);
        Assert.Contains("  2. [--host={Host}]", lines);
        Assert.Contains("  3. [a b]", lines);
        Assert.Contains("  4. [-x]", lines);
    }

    [Fact]
    public async Task APreviewIsNeverOneFormattedCommandLine()
    {
        byte[] bytes = RecipeImportFiles.Bytes(argumentsJson: "[\"-n\", \"4\", \"{Host}\"]");

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes, @"C:\Windows\System32\ping.exe"));

        Assert.DoesNotContain(@"ping.exe -n 4", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ping.exe"" -n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-n 4 {Host}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedListIsShownAsOneBlockWithItsTokensOnTheirOwnLines()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"-n\", [\"-w\", \"{in:wait}\"]]",
            inputsJson: "[{\"name\":\"wait\",\"type\":\"int\",\"label\":\"Wait\",\"required\":false}]");

        string[] lines = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes)));

        Assert.Contains("  1. [-n]", lines);
        Assert.Contains("  A nested list, kept or dropped as a whole:", lines);
        Assert.Contains("    2. [-w]", lines);
        Assert.Contains("    3. [{in:wait}]", lines);
    }

    [Fact]
    public async Task TheInputsAreListedWithTheirTypesAndWhetherTheyAreRequired()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:count}\", \"{in:mode}\"]",
            inputsJson: "[{\"name\":\"count\",\"type\":\"int\",\"label\":\"Count\",\"required\":true,\"default\":\"4\"},"
                + "{\"name\":\"mode\",\"type\":\"choice\",\"label\":\"Mode\",\"required\":true,\"choices\":[\"fast\",\"slow\"]}]");

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes));

        Assert.Contains("  count: Count (type: whole number, required, default: 4)", text, StringComparison.Ordinal);
        Assert.Contains("  mode: Mode (type: one value of a list, required, choices: fast, slow)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AYesNoFieldShowsTheArgumentsItAddsWhenOn()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            argumentsJson: "[\"{in:verbose}\"]",
            inputsJson: "[{\"name\":\"verbose\",\"type\":\"bool\",\"label\":\"Verbose\",\"whenTrue\":[\"-v\",\"-d\"]}]");

        string[] lines = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes)));

        Assert.Contains("    When on, it adds these arguments:", lines);
        Assert.Contains("      [-v]", lines);
        Assert.Contains("      [-d]", lines);
    }

    [Theory]
    [InlineData("[\"{Host}\"]", "host: Yes, port: No")]
    [InlineData("[\"{Port}\"]", "host: No, port: Yes")]
    [InlineData("[\"{Host}\", \"{Port}\"]", "host: Yes, port: Yes")]
    [InlineData("[\"-n\"]", "host: No, port: No")]
    public async Task ThePreviewSaysWhetherTheRecipeReadsTheHostOrThePortOfTheSession(string argumentsJson, string expected)
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(argumentsJson: argumentsJson)));

        Assert.Contains("Reads from the selected session - " + expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreviewNamesTheOutputKindAndTheTimeout()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        Assert.Contains("The output is shown as: plain text", text, StringComparison.Ordinal);
        Assert.EndsWith("It is stopped after 60 seconds.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecipeNameThatLooksLikeAMarkupOrALocaleKeyIsShownAsPlainText()
    {
        byte[] bytes = RecipeImportFiles.Bytes(
            name: "<Bold>BtnYes</Bold>",
            description: "{{Host}} BtnCancel &amp; <Hyperlink>",
            argumentsJson: "[\"BtnYes\", \"<Run Text='x'/>\"]");

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes));

        Assert.Contains("Name: <Bold>BtnYes</Bold>", text, StringComparison.Ordinal);
        Assert.Contains("Description: {{Host}} BtnCancel &amp; <Hyperlink>", text, StringComparison.Ordinal);
        Assert.Contains("  1. [BtnYes]", text, StringComparison.Ordinal);
        Assert.Contains("  2. [<Run Text='x'/>]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALookAlikeLetterInAnArgumentOrTheProgramIsEscapedSoItCannotPassForTheAsciiOne()
    {
        byte[] bytes = RecipeImportFiles.Bytes(argumentsJson: "[\"-\\u0430\"]");

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes, "C:\\Tools\\p\u0430ng.exe"));

        Assert.Contains("  1. [-\\u0430]", text, StringComparison.Ordinal);
        Assert.Contains("  C:\\Tools\\p\\u0430ng.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0430", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AControlOrOverrideCharacterInTheDescriptionIsMadeVisible()
    {
        byte[] bytes = RecipeImportFiles.Bytes(description: "plain");
        RecipeImportPlan plan = PlanOf(bytes);
        RecipeImportPlan hostile = plan with { Recipe = plan.Recipe with { Description = "a\u202Eb\u0007c" } };

        string text = RecipeImportPreview.Compose(await EnglishAsync(), hostile);

        Assert.Contains("Description: a\\u202Eb\\u0007c", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongRecipeIsNotCutShort()
    {
        string manyTokens = string.Join(",", Enumerable.Range(0, 40).Select(index => $"\"token{index}\""));

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(argumentsJson: $"[{manyTokens}]")));

        Assert.Contains("  40. [token39]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplacementShowsTheVersionAndTheProgramThatChange()
    {
        RecipeImportPlan old = PlanOf(RecipeImportFiles.Bytes(version: "1.0.0"), @"C:\Windows\System32\ping.exe");
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);
        RecipeImportPlan replace = PlanOf(
            RecipeImportFiles.Bytes(version: "2.0.0"),
            @"C:\Windows\SysWOW64\ping.exe",
            existing,
            old.Recipe);

        string text = RecipeImportPreview.Compose(await EnglishAsync(), replace);

        Assert.Contains("This replaces the recipe you already imported with the same id.", text, StringComparison.Ordinal);
        Assert.Contains("Version: 1.0.0 becomes 2.0.0", text, StringComparison.Ordinal);
        Assert.Contains(@"Program: C:\Windows\System32\ping.exe becomes C:\Windows\SysWOW64\ping.exe", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplacementWithTheSameProgramSaysSo()
    {
        RecipeImportPlan old = PlanOf(RecipeImportFiles.Bytes(version: "1.0.0"));
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);

        string text = RecipeImportPreview.Compose(
            await EnglishAsync(),
            PlanOf(RecipeImportFiles.Bytes(version: "1.1.0"), @"C:\Windows\System32\ping.exe", existing, old.Recipe));

        Assert.Contains("Program: unchanged", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplacementOfAnUnreadableCopySaysItsVersionCannotBeShown()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(), existing: existing, previous: null));

        Assert.Contains("The imported copy cannot be read, so its version is not shown.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgramAndTheArgumentsComeBeforeWhoWroteTheRecipe()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        int warning = text.IndexOf("This recipe was written by someone else", StringComparison.Ordinal);
        int program = text.IndexOf("Program that will run", StringComparison.Ordinal);
        int arguments = text.IndexOf("Arguments, one per line", StringComparison.Ordinal);
        int name = text.IndexOf("Name: Ping", StringComparison.Ordinal);
        int fields = text.IndexOf("Fields the user fills in", StringComparison.Ordinal);
        int session = text.IndexOf("Reads from the selected session", StringComparison.Ordinal);
        int output = text.IndexOf("The output is shown as", StringComparison.Ordinal);
        int timeout = text.IndexOf("It is stopped after", StringComparison.Ordinal);

        Assert.True(warning == 0 && warning < program && program < arguments && arguments < name, text);
        Assert.True(name < fields && fields < session && session < output && output < timeout, text);
    }

    [Fact]
    public async Task AReplacementBlockStaysRightAfterTheWarningAndBeforeTheProgram()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);
        RecipeImportPlan old = PlanOf(RecipeImportFiles.Bytes(version: "1.0.0"));

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(version: "1.1.0"), existing: existing, previous: old.Recipe));

        int warning = text.IndexOf("This recipe was written by someone else", StringComparison.Ordinal);
        int replace = text.IndexOf("This replaces the recipe", StringComparison.Ordinal);
        int program = text.IndexOf("Program that will run", StringComparison.Ordinal);
        Assert.True(warning < replace && replace < program, text);
    }

    [Fact]
    public async Task AReplacementSaysWhetherTheArgumentsAndTheFieldsDifferComparingParsedRecipesNotBytes()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);
        RecipeImportPlan old = PlanOf(RecipeImportFiles.Bytes(version: "1.0.0", description: "Old text."));

        // Different bytes (version, description, spacing), same arguments and fields.
        string same = RecipeImportPreview.Compose(
            await EnglishAsync(),
            PlanOf(RecipeImportFiles.Bytes(version: "1.1.0", description: "New text.", argumentsJson: "[ \"-n\" ,   \"{Host}\" ]"), existing: existing, previous: old.Recipe));

        Assert.Contains("Arguments: unchanged", same, StringComparison.Ordinal);
        Assert.Contains("Fields: unchanged", same, StringComparison.Ordinal);

        string changed = RecipeImportPreview.Compose(
            await EnglishAsync(),
            PlanOf(
                RecipeImportFiles.Bytes(
                    version: "1.1.0",
                    argumentsJson: "[\"-n\", \"9\", \"{Host}\"]",
                    inputsJson: "[{\"name\":\"w\",\"type\":\"int\",\"label\":\"W\",\"required\":false}]"),
                existing: existing,
                previous: old.Recipe));

        Assert.Contains("Arguments: changed, read them below", changed, StringComparison.Ordinal);
        Assert.Contains("Fields: changed, read them below", changed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplacementOfAnUnreadableCopyDoesNotClaimAnythingAboutArgumentsOrFields()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:.exe", DateTimeOffset.UnixEpoch);

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(), existing: existing, previous: null));

        Assert.DoesNotContain("Arguments: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fields: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewRecipeShowsNoReplacementSection()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes()));

        Assert.DoesNotContain("replaces the recipe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleAndTheButtonFollowWhetherItReplaces()
    {
        RecipeStoreEntry existing = new("acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\a.exe", DateTimeOffset.UnixEpoch);

        Assert.Equal(RecipeImportPreview.ImportTitleKey, RecipeImportPreview.TitleKey(PlanOf(RecipeImportFiles.Bytes())));
        Assert.Equal(RecipeImportPreview.ReplaceTitleKey, RecipeImportPreview.TitleKey(PlanOf(RecipeImportFiles.Bytes(), existing: existing)));
        Assert.Equal(RecipeImportPreview.ImportConfirmKey, RecipeImportPreview.ConfirmKey(PlanOf(RecipeImportFiles.Bytes())));
        Assert.Equal(RecipeImportPreview.ReplaceConfirmKey, RecipeImportPreview.ConfirmKey(PlanOf(RecipeImportFiles.Bytes(), existing: existing)));
    }

    // ---- translations are read before the recipe is stored ---------------------------------------

    private const string TargetField = "[{\"name\":\"target\",\"type\":\"string\",\"label\":\"Target\",\"required\":false}]";

    private const string TwoLanguages = "{\"fr\": {\"name\": \"Sonde\", \"description\": \"Sonde l'h\\u00f4te.\", \"fields\": {\"target\": \"Cible\"}}, \"es-MX\": {\"name\": \"Sonda\"}}";

    [Fact]
    public async Task EveryTranslationIsListedAfterTheFieldsWithItsLanguageNameDescriptionAndFieldLabels()
    {
        byte[] bytes = RecipeImportFiles.Bytes(inputsJson: TargetField, labelsJson: TwoLanguages);

        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(bytes));
        string[] lines = Lines(text);

        Assert.Contains("  Language fr:", lines);
        Assert.Contains("    Name: Sonde", lines);
        Assert.Contains("    Description: Sonde l'h\u00f4te.", lines);
        Assert.Contains("    Field target: Cible", lines);
        Assert.Contains("  Language es-MX:", lines);
        Assert.Contains("    Name: Sonda", lines);
        int fields = text.IndexOf("Fields the user fills in", StringComparison.Ordinal);
        int translations = text.IndexOf("Translations.", StringComparison.Ordinal);
        int session = text.IndexOf("Reads from the selected session", StringComparison.Ordinal);
        Assert.True(fields >= 0 && fields < translations && translations < session, text);
    }

    [Fact]
    public async Task ARecipeWithoutTranslationsSaysSoUnderTheTranslationsHeading()
    {
        string[] lines = Lines(RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes())));

        int heading = Array.FindIndex(lines, line => line.StartsWith("Translations.", StringComparison.Ordinal));
        Assert.True(heading >= 0);
        Assert.Equal("  (none)", lines[heading + 1]);
    }

    // ---- the replacement says, property by property, whether it changed --------------------------

    private static readonly RecipeStoreEntry ExistingEntry = new(
        "acme.ping", RecipeStore.FileNameFor("acme.ping"), new string('a', 64), @"C:\Windows\System32\ping.exe", DateTimeOffset.UnixEpoch);

    private static readonly string[] ReplacementProperties =
        ["Author", "Name", "Description", "Translations", "Output and encoding", "Standard error", "Success exit codes", "Timeout"];

    private static async Task<string[]> ChangedPropertiesAsync(byte[] before, byte[] after)
    {
        LocalizationManager localizer = await EnglishAsync();
        string text = RecipeImportPreview.Compose(localizer, PlanOf(after, existing: ExistingEntry, previous: PlanOf(before).Recipe));
        string[] lines = Lines(text);
        List<string> changed = [];
        foreach (string property in ReplacementProperties)
        {
            string same = $"  {property}: unchanged";
            string different = $"  {property}: changed";
            Assert.True(
                lines.Contains(same, StringComparer.Ordinal) ^ lines.Contains(different, StringComparer.Ordinal),
                $"{property} must say exactly one of unchanged or changed:{Environment.NewLine}{text}");
            if (lines.Contains(different, StringComparer.Ordinal))
            {
                changed.Add(property);
            }
        }

        return [.. changed];
    }

    [Fact]
    public async Task AReplacementWithTheSameParsedRecipeSaysEveryPropertyIsUnchanged()
    {
        byte[] before = RecipeImportFiles.Bytes(inputsJson: TargetField, labelsJson: TwoLanguages);
        // Same recipe, other bytes: the translations are written in the other order.
        byte[] after = RecipeImportFiles.Bytes(
            inputsJson: TargetField,
            labelsJson: "{\"es-MX\": {\"name\": \"Sonda\"}, \"fr\": {\"fields\": {\"target\": \"Cible\"}, \"description\": \"Sonde l'h\\u00f4te.\", \"name\": \"Sonde\"}}");

        Assert.Empty(await ChangedPropertiesAsync(before, after));
    }

    [Fact]
    public async Task AChangedAuthorIsTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Author"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(author: "Other")));

    [Fact]
    public async Task AChangedNameIsTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Name"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(name: "Pong")));

    [Fact]
    public async Task AChangedDescriptionIsTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Description"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(description: "Another text.")));

    [Fact]
    public async Task AChangedTranslationIsTheOnlyLineThatSaysChanged()
    {
        byte[] before = RecipeImportFiles.Bytes(inputsJson: TargetField, labelsJson: TwoLanguages);

        Assert.Equal(
            ["Translations"],
            await ChangedPropertiesAsync(before, RecipeImportFiles.Bytes(inputsJson: TargetField, labelsJson: "{\"fr\": {\"name\": \"Sonde\"}}")));
        Assert.Equal(
            ["Translations"],
            await ChangedPropertiesAsync(
                before,
                RecipeImportFiles.Bytes(
                    inputsJson: TargetField,
                    labelsJson: "{\"fr\": {\"name\": \"Sonde\", \"description\": \"Sonde l'h\\u00f4te.\", \"fields\": {\"target\": \"But\"}}, \"es-MX\": {\"name\": \"Sonda\"}}")));
        Assert.Equal(
            ["Translations"],
            await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(labelsJson: "{\"fr\": {\"name\": \"Sonde\"}}")));
    }

    [Theory]
    [InlineData("\"output\": \"csv\"")]
    [InlineData("\"outputEncoding\": \"oem\"")]
    public async Task AChangedOutputOrEncodingIsTheOnlyLineThatSaysChanged(string extra)
        => Assert.Equal(["Output and encoding"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(extraProperties: extra)));

    [Fact]
    public async Task AChangedStandardErrorModeIsTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Standard error"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(extraProperties: "\"stderr\": \"merge\"")));

    [Fact]
    public async Task ChangedSuccessExitCodesAreTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Success exit codes"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(extraProperties: "\"successExitCodes\": [0, 1]")));

    [Fact]
    public async Task AChangedTimeoutIsTheOnlyLineThatSaysChanged()
        => Assert.Equal(["Timeout"], await ChangedPropertiesAsync(RecipeImportFiles.Bytes(), RecipeImportFiles.Bytes(extraProperties: "\"timeoutSeconds\": 30")));

    [Fact]
    public async Task AReplacementOfAnUnreadableCopySaysNothingAboutThePropertiesItCannotCompare()
    {
        string text = RecipeImportPreview.Compose(await EnglishAsync(), PlanOf(RecipeImportFiles.Bytes(), existing: ExistingEntry, previous: null));

        foreach (string property in ReplacementProperties)
        {
            Assert.DoesNotContain($"  {property}: ", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheNewPropertyLinesAndTheTranslationsAreWordedInTheThreeLanguages()
    {
        byte[] bytes = RecipeImportFiles.Bytes(inputsJson: TargetField, labelsJson: TwoLanguages);
        foreach (string locale in new[] { "fr", "es" })
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);

            string text = RecipeImportPreview.Compose(localizer, PlanOf(bytes, existing: ExistingEntry, previous: PlanOf(RecipeImportFiles.Bytes()).Recipe));

            Assert.DoesNotContain("RecipeImportReplace", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RecipeImportPreviewLabels", text, StringComparison.Ordinal);
        }
    }
}
