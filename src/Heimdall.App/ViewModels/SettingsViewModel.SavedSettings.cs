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
using System.Reflection;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels;

/// <summary>
/// What is on disk, and whether the panel differs from it.
/// </summary>
/// <remarks>
/// <para>One snapshot serves the security posture card ("unsaved") and the dirty flag (Save, Undo
/// changes and the unsaved dot). The flag used to be set by any change to a saved property and
/// cleared only by a load or a save, so ticking a box and unticking it left Save offering to write
/// a panel identical to the disk.</para>
/// <para>Most edits are compared value by value against the snapshot. A few cannot be: the gateway
/// buffer, the external tools, the credential provider unlock secret (only its encrypted form is on
/// disk) and the tools panel flag. An edit to one of those keeps the panel dirty until it is saved
/// or discarded, which is what every edit did before.</para>
/// </remarks>
public partial class SettingsViewModel
{
    /// <summary>The settings as last read from or written to disk, or null before the first read.</summary>
    private AppSettings? _savedSettings;

    /// <summary>Bumped by every capture of the saved settings, so a slower read cannot overwrite a newer one.</summary>
    private int _savedSettingsVersion;

    private Task _savedSettingsLoad = Task.CompletedTask;

    /// <summary>True while <see cref="LoadFromSettings"/> assigns the panel, which is not an edit.</summary>
    private bool _loadingPanel;

    /// <summary>
    /// True after an edit the value comparison cannot read back; cleared by Save and Undo changes.
    /// </summary>
    private bool _hasEditsTheComparisonCannotSee;

    /// <summary>Completes when the saved settings the panel compares against have been read.</summary>
    internal Task WhenSavedSettingsLoadedAsync() => _savedSettingsLoad;

    /// <summary>Records an edit the value comparison cannot see, and marks the panel dirty.</summary>
    private void MarkEditTheComparisonCannotSee()
    {
        _hasEditsTheComparisonCannotSee = true;
        IsDirty = true;
    }

    /// <summary>Records what a save has just written.</summary>
    private void CaptureSavedSettings(AppSettings? written)
    {
        if (written is null)
        {
            return;
        }

        _savedSettingsVersion++;

        // A copy: the instance belongs to the configuration manager, which may keep it.
        _savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(written));
        RefreshSecurityPosture();
    }

    /// <summary>
    /// Reads the saved settings from disk, after a load that may not have come from disk.
    /// </summary>
    /// <remarks>
    /// The panel is loaded from disk at startup, on a reload and on Undo changes, but also from the
    /// factory file by Reset defaults and from a file by the settings import, and only the disk
    /// knows which. Reading it is the one answer that is right for every caller.
    /// </remarks>
    private void ReloadSavedSettings()
    {
        int version = ++_savedSettingsVersion;
        _savedSettingsLoad = ReloadSavedSettingsAsync(version);
    }

    private async Task ReloadSavedSettingsAsync(int version)
    {
        try
        {
            AppSettings saved = await _configManager.LoadSettingsAsync();
            if (version != _savedSettingsVersion)
            {
                return;
            }

            _savedSettings = saved;
            RefreshSecurityPosture();
        }
        catch (Exception ex)
        {
            // The panel keeps the last saved settings it knew, and the dirty flag falls back to
            // "any edit is pending" while it knows none.
            FileLogger.Warn($"[Settings] The saved settings could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the panel has something to save, after <paramref name="changedProperty"/> changed.
    /// </summary>
    private bool PendingDiffersFromSaved(string changedProperty)
    {
        if (!_loadingPanel && !IsComparedProperty(changedProperty))
        {
            _hasEditsTheComparisonCannotSee = true;
        }

        if (_hasEditsTheComparisonCannotSee || _savedSettings is null || _deletedGatewayIds.Count > 0)
        {
            return true;
        }

        foreach ((string name, (PropertyInfo panel, PropertyInfo setting)) in MarkerProperties)
        {
            if (!PanelValuesEqual(panel.GetValue(this), ToPanelValue(setting.GetValue(_savedSettings), panel.PropertyType)))
            {
                return true;
            }

            if (HoldsUncommittedText(name, panel.GetValue(this)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a property's change is one the value comparison reads back.</summary>
    private static bool IsComparedProperty(string propertyName)
        => MarkerProperties.ContainsKey(propertyName)
            || (propertyName.EndsWith(MarkerTextSuffix, StringComparison.Ordinal)
                && MarkerProperties.ContainsKey(propertyName[..^MarkerTextSuffix.Length]));

    /// <summary>
    /// Whether the box of a setting shows something other than the value it committed: text that is
    /// not a number, or a number not yet taken, is an edit whatever the value says.
    /// </summary>
    private bool HoldsUncommittedText(string setting, object? value)
    {
        if (typeof(SettingsViewModel).GetProperty(setting + MarkerTextSuffix, BindingFlags.Public | BindingFlags.Instance)
            is not { } textProperty)
        {
            return false;
        }

        string? text = textProperty.GetValue(this) as string;
        string committed = value switch
        {
            int number => number.ToString(CultureInfo.InvariantCulture),
            string[] list => string.Join(Environment.NewLine, list),
            _ => text ?? string.Empty,
        };
        return !string.Equals(text, committed, StringComparison.Ordinal);
    }
}
