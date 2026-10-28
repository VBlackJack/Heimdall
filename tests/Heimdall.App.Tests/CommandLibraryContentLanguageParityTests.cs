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
using TwinShell.Core.Constants;
using TwinShell.Core.Helpers;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Keeps the content languages of the shipped Command Library in step with its English base:
/// every translation block carries only what the base has, with the same <c>{placeholder}</c>
/// tokens, and every seed action is shown in French and Spanish.
/// </summary>
/// <remarks>
/// <para>
/// The check reads the raw JSON, not the model: the model's reader skips a key it does not
/// know, so a misspelled <c>"descripton"</c> or a stray field would pass through a typed read
/// and the translated text would silently fall back to English.
/// </para>
/// <para>
/// Spanish carries a title and a description only. Notes and example descriptions are not
/// translated in Spanish; an example is keyed by its exact command string, so they fall back
/// to the English base, which <see cref="ASpanishBlockWithoutNotesOrExamples_FallsBackToTheEnglishBase"/>
/// pins on a real seed action.
/// </para>
/// </remarks>
public sealed class CommandLibraryContentLanguageParityTests
{
    private const string SpanishCulture = "es";
    private const string FrenchCulture = "fr";

    /// <summary>
    /// Lower bound on the seed files a healthy sweep reads. A directory that moved would
    /// otherwise leave every check green over nothing.
    /// </summary>
    private const int MinimumSeedFilesRead = 527;

    /// <summary>Lower bound on the actions of the optional debloat pack.</summary>
    private const int MinimumPackActionsRead = 72;

    /// <summary>The longest example or parameter description the seed schema accepts.</summary>
    private const int MaxSchemaDescriptionLength = 500;

    private const string PackFileName = "windows-workstation-debloat.json";

    private static readonly string[] BlockKeys = ["title", "description", "notes", "parameters", "examples"];
    private static readonly string[] ParameterSetKeys = ["windows", "linux"];
    private static readonly string[] ParameterKeys = ["label", "description"];

