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
using System.Reflection;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The "modified from default" markers and the per-setting reset.
/// </summary>
/// <remarks>
/// <para>The defaults are the factory file the full reset loads, read through the same path and
/// the same deserializer, so the marker and the reset can never disagree about what "default"
/// means. Nothing here keeps a second, hand-written copy of a default value.</para>
/// <para>The settings that carry a marker are derived from the same name match as the dirty
/// tracking, minus the exclusions below, so a new setting gets its marker the day it is added to
/// both the settings type and this panel.</para>
/// </remarks>
public partial class SettingsViewModel
{
    /// <summary>The file the factory defaults are read from, beside the executable.</summary>
    private const string FactoryDefaultsFileName = "settings.default.json";

    /// <summary>The suffix of the text property a number field is bound through.</summary>
    private const string MarkerTextSuffix = "Text";

    /// <summary>Separates the items of a list-valued default when it is worded for display.</summary>
    private const string DefaultListSeparator = ", ";

    /// <summary>
    /// Persisted panel properties that deliberately carry no marker, with the reason.
    /// </summary>
    /// <remarks>
    /// The unlock secret is a secret: a marker would say whether one is stored, and a reset would
    /// erase it. The tools panel flag is window state with no control on this panel. The external
    /// tools are an inventory of user-defined entries, not a preference with a value to go back
    /// to: resetting them would delete the user's tools. The resolution presets are list-valued
    /// too, but they are a preference with a built-in value, so they keep their marker and reset.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> DefaultMarkerExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(CredentialProviderUnlockSecret)] = "a secret",
            [nameof(ShowToolsPanel)] = "window state with no control on the panel",
            [nameof(ExternalTools)] = "an inventory of user-defined tools",
        };

    private static IReadOnlyDictionary<string, (PropertyInfo Panel, PropertyInfo Setting)>? s_markerProperties;

    private static readonly Lazy<AppSettings> s_factoryDefaults = new(
        () => ParseFactoryDefaults(ReadFactoryDefaultsJson()));

    private SettingDefaultStates? _defaults;

    /// <summary>The marker state of every setting that has one.</summary>
    public SettingDefaultStates Defaults => _defaults ??= CreateDefaultStates();

    /// <summary>The panel properties that carry a "modified from default" marker.</summary>
    internal static IReadOnlyCollection<string> DefaultMarkerSettings => MarkerProperties.Keys.ToList();

    /// <summary>
    /// The factory defaults, read once. Only ever read from: a reset copies values out of it.
    /// </summary>
    internal static AppSettings FactoryDefaults => s_factoryDefaults.Value;

    private static IReadOnlyDictionary<string, (PropertyInfo Panel, PropertyInfo Setting)> MarkerProperties
        => s_markerProperties ??= BuildMarkerProperties();

    private static IReadOnlyDictionary<string, (PropertyInfo Panel, PropertyInfo Setting)> BuildMarkerProperties()
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
        Dictionary<string, (PropertyInfo Panel, PropertyInfo Setting)> properties = new(StringComparer.Ordinal);

        foreach (string name in PersistedPropertyNames.Order(StringComparer.Ordinal))
        {
            bool isTextOfAField = name.EndsWith(MarkerTextSuffix, StringComparison.Ordinal)
                && PersistedPropertyNames.Contains(name[..^MarkerTextSuffix.Length]);
            if (isTextOfAField || DefaultMarkerExclusions.ContainsKey(name))
            {
                continue;
            }

            PropertyInfo? panel = typeof(SettingsViewModel).GetProperty(name, Public);
            PropertyInfo? setting = typeof(AppSettings).GetProperty(SettingNameOf(name), Public);
            if (panel is not null && setting is not null)
            {
                properties[name] = (panel, setting);
            }
        }

        return properties;
    }

    private SettingDefaultStates CreateDefaultStates()
    {
        SettingDefaultStates states = new(
            MarkerProperties.Keys.Select(name => new SettingDefaultState(name, ResetSettingToDefault)));
        foreach (SettingDefaultState state in states.All)
        {
            UpdateDefaultMarker(state);
        }

        return states;
    }

    private static string FactoryDefaultsPath => Path.Combine(
        AppContext.BaseDirectory,
        AppConstants.BundledConfigDirectoryName,
        FactoryDefaultsFileName);

    private static string? ReadFactoryDefaultsJson()
        => File.Exists(FactoryDefaultsPath) ? File.ReadAllText(FactoryDefaultsPath) : null;

    /// <summary>
    /// Reads the factory defaults from their file content: what the full reset loads and what the
    /// markers compare against.
    /// </summary>
    /// <remarks>
    /// Read from the bundled file rather than from <c>new AppSettings()</c>, because the file also
    /// carries the bundled external tools; a missing file falls back to the type's own defaults.
    /// </remarks>
    internal static AppSettings ParseFactoryDefaults(string? json)
        => json is null
            ? new AppSettings()
            : System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, ImportJsonOptions) ?? new AppSettings();

    private static async Task<AppSettings> LoadFactoryDefaultsAsync(CancellationToken cancellationToken)
    {
        // A fresh copy every time: the full reset mutates what it loads before applying it.
        string? json = File.Exists(FactoryDefaultsPath)
            ? await File.ReadAllTextAsync(FactoryDefaultsPath, cancellationToken)
            : null;
        return ParseFactoryDefaults(json);
    }

    /// <summary>The factory default of a marked setting, in the shape the panel property holds.</summary>
    internal object? DefaultPanelValueOf(string setting)
    {
        (PropertyInfo panel, PropertyInfo source) = MarkerProperties[setting];
        return ToPanelValue(source.GetValue(FactoryDefaults), panel.PropertyType);
    }

    /// <summary>Whether the pending value of a marked setting differs from its factory default.</summary>
    internal bool IsModifiedFromDefault(string setting)
        => !PanelValuesEqual(MarkerProperties[setting].Panel.GetValue(this), DefaultPanelValueOf(setting));

    /// <summary>
    /// Puts the factory default back into one setting as a pending edit.
    /// </summary>
    /// <remarks>
    /// The value goes through the property setter like a typed one, so the panel turns dirty and
    /// Save, Revert and the unsaved-changes prompt treat it like any other edit. A number field is
    /// bound to its text, so the text is rewritten too, or the box would go on showing the value
    /// being reset away from.
    /// </remarks>
    internal void ResetSettingToDefault(string setting)
    {
        PropertyInfo panel = MarkerProperties[setting].Panel;
        object? value = DefaultPanelValueOf(setting);
        panel.SetValue(this, value);

        if (value is int number
            && typeof(SettingsViewModel).GetProperty(setting + MarkerTextSuffix, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } text)
        {
            text.SetValue(this, number.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Brings the marker of one panel property up to date, when it has one.</summary>
    private void RefreshDefaultMarker(string propertyName)
    {
        if (_defaults is not null && _defaults.TryGet(propertyName, out SettingDefaultState state))
        {
            UpdateDefaultMarker(state);
        }
    }

    /// <summary>Rewords every marker, after the interface language changed.</summary>
    private void RefreshAllDefaultMarkers()
    {
        if (_defaults is null)
        {
            return;
        }

        foreach (SettingDefaultState state in _defaults.All)
        {
            UpdateDefaultMarker(state);
        }
    }

    private void UpdateDefaultMarker(SettingDefaultState state)
    {
        string defaultText = FormatDefaultValue(DefaultPanelValueOf(state.Setting));
        state.DefaultText = defaultText;
        state.BadgeAccessibleName = _localizer.Format("SettingsModifiedFromDefault", defaultText);
        state.ResetAccessibleName = _localizer.Format("SettingsResetToDefaultAccessible", defaultText);
        state.IsModified = IsModifiedFromDefault(state.Setting);
    }

    private string FormatDefaultValue(object? value) => value switch
    {
        bool on => _localizer[on ? "SettingsValueOn" : "SettingsValueOff"],
        string[] list => list.Length == 0
            ? _localizer["SettingsDefaultValueEmpty"]
            : string.Join(DefaultListSeparator, list),
        string text => string.IsNullOrWhiteSpace(text) ? _localizer["SettingsDefaultValueEmpty"] : text,
        null => _localizer["SettingsDefaultValueEmpty"],
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Converts a settings value to the type the panel property holds it as.</summary>
    /// <remarks>
    /// The panel keeps optional paths as empty text where the settings keep null, and the SSH agent
    /// preference as its name where the settings keep the enum; the full load does the same.
    /// </remarks>
    private static object? ToPanelValue(object? value, Type panelType)
    {
        if (panelType == typeof(string))
        {
            return value is null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (panelType == typeof(string[]))
        {
            return value is IEnumerable<string> list ? list.ToArray() : Array.Empty<string>();
        }

        return value;
    }

    /// <summary>Whether two panel values mean the same setting.</summary>
    /// <remarks>
    /// Blank text equals blank text, because Save writes a blank optional path as null: a field
    /// holding only spaces is the default, not a modification.
    /// </remarks>
    private static bool PanelValuesEqual(object? current, object? fallback) => (current, fallback) switch
    {
        (string[] left, string[] right) => left.SequenceEqual(right, StringComparer.Ordinal),
        (string left, string right) => (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            || string.Equals(left, right, StringComparison.Ordinal),
        _ => Equals(current, fallback),
    };
}
