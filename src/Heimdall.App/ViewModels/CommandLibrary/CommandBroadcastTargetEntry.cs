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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// One open terminal in the broadcast list, with the checkbox that decides whether it receives
/// the command.
/// </summary>
public sealed partial class CommandBroadcastTargetEntry : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public CommandBroadcastTargetEntry(CommandBroadcastTarget target, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(target);

        Target = target;
        _isSelected = isSelected;
    }

    public CommandBroadcastTarget Target { get; }

    public string Id => Target.Id;

    public string DisplayName => Target.DisplayName;

    public string ConnectionType => Target.ConnectionType;

    public bool IsOrigin => Target.IsOrigin;

    /// <summary>
    /// What a screen reader reads for the row. The connection type is part of it because two
    /// tabs on one host differ only by that.
    /// </summary>
    public string AccessibleName =>
        string.IsNullOrWhiteSpace(ConnectionType)
            ? DisplayName
            : $"{DisplayName} ({ConnectionType})";
}
