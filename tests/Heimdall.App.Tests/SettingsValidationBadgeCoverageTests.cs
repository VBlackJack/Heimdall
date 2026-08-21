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
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Ties a validated setting to the tab badge that is supposed to point at it.
/// </summary>
/// <remarks>
/// <para>Saving is refused when any validated property has an error, across all of them. The banner
/// and the per-tab badges are computed from four hand-written name arrays instead. Two settings were
/// in no array at all, so the save was refused with nothing shown anywhere: pressing Save did
/// nothing and said nothing. Four more were counted on the Advanced badge while their fields are on
/// the RDP tab, so the badge sent the user to look somewhere the field is not.</para>
/// <para>The tab each property belongs to is read from the window markup rather than restated here,
/// because restating it is what went wrong. What this file does state is which array serves which
/// tab, and that is exactly the claim being checked.</para>
/// </remarks>
public sealed class SettingsValidationBadgeCoverageTests
{
    /// <summary>Which settings tab each name array is the badge source for.</summary>
    /// <remarks>
    /// The Security tab is absent on purpose: it holds no validated setting today and so has no
    /// badge. If one is added there this test fails rather than passing quietly, which is the
    /// intended outcome - a validated field with no badge is exactly the defect being frozen out.
    /// </remarks>
    private static readonly (string Array, string Tab)[] ArrayToTab =
    [
        ("GeneralValidatedSettingPropertyNames", "Mw_SettingsTabGeneral"),
        ("TerminalValidatedSettingPropertyNames", "Mw_SettingsTabTerminal"),
        ("SshValidatedSettingPropertyNames", "Mw_SettingsTabSsh"),
        ("RdpValidatedSettingPropertyNames", "Mw_SettingsTabRdp"),
        ("AdvancedValidatedSettingPropertyNames", "Mw_SettingsTabAdvanced"),
    ];

