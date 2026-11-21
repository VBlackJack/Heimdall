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

namespace Heimdall.Core.Models;

/// <summary>
/// Binary split container node in the recursive split pane tree.
/// Holds two children (each a <see cref="SessionPaneModel"/> or another
/// <see cref="SplitContainerModel"/>) separated by a splitter.
/// </summary>
public partial class SplitContainerModel : ObservableObject, ISplitContent
{
    public const double MinRatio = 0.1;
    public const double MaxRatio = 0.9;
    public const double DefaultRatio = 0.5;
    public const int SplitterThickness = 4;

    [ObservableProperty]
    private ISplitContent _first = null!;

    [ObservableProperty]
    private ISplitContent _second = null!;

    [ObservableProperty]
    private SplitOrientation _orientation;

    /// <summary>
    /// Splitter position ratio (0.0-1.0) between the first and second child.
    /// Automatically clamped to [<see cref="MinRatio"/>, <see cref="MaxRatio"/>].
    /// Preserved when switching tabs and persisted via split layout memory.
    /// Manual property (not [ObservableProperty]) to clamp BEFORE PropertyChanged fires.
    /// </summary>
    private double _splitRatio = DefaultRatio;

    public double SplitRatio
    {
        get => _splitRatio;
        set
        {
            var clamped = Math.Clamp(value, MinRatio, MaxRatio);
            SetProperty(ref _splitRatio, clamped);
        }
    }

    private double _resetRatio = DefaultRatio;

    /// <summary>
    /// The ratio a splitter double-click restores: the split the container opened with. A user
    /// split keeps <see cref="DefaultRatio"/>; the SFTP companion split carries its configured share.
    /// Init-only for callers; <see cref="SwapChildren"/> mirrors it when the panes change sides.
    /// </summary>
    public double ResetRatio
    {
        get => _resetRatio;
        init => _resetRatio = value;
    }

    /// <summary>
    /// Exchanges the first and second child. Each pane keeps its size and only changes side:
    /// <see cref="SplitRatio"/> and the reset ratio are mirrored (1 - ratio) and clamped, so a splitter
    /// double-click restores the swapped share instead of handing the other pane the old one.
    /// </summary>
    public void SwapChildren()
    {
        (First, Second) = (Second, First);
        SplitRatio = 1 - SplitRatio;
        _resetRatio = Math.Clamp(1 - _resetRatio, MinRatio, MaxRatio);
    }
}
