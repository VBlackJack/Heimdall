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

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Views.Tools;

public partial class HackerSimulatorView
{
    private const int MaxTemplateRecursionDepth = 12;
    private const int DefaultTypeDelayMs = 50;
    private const int DefaultLoopDelayMs = 5000;
    private const int DefaultGlitchDelayMs = 100;

    private static readonly Regex s_templateTokenRegex = new(@"\{\{([^{}]+)\}\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class LocalizedTextDto
    {
        public string En { get; set; } = string.Empty;
        public string Fr { get; set; } = string.Empty;
    }

    private sealed class ScenarioPackFile
    {
        public List<ScenarioPackScenario> Scenarios { get; set; } = [];
    }

    private sealed class ScenarioPackScenario
    {
        public string Id { get; set; } = string.Empty;
        public string TitleKey { get; set; } = string.Empty;
        public LocalizedTextDto Subtitle { get; set; } = new();
        public string Category { get; set; } = string.Empty;
        public string Realism { get; set; } = string.Empty;
        public string Theme { get; set; } = "green";
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, string> Variables { get; set; } = [];
        public List<ScenarioPackAction> Actions { get; set; } = [];
        public bool AllowRandom { get; set; } = true;
    }

    private sealed class ScenarioPackAction
    {
        public string Kind { get; set; } = "line";
        public LocalizedTextDto? Text { get; set; }
        public LocalizedTextDto? Label { get; set; }
        public LocalizedTextDto? Suffix { get; set; }
        public string? Color { get; set; }
        public string? CompleteColor { get; set; }
        public List<int>? Steps { get; set; }
        public int DelayMs { get; set; } = 80;
    }

    private sealed class PlaylistPackFile
    {
        public List<PlaylistPackDefinition> Playlists { get; set; } = [];
    }

    private sealed class PlaylistPackDefinition
    {
        public string Id { get; set; } = string.Empty;
        public LocalizedTextDto Title { get; set; } = new();
        public LocalizedTextDto Description { get; set; } = new();
        public List<string> ScenarioIds { get; set; } = [];
    }

    private sealed record ScenarioPlaylistDefinition(
        string Id,
        LocalizedText Title,
        LocalizedText Description,
        IReadOnlyList<string> ScenarioIds);

    private sealed record ScenarioPlaylistPickerItem(string Id, string Display, ScenarioPlaylistDefinition? Playlist);

    private readonly Dictionary<string, ScenarioPackScenario> _externalScenarioPacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScenarioPlaylistDefinition> _playlistDefinitions = [];
    private bool _externalCatalogLoaded;
    private string? _selectedPlaylistId;
    private int _playlistCursor;
    private bool _suppressPlaylistEvents;

    private void EnsureExternalCatalogLoaded()
    {
        if (_externalCatalogLoaded)
            return;

        _externalCatalogLoaded = true;
        LoadExternalScenarioPacks();
        LoadExternalPlaylists();
    }

    private void LoadExternalScenarioPacks()
    {
        _externalScenarioPacks.Clear();

        try
        {
            string? path = ResolveConfigOverridePath(
                "hacker-simulator.scenarios.json",
                "hacker-simulator.scenarios.default.json");

            if (path is null || !File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<ScenarioPackFile>(json, s_jsonOptions);

            foreach (var scenario in file?.Scenarios ?? [])
            {
                if (string.IsNullOrWhiteSpace(scenario.Id))
                    continue;

                _externalScenarioPacks[scenario.Id] = scenario;
            }
        }
        catch (Exception)
        {
            _externalScenarioPacks.Clear();
        }
    }

    private void LoadExternalPlaylists()
    {
        _playlistDefinitions.Clear();

        try
        {
            string? path = ResolveConfigOverridePath(
                "hacker-simulator.playlists.json",
                "hacker-simulator.playlists.default.json");

            if (path is not null && File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var file = JsonSerializer.Deserialize<PlaylistPackFile>(json, s_jsonOptions);

                foreach (var playlist in file?.Playlists ?? [])
                {
                    if (string.IsNullOrWhiteSpace(playlist.Id) || playlist.ScenarioIds.Count == 0)
                        continue;

                    _playlistDefinitions.Add(new ScenarioPlaylistDefinition(
                        playlist.Id,
                        new LocalizedText(playlist.Title.En, playlist.Title.Fr),
                        new LocalizedText(playlist.Description.En, playlist.Description.Fr),
                        playlist.ScenarioIds));
                }
            }
        }
        catch (Exception)
        {
            _playlistDefinitions.Clear();
        }

        if (_playlistDefinitions.Count == 0)
        {
            _playlistDefinitions.AddRange(
            [
                new("client-demo",
                    new("Client Demo (3 min)", "Demo Client (3 min)"),
                    new("Short polished audit story for customer-facing demos.", "Demonstration courte et propre pour les demos client."),
                    ["serverchain", "vault", "bluegreen"]),
                new("devops",
                    new("DevOps / Platform", "Mode DevOps / Plateforme"),
                    new("Infrastructure rollout and automation chain.", "Chaine d'automatisation et de deploiement infrastructure."),
                    ["ansible", "serverchain", "awx", "helm", "bluegreen"]),
                new("compliance",
                    new("Compliance / Hardening", "Mode Conformite / Durcissement"),
                    new("Fleet hardening, secret rotation, and evidence style recap.", "Durcissement, rotation de secrets et recapitulatif orientee preuve."),
                    ["rolerollout", "patchfleet", "vault", "pki"]),
            ]);
        }
    }

    private static string? ResolveConfigOverridePath(string overrideFileName, string defaultFileName)
    {
        string configPath = Path.Combine(
            AppContext.BaseDirectory,
            AppConstants.BundledConfigDirectoryName);
        string overridePath = Path.Combine(configPath, overrideFileName);
        if (File.Exists(overridePath))
            return overridePath;

        string defaultPath = Path.Combine(configPath, defaultFileName);
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private IEnumerable<ScenarioDefinition> GetExternalScenarioDefinitions()
    {
        EnsureExternalCatalogLoaded();

        foreach (var pack in _externalScenarioPacks.Values)
        {
            if (!TryParseCategory(pack.Category, out var category)
                || !TryParseRealism(GetRealismOrDefault(pack.Realism), out var realism))
            {
                continue;
            }

            yield return new ScenarioDefinition(
                pack.Id,
                pack.TitleKey,
                new LocalizedText(pack.Subtitle.En, pack.Subtitle.Fr),
                category,
                realism,
                ResolveScenarioTheme(pack.Theme),
                pack.Tags.ToArray(),
                () => BuildExternalScenarioScript(pack),
                IsMatrix: false,
                AllowRandom: pack.AllowRandom);
        }
    }

    private static bool TryParseCategory(string value, out ScenarioCategory category)
        => Enum.TryParse(value, ignoreCase: true, out category);

    private static bool TryParseRealism(string value, out ScenarioRealism realism)
        => Enum.TryParse(value, ignoreCase: true, out realism);

    private ScenarioTheme ResolveScenarioTheme(string theme) => theme.Trim().ToLowerInvariant() switch
    {
        "green" => Theme(s_green, 0, 255, 65),
        "cyan" => Theme(s_cyan, 0, 255, 255),
        "yellow" => Theme(s_yellow, 255, 215, 0),
        "red" => Theme(s_red, 255, 68, 68),
        "amber" => Theme(s_amber, 255, 176, 0),
        _ => Theme(s_green, 0, 255, 65),
    };

    private List<ScriptAction> BuildExternalScenarioScript(ScenarioPackScenario scenario)
    {
        var script = new List<ScriptAction>();
        var variables = ResolveScenarioVariables(scenario.Variables);

        foreach (var action in scenario.Actions)
        {
            string kind = action.Kind.Trim().ToLowerInvariant();
            switch (kind)
            {
                case "line":
                    script.Add(Line(
                        ResolveLocalizedTemplate(action.Text, variables),
                        ResolveActionColor(action.Color),
                        action.DelayMs));
                    break;

                case "type":
                    script.Add(Type(
                        ResolveLocalizedTemplate(action.Text, variables),
                        ResolveActionColor(action.Color),
                        action.DelayMs > 0 ? action.DelayMs : DefaultTypeDelayMs));
                    break;

                case "wait":
                    script.Add(Wait(action.DelayMs));
                    break;

                case "loop":
                    script.Add(Loop(action.DelayMs > 0 ? action.DelayMs : DefaultLoopDelayMs));
                    break;

                case "glitch":
                    script.Add(new ScriptAction(Act.GlitchBurst, "", null, action.DelayMs > 0 ? action.DelayMs : DefaultGlitchDelayMs));
                    break;

                case "progress":
                    AddExternalProgress(script, action, variables);
                    break;
            }
        }

        return script;
    }

    private void AddExternalProgress(List<ScriptAction> script, ScenarioPackAction action, IReadOnlyDictionary<string, string> variables)
    {
        string label = ResolveLocalizedTemplate(action.Label, variables);
        var steps = action.Steps?.Count > 0 ? action.Steps : [0, 25, 50, 75, 100];
        for (int i = 0; i < steps.Count; i++)
        {
            int percent = Math.Clamp(steps[i], 0, 100);
            string suffix = ResolveLocalizedTemplate(
                action.Suffix,
                variables,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["percent"] = percent.ToString(),
                });

            string text = $"{label} {Bar(percent)}";
            if (!string.IsNullOrWhiteSpace(suffix))
                text = $"{text} {suffix}";

            if (i == 0)
            {
                script.Add(Line(text, ResolveActionColor(action.Color), action.DelayMs));
            }
            else
            {
                SolidColorBrush? color = i == steps.Count - 1
                    ? ResolveActionColor(action.CompleteColor ?? action.Color)
                    : ResolveActionColor(action.Color);
                script.Add(Upd(text, color, action.DelayMs));
            }
        }
    }

    private Dictionary<string, string> ResolveScenarioVariables(IReadOnlyDictionary<string, string> definitions)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Resolve(string name, int depth = 0)
        {
            if (resolved.TryGetValue(name, out string? existing))
                return existing;

            if (!definitions.TryGetValue(name, out string? template))
                return string.Empty;

            if (depth > MaxTemplateRecursionDepth)
                return template;

            string value = ExpandTemplate(template, resolved, null, definitions, depth + 1);
            resolved[name] = value;
            return value;
        }

        foreach (string key in definitions.Keys)
            _ = Resolve(key);

        return resolved;
    }

    private string ResolveLocalizedTemplate(
        LocalizedTextDto? text,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string>? transient = null)
        => ExpandTemplate(Tx(text?.En ?? string.Empty, text?.Fr ?? text?.En ?? string.Empty), variables, transient);

    private string ExpandTemplate(
        string template,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string>? transient = null,
        IReadOnlyDictionary<string, string>? definitions = null,
        int depth = 0)
    {
        if (string.IsNullOrEmpty(template) || depth > 12)
            return template;

        return s_templateTokenRegex.Replace(template, match =>
        {
            string token = match.Groups[1].Value.Trim();
            return ResolveTemplateToken(token, variables, transient, definitions, depth + 1);
        });
    }

    private string ResolveTemplateToken(
        string token,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string>? transient,
        IReadOnlyDictionary<string, string>? definitions,
        int depth)
    {
        string[] parts = token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string expression = parts.Length > 0 ? parts[0] : token;
        string value = expression switch
        {
            var x when transient != null && transient.TryGetValue(x, out string? transientValue) => transientValue,
            var x when variables.TryGetValue(x, out string? variableValue) => variableValue,
            var x when definitions != null && definitions.TryGetValue(x, out string? definitionValue)
                => ExpandTemplate(definitionValue, variables, transient, definitions, depth + 1),
            var x when x.StartsWith("pick:", StringComparison.OrdinalIgnoreCase) => ResolvePickToken(x[5..]),
            var x when x.StartsWith("number:", StringComparison.OrdinalIgnoreCase) => ResolveNumberToken(x[7..]),
            var x when x.StartsWith("hex:", StringComparison.OrdinalIgnoreCase) => RandHex(Math.Max(1, ParseIntSafe(x[4..], 8))),
            "ip" => RandIp(),
            "mac" => RandMac(),
            _ => string.Empty,
        };

        for (int i = 1; i < parts.Length; i++)
        {
            value = parts[i].ToLowerInvariant() switch
            {
                "lower" => value.ToLowerInvariant(),
                "upper" => value.ToUpperInvariant(),
                _ => value,
            };
        }

        return value;
    }

    private string ResolvePickToken(string rawChoices)
    {
        var choices = rawChoices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (choices.Length == 0)
            return string.Empty;

        return choices[_rng.Next(choices.Length)];
    }

    private string ResolveNumberToken(string rawRange)
    {
        string[] bounds = rawRange.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bounds.Length != 2)
            return "0";

        int min = ParseIntSafe(bounds[0], 0);
        int max = ParseIntSafe(bounds[1], min);
        if (max < min)
            (min, max) = (max, min);

        return _rng.Next(min, max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static int ParseIntSafe(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private SolidColorBrush? ResolveActionColor(string? colorToken) => colorToken?.Trim().ToLowerInvariant() switch
    {
        "green" => s_green,
        "cyan" => s_cyan,
        "yellow" => s_yellow,
        "red" => s_red,
        "amber" => s_amber,
        "white" => s_white,
        "gray" => s_gray,
        _ => null,
    };

    private static string GetRealismOrDefault(string value)
        => string.IsNullOrWhiteSpace(value) ? nameof(ScenarioRealism.Demo) : value;
}
