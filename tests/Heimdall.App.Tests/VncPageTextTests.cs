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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The embedded VNC page shows only words the host localized. It used to carry the loading label,
/// the title of a failed library and its message in English, and only three of its strings came
/// from the host.
/// </summary>
/// <remarks>
/// Proved against two mutants, each red here and green once restored: dropping one entry from
/// <see cref="VncPageText.Build"/> (the per-language tests name the field), and renaming a field
/// the page reads (the page-and-host test names the difference).
/// </remarks>
public sealed class VncPageTextTests
{
    private const string BootstrapPrefix = "window." + VncPageText.BootstrapGlobal + " = ";

    private static readonly (string Key, string Field)[] Strings =
    [
        (VncPageText.KeyDisconnected, VncPageText.FieldDisconnected),
        (VncPageText.KeyConnectionLost, VncPageText.FieldConnectionLost),
        (VncPageText.KeyInitFailed, VncPageText.FieldInitFailed),
        (VncPageText.KeyLoading, VncPageText.FieldLoading),
        (VncPageText.KeyLibraryFailedTitle, VncPageText.FieldLibraryFailedTitle),
        (VncPageText.KeyLibraryFailedMessage, VncPageText.FieldLibraryFailedMessage),
    ];

    /// <summary>What the page used to show in English on its own.</summary>
    private static readonly string[] RetiredEnglishLiterals =
    [
        "Connecting to VNC server",
        "Connection Failed",
        "noVNC Library Unavailable",
        "Failed to load the noVNC library",
    ];

    /// <summary>U+2028 ends a line in JavaScript source; written by code so no editor swallows it.</summary>
    private static readonly string LineSeparator = ((char)0x2028).ToString();

    public static TheoryData<string> Languages => new() { "en", "fr", "es" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Build_CarriesEveryStringInTheLanguage(string language)
    {
        Dictionary<string, string> catalogue = ReadCatalogue(language);

        IReadOnlyDictionary<string, string?> payload = VncPageText.Build(key => catalogue[key]);

        foreach ((string key, string field) in Strings)
        {
            payload.Should().ContainKey(field, $"the page reads {field} from the host");
            payload[field].Should().Be(catalogue[key], $"{field} is the {language} text of {key}");
            payload[field].Should().NotBeNullOrWhiteSpace();
        }

        payload.Keys.Should().BeEquivalentTo(Strings.Select(entry => entry.Field));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void BootstrapScript_HandsTheSamePayloadToThePageBeforeItsScripts(string language)
    {
        Dictionary<string, string> catalogue = ReadCatalogue(language);

        string script = VncPageText.BuildBootstrapScript(key => catalogue[key]);

        script.Should().StartWith(BootstrapPrefix);
        script.Should().EndWith(";");
        string json = script[BootstrapPrefix.Length..^1];
        var handed = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        foreach ((string key, string field) in Strings)
        {
            handed.Should().ContainKey(field);
            handed[field].Should().Be(catalogue[key]);
        }
    }

    [Fact]
    public void BootstrapScript_CannotBeEndedByATranslation()
    {
        string script = VncPageText.BuildBootstrapScript(_ => "</script><script>alert(1)</script> " + LineSeparator);

        script.Should().NotContain("<");
        script.Should().NotContain(">");
        script.Should().NotContain(LineSeparator);
    }

    [Fact]
    public void Build_KeepsAMissingTranslationNullSoThePageKeepsItsOwn()
    {
        IReadOnlyDictionary<string, string?> payload = VncPageText.Build(_ => null);

        payload.Should().HaveCount(Strings.Length);
        payload.Values.Should().OnlyContain(value => value == null);
    }

    [Fact]
    public void ThePage_CarriesNoEnglishWordOfItsOwn()
    {
        string page = ReadPage();

        foreach (string literal in RetiredEnglishLiterals)
        {
            page.Should().NotContain(literal);
        }
    }

    /// <summary>
    /// The host fills fields the page has to read, and the page reads fields the host has to fill.
    /// A field on one side only is a string that never arrives or a payload nobody reads.
    /// </summary>
    [Fact]
    public void ThePageReadsExactlyTheFieldsTheHostFills()
    {
        string page = ReadPage();

        IEnumerable<string> read = Regex
            .Matches(page, @"\b(?:bootstrapText|source)\.(\w+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

        read.Should().BeEquivalentTo(Strings.Select(entry => entry.Field));
    }

    [Fact]
    public void TheBootstrapGlobal_IsTheNameThePageReads()
    {
        string page = ReadPage();

        Regex.IsMatch(page, @"window\." + VncPageText.BootstrapGlobal + @"\b")
            .Should().BeTrue("the host fills a global the page never reads");
    }

    private static string ReadPage() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "vnc.html"));

    private static Dictionary<string, string> ReadCatalogue(string language)
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "locales", language + ".json");
        var catalogue = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        catalogue.Should().NotBeNull();
        return catalogue!;
    }
}
