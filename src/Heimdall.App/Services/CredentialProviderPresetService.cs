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

namespace Heimdall.App.Services;

/// <summary>
/// Provides the built-in external credential provider command presets shown in Settings.
/// </summary>
public sealed class CredentialProviderPresetService
{
    private static readonly CredentialProviderPreset[] Presets =
    [
        new("Custom", ""),
        new("KeePassXC", "keepassxc-cli show -s -q -a Password \"{Database}\" \"{Title}\""),
        new("KeePassXC (key file)", "keepassxc-cli show -s -q -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\""),
        new("KeePassXC (key file only)", "keepassxc-cli show -s -q --no-password -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\""),
        new("KeePass2 (KPScript)", "KPScript.exe -c:GetEntryString \"{Database}\" -Field:Password -ref-Title:\"{Title}\""),
        new("Bitwarden CLI", "bw get password \"{Title}\""),
        new("1Password CLI", "op read \"op://{Title}/password\""),
        new("pass (GPG)", "pass show \"{Title}\""),
    ];

    /// <summary>
    /// Gets the labels shown in the preset selector, preserving display order.
    /// </summary>
    public IReadOnlyList<string> GetPresetLabels()
    {
        return Presets.Select(static preset => preset.Label).ToArray();
    }

    /// <summary>
    /// Resolves a preset command by its selector index.
    /// </summary>
    public bool TryGetCommand(int selectedIndex, out string command)
    {
        if (selectedIndex <= 0 || selectedIndex >= Presets.Length)
        {
            command = string.Empty;
            return false;
        }

        command = Presets[selectedIndex].Command;
        return true;
    }

    private sealed record CredentialProviderPreset(string Label, string Command);
}
