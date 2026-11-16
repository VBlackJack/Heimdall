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
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Every text of the root questions and of the lines that say root was not run exists in the three
/// languages, takes the same arguments in each, and reads as the language it is in.
/// </summary>
public sealed class SftpSudoConsentLocaleKeysTests
{
    private static readonly string[] s_locales = ["en", "fr", "es"];

    private static readonly Regex s_placeholder = new(
        @"\{(\d+)\}", RegexOptions.None, TimeSpan.FromSeconds(5));

    // The text a two-argument join carries no words in, and the one note that is a pure format.
    private const string WordlessKey = "SftpSudoRefusalWithNote";

    [Fact]
    public void EveryKeyIsDefinedInEveryLanguage()
    {
        List<string> missing = [];
        foreach (string locale in s_locales)
        {
            Dictionary<string, string> catalogue = Load(locale);
            foreach (string key in SftpSudoConsentLocaleKeys.All)
            {
                if (!catalogue.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                {
                    missing.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(missing);
        Assert.True(SftpSudoConsentLocaleKeys.All.Count >= 40, "the key list shrank, so the guard measures less than it did");
    }

    [Fact]
    public void EveryKeyTakesTheSameArgumentsInEveryLanguage()
    {
        List<string> mismatched = [];
        Dictionary<string, string> english = Load("en");
        foreach (string locale in s_locales.Skip(1))
        {
            Dictionary<string, string> other = Load(locale);
            foreach (string key in SftpSudoConsentLocaleKeys.All)
            {
                if (!Placeholders(english[key]).SequenceEqual(Placeholders(other[key])))
                {
                    mismatched.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(mismatched);
    }

    [Fact]
    public void ATextIsWrittenInItsOwnLanguage()
    {
        Dictionary<string, string> english = Load("en");
        List<string> copied = [];
        foreach (string locale in s_locales.Skip(1))
        {
            Dictionary<string, string> other = Load(locale);
            foreach (string key in SftpSudoConsentLocaleKeys.All.Where(key => key != WordlessKey))
            {
                if (string.Equals(english[key], other[key], StringComparison.Ordinal))
                {
                    copied.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(copied);
    }

    [Fact]
    public void NoTextCarriesAnEmDashAnEnDashOrACurlyQuote()
    {
        char[] forbidden = ['\u2014', '\u2013', '\u2018', '\u2019', '\u201C', '\u201D', '\u00A0'];
        List<string> offenders = [];
        foreach (string locale in s_locales)
        {
            Dictionary<string, string> catalogue = Load(locale);
            foreach (string key in SftpSudoConsentLocaleKeys.All)
            {
                if (catalogue[key].IndexOfAny(forbidden) >= 0)
                {
                    offenders.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheFrenchQuestionsCarryTheirAccents()
    {
        Dictionary<string, string> french = Load("fr");

        Assert.Contains("é", french["SftpConfirmSudoDownloadTitle"], StringComparison.Ordinal);
        Assert.Contains("é", french["SftpConfirmSudoNewFolderTitle"], StringComparison.Ordinal);
        Assert.Contains("é", french["SftpSudoNoteNever"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(SftpRootActKind.Upload))]
    [InlineData(nameof(SftpRootActKind.Overwrite))]
    [InlineData(nameof(SftpRootActKind.InlineSave))]
    [InlineData(nameof(SftpRootActKind.EditOpen))]
    [InlineData(nameof(SftpRootActKind.NewFolder))]
    [InlineData(nameof(SftpRootActKind.Rename))]
    [InlineData(nameof(SftpRootActKind.PermissionChange))]
    [InlineData(nameof(SftpRootActKind.Download))]
    [InlineData(nameof(SftpRootActKind.Delete))]
    [InlineData(nameof(SftpRootActKind.Listing))]
    public void EveryActThatCanAskHasItsFourTexts_AndTwoDifferentAnswers(string actName)
    {
        SftpRootActKind act = Enum.Parse<SftpRootActKind>(actName);
        SftpSudoQuestionKeys keys = SftpSudoConsentLocaleKeys.For(act);

        foreach (string locale in s_locales)
        {
            Dictionary<string, string> catalogue = Load(locale);
            Assert.Contains(keys.Title, SftpSudoConsentLocaleKeys.All);
            Assert.Contains(keys.Message, SftpSudoConsentLocaleKeys.All);
            Assert.Contains(keys.Accept, SftpSudoConsentLocaleKeys.All);
            Assert.Contains(keys.Decline, SftpSudoConsentLocaleKeys.All);
            Assert.NotEqual(catalogue[keys.Accept], catalogue[keys.Decline]);
        }
    }

    [Fact]
    public void TheNotes_AreOneForEveryReasonRootWasNotRun_AndNoneForAnAllowedAct()
    {
        Assert.Equal(SftpSudoConsentLocaleKeys.NoteNever, SftpSudoConsentLocaleKeys.NoteFor(SftpSudoDecision.RefusedBySetting));
        Assert.Equal(SftpSudoConsentLocaleKeys.NoteDeclined, SftpSudoConsentLocaleKeys.NoteFor(SftpSudoDecision.Declined));
        Assert.Equal(SftpSudoConsentLocaleKeys.NoteNotAsked, SftpSudoConsentLocaleKeys.NoteFor(SftpSudoDecision.NotAsked));
        Assert.Null(SftpSudoConsentLocaleKeys.NoteFor(SftpSudoDecision.Allowed));
        Assert.Null(SftpSudoConsentLocaleKeys.NoteFor(SftpSudoDecision.AllowedAfterQuestion));
    }

    private static string[] Placeholders(string text)
        => s_placeholder.Matches(text).Select(match => match.Value).Distinct().Order().ToArray();

    private static Dictionary<string, string> Load(string locale)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", locale + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
}
