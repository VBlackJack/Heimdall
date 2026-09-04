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

using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// The one "Route via" mechanism the network tools share: fills a combo from the gateway
/// inventory, follows the inventory as it changes, and tells the view which gateway to dial.
/// </summary>
/// <remarks>
/// <para>Sixteen views used to copy the gateway list at initialisation into a private field and
/// tag their combo items with those snapshot DTOs, which the tool then tunnelled through. A
/// gateway edited while a tab was open kept its old name in the combo and, worse, its old host,
/// port and credentials on the wire. The selector subscribes to
/// <see cref="IGatewayInventory.Changed"/> and rebuilds the combo from the new inventory, keeping
/// the user's choice by gateway id.</para>
/// <para><b>What the view hears.</b> The selection callback runs when the user picks an entry,
/// when the picked gateway is saved with a change, and when it is deleted, with null and with the
/// status callback carrying the sentence. It does not run when the selector is built, nor on a
/// save that leaves the picked gateway as it was: the two views that answer it with a remote
/// subnet probe over SSH must not probe on every unrelated save.</para>
/// <para>Labels are rebuilt on <see cref="Relocalize"/> without touching the selection.
/// <see cref="IGatewayInventory.Changed"/> arrives on the saving thread and is marshalled to the
/// combo's dispatcher; a notification that lands after <see cref="Dispose"/> is dropped there.</para>
/// </remarks>
internal sealed class GatewayRouteSelector : IDisposable
{
    private readonly ComboBox _combo;
    private readonly Func<string, string> _localize;
    private readonly IGatewayInventory? _inventory;
    private readonly Action<SshGatewayDto?> _onSelected;
    private readonly Action<string>? _onStatus;
    private readonly GatewayRouteModel _model = new();
    private bool _rebuilding;
    private bool _disposed;

    /// <summary>
    /// Binds the selector to a combo and seeds it.
    /// </summary>
    /// <param name="combo">The "Route via" combo of the view.</param>
    /// <param name="context">
    /// Carries the live inventory when the tool was opened by the session manager; the snapshot
    /// list is the seed when it does not.
    /// </param>
    /// <param name="localize">Resolves a locale key.</param>
    /// <param name="onSelected">Receives the gateway to dial, null for direct.</param>
    /// <param name="onStatus">Receives the sentence to show when the selected gateway is deleted.</param>
    public GatewayRouteSelector(
        ComboBox combo,
        ToolContext? context,
        Func<string, string> localize,
        Action<SshGatewayDto?> onSelected,
        Action<string>? onStatus = null)
    {
        _combo = combo ?? throw new ArgumentNullException(nameof(combo));
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
        _onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
        _onStatus = onStatus;
        _inventory = context?.GatewayInventory;

        IEnumerable<SshGatewayDto>? seed = _inventory?.Current
            ?? context?.SshGateways?.OfType<SshGatewayDto>();
        _model.Seed(seed);
        Rebuild();

        _combo.SelectionChanged += OnComboSelectionChanged;
        if (_inventory is not null)
        {
            _inventory.Changed += OnInventoryChanged;
        }
    }

    /// <summary>The selected gateway as the current inventory holds it, or null for direct.</summary>
    public SshGatewayDto? SelectedGateway => _model.Selected;

    /// <summary>The id of the selected gateway, or null for direct.</summary>
    public string? SelectedGatewayId => _model.SelectedId;

    /// <summary>The gateways listed, in inventory order.</summary>
    public IReadOnlyList<SshGatewayDto> Gateways => _model.Gateways;

    /// <summary>Rebuilds the labels in the current locale, keeping the selection.</summary>
    public void Relocalize() => Rebuild();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _combo.SelectionChanged -= OnComboSelectionChanged;
        if (_inventory is not null)
        {
            _inventory.Changed -= OnInventoryChanged;
        }
    }

    /// <summary>
    /// Applies a new inventory on the UI thread: the dispatcher path lands here, and so do tests.
    /// </summary>
    internal void ApplyInventory(IReadOnlyList<SshGatewayDto> inventory)
    {
        if (_disposed)
        {
            return;
        }

        GatewayRouteRefresh refresh = _model.Apply(inventory);
        Rebuild();

        if (refresh.LostGateway is { } lost && _onStatus is not null)
        {
            _onStatus(string.Format(
                CultureInfo.CurrentCulture,
                _localize("ToolTunnelGatewayRemoved"),
                lost.Name));
        }

        if (refresh.SelectionChanged)
        {
            _onSelected(refresh.Selected);
        }
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            _combo.Items.Clear();
            _combo.Items.Add(new ComboBoxItem { Content = _localize("ToolTunnelDirect") });

            foreach (SshGatewayDto gateway in _model.Gateways)
            {
                _combo.Items.Add(new ComboBoxItem
                {
                    Content = $"{gateway.Name} ({gateway.Host}:{gateway.Port})",
                    Tag = gateway
                });
            }

            _combo.SelectedIndex = _model.SelectedIndex;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void OnComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || _disposed)
        {
            return;
        }

        _onSelected(_model.Select(_combo.SelectedIndex));
    }

    private void OnInventoryChanged(IReadOnlyList<SshGatewayDto> inventory)
    {
        if (_disposed)
        {
            return;
        }

        _ = _combo.Dispatcher.InvokeAsync(() => ApplyInventory(inventory), DispatcherPriority.Normal);
    }
}
