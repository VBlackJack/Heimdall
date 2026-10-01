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
using CommunityToolkit.Mvvm.Input;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// Whether one setting on the panel differs from its factory default, and the way back to it.
/// </summary>
/// <remarks>
/// The marker beside a field binds to one of these. It is updated in place by the panel whenever
/// the pending value changes, so the marker follows every edit, a revert, a reset and an import.
/// </remarks>
public sealed partial class SettingDefaultState : ObservableObject
{
    private readonly Action<string> _reset;

    internal SettingDefaultState(string setting, Action<string> reset)
    {
        Setting = setting;
        _reset = reset;
    }

    /// <summary>The panel property the marker is about.</summary>
    public string Setting { get; }

    /// <summary>True when the pending value differs from the factory default.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _isModified;

    /// <summary>The factory default, worded for display.</summary>
    [ObservableProperty]
    private string _defaultText = string.Empty;

    /// <summary>
    /// The badge's name for a screen reader: that the value is modified, and what the default is.
    /// </summary>
    /// <remarks>The visible badge only says "Modified"; colour and a dot are never the only signal.</remarks>
    [ObservableProperty]
    private string _badgeAccessibleName = string.Empty;

    /// <summary>The accessible name and tooltip of the reset button, naming the value it restores.</summary>
    [ObservableProperty]
    private string _resetAccessibleName = string.Empty;

    /// <summary>
    /// Puts the factory default back as the pending value. Nothing is written: Save and Revert act
    /// on it like on any other edit.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsModified))]
    private void Reset() => _reset(Setting);
}

/// <summary>The marker state of every setting that has one, looked up by panel property name.</summary>
/// <remarks>
/// Bound from the markup as <c>Settings.Defaults[PropertyName]</c>. An unknown name throws rather than
/// returning nothing, so a marker bound to a misspelt setting is a binding error in the output
/// window, and the markup guard catches it before that.
/// </remarks>
public sealed class SettingDefaultStates
{
    private readonly Dictionary<string, SettingDefaultState> _states;

    internal SettingDefaultStates(IEnumerable<SettingDefaultState> states)
    {
        _states = states.ToDictionary(state => state.Setting, StringComparer.Ordinal);
    }

    /// <summary>The marker state of <paramref name="setting"/>.</summary>
    public SettingDefaultState this[string setting] => _states[setting];

    /// <summary>The settings that have a marker.</summary>
    public IReadOnlyCollection<string> Settings => _states.Keys;

    /// <summary>Every marker state.</summary>
    public IEnumerable<SettingDefaultState> All => _states.Values;

    /// <summary>Whether <paramref name="setting"/> has a marker.</summary>
    public bool Contains(string setting) => _states.ContainsKey(setting);

    /// <summary>The marker state of <paramref name="setting"/>, when it has one.</summary>
    public bool TryGet(string setting, out SettingDefaultState state)
        => _states.TryGetValue(setting, out state!);
}
