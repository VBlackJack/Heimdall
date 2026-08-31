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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Validates coherence between ToolRegistry entries and locale JSON files.
/// Every registered tool must have matching ToolDesc, ToolHelp, and palette
/// title keys in both EN and FR locale files.
/// </summary>
public class ToolRegistryLocaleCoherenceTests
{
    private static readonly Lazy<ToolRegistry> s_registry = new(() => new ToolRegistry());
    private static readonly Lazy<Dictionary<string, string>> s_enLocale = new(LoadLocale("en"));
    private static readonly Lazy<Dictionary<string, string>> s_frLocale = new(LoadLocale("fr"));

    private static Dictionary<string, string> EnLocale => s_enLocale.Value;
    private static Dictionary<string, string> FrLocale => s_frLocale.Value;
    private static ToolRegistry Registry => s_registry.Value;

    // ── ToolDesc keys ────────────────────────────────────────────────────

    [Fact]
    public void AllTools_HaveToolDescKey_InEnLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolDesc{tool.Id}";
            if (!EnLocale.ContainsKey(key))
                missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"Missing ToolDesc keys in en.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllTools_HaveToolDescKey_InFrLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolDesc{tool.Id}";
            if (!FrLocale.ContainsKey(key))
                missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"Missing ToolDesc keys in fr.json: {string.Join(", ", missing)}");
    }

    // ── ToolHelp keys ────────────────────────────────────────────────────

    [Fact]
    public void AllTools_HaveToolHelpKey_InEnLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolHelp{tool.Id}";
            if (!EnLocale.ContainsKey(key))
                missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"Missing ToolHelp keys in en.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllTools_HaveToolHelpKey_InFrLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolHelp{tool.Id}";
            if (!FrLocale.ContainsKey(key))
                missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"Missing ToolHelp keys in fr.json: {string.Join(", ", missing)}");
    }

    // ── Palette title keys ───────────────────────────────────────────────

    [Fact]
    public void AllTools_HavePaletteTitleKey_InEnLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            if (!EnLocale.ContainsKey(tool.LabelKey))
                missing.Add($"{tool.Id} -> {tool.LabelKey}");
        }

        Assert.True(missing.Count == 0,
            $"Missing palette title keys in en.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllTools_HavePaletteTitleKey_InFrLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            if (!FrLocale.ContainsKey(tool.LabelKey))
                missing.Add($"{tool.Id} -> {tool.LabelKey}");
        }

        Assert.True(missing.Count == 0,
            $"Missing palette title keys in fr.json: {string.Join(", ", missing)}");
    }

    // ── EN/FR parity for tool-specific keys ──────────────────────────────

    [Fact]
    public void ToolDescKeys_HaveEnFrParity()
    {
        var enOnly = new List<string>();
        var frOnly = new List<string>();

        foreach (var tool in Registry.All)
        {
            var key = $"ToolDesc{tool.Id}";
            var inEn = EnLocale.ContainsKey(key);
            var inFr = FrLocale.ContainsKey(key);

            if (inEn && !inFr) enOnly.Add(key);
            if (inFr && !inEn) frOnly.Add(key);
        }

        Assert.True(enOnly.Count == 0 && frOnly.Count == 0,
            $"EN-only ToolDesc keys: [{string.Join(", ", enOnly)}]; " +
            $"FR-only ToolDesc keys: [{string.Join(", ", frOnly)}]");
    }

    [Fact]
    public void ToolHelpKeys_HaveEnFrParity()
    {
        var enOnly = new List<string>();
        var frOnly = new List<string>();

        foreach (var tool in Registry.All)
        {
            var key = $"ToolHelp{tool.Id}";
            var inEn = EnLocale.ContainsKey(key);
            var inFr = FrLocale.ContainsKey(key);

            if (inEn && !inFr) enOnly.Add(key);
            if (inFr && !inEn) frOnly.Add(key);
        }

        Assert.True(enOnly.Count == 0 && frOnly.Count == 0,
            $"EN-only ToolHelp keys: [{string.Join(", ", enOnly)}]; " +
            $"FR-only ToolHelp keys: [{string.Join(", ", frOnly)}]");
    }

    // ── Category label keys ──────────────────────────────────────────────

    [Fact]
    public void AllTools_HaveCategoryLabelKey_InEnLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            if (!EnLocale.ContainsKey(tool.CategoryLabelKey))
                missing.Add($"{tool.Id} -> {tool.CategoryLabelKey}");
        }

        Assert.True(missing.Count == 0,
            $"Missing category label keys in en.json: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllTools_HaveCategoryLabelKey_InFrLocale()
    {
        var missing = new List<string>();
        foreach (var tool in Registry.All)
        {
            if (!FrLocale.ContainsKey(tool.CategoryLabelKey))
                missing.Add($"{tool.Id} -> {tool.CategoryLabelKey}");
        }

        Assert.True(missing.Count == 0,
            $"Missing category label keys in fr.json: {string.Join(", ", missing)}");
    }

    // ── ToolDesc values are non-empty ────────────────────────────────────

    [Fact]
    public void AllToolDescValues_AreNonEmpty_InEnLocale()
    {
        var empty = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolDesc{tool.Id}";
            if (EnLocale.TryGetValue(key, out var value) && string.IsNullOrWhiteSpace(value))
                empty.Add(key);
        }

        Assert.True(empty.Count == 0,
            $"Empty ToolDesc values in en.json: {string.Join(", ", empty)}");
    }

    [Fact]
    public void AllToolHelpValues_AreNonEmpty_InEnLocale()
    {
        var empty = new List<string>();
        foreach (var tool in Registry.All)
        {
            var key = $"ToolHelp{tool.Id}";
            if (EnLocale.TryGetValue(key, out var value) && string.IsNullOrWhiteSpace(value))
                empty.Add(key);
        }

        Assert.True(empty.Count == 0,
            $"Empty ToolHelp values in en.json: {string.Join(", ", empty)}");
    }

    // ── Registry sanity ──────────────────────────────────────────────────

    [Fact]
    public void Registry_HasExpectedToolCount()
    {
        // 58 built-in tools. External provider tools are not in this count: they live
        // in the runtime overlay and are appended only after a startup scan.
        Assert.Equal(58, Registry.All.Count);
    }

    [Fact]
    public void Registry_HasNoDuplicateIds()
    {
        var ids = Registry.All.Select(t => t.Id).ToList();
        var duplicates = ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate tool IDs: {string.Join(", ", duplicates)}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Dictionary<string, string> LoadLocale(string locale)
    {
        var localesPath = FindLocalesPath();
        var filePath = Path.Combine(localesPath, $"{locale}.json");

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Locale file not found: {filePath}");

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize {locale}.json");
    }

    /// <summary>
    /// Walks up from the test binary directory to find the locales/ folder.
    /// </summary>
    private static string FindLocalesPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "locales");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "en.json")))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: relative path from test project
        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "locales"));

        if (Directory.Exists(fallback))
            return fallback;

        throw new DirectoryNotFoundException(
            "Cannot find locales/ directory. Ensure the test runs from the repository.");
    }
}
