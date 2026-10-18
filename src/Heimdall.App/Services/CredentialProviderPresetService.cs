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
    /// <summary>The locale key of the first entry, which applies no preset.</summary>
    internal const string CustomLabelKey = "CredProvPresetCustom";

    // Product names are not translated; only the "Custom" entry is, through CustomLabelKey.
    // 1Password: "op read op://{Title}/password" named a vault called {Title} and an item called
    // "password", with no field, which op rejects; "op item get" looks the item up by name.
    // KPScript prints an "OK: ..." status line after the value and pass prints the notes after
    // the password, so both presets turn "first line only" on.
    private static readonly CredentialProviderPreset[] Presets =
    [
        new(null, "", FirstLineOnly: false),
        new("KeePassXC", "keepassxc-cli show -s -q -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false),
        new("KeePassXC (key file)", "keepassxc-cli show -s -q -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false),
        new("KeePassXC (key file only)", "keepassxc-cli show -s -q --no-password -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false),
        new("KeePass2 (KPScript)", "KPScript.exe -c:GetEntryString \"{Database}\" -Field:Password -ref-Title:\"{Title}\"", FirstLineOnly: true),
        new("Bitwarden CLI", "bw get password \"{Title}\"", FirstLineOnly: false),
        new("1Password CLI", "op item get \"{Title}\" --fields label=password --reveal", FirstLineOnly: false),
        new("pass (GPG)", "pass show \"{Title}\"", FirstLineOnly: true),
    ];

    /// <summary>
    /// Gets the labels shown in the preset selector, preserving display order.
    /// </summary>
    /// <param name="localize">Resolves the locale key of the "Custom" entry.</param>
    public IReadOnlyList<string> GetPresetLabels(Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        return Presets.Select(preset => preset.Label ?? localize(CustomLabelKey)).ToArray();
    }

    /// <summary>
    /// Resolves a preset by its selector index: its command and whether its tool prints more
    /// than the value, so that only the first line is to be read.
    /// </summary>
    public bool TryGetPreset(int selectedIndex, out string command, out bool firstLineOnly)
    {
        if (selectedIndex <= 0 || selectedIndex >= Presets.Length)
        {
            command = string.Empty;
            firstLineOnly = false;
            return false;
        }

        command = Presets[selectedIndex].Command;
        firstLineOnly = Presets[selectedIndex].FirstLineOnly;
        return true;
    }

    private sealed record CredentialProviderPreset(string? Label, string Command, bool FirstLineOnly);
}
