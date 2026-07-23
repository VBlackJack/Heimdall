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
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Persists and loads terminal macros beneath the user-writable application data root.
/// </summary>
public static class MacroService
{
    private static readonly string MacroDirectory =
        ApplicationDataPathResolver.GetMacrosDirectory(
            ApplicationDataPathResolver.Resolve());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Saves a macro to a JSON file named after its ID.</summary>
    public static async Task SaveMacroAsync(TerminalMacro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        Directory.CreateDirectory(MacroDirectory);

        var filePath = Path.Combine(MacroDirectory, $"{macro.Id}.json");
        var json = JsonSerializer.Serialize(macro, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8).ConfigureAwait(false);

        Core.Logging.FileLogger.Info($"Macro saved: {macro.Name} ({macro.Id})");
    }

    /// <summary>Loads all saved macros from the macros directory.</summary>
    public static async Task<List<TerminalMacro>> LoadMacrosAsync()
    {
        var macros = new List<TerminalMacro>();

        if (!Directory.Exists(MacroDirectory))
        {
            return macros;
        }

        foreach (var file in Directory.GetFiles(MacroDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, System.Text.Encoding.UTF8).ConfigureAwait(false);
                var macro = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);
                if (macro is not null)
                {
                    macros.Add(macro);
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"Failed to load macro from {file}: {ex.Message}");
            }
        }

        return macros.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Loads all saved macros synchronously. Use for UI-thread context menu population.</summary>
    public static List<TerminalMacro> LoadMacros()
    {
        var macros = new List<TerminalMacro>();

        if (!Directory.Exists(MacroDirectory))
        {
            return macros;
        }

        foreach (var file in Directory.GetFiles(MacroDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                var macro = JsonSerializer.Deserialize<TerminalMacro>(json, ReadOptions);
                if (macro is not null)
                {
                    macros.Add(macro);
                }
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"Failed to load macro from {file}: {ex.Message}");
            }
        }

        return macros.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Deletes a macro file by its ID.</summary>
    public static void DeleteMacro(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var filePath = Path.Combine(MacroDirectory, $"{id}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            Core.Logging.FileLogger.Info($"Macro deleted: {id}");
        }
    }
}
