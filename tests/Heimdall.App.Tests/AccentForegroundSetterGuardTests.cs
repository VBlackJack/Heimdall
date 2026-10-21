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
/// Accent-coloured text is painted with AccentTextBrush, the accent the theme service derives to
/// reach 4.5 to 1 on the background and on a card (WCAG 1.4.3). The attribute sites moved to it
/// first; a Foreground setter in a style or a trigger kept the raw AccentBrush, which is 3.79 to 1
/// on the Drakul card.
/// </summary>
/// <remarks>
/// A Foreground setter may keep AccentBrush only where the foreground is not text: the fill of a
/// progress bar or a slider, or a status glyph whose siblings use the non-text status brushes.
/// Each of those sites is listed with its reason, and a listed site that no longer exists fails.
/// </remarks>
public sealed class AccentForegroundSetterGuardTests
{
    private const string RawAccent = "{DynamicResource AccentBrush}";

    /// <summary>Foreground setters found under src/Heimdall.App on 2026-10-21: 159. A floor for the walk.</summary>
    private const int MinimumForegroundSetterCount = 140;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The Foreground setters that keep AccentBrush, by file and target, with the reason.</summary>
    private static readonly Dictionary<string, string> NonTextSites = new(StringComparer.Ordinal)
    {
        ["CommonControls.xaml|ThemedProgressBarStyle"] = "the progress bar fill",
        ["CommonControls.xaml|ThemedSliderStyle"] = "the slider track and thumb fill",
        ["CommonControls.xaml|ToolLoadingBarStyle"] = "the indeterminate progress bar fill",
        ["CommonControls.xaml|ToolDeterminateProgressBarStyle"] = "the progress bar fill",
        ["ServerDialog.xaml|SshTestChipIconStyle"] = "the in-progress status glyph; the other states use SuccessBrush and ErrorBrush",
        ["EmbeddedSftpView.xaml|JobGlyph"] = "the running transfer status glyph",
    };

    [Fact]
    public void AnAccentForegroundSetter_IsANonTextSite()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        int foregroundSetters = 0;
        HashSet<string> accentSites = new(StringComparer.Ordinal);
        List<string> text = [];

        foreach (string path in SourceFileEnumeration.EnumerateFiles(root, "*.xaml"))
        {
            foreach (XElement setter in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants()
                .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Foreground"))
            {
                foregroundSetters++;
                if ((string?)setter.Attribute("Value") != RawAccent)
                {
                    continue;
                }

                string site = $"{Path.GetFileName(path)}|{Target(setter)}";
                accentSites.Add(site);
                if (!NonTextSites.ContainsKey(site))
                {
                    text.Add($"{site} (line {((System.Xml.IXmlLineInfo)setter).LineNumber})");
                }
            }
        }

        Assert.True(
            foregroundSetters >= MinimumForegroundSetterCount,
            $"the walk found {foregroundSetters} Foreground setters, under the floor of {MinimumForegroundSetterCount}");
        Assert.True(
            text.Count == 0,
            "These Foreground setters paint text with AccentBrush; use AccentTextBrush, or list the site "
            + "in " + nameof(NonTextSites) + " with the reason it is not text: " + string.Join(", ", text));

        List<string> stale = NonTextSites.Keys.Where(site => !accentSites.Contains(site)).ToList();
        Assert.True(
            stale.Count == 0,
            "These sites no longer set a Foreground to AccentBrush; drop them from "
            + nameof(NonTextSites) + ": " + string.Join(", ", stale));
    }

    /// <summary>The setter's TargetName, else the key or the target type of its enclosing style.</summary>
    private static string Target(XElement setter)
    {
        if ((string?)setter.Attribute("TargetName") is { } targetName)
        {
            return targetName;
        }

        XElement? style = setter.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Style");
        return (string?)style?.Attribute(Xaml + "Key")
            ?? (string?)style?.Attribute("TargetType")
            ?? "(no style)";
    }
}
