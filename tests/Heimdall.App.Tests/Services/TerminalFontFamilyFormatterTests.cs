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

using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Services;

public sealed class TerminalFontFamilyFormatterTests
{
    private const string Tail = "\"Courier New\", monospace";

    [Fact]
    public void BuildCssFontList_QuotesEachName_AndAppendsTheFallbacks()
    {
        string list = TerminalFontFamilyFormatter.BuildCssFontList("'Cascadia Code', Consolas");

        Assert.Equal("\"Cascadia Code\", \"Consolas\", " + Tail, list);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,")]
    public void BuildCssFontList_WithNoUsableName_FallsBackToTheSettingsDefault(string? input)
    {
        string list = TerminalFontFamilyFormatter.BuildCssFontList(input);

        Assert.Equal($"\"{AppSettings.DefaultTerminalFontFamily}\", " + Tail, list);
    }

    [Theory]
    [InlineData("Evil\" onload=\"x")]
    [InlineData("a\nb")]
    [InlineData("</script>")]
    [InlineData("lt<only")]
    [InlineData("gt>only")]
    [InlineData("back\\slash")]
    [InlineData("semi;colon")]
    [InlineData("brace{open")]
    [InlineData("a/*comment*/b")]
    public void BuildCssFontList_DropsNamesWithDangerousCharacters(string hostile)
    {
        string list = TerminalFontFamilyFormatter.BuildCssFontList(hostile + ", Consolas");

        Assert.Equal("\"Consolas\", " + Tail, list);
    }

    [Theory]
    [InlineData("serif")]
    [InlineData("Sans-Serif")]
    [InlineData("ui-monospace")]
    [InlineData("emoji")]
    public void BuildCssFontList_LeavesGenericFamilyKeywordsUnquoted(string keyword)
    {
        string list = TerminalFontFamilyFormatter.BuildCssFontList("Consolas, " + keyword);

        Assert.Equal("\"Consolas\", " + keyword + ", " + Tail, list);
    }

    [Fact]
    public void BuildCssFontList_KeepsUnicodeLetters()
    {
        string list = TerminalFontFamilyFormatter.BuildCssFontList("\u00C9criture Mono");

        Assert.StartsWith("\"\u00C9criture Mono\", ", list, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ProducesAJsonLiteralThatParsesBack_WithNoRawMarkupCharacters()
    {
        string html = $"[{TerminalFontFamilyFormatter.MarkerJson}]";

        string applied = TerminalFontFamilyFormatter.Apply(html, "'Cascadia Code', </script>, \u00C9criture");

        string literal = applied[1..^1];
        Assert.DoesNotContain('<', literal);
        Assert.DoesNotContain('\'', literal);
        Assert.DoesNotContain('\n', literal);
        using JsonDocument document = JsonDocument.Parse(literal);
        Assert.Equal(
            TerminalFontFamilyFormatter.BuildCssFontList("'Cascadia Code', </script>, \u00C9criture"),
            document.RootElement.GetString());
    }

    [Fact]
    public void Apply_KeepsGenericKeywordsBareInTheJsonForm()
    {
        string applied = TerminalFontFamilyFormatter.Apply(
            $"[{TerminalFontFamilyFormatter.MarkerJson}]",
            "serif");

        using JsonDocument document = JsonDocument.Parse(applied[1..^1]);
        Assert.StartsWith("serif, ", document.RootElement.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_OnTheRealTerminalPage_LeavesNoFontPlaceholder_AndInjectsBothForms()
    {
        string applied = TerminalFontFamilyFormatter.Apply(TerminalAssetsLoader.TerminalHtml, "Cascadia Code");

        Assert.DoesNotContain("{{TERMINAL_FONT_FAMILY", applied, StringComparison.Ordinal);
        Assert.Contains("font-family: \"Cascadia Code\", " + Tail + ";", applied, StringComparison.Ordinal);
        Assert.Contains(
            "fontFamily: " + JsonSerializer.Serialize("\"Cascadia Code\", " + Tail) + ",",
            applied,
            StringComparison.Ordinal);
    }
}
