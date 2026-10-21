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
using System.Reflection;
using System.Text.Json;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Every top-level key of a shipped seed action is a property the loader reads into
/// <see cref="ActionModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// The loader deserializes case-insensitively and drops unknown keys without a word, so a
/// key that maps to nothing (supportedPlatforms, crossPlatformNotes and an action-level
/// parameters array did, until 2026-10-20) looks like data to whoever edits the file and
/// does nothing.
/// </para>
/// <para>
/// The known set is read off the model by reflection, so a property added to the model
/// widens it by itself. The guard is for the shipped files only: the import door keeps
/// tolerating unknown keys from files it did not write.
/// </para>
/// </remarks>
public sealed class SeedSchemaGuardTests
{
    private static IReadOnlySet<string> KnownKeys { get; } =
        typeof(ActionModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The reflection has to produce the model's real keys, or the guard below would compare
    /// against nothing and refuse everything, or against the wrong names and pass nothing.
    /// </summary>
    [Fact]
    public void TheKnownKeysAreTheModelsSerializedNames()
    {
        Assert.Contains("windowsCommandTemplate", KnownKeys);
        Assert.Contains("linuxExamples", KnownKeys);
        Assert.Contains("id", KnownKeys);
        Assert.DoesNotContain("supportedPlatforms", KnownKeys);
        Assert.DoesNotContain("parameters", KnownKeys);
    }

    [Fact]
    public void EverySeedKeyIsAnActionProperty()
    {
        var files = Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        // A floor against a discovery that finds nothing, not the count: the seed set grows and
        // shrinks, and docs/TOOLS.md states its current size.
        Assert.True(files.Count >= 400, $"Expected the seed catalogue, found {files.Count} files.");

        var offenders = new List<string>();
        foreach (var path in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var unknown = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !KnownKeys.Contains(name))
                .ToList();
            if (unknown.Count > 0)
            {
                offenders.Add($"{Path.GetFileName(path)} ({string.Join(", ", unknown)})");
            }
        }

        Assert.True(offenders.Count == 0, "Seed keys no Action property reads: " + string.Join("; ", offenders));
    }
}