    [Fact]
    public void EveryValidatedSettingIsCountedByTheBadgeOfTheTabItIsOn()
    {
        IReadOnlyList<string> validated = ValidatedPropertyNames();
        IReadOnlyDictionary<string, string> propertyToArray = PropertyToArray();
        IReadOnlyDictionary<string, string> propertyToTab = PropertyToTabFromMarkup(validated);
        Dictionary<string, string> tabForArray = ArrayToTab.ToDictionary(
            entry => entry.Array,
            entry => entry.Tab,
            System.StringComparer.Ordinal);

        List<string> problems = [];
        int checkedProperties = 0;

        foreach (string property in validated)
        {
            if (!propertyToTab.TryGetValue(property, out string? tab))
            {
                // Not bound in the window: nothing on screen can carry its error, so no badge can
                // be expected to. Left out rather than guessed at.
                continue;
            }

            checkedProperties++;

            if (!propertyToArray.TryGetValue(property, out string? array))
            {
                problems.Add(
                    $"{property} validates and is on {tab}, but is in no badge array. Saving is "
                        + "refused with no banner, no badge and no field error.");
                continue;
            }

            if (!tabForArray.TryGetValue(array, out string? countedTab) || countedTab != tab)
            {
                problems.Add(
                    $"{property} is on {tab} but is counted by {array}, whose badge is on "
                        + $"{countedTab ?? "an unknown tab"}.");
            }
        }

        // Guarding the guard: a markup scan that matched nothing, or reflection that found no
        // validated property, would report success having checked nothing at all.
        Assert.True(
            checkedProperties >= 15,
            $"only {checkedProperties} validated settings were matched to a tab, so the scan is no "
                + "longer reading what it thinks it is");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryValidationMessageResolvesToALocalizedKey()
    {
        string source = ReadViewModelSource();
        IReadOnlyDictionary<string, string> messageToKey = MessageToKey(source);
        IReadOnlyDictionary<string, JsonElement> english = ReadLocale("en");
        IReadOnlyDictionary<string, JsonElement> french = ReadLocale("fr");

        List<string> problems = [];
        int messages = 0;

        foreach (Match match in Regex.Matches(source, @"ErrorMessage\s*=\s*""([^""]+)"""))
        {
            messages++;
            string message = match.Groups[1].Value;
            if (!messageToKey.TryGetValue(message, out string? key))
            {
                problems.Add($"no localization key for the validation message: \"{message}\"");
                continue;
            }

            if (!english.ContainsKey(key))
            {
                problems.Add($"'{key}' is missing from en.json");
            }

            if (!french.ContainsKey(key))
            {
                problems.Add($"'{key}' is missing from fr.json");
            }
        }

        Assert.True(messages >= 15, $"only {messages} validation messages found, so nothing was checked");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // A property may not be counted twice, or one error would light two badges and the banner would
    // depend on which array happened to be read first.
    [Fact]
    public void NoValidatedSettingIsCountedByTwoBadges()
    {
        string source = ReadViewModelSource();
        Dictionary<string, List<string>> arraysByProperty = [];

        foreach ((string array, _) in ArrayToTab)
        {
            foreach (string property in NamesInArray(source, array))
            {
                if (!arraysByProperty.TryGetValue(property, out List<string>? arrays))
                {
                    arrays = [];
                    arraysByProperty[property] = arrays;
                }

                arrays.Add(array);
            }
        }

        Assert.NotEmpty(arraysByProperty);

        List<string> duplicates =
        [
            .. arraysByProperty
                .Where(entry => entry.Value.Count > 1)
                .Select(entry => $"{entry.Key} appears in {string.Join(" and ", entry.Value)}")
        ];

        Assert.True(duplicates.Count == 0, string.Join("\n", duplicates));
    }

    private static IReadOnlyList<string> ValidatedPropertyNames()
    {
        List<string> names = [];
        foreach (FieldInfo field in typeof(SettingsViewModel)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.GetCustomAttributes<ValidationAttribute>().Any()
                && field.Name.StartsWith('_'))
            {
                names.Add(char.ToUpperInvariant(field.Name[1]) + field.Name[2..]);
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<string, string> PropertyToArray()
    {
        string source = ReadViewModelSource();
        Dictionary<string, string> map = new(System.StringComparer.Ordinal);
        foreach ((string array, _) in ArrayToTab)
        {
            foreach (string property in NamesInArray(source, array))
            {
                map.TryAdd(property, array);
            }
        }

        return map;
    }

    private static IReadOnlyList<string> NamesInArray(string source, string arrayName)
    {
        Match declaration = Regex.Match(
            source,
            @"string\[\] " + Regex.Escape(arrayName) + @"\s*=\s*\[(?<Body>.*?)\];",
            RegexOptions.Singleline);

        Assert.True(declaration.Success, $"array not found in the view model: {arrayName}");

        return [.. Regex.Matches(declaration.Groups["Body"].Value, @"nameof\((\w+)\)")
            .Select(match => match.Groups[1].Value)];
    }

    private static IReadOnlyDictionary<string, string> PropertyToTabFromMarkup(
        IReadOnlyList<string> validated)
    {
        string[] lines = File.ReadAllLines(
            Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

        // Where each settings tab starts. A property belongs to the last tab opened above it.
        List<(int Line, string Tab)> tabStarts = [];
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = Regex.Match(lines[index], @"<TabItem x:Name=""(Mw_SettingsTab\w+)""");
            if (match.Success)
            {
                tabStarts.Add((index, match.Groups[1].Value));
            }
        }

        Assert.True(tabStarts.Count >= 4, $"only {tabStarts.Count} settings tabs found in the markup");

        Dictionary<string, string> map = new(System.StringComparer.Ordinal);
        foreach (string property in validated)
        {
            Regex binding = new(@"Settings\." + Regex.Escape(property) + @"[,}]");
            for (int index = 0; index < lines.Length; index++)
            {
                if (!binding.IsMatch(lines[index]))
                {
                    continue;
                }

                (int Line, string Tab)? owner = tabStarts
                    .Where(start => start.Line < index)
                    .Cast<(int Line, string Tab)?>()
                    .LastOrDefault();

                if (owner is { } found)
                {
                    map[property] = found.Tab;
                }

                break;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> MessageToKey(string source)
    {
        Dictionary<string, string> map = new(System.StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(source, @"\[""([^""]+)""\]\s*=\s*""(\w+)"""))
        {
            map[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return map;
    }

    private static string ReadViewModelSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "Heimdall.App", "ViewModels", "SettingsViewModel.cs"));

    private static IReadOnlyDictionary<string, JsonElement> ReadLocale(string language)
    {
        string path = Path.Combine(FindRepoRoot(), "locales", $"{language}.json");
        Dictionary<string, JsonElement>? parsed =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
