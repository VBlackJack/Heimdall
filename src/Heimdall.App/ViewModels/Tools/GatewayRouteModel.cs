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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// What a "Route via" selector lists and which entry it holds, independent of any control.
/// </summary>
/// <remarks>
/// <para>The selection is held by gateway id, not by list position and not by object. A save
/// replaces every entry of the inventory with a fresh clone and may reorder or remove entries; the
/// id is the one thing an edit keeps.</para>
/// <para><see cref="Apply"/> says whether the tool must be handed a new gateway. A save that
/// touched the selected entry is told from one that did not without a hand-kept list of the
/// fields that matter: the two entries are compared through the DTO's own serialization, so a
/// property added to <see cref="SshGatewayDto"/> later counts without anyone remembering to list
/// it here. A save that leaves the selected entry byte-identical reports nothing, which is what
/// keeps the two tools that answer a gateway change with a remote subnet probe over SSH from
/// probing on every unrelated save.</para>
/// </remarks>
public sealed class GatewayRouteModel
{
    private static readonly JsonSerializerOptions s_comparison = new() { WriteIndented = false };

    private List<SshGatewayDto> _gateways = [];

    /// <summary>The gateways listed, in inventory order.</summary>
    public IReadOnlyList<SshGatewayDto> Gateways => _gateways;

    /// <summary>The id of the selected gateway, or null for a direct connection.</summary>
    public string? SelectedId { get; private set; }

    /// <summary>The selected gateway as the current inventory holds it, or null for direct.</summary>
    public SshGatewayDto? Selected => FindById(SelectedId);

    /// <summary>
    /// The combo index of the selection: 0 for direct, otherwise the 1-based position in
    /// <see cref="Gateways"/>.
    /// </summary>
    public int SelectedIndex
    {
        get
        {
            if (SelectedId is null)
            {
                return 0;
            }

            int index = _gateways.FindIndex(gateway => gateway.Id == SelectedId);
            return index < 0 ? 0 : index + 1;
        }
    }

    /// <summary>Replaces the list and resets the selection to direct.</summary>
    public void Seed(IEnumerable<SshGatewayDto>? gateways)
    {
        _gateways = gateways?.ToList() ?? [];
        SelectedId = null;
    }

    /// <summary>
    /// Records the user's pick by combo index and returns the gateway it names, null for direct.
    /// </summary>
    public SshGatewayDto? Select(int comboIndex)
    {
        SshGatewayDto? picked = comboIndex >= 1 && comboIndex <= _gateways.Count
            ? _gateways[comboIndex - 1]
            : null;

        SelectedId = picked?.Id;
        return picked;
    }

    /// <summary>
    /// Replaces the list with a new inventory, keeps the selection by id, and reports what the
    /// tool must be told.
    /// </summary>
    public GatewayRouteRefresh Apply(IReadOnlyList<SshGatewayDto> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        SshGatewayDto? before = Selected;
        _gateways = inventory.ToList();

        if (before is null)
        {
            // Direct stays direct whatever the inventory did.
            return GatewayRouteRefresh.Unchanged;
        }

        SshGatewayDto? after = Selected;
        if (after is null)
        {
            SelectedId = null;
            return new GatewayRouteRefresh(SelectionChanged: true, Selected: null, LostGateway: before);
        }

        return SameGateway(before, after)
            ? GatewayRouteRefresh.Unchanged
            : new GatewayRouteRefresh(SelectionChanged: true, Selected: after, LostGateway: null);
    }

    /// <summary>
    /// Whether two entries are the same gateway in every persisted property.
    /// </summary>
    internal static bool SameGateway(SshGatewayDto first, SshGatewayDto second)
        => string.Equals(
            JsonSerializer.Serialize(first, s_comparison),
            JsonSerializer.Serialize(second, s_comparison),
            StringComparison.Ordinal);

    private SshGatewayDto? FindById(string? id)
        => id is null ? null : _gateways.Find(gateway => gateway.Id == id);
}

/// <summary>
/// The outcome of <see cref="GatewayRouteModel.Apply"/>.
/// </summary>
/// <param name="SelectionChanged">Whether the tool must be handed <paramref name="Selected"/>.</param>
/// <param name="Selected">The gateway to hand over; null means the connection is now direct.</param>
/// <param name="LostGateway">The gateway that was selected and no longer exists, when that is why.</param>
public readonly record struct GatewayRouteRefresh(
    bool SelectionChanged,
    SshGatewayDto? Selected,
    SshGatewayDto? LostGateway)
{
    /// <summary>Nothing for the tool to hear.</summary>
    public static GatewayRouteRefresh Unchanged => default;
}
