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

using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The "Tool packs" block: one switch per optional pack, over the pending list of disabled packs.
/// </summary>
/// <remarks>
/// <para>The pending edit is <see cref="DisabledToolPacks"/>, a list of pack names taken from
/// <see cref="ToolPack"/>, the form the settings file stores. A switch turned off adds its name, a
/// switch turned on removes it, and every other entry, including a name this build does not know,
/// stays where it is. <see cref="ToolPack.Core"/> has no switch, so it never enters the list from
/// here.</para>
/// <para>An organisation policy that forbids a pack locks its switch off but never rewrites the
/// list, the way the SFTP cap leaves the user's own choice alone: removing the policy later gives
/// the user back what they had picked.</para>
/// </remarks>
public partial class SettingsViewModel
{
    private readonly IToolAvailability? _toolAvailability;
    private readonly ToolRegistry _toolRegistry;
    private IReadOnlyList<ToolPackSwitchViewModel>? _toolPackSwitches;

    /// <summary>
    /// The pending list of switched-off pack names. Bound to the settings file member of the
    /// same name, so it travels with Save, Restore defaults, the default marker and the import.
    /// </summary>
    [ObservableProperty]
    private string[] _disabledToolPacks = [];

    /// <summary>The switches of the optional packs, in the order of <see cref="ToolPack"/>.</summary>
    public IReadOnlyList<ToolPackSwitchViewModel> ToolPackSwitches => _toolPackSwitches ??= CreateToolPackSwitches();

    /// <summary>
    /// The list that results from turning a pack on or off in <paramref name="disabled"/>: other
    /// entries are kept in place, and a pack already in the wanted state changes nothing.
    /// </summary>
    /// <param name="disabled">The pending list of switched-off pack names.</param>
    /// <param name="pack">The pack the switch stands for.</param>
    /// <param name="enabled">The wanted state of the pack.</param>
    internal static string[] WithPackState(IEnumerable<string> disabled, ToolPack pack, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);

        string name = pack.ToString();
        if (enabled)
        {
            return [.. disabled.Where(entry => !IsPackName(entry, name))];
        }

        // Already listed: nothing to add, so turning a pack off twice is a no-op.
        return ListsPack(disabled, pack) ? [.. disabled] : [.. disabled, name];
    }

    /// <summary>Whether the list names the pack. Case is ignored, like the availability service does.</summary>
    internal static bool ListsPack(IEnumerable<string>? disabled, ToolPack pack)
        => disabled is not null && disabled.Any(entry => IsPackName(entry, pack.ToString()));

    private static bool IsPackName(string? entry, string packName)
        => string.Equals(entry, packName, StringComparison.OrdinalIgnoreCase);

    partial void OnDisabledToolPacksChanged(string[] value)
    {
        if (_toolPackSwitches is null)
        {
            return;
        }

        foreach (ToolPackSwitchViewModel toolPackSwitch in _toolPackSwitches)
        {
            toolPackSwitch.RefreshState();
        }
    }

    private IReadOnlyList<ToolPackSwitchViewModel> CreateToolPackSwitches()
    {
        List<ToolPackSwitchViewModel> switches = [];
        foreach (ToolPack pack in Enum.GetValues<ToolPack>().Where(pack => pack != ToolPack.Core))
        {
            ToolPack current = pack;
            ToolPackSwitchViewModel toolPackSwitch = new(
                current,
                _toolRegistry.All.Count(descriptor => descriptor.Pack == current),
                _toolAvailability?.IsPackForbiddenByPolicy(current) ?? false,
                current == ToolPack.Audit,
                () => !ListsPack(DisabledToolPacks, current),
                enabled => DisabledToolPacks = WithPackState(DisabledToolPacks, current, enabled));
            ApplyToolPackTexts(toolPackSwitch);
            switches.Add(toolPackSwitch);
        }

        return switches;
    }

    /// <summary>Rewords every switch, after the interface language changed.</summary>
    private void RefreshToolPackTexts()
    {
        if (_toolPackSwitches is null)
        {
            return;
        }

        foreach (ToolPackSwitchViewModel toolPackSwitch in _toolPackSwitches)
        {
            ApplyToolPackTexts(toolPackSwitch);
        }
    }

    private void ApplyToolPackTexts(ToolPackSwitchViewModel toolPackSwitch)
    {
        toolPackSwitch.ApplyTexts(
            _localizer[ToolPackLabelKey(toolPackSwitch.Pack)],
            _localizer[ToolPackDescriptionKey(toolPackSwitch.Pack)],
            _localizer.FormatCount(
                toolPackSwitch.ToolCount,
                ToolPackCountOneKey,
                ToolPackCountKey,
                toolPackSwitch.ToolCount),
            toolPackSwitch.CarriesWarning ? _localizer[ToolPackWarningKey] : string.Empty,
            toolPackSwitch.IsForbiddenByPolicy ? _localizer[ToolPackPolicyKey] : string.Empty);
    }

    /// <summary>The locale key of a pack's name, composed from the member name of the pack.</summary>
    internal static string ToolPackLabelKey(ToolPack pack) => "SettingsToolPack" + pack + "Label";

    /// <summary>The locale key of a pack's one-line description, composed from the member name of the pack.</summary>
    internal static string ToolPackDescriptionKey(ToolPack pack) => "SettingsToolPack" + pack + "Description";

    private const string ToolPackCountOneKey = "SettingsToolPackCountOne";

    private const string ToolPackCountKey = "SettingsToolPackCount";

    private const string ToolPackWarningKey = "SettingsToolPackAuditWarning";

    private const string ToolPackPolicyKey = "SettingsToolPackSetByPolicy";
}
