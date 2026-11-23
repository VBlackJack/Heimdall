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
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// One switch of the "Tool packs" block: an optional pack, its words, and whether the
/// organisation policy has taken the choice away.
/// </summary>
/// <remarks>
/// The switch holds no state of its own. It reads and writes the panel's pending list of disabled
/// packs through the two delegates it is given, so the list stays the single source of truth and a
/// name this build does not know is never touched by a switch.
/// </remarks>
public sealed class ToolPackSwitchViewModel : ObservableObject
{
    private readonly Func<bool> _readEnabled;
    private readonly Action<bool> _writeEnabled;
    private string _label = string.Empty;
    private string _description = string.Empty;
    private string _countText = string.Empty;
    private string _warningText = string.Empty;
    private string _policyText = string.Empty;

    /// <param name="pack">The optional pack this switch stands for.</param>
    /// <param name="toolCount">How many tools of the registry belong to the pack.</param>
    /// <param name="isForbiddenByPolicy">Whether the organisation policy forbids the pack.</param>
    /// <param name="hasWarning">Whether the pack carries the "probes targets" warning.</param>
    /// <param name="readEnabled">Whether the panel's pending list leaves the pack on.</param>
    /// <param name="writeEnabled">Turns the pack on or off in the panel's pending list.</param>
    public ToolPackSwitchViewModel(
        ToolPack pack,
        int toolCount,
        bool isForbiddenByPolicy,
        bool hasWarning,
        Func<bool> readEnabled,
        Action<bool> writeEnabled)
    {
        ArgumentNullException.ThrowIfNull(readEnabled);
        ArgumentNullException.ThrowIfNull(writeEnabled);

        Pack = pack;
        ToolCount = toolCount;
        IsForbiddenByPolicy = isForbiddenByPolicy;
        CarriesWarning = hasWarning;
        _readEnabled = readEnabled;
        _writeEnabled = writeEnabled;
    }

    /// <summary>The pack this switch stands for.</summary>
    public ToolPack Pack { get; }

    /// <summary>How many tools of the registry belong to the pack.</summary>
    public int ToolCount { get; private set; }

    /// <summary>Sets the tool count after the registry changed (the recipe tools arrive after the panel exists).</summary>
    internal void UpdateToolCount(int toolCount)
    {
        if (toolCount == ToolCount)
        {
            return;
        }

        ToolCount = toolCount;
        OnPropertyChanged(nameof(ToolCount));
    }

    /// <summary>True when the organisation policy forbids the pack, whatever the user chose.</summary>
    public bool IsForbiddenByPolicy { get; }

    /// <summary>Whether the pack is one whose tools probe or enumerate targets.</summary>
    public bool CarriesWarning { get; }

    /// <summary>The switch can be flipped: false under a policy veto.</summary>
    public bool IsEditable => !IsForbiddenByPolicy;

    /// <summary>
    /// Whether the pack is on. A forbidden pack reads off and ignores an attempt to turn it on,
    /// without touching what the user chose, so lifting the policy gives that choice back.
    /// </summary>
    public bool IsEnabled
    {
        get => !IsForbiddenByPolicy && _readEnabled();
        set
        {
            if (IsForbiddenByPolicy || value == _readEnabled())
            {
                return;
            }

            _writeEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>The name of the pack.</summary>
    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    /// <summary>The one-line description of what the pack holds.</summary>
    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    /// <summary>How many tools the pack holds, worded.</summary>
    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    /// <summary>The caution line of a pack that probes targets; empty for the others.</summary>
    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    /// <summary>The "set by your organisation" line shown under a policy veto; empty otherwise.</summary>
    public string PolicyText
    {
        get => _policyText;
        private set
        {
            if (SetProperty(ref _policyText, value))
            {
                OnPropertyChanged(nameof(HasPolicyText));
            }
        }
    }

    /// <summary>True when <see cref="WarningText"/> has something to show.</summary>
    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    /// <summary>True when <see cref="PolicyText"/> has something to show.</summary>
    public bool HasPolicyText => !string.IsNullOrEmpty(PolicyText);

    /// <summary>Sets the words of the switch, after the language is known or has changed.</summary>
    internal void ApplyTexts(string label, string description, string countText, string warningText, string policyText)
    {
        Label = label;
        Description = description;
        CountText = countText;
        WarningText = warningText;
        PolicyText = policyText;
    }

    /// <summary>Re-reads the state from the panel, after its pending list changed.</summary>
    internal void RefreshState() => OnPropertyChanged(nameof(IsEnabled));
}
