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
    // the password, so both presets turn "first line only" on. KPScript also takes
    // -FailIfNoEntry (documented on keepass.info, not measured here): without it a missing entry
    // is not an error, and the status line left on line 1 would be read as the password.
    // KPScript.exe and keepassxc-cli are not on PATH after a standard install, so their presets
    // start with the installed program's full path when it is found (see ResolveCommand).
    private const string KpScriptTool = "KPScript.exe";
    private const string KpScriptRelativePath = @"KeePass Password Safe 2\KPScript.exe";
    private const string KeePassXcTool = "keepassxc-cli";
    private const string KeePassXcRelativePath = @"KeePassXC\keepassxc-cli.exe";
    private const char PathQuote = '"';

    private static readonly CredentialProviderPreset[] Presets =
    [
        new(null, "", FirstLineOnly: false),
        new("KeePassXC", "keepassxc-cli show -s -q -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false, KeePassXcTool, KeePassXcRelativePath),
        new("KeePassXC (key file)", "keepassxc-cli show -s -q -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false, KeePassXcTool, KeePassXcRelativePath),
        new("KeePassXC (key file only)", "keepassxc-cli show -s -q --no-password -k \"{KeyFile}\" -a Password \"{Database}\" \"{Title}\"", FirstLineOnly: false, KeePassXcTool, KeePassXcRelativePath),
        new("KeePass2 (KPScript)", "KPScript.exe -c:GetEntryString \"{Database}\" -Field:Password -ref-Title:\"{Title}\" -FailIfNoEntry", FirstLineOnly: true, KpScriptTool, KpScriptRelativePath),
        new("Bitwarden CLI", "bw get password \"{Title}\"", FirstLineOnly: false),
        new("1Password CLI", "op item get \"{Title}\" --fields label=password --reveal", FirstLineOnly: false),
        new("pass (GPG)", "pass show \"{Title}\"", FirstLineOnly: true),
    ];

    private readonly Func<string, bool> _fileExists;

    /// <summary>Creates the service, looking for programs on the file system.</summary>
    public CredentialProviderPresetService()
        : this(File.Exists)
    {
    }

    /// <summary>Creates the service with an explicit file-existence check.</summary>
    /// <param name="fileExists">Tells whether the program at a full path is installed.</param>
    public CredentialProviderPresetService(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        _fileExists = fileExists;
    }

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

        command = ResolveCommand(Presets[selectedIndex]);
        firstLineOnly = Presets[selectedIndex].FirstLineOnly;
        return true;
    }

    /// <summary>
    /// The selector index of the preset whose command is <paramref name="command"/>, or 0 (the
    /// "Custom" entry) when it is blank or no preset matches it.
    /// </summary>
    public int IndexOfCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return 0;
        }

        string trimmed = command.Trim();
        for (int index = 1; index < Presets.Length; index++)
        {
            if (IsKnownForm(Presets[index], trimmed))
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>
    /// The preset's command, started by the quoted full path of its program when that file exists
    /// under Program Files, then under Program Files (x86), else by the bare program name.
    /// </summary>
    private string ResolveCommand(CredentialProviderPreset preset)
    {
        foreach (string path in EnumerateInstallPaths(preset))
        {
            if (_fileExists(path))
            {
                return WithProgram(preset, path);
            }
        }

        return preset.Command;
    }

    /// <summary>
    /// Whether <paramref name="saved"/> is the preset's command, bare or started by the quoted full
    /// path of its program under any install folder. Everything after the program must match exactly.
    /// </summary>
    private static bool IsKnownForm(CredentialProviderPreset preset, string saved)
    {
        if (string.Equals(preset.Command, saved, StringComparison.Ordinal))
        {
            return true;
        }

        if (preset.Tool is null || preset.RelativePath is null || saved.Length == 0 || saved[0] != PathQuote)
        {
            return false;
        }

        int closing = saved.IndexOf(PathQuote, 1);
        if (closing < 0)
        {
            return false;
        }

        string fileName = Path.GetFileName(saved[1..closing]);
        string expectedFileName = Path.GetFileName(preset.RelativePath);
        return string.Equals(fileName, expectedFileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(saved[(closing + 1)..], preset.Command[preset.Tool.Length..], StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateInstallPaths(CredentialProviderPreset preset)
    {
        if (preset.Tool is null || preset.RelativePath is null)
        {
            yield break;
        }

        foreach (Environment.SpecialFolder folder in ProgramFilesFolders)
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, preset.RelativePath);
            }
        }
    }

    private static string WithProgram(CredentialProviderPreset preset, string fullPath) =>
        PathQuote + fullPath + PathQuote + preset.Command[preset.Tool!.Length..];

    private static readonly Environment.SpecialFolder[] ProgramFilesFolders =
    [
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
    ];

    private sealed record CredentialProviderPreset(
        string? Label,
        string Command,
        bool FirstLineOnly,
        string? Tool = null,
        string? RelativePath = null);
}
