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

using System.Collections.Frozen;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Immutable snapshot of every active session-tree filter. All non-empty
/// facets compose with AND semantics.
/// </summary>
public sealed record ServerFilterSpec
{
    private static readonly FrozenSet<string> EmptyProtocols =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static ServerFilterSpec Empty { get; } = new();

    public string NormalizedText { get; init; } = "";

    public FrozenSet<string> Protocols { get; init; } = EmptyProtocols;

    public bool FavoritesOnly { get; init; }

    public bool ConnectedOnly { get; init; }

    public string ProjectName { get; init; } = "";

    public bool IsActive =>
        NormalizedText.Length > 0
        || Protocols.Count > 0
        || FavoritesOnly
        || ConnectedOnly
        || ProjectName.Length > 0;

    public static ServerFilterSpec Create(
        string? text,
        IEnumerable<string>? protocols = null,
        bool favoritesOnly = false,
        bool connectedOnly = false,
        string? projectName = null)
    {
        return new ServerFilterSpec
        {
            NormalizedText = ServerItemViewModel.NormalizeSearchTerm(text),
            Protocols = (protocols ?? [])
                .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            FavoritesOnly = favoritesOnly,
            ConnectedOnly = connectedOnly,
            ProjectName = projectName?.Trim() ?? ""
        };
    }

    public bool Matches(ServerItemViewModel server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return (NormalizedText.Length == 0
                || server.NormalizedSearchText.Contains(
                    NormalizedText,
                    StringComparison.Ordinal))
            && (Protocols.Count == 0
                || Protocols.Contains(server.ConnectionType))
            && (!FavoritesOnly || server.IsFavorite)
            && (!ConnectedOnly
                || ConnectionStateSets.IsConnected(server.ConnectionState))
            && (ProjectName.Length == 0
                || string.Equals(
                    server.ProjectName,
                    ProjectName,
                    StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Selectable protocol facet backed by a protocol handler registered at
/// runtime.
/// </summary>
public partial class ProtocolFilterOptionViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public ProtocolFilterOptionViewModel(
        string protocol,
        string accessibilityName,
        Action selectionChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessibilityName);
        ArgumentNullException.ThrowIfNull(selectionChanged);

        Protocol = protocol;
        AccessibilityName = accessibilityName;
        _selectionChanged = selectionChanged;
    }

    public string Protocol { get; }

    public string AccessibilityName { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
