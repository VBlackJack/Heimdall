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

namespace Heimdall.Core.Tests;

/// <summary>
/// Refuses a locale value that documents Ctrl+Delete as the gesture that deletes a session.
/// </summary>
/// <remarks>
/// The window shortcut is plain Delete, and the context menus name it from the localized label
/// TreeCtxGestureDelete (Del, Suppr, Supr). The tooltip of the detail pane's Delete button, the
/// hint line under it and the F1 help went on saying Ctrl+Del after the menus were corrected, so
/// one product named two gestures for one action. Those texts now take the label as an argument;
/// this guard stops a translation from writing the old gesture back in by hand. Ctrl+Delete
/// still works, it is just not the gesture the product teaches.
/// </remarks>
public sealed class LocaleDeleteGestureGuardTests
{
    private const string LocalesDirectoryName = "locales";

    /// <summary>Lower bound on the catalogues a healthy enumeration returns.</summary>
    private const int MinimumCataloguesEnumerated = 3;

    /// <summary>
    /// Ctrl, then the Delete key in any shipped language: Del or Delete (English), Suppr (French),
    /// Supr (Spanish). Spaces around the plus sign are tolerated, case is not significant.
    /// </summary>
    private static readonly Regex s_ctrlDeleteGesture = new(
        @"\bCtrl\s*\+\s*(Del(ete)?|Suppr|Supr)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every catalogue file name under <c>locales/</c>.</summary>
    public static TheoryData<string> ShippedCatalogues()
    {
        TheoryData<string> data = new();
        foreach (string path in EnumerateCatalogues())
            data.Add(Path.GetFileName(path));

        return data;
    }

    /// <summary>
    /// The enumeration decides what the sweep reads, and an empty member-data source passes.
    /// </summary>
    [Fact]
    public void TheSweepCoversEveryShippedCatalogue()
    {
        List<string> names = EnumerateCatalogues().Select(Path.GetFileName).ToList()!;

        Assert.True(
            names.Count >= MinimumCataloguesEnumerated,
            $"only {names.Count} catalogue(s) were enumerated from {LocalesDirectoryName}/");
        Assert.Contains("en.json", names);
        Assert.Contains("fr.json", names);
        Assert.Contains("es.json", names);
    }

    /// <summary>
    /// Positive control: the matcher recognises every spelling the shipped catalogues used, and
    /// leaves the plain Delete labels and the other Ctrl gestures alone. Without this, a pattern
    /// that matched nothing would keep the sweep below green forever.
    /// </summary>
    [Theory]
    [InlineData("Delete selected session (Ctrl+Del)", true)]
    [InlineData("Ctrl+E modifier \u00b7 Ctrl+Suppr supprimer", true)]
    [InlineData("  Ctrl+Supr\tEliminar servidor seleccionado", true)]
    [InlineData("Press ctrl + delete to remove it", true)]
    [InlineData("Delete selected session (Del)", false)]
    [InlineData("Supprimer la session s\u00e9lectionn\u00e9e (Suppr)", false)]
    [InlineData("Ctrl+E editar \u00b7 Supr eliminar", false)]
    [InlineData("Ctrl+D duplicates, Ctrl+Shift+Delta is not a key", false)]
    public void TheMatcherRecognisesTheCtrlDeleteGesture(string value, bool expected)
    {
        Assert.Equal(expected, s_ctrlDeleteGesture.IsMatch(value));
    }

    [Theory]
    [MemberData(nameof(ShippedCatalogues))]
    public void NoLocaleValueDocumentsCtrlDelete(string fileName)
    {
        string localePath = Path.Combine(FindRepoRoot(), LocalesDirectoryName, fileName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localePath));

        int valueCount = 0;
        List<string> violations = [];
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            valueCount++;
            string value = property.Value.GetString() ?? string.Empty;
            Match match = s_ctrlDeleteGesture.Match(value);
            if (match.Success)
                violations.Add($"  {fileName}::{property.Name} documents '{match.Value}'");
        }

        Assert.True(valueCount > 0, $"{fileName} yielded no string values, so nothing was checked");
        Assert.True(
            violations.Count == 0,
            $"{violations.Count} value(s) name Ctrl+Delete; the delete gesture is the label in "
            + "TreeCtxGestureDelete, passed in as an argument:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> EnumerateCatalogues()
        => Directory
            .GetFiles(Path.Combine(FindRepoRoot(), LocalesDirectoryName), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
