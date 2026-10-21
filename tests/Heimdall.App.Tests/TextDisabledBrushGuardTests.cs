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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Text a user is expected to read is never painted with <c>TextDisabledBrush</c>.
/// </summary>
/// <remarks>
/// <para>The bridge draws that brush as the secondary text colour at 55 percent: 3.33 to 1 on
/// Drakul and 2.40 to 1 on Parchment and Folio, under the 4.5 WCAG 1.4.3 asks for text. It was
/// the foreground of two dozen hints, captions and placeholders. Readable secondary text is
/// <c>TextSecondaryBrush</c>; the disabled brush is for a control whose <c>IsEnabled</c> is false,
/// set from a trigger on that state, and for the few state glyphs that are dimmed on purpose,
/// which are named below.</para>
/// <para>Kept in its own file: it reads production markup.</para>
/// </remarks>
public sealed class TextDisabledBrushGuardTests
{
    private const string DisabledBrush = "{DynamicResource TextDisabledBrush}";
    private const string ForegroundProperty = "Foreground";

    /// <summary>
    /// Far under the number of markup files the application carries, far above what a sweep of
    /// the wrong directory would find.
    /// </summary>
    private const int MinimumFilesRead = 100;

    /// <summary>The disabled-state setters the shared control styles carry today.</summary>
    private const int MinimumDisabledStateSetters = 4;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Sites that keep the disabled brush, by file and by the x:Name of the element or the x:Key
    /// of the style that carries it, each with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string File, string Site), string> Kept =
        new Dictionary<(string File, string Site), string>
        {
            [("EmbeddedRdpView.xaml", "HealthDotGlyph")] =
                "the health dot's state glyph: code-behind recolours it per state, unknown is dimmed on purpose",
            [("CommonControls.xaml", "ToolEmptyStateIconStyle")] =
                "the decorative icon glyph above an empty-state message, not the message itself",
        };

    [Fact]
    public void NoReadableTextIsPaintedWithTheDisabledBrush()
    {
        int filesRead = 0;
        int disabledStateSetters = 0;
        HashSet<(string File, string Site)> keptSeen = [];
        List<string> offenders = [];

        foreach (string path in MarkupFiles())
        {
            filesRead++;
            string file = Path.GetFileName(path);
            Sweep sweep = Classify(XDocument.Load(path, LoadOptions.SetLineInfo));
            disabledStateSetters += sweep.DisabledStateSetters;

            foreach (string site in sweep.Offenders)
            {
                if (Kept.ContainsKey((file, site)))
                {
                    keptSeen.Add((file, site));
                }
                else
                {
                    offenders.Add($"{file}: {site}");
                }
            }
        }

        Assert.True(filesRead >= MinimumFilesRead, $"only {filesRead} markup files were read");
        Assert.True(
            disabledStateSetters >= MinimumDisabledStateSetters,
            $"only {disabledStateSetters} disabled-state setters were seen, so the trigger test measured nothing");
        Assert.True(
            offenders.Count == 0,
            "readable text painted with TextDisabledBrush; use TextSecondaryBrush:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
        Assert.True(
            keptSeen.Count == Kept.Count,
            "kept sites no longer found (remove them from the list): "
            + string.Join(", ", Kept.Keys.Except(keptSeen)));
    }

    /// <summary>
    /// The control the guard needs: a text block and a plain style setter are flagged, a setter in
    /// an <c>IsEnabled</c> false trigger is not.
    /// </summary>
    [Fact]
    public void TheSweepFlagsTextAndStyleSettersButNotTheDisabledState()
    {
        const string Markup = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style x:Key="HintStyle" TargetType="TextBlock">
                    <Setter Property="Foreground" Value="{DynamicResource TextDisabledBrush}"/>
                </Style>
                <Style x:Key="ButtonLook" TargetType="Button">
                    <Style.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource TextDisabledBrush}"/>
                        </Trigger>
                    </Style.Triggers>
                </Style>
                <TextBlock x:Key="Sample" x:Name="Hint" Foreground="{DynamicResource TextDisabledBrush}"/>
                <Border x:Key="Fill" Background="{DynamicResource TextDisabledBrush}"/>
            </ResourceDictionary>
            """;

        Sweep sweep = Classify(XDocument.Parse(Markup));

        Assert.Equal(["HintStyle", "Hint"], sweep.Offenders);
        Assert.Equal(1, sweep.DisabledStateSetters);
    }

    private static Sweep Classify(XDocument markup)
    {
        List<string> offenders = [];
        int disabledStateSetters = 0;

        foreach (XElement element in markup.Descendants())
        {
            if (element.Name.LocalName == "Setter")
            {
                if ((string?)element.Attribute("Property") != ForegroundProperty
                    || !IsDisabledBrush((string?)element.Attribute("Value")))
                {
                    continue;
                }

                if (IsInADisabledStateTrigger(element))
                {
                    disabledStateSetters++;
                    continue;
                }

                offenders.Add(SiteOf(element));
                continue;
            }

            if (IsDisabledBrush((string?)element.Attribute(ForegroundProperty)))
            {
                offenders.Add(SiteOf(element));
            }
        }

        return new Sweep(offenders, disabledStateSetters);
    }

    private static bool IsDisabledBrush(string? value) =>
        value is not null && string.Equals(value.Trim(), DisabledBrush, StringComparison.Ordinal);

    private static bool IsInADisabledStateTrigger(XElement setter)
    {
        XElement? trigger = setter.Ancestors().FirstOrDefault(e => e.Name.LocalName is "Trigger" or "DataTrigger");
        if (trigger is null)
        {
            return false;
        }

        bool onEnabled = trigger.Name.LocalName == "Trigger"
            ? (string?)trigger.Attribute("Property") == "IsEnabled"
            : ((string?)trigger.Attribute("Binding"))?.Contains("IsEnabled", StringComparison.Ordinal) == true;
        return onEnabled && string.Equals((string?)trigger.Attribute("Value"), "False", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The element's x:Name, or for a setter the x:Key of its style, or the element name with its
    /// line when neither exists.
    /// </summary>
    private static string SiteOf(XElement element)
    {
        XElement named = element.Name.LocalName == "Setter"
            ? element.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Style") ?? element
            : element;
        string? identity = (string?)named.Attribute(Xaml + "Name") ?? (string?)named.Attribute(Xaml + "Key");
        return identity ?? $"{element.Name.LocalName} at line {((System.Xml.IXmlLineInfo)element).LineNumber}";
    }

    private static IEnumerable<string> MarkupFiles() =>
        SourceFileEnumeration.EnumerateFiles(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App"), "*.xaml");

    private sealed record Sweep(IReadOnlyList<string> Offenders, int DisabledStateSetters);
}
