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
using System.Text;
using System.Text.Json;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

internal interface IPasswordPresetStorage
{
    List<PasswordGeneratorViewModel.PasswordPreset> Load();
    void Save(List<PasswordGeneratorViewModel.PasswordPreset> presets);
}

internal sealed class PasswordPresetStorage : IPasswordPresetStorage
{
    private const string PresetsFileName = "password-presets.json";
    private readonly string _filePath;

    public PasswordPresetStorage()
        : this(ApplicationDataPathResolver.Resolve())
    {
    }

    internal PasswordPresetStorage(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _filePath = Path.Combine(directoryPath, PresetsFileName);
    }

    public List<PasswordGeneratorViewModel.PasswordPreset> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<PasswordGeneratorViewModel.PasswordPreset>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(List<PasswordGeneratorViewModel.PasswordPreset> presets)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(presets, options);
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to save custom presets: {ex.Message}");
        }
    }
}
