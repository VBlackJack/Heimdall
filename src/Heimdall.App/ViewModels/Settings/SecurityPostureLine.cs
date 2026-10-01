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
/// One line of the security posture card as the view shows it: the decision from
/// <see cref="SecurityPosture"/>, worded.
/// </summary>
/// <remarks>
/// The line objects live as long as the panel and are updated in place, so a keyboard user standing
/// on a "Go to setting" link does not lose it when an unrelated value changes.
/// </remarks>
public sealed partial class SecurityPostureLine : ObservableObject
{
    private readonly Action<string> _navigate;

    internal SecurityPostureLine(SecurityPostureKey key, Action<string> navigate)
    {
        Key = key;
        _navigate = navigate;
    }

    /// <summary>The choice this line is about.</summary>
    public SecurityPostureKey Key { get; }

    /// <summary>The name of the choice.</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>The state of the choice, worded.</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>The visible line: the name of the choice and its state.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>True when the state is the documented insecure choice.</summary>
    [ObservableProperty]
    private bool _isRisky;

    /// <summary>Why the state needs attention; empty when it does not.</summary>
    [ObservableProperty]
    private string _warningText = string.Empty;

    /// <summary>True when the pending state differs from the one on disk.</summary>
    [ObservableProperty]
    private bool _isUnsaved;

    /// <summary>The whole line for a screen reader: label, state, warning, and unsaved.</summary>
    [ObservableProperty]
    private string _accessibleName = string.Empty;

    /// <summary>The accessible name of the line's "Go to setting" link.</summary>
    [ObservableProperty]
    private string _goToAccessibleName = string.Empty;

    /// <summary>The x:Name of the settings control the link lands on.</summary>
    [ObservableProperty]
    private string _targetSettingId = string.Empty;

    /// <summary>Takes the user to the setting the line is about.</summary>
    [RelayCommand]
    private void GoTo() => _navigate(TargetSettingId);
}

/// <summary>The lines of the posture card, in card order, looked up by key name from the markup.</summary>
/// <remarks>
/// The markup writes one row per line, <c>Settings.SecurityPosture[RdpNla]</c>, rather than an items
/// template: an accessible name set inside a data template does not reach the automation tree, and
/// every line's name is the point of the card for a screen reader.
/// </remarks>
public sealed class SecurityPostureLines
{
    private readonly Dictionary<SecurityPostureKey, SecurityPostureLine> _lines;

    internal SecurityPostureLines(IEnumerable<SecurityPostureLine> lines)
    {
        All = lines.ToList();
        _lines = All.ToDictionary(line => line.Key);
    }

    /// <summary>Every line, in card order.</summary>
    public IReadOnlyList<SecurityPostureLine> All { get; }

    /// <summary>The line about <paramref name="key"/>.</summary>
    /// <remarks>A method, not a second indexer: the markup's string indexer must be the only one.</remarks>
    public SecurityPostureLine Get(SecurityPostureKey key) => _lines[key];

    /// <summary>The line named <paramref name="key"/>, as the markup spells it.</summary>
    public SecurityPostureLine this[string key] => _lines[Enum.Parse<SecurityPostureKey>(key, ignoreCase: false)];
}
