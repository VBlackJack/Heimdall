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
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TwinShell.Core.Helpers;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// The shipped seeds carry an English pivot and their other languages under
/// <c>translations</c>, in the shape the loader reads and writes back unchanged, and every
/// translated entry points at something the pivot has.
/// </summary>
public sealed class SeedTranslationContractTests
{
    private const string WorkedExample = "unified-service-restart.json";

    private static readonly string[] PivotKeys = ["title", "description", "notes", "category", "translations"];

    [Fact]
    public void TheWorkedExample_ReadsAndWritesBackToTheSameJson()
    {
        var path = Path.Combine(CommandGeneratorServiceTests.FindSeedActionsDirectory(), WorkedExample);
        var original = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var action = JsonSerializer.Deserialize<ActionModel>(original.ToJsonString(), JsonOptionsHelper.CaseInsensitive)!;
        var written = JsonNode.Parse(JsonSerializer.Serialize(action, JsonOptionsHelper.CamelCaseForExport))!.AsObject();

        foreach (var key in PivotKeys)
        {
            Assert.True(
                JsonNode.DeepEquals(original[key], written[key]),
                $"{key} changed on the way through: {original[key]?.ToJsonString()} became {written[key]?.ToJsonString()}");
        }

        Assert.Equal("Restart a service", action.Title);
        var french = ActionLocalizer.Localize(action, "fr");
        Assert.Equal("Redémarrer un service", french.Title);
        Assert.Equal("Nom du service", french.LinuxCommandTemplate!.Parameters.Single().Label);
        Assert.Equal("Redémarre le service nginx", french.LinuxExamples.Single(e => e.Command == "sudo systemctl restart nginx").Description);
    }

    [Fact]
    public void EveryTranslatedEntry_PointsAtSomethingThePivotHas()
    {
        var files = Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var offenders = new List<string>();
        var translatedFiles = 0;

        foreach (var path in files)
        {
            var action = JsonSerializer.Deserialize<ActionModel>(File.ReadAllText(path), JsonOptionsHelper.CaseInsensitive)!;
            if (action.Translations.Count == 0)
            {
                continue;
            }

            translatedFiles++;
            var name = Path.GetFileName(path);
            var commands = action.Examples.Concat(action.WindowsExamples).Concat(action.LinuxExamples)
                .Select(example => example.Command)
                .ToHashSet(StringComparer.Ordinal);
            var windowsParameters = (action.WindowsCommandTemplate?.Parameters ?? []).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var linuxParameters = (action.LinuxCommandTemplate?.Parameters ?? []).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var translation in action.Translations)
            {
                if (!Regex.IsMatch(translation.CultureCode, "^[a-z]{2}$") || translation.CultureCode == ActionLocalizer.PivotCulture)
                {
                    offenders.Add($"{name}: culture '{translation.CultureCode}'");
                }

                if (translation.Notes is not null && string.IsNullOrWhiteSpace(action.Notes))
                {
                    offenders.Add($"{name}/{translation.CultureCode}: notes with no pivot notes");
                }

                offenders.AddRange(translation.WindowsParameters.Keys.Where(key => !windowsParameters.Contains(key))
                    .Select(key => $"{name}/{translation.CultureCode}: windows parameter '{key}'"));
                offenders.AddRange(translation.LinuxParameters.Keys.Where(key => !linuxParameters.Contains(key))
                    .Select(key => $"{name}/{translation.CultureCode}: linux parameter '{key}'"));
                offenders.AddRange(translation.ExampleDescriptions.Keys.Where(key => !commands.Contains(key))
                    .Select(key => $"{name}/{translation.CultureCode}: example '{key}'"));
            }
        }

        Assert.True(translatedFiles > 0, "No seed carries a translation, so this guard reached nothing.");
        Assert.True(offenders.Count == 0, "Translated entries the pivot does not have: " + string.Join("; ", offenders));
    }
}
