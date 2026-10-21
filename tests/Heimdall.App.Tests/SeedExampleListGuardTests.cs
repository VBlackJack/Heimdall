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
using TwinShell.Core.Constants;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Keeps the example lists of the shipped seed actions free of the filler removed on
/// 2026-10-20: a command repeated in the same list, a man or --help page standing in for an
/// example, and lists longer than <see cref="ValidationConstants.MaxActionExamplesCount"/>.
/// </summary>
/// <remarks>
/// A list is the unit, not the file: the panel shows the Windows or the Linux list (or the
/// generic one when the platform list is empty), never two at once, so the same command in
/// two lists is not a repeat the operator sees. Each failure names the offending files.
/// </remarks>
public sealed class SeedExampleListGuardTests
{
    private static readonly Regex HelpCommand = new(
        @"^(man \S+|\S+ --help|Get-Help .*)$",
        RegexOptions.CultureInvariant);

    private static IReadOnlyList<(string File, string List, List<CommandExample> Examples)> ExampleLists { get; } = LoadExampleLists();

    /// <summary>
    /// The three guards iterate the lists, so finding none would leave them green over
    /// nothing. The catalogue holds well over a thousand lists across 442 files.
    /// </summary>
    [Fact]
    public void TheSeedExampleListsAreFound()
    {
        Assert.True(
            ExampleLists.Select(entry => entry.File).Distinct().Count() >= 400,
            $"Expected the seed catalogue, found {ExampleLists.Count} example lists.");
    }

    [Fact]
    public void NoListRepeatsACommand()
    {
        var offenders = ExampleLists
            .Where(entry => entry.Examples
                .GroupBy(example => (example.Command ?? string.Empty).Trim(), StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
            .Select(entry => $"{entry.File} [{entry.List}]")
            .ToList();

        Assert.True(offenders.Count == 0, "Repeated example commands in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoExampleIsAManOrHelpPage()
    {
        var offenders = ExampleLists
            .SelectMany(entry => entry.Examples
                .Where(example => HelpCommand.IsMatch((example.Command ?? string.Empty).Trim()))
                .Select(example => $"{entry.File} [{entry.List}]: {example.Command}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Help pages used as examples in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoListIsLongerThanTheBound()
    {
        var offenders = ExampleLists
            .Where(entry => entry.Examples.Count > ValidationConstants.MaxActionExamplesCount)
            .Select(entry => $"{entry.File} [{entry.List}] has {entry.Examples.Count}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Example lists longer than {ValidationConstants.MaxActionExamplesCount}: " + string.Join(", ", offenders));
    }

    private static List<(string File, string List, List<CommandExample> Examples)> LoadExampleLists()
    {
        var lists = new List<(string, string, List<CommandExample>)>();
        foreach (var path in Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var action = JsonSerializer.Deserialize<ActionModel>(File.ReadAllText(path), JsonOptionsHelper.CaseInsensitive)
                ?? throw new InvalidOperationException($"Seed {path} did not parse.");
            var file = Path.GetFileName(path);
            lists.Add((file, "examples", action.Examples ?? []));
            lists.Add((file, "windowsExamples", action.WindowsExamples ?? []));
            lists.Add((file, "linuxExamples", action.LinuxExamples ?? []));
        }

        return lists;
    }
}
