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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Reads one region of the shell markup, so a guard states which surface it is talking about.
/// </summary>
/// <remarks>
/// A repository-wide search would answer yes to an attribute written anywhere in four thousand
/// lines, which is how a markup oracle stays green through the defect it was written for.
/// </remarks>
internal static class MainWindowMarkup
{
    internal static string Text() => File.ReadAllText(Path.Combine(
        SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

    /// <summary>The markup from the first occurrence of <paramref name="from"/> to the first
    /// <paramref name="to"/> that follows it, both included.</summary>
    internal static string Block(string from, string to)
    {
        string markup = Text();

        int start = markup.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the markup no longer contains \"{from}\"");

        int end = markup.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"the markup no longer closes \"{from}\" with \"{to}\"");

        return markup[start..(end + to.Length)];
    }
}

/// <summary>
/// What the sessions sidebar names, and what it explains.
/// </summary>
/// <remarks>
/// Both claims here are about text the user reads and nothing else observes: a menu whose entries
/// were internal handler keys, and a hover that answered with the word being hovered. Neither can
/// fail a binding, a build or any view-model oracle, so the markup is where they are measured.
/// </remarks>
public sealed class SessionTreeSidebarMarkupTests
{
    private const int MinExpectedProtocolHandlers = 9;

    /// <summary>
    /// Every protocol the funnel can list is named by the product, not by its handler key.
    /// </summary>
    /// <remarks>
    /// The checklist was bound straight to the registered handler key, so it read CITRIX, FTP,
    /// LOCAL, RDP, SFTP, SSH, TELNET, VNC, WINRM. LOCAL and WINRM name nothing a reader knows.
    /// <para>The census starts at the handlers rather than at the mapping on purpose: asking "is
    /// every entry written here still needed" cannot notice a tenth handler registered with no
    /// name, which is the way this defect comes back.</para>
    /// <para>It used to read the nine DataTriggers in MainWindow.xaml. Translating only the
    /// visible header left the accessible name quoting the raw key, so the two channels disagreed;
    /// the mapping now lives in ConnectionTypeCatalog and feeds both. This guard follows it there,
    /// and gained the half the markup scan could not have: that the key resolves in BOTH locales.
    /// A name that exists only in English is the same defect for half the users.</para>
    /// </remarks>
    [Fact]
    public void EveryRegisteredProtocolIsNamedByTheProductInBothLocales()
    {
        IReadOnlyList<string> protocols = RegisteredProtocolTokens();

        Assert.True(
            protocols.Count >= MinExpectedProtocolHandlers,
            $"only {protocols.Count} protocol handlers were found, so the census is no longer "
                + "reading what it thinks it is");

        IReadOnlyDictionary<string, string> english = ReadLocale("en");
        IReadOnlyDictionary<string, string> french = ReadLocale("fr");

        List<string> problems = [];
        foreach (string protocol in protocols)
        {
            string? key = ConnectionTypeCatalog.GetDisplayNameKey(protocol);
            if (key is null)
            {
                problems.Add(
                    $"the protocol filter would offer the raw handler key \"{protocol}\" - add it "
                        + "to ConnectionTypeCatalog.DisplayNameKeys with a localized name");
                continue;
            }

            if (!english.ContainsKey(key))
            {
                problems.Add($"{protocol} maps to '{key}', which is missing from en.json");
            }

            if (!french.ContainsKey(key))
            {
                problems.Add($"{protocol} maps to '{key}', which is missing from fr.json");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The checklist renders the resolved name, and the fallback to the raw key survives.
    /// </summary>
    /// <remarks>
    /// A tenth handler registered with no name must still be offered under its key rather than as
    /// a blank row, for as long as the guard above is being answered.
    /// </remarks>
    [Fact]
    public void TheProtocolChecklistBindsTheResolvedDisplayName()
    {
        string filterMenu = MainWindowMarkup.Block(
            "ItemsSource=\"{Binding ServerList.ProtocolFilters}\"",
            "</MenuItem.ItemContainerStyle>");

        Assert.Contains("<Setter Property=\"Header\" Value=\"{Binding DisplayName}\"/>", filterMenu);
        Assert.DoesNotContain("Value=\"{Binding Protocol}\"", filterMenu);
    }

    private static IReadOnlyDictionary<string, string> ReadLocale(string language)
    {
        string path = Path.Combine(
            SettingsNumericFields.FindRepoRoot(), "locales", language + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                table[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return table;
    }

    /// <summary>
    /// The (No Group) drop zone explains itself instead of repeating its own caption.
    /// </summary>
    /// <remarks>
    /// It is the only standing hint that ungrouped sessions have a destination, and it is on
    /// screen before the user owns a session to drag onto it. A tooltip that answers with the
    /// caption teaches nothing, so hovering the one affordance that needed explaining returned
    /// the words already printed on the pill.
    /// </remarks>
    [Fact]
    public void TheNoGroupDropZoneTooltipSaysSomethingItsCaptionDoesNot()
    {
        string dropZone = MainWindowMarkup.Block(
            "<Border x:Name=\"SessionTreeNoGroupDropZone\"",
            "</Border>");

        Match tooltip = Regex.Match(dropZone, "ToolTip=\"\\{loc:Translate ([A-Za-z0-9_]+)\\}\"");
        Assert.True(tooltip.Success, "the drop zone lost its tooltip entirely");

        // The word boundary keeps attributes that merely end in "Text=" out of the caption census;
        // only what the pill prints counts as repeating itself.
        HashSet<string> visibleText = Regex
            .Matches(dropZone, "\\bText=\"\\{loc:Translate ([A-Za-z0-9_]+)\\}\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            visibleText.Count > 0,
            "no caption was found inside the drop zone, so this compared nothing");

        Assert.False(
            visibleText.Contains(tooltip.Groups[1].Value),
            $"the drop zone's tooltip resolves {tooltip.Groups[1].Value}, the same key as its own "
                + "caption, so hovering the pill returns the words already printed on it");
    }

    /// <summary>Every protocol token a registered handler answers with.</summary>
    private static IReadOnlyList<string> RegisteredProtocolTokens()
    {
        string handlers = Path.Combine(
            SettingsNumericFields.FindRepoRoot(),
            "src", "Heimdall.App", "Services", "Handlers");

        return
        [
            .. Directory
                .EnumerateFiles(handlers, "*.cs", SearchOption.TopDirectoryOnly)
                .SelectMany(path => Regex
                    .Matches(
                        File.ReadAllText(path),
                        "public string Protocol\\s*=>\\s*\"([A-Za-z0-9_]+)\"")
                    .Select(match => match.Groups[1].Value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(protocol => protocol, StringComparer.Ordinal)
        ];
    }
}
