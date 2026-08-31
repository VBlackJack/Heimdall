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
using System.Xml.Linq;

namespace Heimdall.App.Tests;

/// <summary>
/// Every DataGrid must name its rows.
/// </summary>
/// <remarks>
/// <para>
/// A row with no <c>AutomationProperties.Name</c> falls back to <c>ToString()</c> of the bound
/// item, so a screen reader announces the view-model type. Twenty-one grids shipped that way. The
/// point of a guard rather than twenty-one fixes is the twenty-second grid: a new one added
/// without a row style is a silent regression, invisible to the compiler and to every test that
/// does not walk a live automation tree.
/// </para>
/// <para>
/// Two routes are accepted, because one cannot cover the ground: a row type owned by the
/// application implements <c>IAccessibleItemViewModel</c> and the style enables
/// <c>ItemContainerAccessibilityBehavior</c>; a row type declared in <c>Heimdall.Core</c> - which
/// holds no project reference and so cannot see that interface - is named through
/// <c>AccessibleRowNameConverter</c>.
/// </para>
/// </remarks>
public sealed class DataGridRowNameGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryDataGrid_NamesItsRows()
    {
        List<string> unnamed = new();

        foreach (string file in EnumerateXaml())
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (System.Xml.XmlException ex)
            {
                Assert.Fail($"{Relative(file)} is not well-formed XAML: {ex.Message}");
                return;
            }

            foreach (XElement grid in doc.Descendants(Xaml + "DataGrid"))
            {
                string markup = grid.ToString();
                bool named = markup.Contains("AccessibleRowNameConverter", StringComparison.Ordinal)
                    || markup.Contains("ItemContainerAccessibilityBehavior", StringComparison.Ordinal);

                if (!named)
                {
                    string id = (string?)grid.Attribute(XNamespace.Get(
                        "http://schemas.microsoft.com/winfx/2006/xaml") + "Name") ?? "(unnamed)";
                    unnamed.Add($"  {Relative(file)} : DataGrid {id}");
                }
            }
        }

        Assert.True(
            unnamed.Count == 0,
            $"{unnamed.Count} DataGrid(s) leave their rows to announce the view-model type name."
            + " Add a <DataGrid.RowStyle> that either enables"
            + " ItemContainerAccessibilityBehavior (for a row type this project owns) or binds"
            + " AutomationProperties.Name through AccessibleRowNameConverter (for a row type"
            + " declared in Heimdall.Core or in the framework):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unnamed));
    }

    /// <summary>
    /// The number of value bindings must equal the number of slots the format declares. A
    /// mismatch does not fail the build: the converter falls back to a bare join, so the labels
    /// silently disappear from the announcement and nothing says so.
    /// </summary>
    [Fact]
    public void EveryAccessibleRowName_SuppliesAsManyValuesAsItsFormatAsksFor()
    {
        string repoRoot = FindRepoRoot();
        var en = ReadLocale(Path.Combine(repoRoot, "locales", "en.json"));
        var fr = ReadLocale(Path.Combine(repoRoot, "locales", "fr.json"));

        var slotPattern = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);
        var keyPattern = new Regex(@"Path=""\[(A11y\w*Row)\]""", RegexOptions.Compiled);

        List<string> problems = new();

        foreach (string file in EnumerateXaml())
        {
            foreach (XElement multi in XDocument.Load(file).Descendants(Xaml + "MultiBinding"))
            {
                string markup = multi.ToString();
                if (!markup.Contains("AccessibleRowNameConverter", StringComparison.Ordinal))
                {
                    continue;
                }

                Match key = keyPattern.Match(markup);
                if (!key.Success)
                {
                    problems.Add($"  {Relative(file)}: a row-name MultiBinding with no A11y*Row key");
                    continue;
                }

                string name = key.Groups[1].Value;
                int supplied = multi.Elements(Xaml + "Binding").Count() - 1;

                foreach (var (locale, table) in new[] { ("en", en), ("fr", fr) })
                {
                    if (!table.TryGetValue(name, out string? format))
                    {
                        problems.Add($"  {Relative(file)}: {name} is missing from locales/{locale}.json");
                        continue;
                    }

                    int slots = slotPattern.Matches(format)
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .Count();

                    if (slots != supplied)
                    {
                        problems.Add(
                            $"  {Relative(file)}: {name} ({locale}) declares {slots} slot(s)"
                            + $" but the XAML supplies {supplied} value binding(s)");
                    }
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Row-name formats and their bindings disagree:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Proves the sweep reaches a subdirectory. Without it a broken enumeration would report zero
    /// problems and read as a clean tree.
    /// </summary>
    [Fact]
    public void TheSweep_ReachesTheToolViews()
    {
        Assert.Contains(
            EnumerateXaml(),
            f => Relative(f).Replace('/', Path.DirectorySeparatorChar)
                .EndsWith(Path.Combine("Views", "Tools", "ArpMonitorView.xaml"), StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ReadLocale(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                table[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return table;
    }

    private static IEnumerable<string> EnumerateXaml() =>
        SourceFileEnumeration.EnumerateFiles(
            Path.Combine(FindRepoRoot(), "src", "Heimdall.App"), "*.xaml");

    private static string Relative(string file) =>
        Path.GetRelativePath(Path.Combine(FindRepoRoot(), "src", "Heimdall.App"), file);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
