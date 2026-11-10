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

namespace Heimdall.App.ViewModels;

/// <summary>What a hidden folder's tabs have to say, the worst of them winning.</summary>
public enum SessionTabGroupAttention
{
    /// <summary>Nothing to report, or the folder is shown.</summary>
    None,

    /// <summary>A tab lost its connection.</summary>
    Disconnected,

    /// <summary>A tab failed.</summary>
    Failed,
}

/// <summary>
/// One folder on the row of folders above the session tab strip: its name, colour, how many tabs
/// it holds, and whether its tabs are the ones shown. Created and kept up to date by
/// <see cref="ConnectionViewModel"/>, which reconciles the list in place so a header keeps its
/// identity (and its keyboard focus) while its tabs open, close or move.
/// </summary>
public sealed partial class SessionTabGroupHeaderViewModel : ObservableObject
{
    /// <summary>Creates the header of the folder whose full path is <paramref name="key"/>.</summary>
    public SessionTabGroupHeaderViewModel(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    /// <summary>The full path of the folder, which identifies the group.</summary>
    public string Key { get; }

    /// <summary>The folder name drawn on the header.</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>The folder colour as a palette hex string, or empty for the themed default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColor))]
    private string _color = string.Empty;

    /// <summary>The number of tabs the folder holds.</summary>
    [ObservableProperty]
    private int _tabCount;

    /// <summary>True when the tabs of this folder are the ones shown in the strip.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>
    /// The worst state among the tabs of a folder that is not shown, so the header says what the
    /// hidden strip cannot. <see cref="SessionTabGroupAttention.None"/> while the folder is shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(HasFailedTab))]
    [NotifyPropertyChangedFor(nameof(HasDisconnectedTab))]
    private SessionTabGroupAttention _attention;

    /// <summary>True when a tab of the hidden folder failed or lost its connection.</summary>
    public bool NeedsAttention => Attention != SessionTabGroupAttention.None;

    /// <summary>True when the worst state is a failure: drawn as the strip draws it, a red triangle.</summary>
    public bool HasFailedTab => Attention == SessionTabGroupAttention.Failed;

    /// <summary>True when the worst state is a lost connection: drawn as the strip draws it, a grey ring.</summary>
    public bool HasDisconnectedTab => Attention == SessionTabGroupAttention.Disconnected;

    /// <summary>What a screen reader reads for the header, state and tab count included.</summary>
    [ObservableProperty]
    private string _accessibleName = string.Empty;

    /// <summary>True when the header takes the folder's colour rather than the theme's.</summary>
    public bool HasColor => !string.IsNullOrWhiteSpace(Color);
}
