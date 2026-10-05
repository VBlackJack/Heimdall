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
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>A setting a settings file changes, with the value it has and the value it would take.</summary>
/// <param name="Key">The <see cref="AppSettings"/> property name.</param>
/// <param name="Before">The current value, as JSON.</param>
/// <param name="After">The value from the file, as JSON.</param>
internal sealed record SettingsTransferChange(string Key, JsonNode? Before, JsonNode? After);

/// <summary>
/// Builds and reads the portable settings file: the preferences the settings panel edits, and
/// nothing that is a secret or belongs to this machine.
/// </summary>
/// <remarks>
/// <para>What travels is an allow-list, derived from the panel: a setting is exported only when
/// the panel edits it. Everything else in <see cref="AppSettings"/> stays behind by construction -
/// the vault material, the PIN hash and its lockout, the Git access token, the gateways and
/// their stored passwords, window geometry, the last selection, the migration markers - so a
/// secret added to the settings type later cannot leak through an export nobody updated.</para>
/// <para>Two panel settings are held back as well: the credential provider unlock secret, which is
/// a secret the panel happens to edit, and the tools panel toggle, which is window state. A path
/// under the user's own profile is machine-specific and is left out unless the user asks for it.
/// The same allow-list filters an import, so a hand-edited file cannot slip a PIN hash in.</para>
/// </remarks>
internal static class SettingsTransfer
{
    /// <summary>The value of <see cref="FormatProperty"/> in a settings file.</summary>
    internal const string FormatName = "heimdall-settings";

    /// <summary>The version this build writes and reads.</summary>
    internal const int FormatVersion = 1;

    internal const string FormatProperty = "format";

    internal const string VersionProperty = "version";

    internal const string SettingsProperty = "settings";

    /// <summary>Panel settings that are never exported, although the panel edits them.</summary>
    private static readonly HashSet<string> HeldBack = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.CredentialProviderUnlockSecretEncrypted),
        nameof(AppSettings.ShowToolsPanel),
    };

    /// <summary>The <see cref="AppSettings"/> properties a settings file may carry.</summary>
    internal static IReadOnlySet<string> TransferableKeys { get; } = BuildTransferableKeys();

    private static HashSet<string> BuildTransferableKeys()
    {
        HashSet<string> settingNames = typeof(AppSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string panelName in SettingsViewModel.PersistedPropertyNames)
        {
            string settingName = SettingsViewModel.SettingNameOf(panelName);
            if (settingNames.Contains(settingName) && !HeldBack.Contains(settingName))
            {
                keys.Add(settingName);
            }
        }

        return keys;
    }

    /// <summary>
    /// Builds the settings file for <paramref name="settings"/>.
    /// </summary>
    /// <param name="settings">The settings to export.</param>
    /// <param name="includeUserPaths">Whether values naming the user's profile folder travel too.</param>
    /// <param name="userProfile">The user's profile folder.</param>
    /// <param name="heldBackPaths">How many values were left out because they name that folder.</param>
    internal static JsonObject Export(
        AppSettings settings,
        bool includeUserPaths,
        string userProfile,
        out int heldBackPaths)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        JsonObject exported = new();
        heldBackPaths = 0;

        foreach (string key in TransferableKeys.Order(StringComparer.Ordinal))
        {
            if (!all.TryGetPropertyValue(key, out JsonNode? value))
            {
                continue;
            }

            if (!includeUserPaths && NamesUserProfile(value, userProfile))
            {
                heldBackPaths++;
                continue;
            }

            exported[key] = value?.DeepClone();
        }

        return new JsonObject
        {
            [FormatProperty] = FormatName,
            [VersionProperty] = FormatVersion,
            [SettingsProperty] = exported,
        };
    }

    /// <summary>
    /// Lays the settings of a settings file over <paramref name="current"/>.
    /// </summary>
    /// <returns>The merged settings, and the settings whose value changed with both values.</returns>
    /// <exception cref="FormatException">The document is not a settings file this build reads.</exception>
    internal static (AppSettings Merged, IReadOnlyList<SettingsTransferChange> Changed) Import(AppSettings current, JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (document is not JsonObject root
            || root[FormatProperty]?.GetValueKind() != JsonValueKind.String
            || root[FormatProperty]!.GetValue<string>() != FormatName
            || root[VersionProperty] is not JsonValue versionValue
            || !versionValue.TryGetValue(out int version)
            || version != FormatVersion
            || root[SettingsProperty] is not JsonObject imported)
        {
            throw new FormatException("Not a Heimdall settings file of a supported version.");
        }

        JsonObject merged = JsonSerializer.SerializeToNode(current)!.AsObject();
        List<SettingsTransferChange> changed = [];
        foreach ((string key, JsonNode? value) in imported)
        {
            if (!TransferableKeys.Contains(key))
            {
                continue;
            }

            // A null for a setting that cannot be null, or a null inside a list, passed the import
            // and threw only once the panel loaded it, after the user had confirmed.
            if ((value is null && !AcceptsNull(key))
                || (value is JsonArray items && items.Any(item => item is null)))
            {
                throw new FormatException($"The settings file holds an empty value for {key}.");
            }

            JsonNode? before = merged[key];
            if (!JsonNode.DeepEquals(before, value))
            {
                changed.Add(new SettingsTransferChange(key, before?.DeepClone(), value?.DeepClone()));
            }

            merged[key] = value?.DeepClone();
        }

        AppSettings result = merged.Deserialize<AppSettings>()
            ?? throw new FormatException("The settings file could not be read.");
        return (result, changed);
    }

    private static readonly System.Reflection.NullabilityInfoContext Nullability = new();

    /// <summary>Whether the setting saved under <paramref name="key"/> may hold null.</summary>
    private static bool AcceptsNull(string key)
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettings).GetProperty(key);
        if (property is null)
        {
            return false;
        }

        lock (Nullability)
        {
            return Nullability.Create(property).WriteState == System.Reflection.NullabilityState.Nullable;
        }
    }

    /// <summary>Whether a value names the user's profile folder anywhere inside it.</summary>
    internal static bool NamesUserProfile(JsonNode? value, string userProfile)
    {
        if (value is null || string.IsNullOrWhiteSpace(userProfile))
        {
            return false;
        }

        return value switch
        {
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String =>
                scalar.GetValue<string>().Contains(userProfile, StringComparison.OrdinalIgnoreCase),
            JsonArray array => array.Any(item => NamesUserProfile(item, userProfile)),
            JsonObject obj => obj.Any(property => NamesUserProfile(property.Value, userProfile)),
            _ => false,
        };
    }
}
