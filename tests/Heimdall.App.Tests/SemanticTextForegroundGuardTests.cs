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
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Theming;

namespace Heimdall.App.Tests;

/// <summary>
/// Semantic and accent text is painted with the derived <c>*TextBrush</c> keys, never with the raw
/// <c>ErrorBrush</c>, <c>WarningBrush</c>, <c>SuccessBrush</c>, <c>InfoBrush</c> or <c>AccentBrush</c>.
/// The raw brushes carry the theme colour for fills, dots and borders; as text the Drakul error red
/// is 2.91 to 1 on a card and the accent 3.79, under WCAG 1.4.3.
/// </summary>
/// <remarks>
/// <para>Four doors lead a raw brush to a text foreground, and each is walked: a Foreground attribute
/// or setter in XAML; a converter bound to a Foreground; a view-model key bound to a Foreground
/// through <c>ResourceKeyToBrushConverter</c>; and a C# statement that assigns a Foreground.</para>
/// <para>Not seen: a brush a helper method returns and a caller then assigns to a Foreground in a
/// statement of its own. The sites of that shape on 2026-10-25 were moved by hand.</para>
/// <para>A ProgressBar's or a Slider's Foreground is its fill, not text, and keeps the raw accent.</para>
/// </remarks>
public sealed class SemanticTextForegroundGuardTests
{
    /// <summary>Foreground attributes and setters under src/Heimdall.App on 2026-10-25: about 1700. A floor for the walk.</summary>
    private const int MinimumForegroundSiteCount = 1200;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ForegroundProperties =
        ["Foreground", "TextElement.Foreground", "TextBlock.Foreground", "Control.Foreground"];

    /// <summary>Controls whose Foreground is a fill rather than text.</summary>
    private static readonly HashSet<string> FillForegrounds = new(StringComparer.Ordinal) { "ProgressBar", "Slider" };

    private static readonly Regex RawLiteral = new(
        "\"(" + string.Join("|", SemanticTextBrushes.RawKeys.Select(Regex.Escape)) + ")\"",
        RegexOptions.CultureInvariant);

    private static readonly Regex ResourceReference = new(
        @"^\{(?:Dynamic|Static)Resource\s+(\w+)\}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Comment = new(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string AppRoot => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");

    [Fact]
    public void NoXamlForegroundPaintsTextWithARawSemanticBrush()
    {
        int sites = 0;
        List<string> raw = [];
        foreach (string path in XamlFiles())
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            sites += document.Descendants().Count(IsForegroundSite);
            raw.AddRange(RawXamlForegrounds(document).Select(site => $"{Path.GetFileName(path)}:{site}"));
        }

