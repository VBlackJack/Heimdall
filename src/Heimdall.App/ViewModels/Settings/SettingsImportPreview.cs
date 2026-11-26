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

using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// Words the confirmation shown before a settings file is loaded into the panel: each change by
/// the label the panel shows for it, where it lives, the value it has and the value it takes.
/// </summary>
internal static class SettingsImportPreview
{
    /// <summary>How many changed settings the preview names before summing up the rest.</summary>
    internal const int MaxLines = 20;

    /// <summary>How long a value may run in the preview before it is cut short.</summary>
    internal const int MaxValueLength = 60;

    private const string Ellipsis = "...";

    /// <summary>
    /// Settings that decide what runs on this computer: commands, executables, where code is
    /// fetched from, and the shared session files read at every start (a session file names hosts
    /// and the commands typed into them, and a network path is opened with the user's Windows
    /// sign-in), and the list of switched-off tool packs, which is how the scanning and enumeration
    /// tools are turned on. A settings file is shared between people, and these are the lines an
    /// author with bad intent would hide among harmless ones.
    /// </summary>
    internal static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "UseExternalCredentialProvider",
        "CredentialProviderType",
        "CredentialProviderCommand",
        "CredentialProviderUsernameCommand",
        "CredentialProviderDatabase",
        "CredentialProviderKeyFile",
        "ExternalTools",
        "PlinkPath",
        "PuttyPath",
        "PsftpPath",
        "X11ServerPath",
        "X11AutoStart",
        "CitrixSelfServicePath",
        "PowerShellExecutionPolicy",
        "ExternalEditorPath",
        "SysinternalsPath",
        "NirSoftPath",
        "NanaRunPath",
        "CmdLibGitSyncUrl",
        "SharedSessionSources",
        "DisabledToolPacks",
        "RecipeProgramFolders",
        SettingsTransfer.RecipesPackTurnedOnKey,
    };

    private const string ListSeparator = ", ";

    /// <summary>The whole confirmation text for <paramref name="changes"/>.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="changes">The settings the file changes, in file order.</param>
    internal static string Compose(LocalizationManager localizer, IReadOnlyList<SettingsTransferChange> changes)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(changes);

        // The sensitive changes come first, every one of them, at full length. The preview used to
        // name the first twenty changes in the file's own order, which its author chooses, and cut
        // every value at sixty characters: a credential command placed twenty-first read as
        // "and 1 more".
        List<SettingsTransferChange> sensitive = changes.Where(change => SensitiveKeys.Contains(change.Key)).ToList();
        List<SettingsTransferChange> others = changes.Where(change => !SensitiveKeys.Contains(change.Key)).ToList();

        string lines = string.Join(
            Environment.NewLine,
            others.Take(MaxLines).Select(change => DescribeChange(localizer, change)));

        // The notice that the file turns the Recipes pack on is a line of its own, not a setting: the
        // count in the header is the number of settings.
        int settingCount = changes.Count(change => change.Key != SettingsTransfer.RecipesPackTurnedOnKey);
        string text = localizer.FormatCount(
            settingCount,
            "SettingsImportPreviewOne",
            "SettingsImportPreview",
            settingCount,
            lines);

        int rest = others.Count - MaxLines;
        if (rest > 0)
        {
            text += Environment.NewLine + localizer.FormatCount(
                rest,
                "SettingsImportPreviewMoreOne",
                "SettingsImportPreviewMore",
                rest);
        }

        if (sensitive.Count == 0)
        {
            return text;
        }

        string warning = localizer["SettingsImportPreviewSensitive"] + Environment.NewLine + string.Join(
            Environment.NewLine,
            sensitive.Select(change => DescribeChange(localizer, change, fullLength: true)));
        return warning + Environment.NewLine + Environment.NewLine + text;
    }

    /// <summary>One line of the preview: where the setting lives, then its old and new value.</summary>
    /// <remarks>
    /// A setting the label catalog does not know falls back to its property name; the catalog
    /// test fails for every transferable setting without an entry, so the fallback never ships.
    /// </remarks>
    internal static string DescribeChange(LocalizationManager localizer, SettingsTransferChange change)
        => DescribeChange(localizer, change, fullLength: false);

    /// <param name="fullLength">Whether values are shown whole, lists of tools item by item.</param>
    internal static string DescribeChange(LocalizationManager localizer, SettingsTransferChange change, bool fullLength)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(change);

        if (change.Key == SettingsTransfer.RecipesPackTurnedOnKey)
        {
            return localizer["SettingsImportPreviewRecipesOn"];
        }

        string name = SettingsLabelCatalog.TryGetPlace(change.Key, out SettingsPanelPlace? place) && place is not null
            ? DescribePlace(localizer, place)
            : change.Key;
        return localizer.Format(
            "SettingsImportPreviewLine",
            name,
            fullLength ? DescribeFullValue(localizer, change.Before, change.Key) : DescribeValue(localizer, change.Before),
            fullLength ? DescribeFullValue(localizer, change.After, change.Key) : DescribeValue(localizer, change.After));
    }

    /// <summary>
    /// A sensitive value as the preview shows it: never cut, a tool list tool by tool, a list of
    /// shared session sources source by source.
    /// </summary>
    /// <param name="key">The setting the value belongs to; it decides how a list of entries reads.</param>
    internal static string DescribeFullValue(LocalizationManager localizer, JsonNode? value, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        switch (value)
        {
            case JsonArray array when array.Count > 0 && array.All(item => item is JsonObject):
                Func<JsonObject, string> describe = string.Equals(key, "SharedSessionSources", StringComparison.Ordinal)
                    ? source => DescribeSharedSource(localizer, source)
                    : tool => DescribeTool(localizer, tool);
                return string.Join(ListSeparator, array.Select(item => describe((JsonObject)item!)));
            case JsonArray names when names.Count > 0 && names.All(IsString):
                return string.Join(ListSeparator, names.Select(item => Visible(item!.GetValue<string>())));
            case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String
                && !string.IsNullOrEmpty(scalar.GetValue<string>()):
                return Visible(scalar.GetValue<string>());
            default:
                return DescribeValue(localizer, value);
        }
    }

    private static string DescribeTool(LocalizationManager localizer, JsonObject tool)
    {
        string name = Visible(ReadString(tool, "Name"));
        string executable = Visible(ReadString(tool, "ExecutablePath"));
        bool elevated = tool["RunAsAdministrator"]?.GetValueKind() == JsonValueKind.True;
        return localizer.Format(elevated ? "SettingsImportValueToolAdmin" : "SettingsImportValueTool", name, executable);
    }

    /// <summary>A shared session source: its name and the file it reads, and whether it is off.</summary>
    private static string DescribeSharedSource(LocalizationManager localizer, JsonObject source)
    {
        string name = Visible(ReadString(source, "Name"));
        string path = Visible(ReadString(source, "Path"));
        bool disabled = source["Enabled"]?.GetValueKind() == JsonValueKind.False;
        return localizer.Format(
            disabled ? "SettingsImportValueSharedSourceDisabled" : "SettingsImportValueSharedSource",
            name,
            path);
    }

    private static string ReadString(JsonObject obj, string property)
        => obj[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : string.Empty;

    /// <summary>A setting value as the preview shows it.</summary>
    internal static string DescribeValue(LocalizationManager localizer, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        switch (value)
        {
            case null:
                return localizer["SettingsImportValueEmpty"];
            case JsonArray array when array.Count > 0 && array.All(IsString):
                return Shorten(string.Join(ListSeparator, array.Select(item => item!.GetValue<string>())));
            case JsonArray array:
                return localizer.FormatCount(array.Count, "SettingsImportValueItemsOne", "SettingsImportValueItems", array.Count);
            case JsonObject obj:
                return localizer.FormatCount(obj.Count, "SettingsImportValueItemsOne", "SettingsImportValueItems", obj.Count);
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => localizer["SettingsImportValueOn"],
            JsonValueKind.False => localizer["SettingsImportValueOff"],
            JsonValueKind.Null => localizer["SettingsImportValueEmpty"],
            JsonValueKind.String when string.IsNullOrEmpty(value.GetValue<string>()) => localizer["SettingsImportValueEmpty"],
            JsonValueKind.String => Shorten(value.GetValue<string>()),
            _ => Shorten(value.ToJsonString()),
        };
    }

    private static string DescribePlace(LocalizationManager localizer, SettingsPanelPlace place)
    {
        string area = localizer.Format("SettingsImportPreviewPath", localizer[place.TabKey], localizer[place.AreaKey]);
        return localizer.Format("SettingsImportPreviewPath", area, localizer[place.LabelKey]);
    }

    private static bool IsString(JsonNode? item) => item?.GetValueKind() == JsonValueKind.String;

    /// <summary>A value written out whole, with every control and formatting character made visible.</summary>
    private static string Visible(string text) => UntrustedText.MakeVisible(text, UntrustedText.NoLengthLimit);

    /// <summary>
    /// A value as the preview shows it: control characters made visible, then cut to
    /// <see cref="MaxValueLength"/> characters.
    /// </summary>
    /// <remarks>
    /// The escaping always runs on the raw text, and a list is joined raw before it is escaped:
    /// the separator is plain ASCII, so the result equals the items escaped one by one, and the
    /// cut is made by the escaper itself, which never splits an escape. The first pass keeps
    /// <see cref="MaxValueLength"/> characters of the value and appends the marker, so it can run
    /// three characters over; when it does, a second pass keeps room for the marker.
    /// </remarks>
    private static string Shorten(string text)
    {
        string visible = UntrustedText.MakeVisible(text, MaxValueLength);
        return visible.Length <= MaxValueLength
            ? visible
            : UntrustedText.MakeVisible(text, MaxValueLength - Ellipsis.Length);
    }
}