    private static readonly Regex PlaceholderToken = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant);

    [Fact]
    public void EverySeedAction_CarriesAFrenchAndASpanishTitleAndDescription()
    {
        var actions = LoadSeedActions();

        Assert.True(
            actions.Count >= MinimumSeedFilesRead,
            $"Only {actions.Count} seed files were read; expected at least {MinimumSeedFilesRead}.");

        var offenders = FindMissingTitleOrDescription(actions, [FrenchCulture, SpanishCulture]);

        Assert.True(offenders.Count == 0, "Seed actions without a title or description: " + string.Join("; ", offenders));
    }

    [Fact]
    public void EverySeedTranslationBlock_OnlyCarriesWhatTheBaseHas()
    {
        var actions = LoadSeedActions();

        Assert.True(actions.Count >= MinimumSeedFilesRead, $"Only {actions.Count} seed files were read.");

        var offenders = FindParityOffenders(actions);

        Assert.True(offenders.Count == 0, "Seed translation drift: " + string.Join("; ", offenders));
    }

    [Fact]
    public void EveryPackAction_CarriesAFrenchAndASpanishBlockThatMatchesTheBase()
    {
        var actions = LoadPackActions();

        Assert.True(
            actions.Count >= MinimumPackActionsRead,
            $"Only {actions.Count} pack actions were read; expected at least {MinimumPackActionsRead}.");

        var offenders = FindMissingTitleOrDescription(actions, [FrenchCulture, SpanishCulture])
            .Concat(FindParityOffenders(actions))
            .ToList();

        Assert.True(offenders.Count == 0, "Pack translation drift: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// The negative control of the checks above: a Spanish title taken out of one action must
    /// be reported with that action's id, not swallowed.
    /// </summary>
    [Fact]
    public void ARemovedSpanishTitle_IsReportedWithTheActionId()
    {
        var actions = LoadSeedActions();
        var victim = actions[0];
        var victimId = victim.Action["id"]!.GetValue<string>();
        victim.Action["translations"]![SpanishCulture]!.AsObject().Remove("title");

        var offenders = FindMissingTitleOrDescription(actions, [SpanishCulture]);

        Assert.Contains($"{victimId}/{SpanishCulture}: no title", offenders);
    }

    /// <summary>
    /// The negative control of the token check: a placeholder dropped from a translation is
    /// reported, so a translation that loses a <c>{token}</c> cannot ship. The real action
    /// is one of the SCCM schedule-id notes, whose <c>{01}</c> is the part that matters.
    /// </summary>
    [Fact]
    public void ADroppedPlaceholder_IsReportedWithTheActionId()
    {
        const string actionId = "sccm-trigger-hardware-inventory";
        var entry = LoadSeedActions().Single(candidate => candidate.Action["id"]!.GetValue<string>() == actionId);
        Assert.Empty(FindParityOffenders([entry]));
        var french = entry.Action["translations"]![FrenchCulture]!.AsObject();
        var notes = french["notes"]!.GetValue<string>();
        Assert.Matches(PlaceholderToken, notes);

        french["notes"] = PlaceholderToken.Replace(notes, string.Empty);
        var offenders = FindParityOffenders([entry]);

        Assert.Contains(offenders, offender => offender.Contains(actionId, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyTheBaseDoesNotHave_IsReported()
    {
        var actions = LoadSeedActions();
        var victim = actions[0];
        var victimId = victim.Action["id"]!.GetValue<string>();
        victim.Action["translations"]![SpanishCulture]!.AsObject()["descripton"] = "typo";

        var offenders = FindParityOffenders(actions);

        Assert.Contains(offenders, offender => offender.Contains(victimId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Spanish has no notes and no example descriptions, so a Spanish reader gets the English
    /// ones for those two, and the Spanish title and description for the rest.
    /// </summary>
    [Fact]
    public void ASpanishBlockWithoutNotesOrExamples_FallsBackToTheEnglishBase()
    {
        var path = Path.Combine(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "aa-status-show-profiles.json");
        var action = JsonSerializer.Deserialize<ActionModel>(File.ReadAllText(path), JsonOptionsHelper.CaseInsensitive)!;
        var spanishBlock = action.Translations.Single(translation => translation.CultureCode == SpanishCulture);
        Assert.Null(spanishBlock.Notes);
        Assert.Empty(spanishBlock.ExampleDescriptions);
        Assert.False(string.IsNullOrWhiteSpace(action.Notes));
        Assert.NotEmpty(action.Examples);

        var spanish = ActionLocalizer.Localize(action, SpanishCulture);

        Assert.Equal(spanishBlock.Title, spanish.Title);
        Assert.Equal(spanishBlock.Description, spanish.Description);
        Assert.NotEqual(action.Title, spanish.Title);
        Assert.Equal(action.Notes, spanish.Notes);
        Assert.Equal(
            action.Examples.Select(example => example.Description),
            spanish.Examples.Select(example => example.Description));
    }

    private static List<(string File, JsonObject Action)> LoadSeedActions()
        => Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), JsonNode.Parse(File.ReadAllText(path))!.AsObject()))
            .ToList();

    private static List<(string File, JsonObject Action)> LoadPackActions()
    {
        var seedActions = new DirectoryInfo(CommandGeneratorServiceTests.FindSeedActionsDirectory());
        var path = Path.Combine(seedActions.Parent!.Parent!.FullName, "packs", PackFileName);
        Assert.True(File.Exists(path), $"pack not found: {path}");

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return root["actions"]!.AsArray()
            .Select(node => (PackFileName, node!.AsObject()))
            .ToList();
    }

    private static List<string> FindMissingTitleOrDescription(
        IEnumerable<(string File, JsonObject Action)> actions,
        string[] cultures)
    {
        var offenders = new List<string>();
        foreach (var (file, action) in actions)
        {
            var id = action["id"]?.GetValue<string>() ?? file;
            foreach (var culture in cultures)
            {
                var block = action["translations"]?[culture] as JsonObject;
                foreach (var field in new[] { "title", "description" })
                {
                    var text = block?[field] is JsonValue value && value.TryGetValue(out string? s) ? s : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        offenders.Add($"{id}/{culture}: no {field}");
                    }
                }
            }
        }

        return offenders;
    }

    private static List<string> FindParityOffenders(IEnumerable<(string File, JsonObject Action)> actions)
    {
        var offenders = new List<string>();
        foreach (var (file, action) in actions)
        {
            var id = action["id"]?.GetValue<string>() ?? file;
            if (action["translations"] is not JsonObject translations)
            {
                continue;
            }

            var commands = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var listName in new[] { "examples", "windowsExamples", "linuxExamples" })
            {
                foreach (var example in action[listName]?.AsArray() ?? [])
                {
                    commands.TryAdd(
                        example!["command"]!.GetValue<string>(),
                        example["description"]?.GetValue<string>());
                }
            }

            foreach (var (culture, node) in translations)
            {
                var where = $"{id}/{culture}";
                if (node is not JsonObject block)
                {
                    offenders.Add($"{where}: not an object");
                    continue;
                }

                offenders.AddRange(block.Select(pair => pair.Key)
                    .Where(key => !BlockKeys.Contains(key, StringComparer.Ordinal))
                    .Select(key => $"{where}: unknown key '{key}'"));

                CheckText(offenders, where + "/title", block["title"], Base(action, "title"), ValidationConstants.MaxActionTitleLength);
                CheckText(offenders, where + "/description", block["description"], Base(action, "description"), ValidationConstants.MaxActionDescriptionLength);
                CheckText(offenders, where + "/notes", block["notes"], Base(action, "notes"), ValidationConstants.MaxActionNotesLength);

                if (block["notes"] is not null && string.IsNullOrWhiteSpace(Base(action, "notes")))
                {
                    offenders.Add($"{where}: notes with no base notes");
                }

                CheckParameters(offenders, where, block["parameters"], action);

                if (block["examples"] is JsonObject examples)
                {
                    foreach (var (command, text) in examples)
                    {
                        if (!commands.TryGetValue(command, out var baseDescription))
                        {
                            offenders.Add($"{where}: example '{command}' is not in the base");
                            continue;
                        }

                        CheckText(offenders, $"{where}/example '{command}'", text, baseDescription, MaxSchemaDescriptionLength);
                    }
                }
            }
        }

        return offenders;
    }

    private static void CheckParameters(List<string> offenders, string where, JsonNode? parameters, JsonObject action)
    {
        if (parameters is null)
        {
            return;
        }

        if (parameters is not JsonObject sets)
        {
            offenders.Add($"{where}: parameters is not an object");
            return;
        }

        foreach (var (setName, setNode) in sets)
        {
            if (!ParameterSetKeys.Contains(setName, StringComparer.Ordinal))
            {
                offenders.Add($"{where}: unknown parameter set '{setName}'");
                continue;
            }

            var template = action[setName + "CommandTemplate"] as JsonObject;
            var baseParameters = (template?["parameters"]?.AsArray() ?? [])
                .ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!.AsObject(), StringComparer.Ordinal);

            foreach (var (name, parameterNode) in setNode?.AsObject() ?? [])
            {
                if (!baseParameters.TryGetValue(name, out var baseParameter))
                {
                    offenders.Add($"{where}: {setName} parameter '{name}' is not in the base");
                    continue;
                }

                var parameter = parameterNode?.AsObject() ?? [];
                offenders.AddRange(parameter.Select(pair => pair.Key)
                    .Where(key => !ParameterKeys.Contains(key, StringComparer.Ordinal))
                    .Select(key => $"{where}: {setName} parameter '{name}' has unknown key '{key}'"));

                CheckText(offenders, $"{where}/{setName} parameter '{name}' label", parameter["label"], BaseOf(baseParameter, "label"), ValidationConstants.MaxActionTitleLength);
                CheckText(offenders, $"{where}/{setName} parameter '{name}' description", parameter["description"], BaseOf(baseParameter, "description"), MaxSchemaDescriptionLength);
            }
        }
    }

    private static void CheckText(List<string> offenders, string where, JsonNode? translated, string? baseText, int maxLength)
    {
        if (translated is null)
        {
            return;
        }

        if (translated is not JsonValue value || !value.TryGetValue(out string? text))
        {
            offenders.Add($"{where}: not a string");
            return;
        }

        if (text.Length > maxLength)
        {
            offenders.Add($"{where}: {text.Length} characters, over {maxLength}");
        }

        var wanted = Tokens(baseText);
        var actual = Tokens(text);
        if (!wanted.SequenceEqual(actual, StringComparer.Ordinal))
        {
            offenders.Add($"{where}: tokens [{string.Join(",", actual)}] differ from the base [{string.Join(",", wanted)}]");
        }
    }

    private static List<string> Tokens(string? text)
        => PlaceholderToken.Matches(text ?? string.Empty)
            .Select(match => match.Value)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

    private static string? Base(JsonObject action, string field) => BaseOf(action, field);

    private static string? BaseOf(JsonObject owner, string field)
        => owner[field] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