        Assert.True(sites >= MinimumForegroundSiteCount, $"the walk found {sites} Foreground sites, under the floor of {MinimumForegroundSiteCount}");
        Assert.True(raw.Count == 0, "Foregrounds painted with a raw semantic brush; use its *TextBrush: " + string.Join(", ", raw));
    }

    [Fact]
    public void NoConverterBoundToAForegroundReturnsARawSemanticBrush()
    {
        Dictionary<string, string> converterClasses = ConverterClassesByKey();
        HashSet<string> foregroundConverters = new(StringComparer.Ordinal);
        foreach (string path in XamlFiles())
        {
            foreach (string key in ForegroundConverterKeys(XDocument.Load(path)))
            {
                foregroundConverters.Add(key);
            }
        }

        Assert.Contains("SmbSeverityBrush", foregroundConverters);
        List<string> raw = [];
        foreach (string key in foregroundConverters.Where(converterClasses.ContainsKey))
        {
            string className = converterClasses[key];
            string source = ClassSource(className);
            if (RawLiteral.IsMatch(Comment.Replace(source, string.Empty)))
            {
                raw.Add($"{key} ({className})");
            }
        }

        Assert.True(raw.Count == 0, "converters bound to a Foreground return a raw semantic brush key: " + string.Join(", ", raw));
    }

    [Fact]
    public void NoViewModelKeyBoundToAForegroundIsARawSemanticBrush()
    {
        HashSet<string> keyProperties = new(StringComparer.Ordinal);
        foreach (string path in XamlFiles())
        {
            foreach (string property in ForegroundKeyProperties(XDocument.Load(path)))
            {
                keyProperties.Add(property);
            }
        }

        Assert.Contains("FileStatusBrushKey", keyProperties);
        List<string> raw = [];
        foreach (string path in CSharpFiles())
        {
            foreach (string statement in Statements(File.ReadAllText(path)))
            {
                if (RawLiteral.IsMatch(statement) && keyProperties.Any(property => AssignsProperty(statement, property)))
                {
                    raw.Add($"{Path.GetFileName(path)}: {Shorten(statement)}");
                }
            }
        }

        Assert.True(raw.Count == 0, "keys bound to a Foreground are raw semantic brushes: " + string.Join(Environment.NewLine, raw));
    }

    [Fact]
    public void NoCSharpStatementPaintsAForegroundWithARawSemanticBrush()
    {
        List<string> raw = [];
        foreach (string path in CSharpFiles())
        {
            raw.AddRange(RawCSharpForegrounds(File.ReadAllText(path)).Select(statement => $"{Path.GetFileName(path)}: {statement}"));
        }

        Assert.True(raw.Count == 0, "statements paint a Foreground with a raw semantic brush: " + string.Join(Environment.NewLine, raw));
    }

    /// <summary>The positive control: each scanner reports the shapes it exists to catch, and only those.</summary>
    [Fact]
    public void TheScannersReportARawForegroundAndLeaveAFill()
    {
        XDocument xaml = XDocument.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBlock Foreground="{DynamicResource ErrorBrush}"/>
              <TextBlock Foreground="{DynamicResource ErrorTextBrush}"/>
              <ProgressBar Foreground="{DynamicResource AccentBrush}"/>
              <Border Background="{DynamicResource ErrorBrush}"/>
              <TextBlock><TextBlock.Style><Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource WarningBrush}"/>
              </Style></TextBlock.Style></TextBlock>
            </Grid>
            """,
            LoadOptions.SetLineInfo);
        Assert.Equal(2, RawXamlForegrounds(xaml).Count);

        const string Code =
            """
            BtnStop.Foreground = (Brush)FindResource("ErrorBrush");
            label.SetResourceReference(TextBlock.ForegroundProperty,
                ok ? "SuccessBrush" : "TextSecondaryBrush");
            StrengthBar.Background = (Brush)FindResource("ErrorBrush");
            BtnStop.Foreground = (Brush)FindResource("ErrorTextBrush");
            // BtnStop.Foreground = (Brush)FindResource("ErrorBrush");
            """;
        Assert.Equal(2, RawCSharpForegrounds(Code).Count);

        Assert.True(AssignsProperty("FileStatusBrushKey = \"ErrorBrush\"", "FileStatusBrushKey"));
        Assert.False(AssignsProperty("OtherFileStatusBrushKey = \"ErrorBrush\"", "FileStatusBrushKey"));
    }

    private static IEnumerable<string> XamlFiles() => SourceFileEnumeration.EnumerateFiles(AppRoot, "*.xaml");

    private static IEnumerable<string> CSharpFiles() => SourceFileEnumeration.EnumerateFiles(AppRoot, "*.cs")
        .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsForegroundSite(XElement element) =>
        element.Name.LocalName == "Setter"
            ? ForegroundProperties.Contains((string?)element.Attribute("Property"))
            : ForegroundProperties.Any(property => element.Attribute(property) is not null);

    /// <summary>Every Foreground attribute or setter of the document set to a raw semantic brush.</summary>
    private static List<string> RawXamlForegrounds(XDocument document)
    {
        List<string> raw = [];
        foreach (XElement element in document.Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            if (element.Name.LocalName == "Setter")
            {
                if (ForegroundProperties.Contains((string?)element.Attribute("Property"))
                    && IsRaw(element.Attribute("Value")?.Value)
                    && !FillForegrounds.Contains(SetterTarget(element)))
                {
                    raw.Add($"{line} (setter)");
                }

                continue;
            }

            if (FillForegrounds.Contains(element.Name.LocalName))
            {
                continue;
            }

            foreach (string property in ForegroundProperties)
            {
                if (IsRaw(element.Attribute(property)?.Value))
                {
                    raw.Add($"{line} ({element.Name.LocalName}.{property})");
                }
            }
        }

        return raw;
    }

    private static bool IsRaw(string? value) =>
        value is not null
        && ResourceReference.Match(value.Trim()) is { Success: true } match
        && SemanticTextBrushes.RawKeys.Contains(match.Groups[1].Value);

    /// <summary>The element type a setter paints: its TargetName's element, else its style's or template's target type.</summary>
    private static string SetterTarget(XElement setter)
    {
        string? targetName = (string?)setter.Attribute("TargetName");
        foreach (XElement ancestor in setter.Ancestors())
        {
            if (targetName is not null && ancestor.Name.LocalName == "ControlTemplate")
            {
                XElement? named = ancestor.Descendants().FirstOrDefault(e => (string?)e.Attribute(Xaml + "Name") == targetName);
                if (named is not null)
                {
                    return named.Name.LocalName;
                }
            }

            if (ancestor.Name.LocalName is "Style" or "ControlTemplate" && (string?)ancestor.Attribute("TargetType") is { } targetType)
            {
                return targetType.Replace("{x:Type ", string.Empty, StringComparison.Ordinal).TrimEnd('}').Split(':')[^1];
            }
        }

        return string.Empty;
    }

    /// <summary>The resource keys of converters a Foreground binding of the document goes through.</summary>
    private static IEnumerable<string> ForegroundConverterKeys(XDocument document)
    {
        Regex converter = new(@"Converter=\{StaticResource\s+(\w+)\}", RegexOptions.CultureInvariant);
        foreach (XElement element in document.Descendants())
        {
            foreach (string property in ForegroundProperties)
            {
                if ((string?)element.Attribute(property) is { } value && value.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    foreach (Match match in converter.Matches(value))
                    {
                        yield return match.Groups[1].Value;
                    }
                }
            }

            if (element.Name.LocalName.EndsWith(".Foreground", StringComparison.Ordinal))
            {
                foreach (XElement binding in element.Descendants().Where(e => e.Name.LocalName is "Binding" or "MultiBinding"))
                {
                    if ((string?)binding.Attribute("Converter") is { } value && converter.Match(value) is { Success: true } match)
                    {
                        yield return match.Groups[1].Value;
                    }
                }
            }
        }
    }

    /// <summary>The view-model properties whose key a Foreground resolves through ResourceKeyToBrushConverter.</summary>
    private static IEnumerable<string> ForegroundKeyProperties(XDocument document)
    {
        const string KeyConverter = "ResourceKeyToBrushConverter";
        Regex path = new(@"^\{Binding\s+(?:Path=)?([\w.]+)", RegexOptions.CultureInvariant);
        foreach (XElement element in document.Descendants())
        {
            foreach (string property in ForegroundProperties)
            {
                if ((string?)element.Attribute(property) is { } value
                    && value.Contains(KeyConverter, StringComparison.Ordinal)
                    && path.Match(value) is { Success: true } match)
                {
                    yield return match.Groups[1].Value.Split('.')[^1];
                }
            }

            if (element.Name.LocalName.EndsWith(".Foreground", StringComparison.Ordinal)
                && element.Elements().FirstOrDefault(e => e.Name.LocalName == "MultiBinding") is { } multi
                && ((string?)multi.Attribute("Converter"))?.Contains(KeyConverter, StringComparison.Ordinal) == true
                && multi.Elements().FirstOrDefault(e => e.Name.LocalName == "Binding") is { } first
                && first.Attribute("Converter") is null
                && (string?)first.Attribute("Path") is { } keyPath)
            {
                yield return keyPath.Split('.')[^1];
            }
        }
    }

    /// <summary>The converter class declared under each resource key, across the app's XAML.</summary>
    private static Dictionary<string, string> ConverterClassesByKey()
    {
        Dictionary<string, string> classes = new(StringComparer.Ordinal);
        foreach (string path in XamlFiles())
        {
            foreach (XElement element in XDocument.Load(path).Descendants())
            {
                if ((string?)element.Attribute(Xaml + "Key") is { } key && element.Name.LocalName.EndsWith("Converter", StringComparison.Ordinal))
                {
                    classes.TryAdd(key, element.Name.LocalName);
                }
            }
        }

        return classes;
    }

    /// <summary>
    /// The text of one class, from its declaration to the next class declaration: a file may hold a
    /// text converter beside a fill converter.
    /// </summary>
    private static string ClassSource(string className)
    {
        Regex declaration = new($@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant);
        string file = Assert.Single(CSharpFiles(), path => declaration.IsMatch(File.ReadAllText(path)));
        string source = File.ReadAllText(file);
        int start = declaration.Match(source).Index;
        Match next = new Regex(@"\bclass\s+\w+", RegexOptions.CultureInvariant).Match(source, start + 1);
        return next.Success ? source[start..next.Index] : source[start..];
    }

    /// <summary>The C# statements of a source that touch a Foreground and name a raw semantic brush.</summary>
    private static List<string> RawCSharpForegrounds(string source) =>
        Statements(source)
            .Where(statement => statement.Contains("Foreground", StringComparison.Ordinal) && RawLiteral.IsMatch(statement))
            .Select(Shorten)
            .ToList();

    /// <summary>A source cut at each semicolon, comments removed.</summary>
    private static IEnumerable<string> Statements(string source) =>
        Comment.Replace(source, string.Empty).Split(';').Select(statement => statement.Trim()).Where(statement => statement.Length > 0);

    private static bool AssignsProperty(string statement, string property) =>
        Regex.IsMatch(
            statement,
            $@"(?<![\w.])(?:{Regex.Escape(property)}|_{Regex.Escape(char.ToLowerInvariant(property[0]) + property[1..])})\s*(?:=>|=(?!=))",
            RegexOptions.CultureInvariant);

    private static string Shorten(string statement)
    {
        string flat = Regex.Replace(statement, @"\s+", " ");
        int at = RawLiteral.Match(flat).Index;
        int start = Math.Max(0, at - 90);
        return flat[start..Math.Min(flat.Length, at + 30)];
    }
}
