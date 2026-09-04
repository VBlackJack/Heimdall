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

using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The shared "Route via" selector against a bare combo: what it tells the view, and when.
/// </summary>
/// <remarks>
/// The inventory change is raised on the test's own thread and travels through the combo's
/// dispatcher exactly as a settings save does from its background thread; each test drains that
/// dispatcher before reading. A change delivered synchronously would pass these tests through a
/// path the product never takes.
/// </remarks>
public sealed class GatewayRouteSelectorTests
{
    [Fact]
    public void Construction_ListsTheInventory_AndTellsTheViewNothing()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin"));

            Assert.Equal(3, harness.Combo.Items.Count);
            Assert.Equal(0, harness.Combo.SelectedIndex);
            Assert.Empty(harness.Selections);
        });
    }

    [Fact]
    public void UserPick_HandsTheViewTheGateway_AndDirectHandsNull()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"));

            harness.Combo.SelectedIndex = 1;
            harness.Combo.SelectedIndex = 0;

            Assert.Equal(["gw-1", null], harness.Selections.Select(g => g?.Id));
        });
    }

    [Fact]
    public void InventoryChange_KeepsThePickById_AndHandsOverTheEditedGateway()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin", host: "10.0.0.1"));
            harness.Combo.SelectedIndex = 2;
            harness.Selections.Clear();

            // Reordered and re-hosted: the id is the only thing the edit kept.
            harness.Inventory.Publish(Gateway("gw-2", "Berlin", host: "10.0.0.2"), Gateway("gw-1", "Paris"));
            Drain();

            Assert.Equal(1, harness.Combo.SelectedIndex);
            Assert.Equal("gw-2", harness.Selector.SelectedGatewayId);
            SshGatewayDto handed = Assert.Single(harness.Selections)!;
            Assert.Equal("10.0.0.2", handed.Host);
            Assert.Same(harness.Selector.SelectedGateway, handed);
        });
    }

    [Fact]
    public void UnrelatedSave_DoesNotCallTheView()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin"));
            harness.Combo.SelectedIndex = 1;
            harness.Selections.Clear();

            // The other gateway edited; the picked one arrives as an identical clone.
            harness.Inventory.Publish(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin DC"));
            Drain();

            Assert.Empty(harness.Selections);
            Assert.Equal(1, harness.Combo.SelectedIndex);
            Assert.Contains("Berlin DC", ((ComboBoxItem)harness.Combo.Items[2]).Content.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DeletedPick_FallsBackToDirect_AndSaysWhichGatewayWentAway()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin"));
            harness.Combo.SelectedIndex = 2;
            harness.Selections.Clear();

            harness.Inventory.Publish(Gateway("gw-1", "Paris"));
            Drain();

            Assert.Equal(0, harness.Combo.SelectedIndex);
            Assert.Equal(2, harness.Combo.Items.Count);
            Assert.Null(Assert.Single(harness.Selections));
            Assert.Equal("Berlin is gone", Assert.Single(harness.Statuses));
        });
    }

    [Fact]
    public void Dispose_ReleasesTheInventorySubscription()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"));
            harness.Combo.SelectedIndex = 1;
            harness.Selections.Clear();

            harness.Selector.Dispose();
            harness.Inventory.Publish(Gateway("gw-1", "Paris", host: "10.0.0.9"));
            Drain();

            // Neither the combo nor the view heard about it: the handler did not run at all.
            Assert.Empty(harness.Selections);
            Assert.Contains("127.0.0.1", ((ComboBoxItem)harness.Combo.Items[1]).Content.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, harness.Inventory.SubscriberCount);
        });
    }

    [Fact]
    public void Relocalize_RebuildsTheLabels_AndKeepsThePick()
    {
        RunOnStaThread(() =>
        {
            Harness harness = Harness.Create(Gateway("gw-1", "Paris"));
            harness.Combo.SelectedIndex = 1;
            harness.Selections.Clear();

            harness.Localized["ToolTunnelDirect"] = "Directe";
            harness.Selector.Relocalize();

            Assert.Equal("Directe", ((ComboBoxItem)harness.Combo.Items[0]).Content);
            Assert.Equal(1, harness.Combo.SelectedIndex);
            Assert.Empty(harness.Selections);
        });
    }

    [Fact]
    public void WithoutAnInventory_TheSnapshotListSeedsTheCombo()
    {
        RunOnStaThread(() =>
        {
            ComboBox combo = new();
            ToolContext context = new(SshGateways: new List<SshGatewayDto> { Gateway("gw-1", "Paris") });

            using GatewayRouteSelector selector = new(combo, context, key => key, _ => { });

            Assert.Equal(2, combo.Items.Count);
        });
    }

    /// <summary>
    /// Runs every operation queued on this thread's dispatcher, which is where the selector
    /// marshals an inventory change.
    /// </summary>
    private static void Drain()
        => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    private static SshGatewayDto Gateway(string id, string name, string host = "127.0.0.1")
        => new()
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 22,
            User = "bastion"
        };

    private sealed class Harness
    {
        public required ComboBox Combo { get; init; }
        public required FakeGatewayInventory Inventory { get; init; }
        public required GatewayRouteSelector Selector { get; init; }
        public required List<SshGatewayDto?> Selections { get; init; }
        public required List<string> Statuses { get; init; }
        public required Dictionary<string, string> Localized { get; init; }

        public static Harness Create(params SshGatewayDto[] gateways)
        {
            ComboBox combo = new();
            FakeGatewayInventory inventory = new(gateways);
            List<SshGatewayDto?> selections = [];
            List<string> statuses = [];
            Dictionary<string, string> localized = new(StringComparer.Ordinal)
            {
                ["ToolTunnelDirect"] = "Direct",
                ["ToolTunnelGatewayRemoved"] = "{0} is gone"
            };

            GatewayRouteSelector selector = new(
                combo,
                new ToolContext(GatewayInventory: inventory),
                key => localized.TryGetValue(key, out string? value) ? value : key,
                selections.Add,
                statuses.Add);

            return new Harness
            {
                Combo = combo,
                Inventory = inventory,
                Selector = selector,
                Selections = selections,
                Statuses = statuses,
                Localized = localized
            };
        }
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

/// <summary>
/// An inventory a test publishes into, shared by the selector tests and the tool-view tests.
/// </summary>
internal sealed class FakeGatewayInventory : IGatewayInventory
{
    private Action<IReadOnlyList<SshGatewayDto>>? _changed;

    public FakeGatewayInventory(params SshGatewayDto[] gateways)
    {
        Current = gateways;
    }

    public IReadOnlyList<SshGatewayDto> Current { get; private set; }

    public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;

    public event Action<IReadOnlyList<SshGatewayDto>>? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// <summary>Replaces the inventory and raises the change on the calling thread.</summary>
    public void Publish(params SshGatewayDto[] gateways)
    {
        Current = gateways;
        _changed?.Invoke(Current);
    }
}
