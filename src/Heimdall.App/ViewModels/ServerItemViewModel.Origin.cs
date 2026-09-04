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
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels;

public partial class ServerItemViewModel
{
    private LocalizationManager? _localizer;

    [ObservableProperty]
    private ProfileOrigin _origin = ProfileOrigin.Manual;

    public string OriginBadgeCode => ProfileOriginDisplay.GetBadgeCode(Origin);

    public string OriginDisplayName => ProfileOriginDisplay.GetDisplayName(Origin, _localizer);

    public bool IsOriginBadgeVisible => Origin != ProfileOrigin.Manual;

    /// <summary>
    /// Whether this row is a destination typed into the command palette rather than a saved
    /// profile.
    /// </summary>
    /// <remarks>
    /// <para>Set only where the palette mints such a row, and by nothing that reads a profile
    /// from disk: <see cref="FromDto"/> and <see cref="UpdateFromDto"/> leave it false. The
    /// palette used to tell the two apart by the identifier's prefix, so a saved profile whose
    /// identifier happened to carry that prefix was dialled as a typed destination, through a
    /// bare profile that had lost its gateway, ports, credentials and RDP settings. A mark that
    /// only palette code can set cannot be produced by a hand-edited or imported file, which is
    /// why it is not a persisted <see cref="ProfileOrigin"/> value.</para>
    /// </remarks>
    public bool IsTypedDestination { get; init; }

    partial void OnOriginChanged(ProfileOrigin value)
    {
        OnPropertyChanged(nameof(OriginBadgeCode));
        OnPropertyChanged(nameof(OriginDisplayName));
        OnPropertyChanged(nameof(IsOriginBadgeVisible));
    }
}
