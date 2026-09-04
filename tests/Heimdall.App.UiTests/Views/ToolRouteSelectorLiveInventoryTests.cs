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

using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Tools;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// A real tool view, initialised the way the session manager does it, follows the gateway
/// inventory: the combo keeps the pick by id and the ViewModel is handed the edited DTO.
/// </summary>
/// <remarks>
/// <para>No window is built: a window seals the application styles onto the shared dispatcher
/// and fails the affinity tests that follow. A <see cref="UserControl"/> on the host dispatcher is
/// enough to run the markup, the selector and the ViewModel together.</para>
/// <para>In the desktop collection, and therefore in the desktop lane, for a reason measured on
/// 2026-09-04: the shared host keeps one application alive for the whole run, and the deferred
/// resources of that application end up owned by whichever thread first looks them up. A view
/// built here on the host thread claims the resources the session tree and toolbar tests build on
/// threads of their own, and the two sides fail each other by affinity when they run in parallel.
/// The desktop collection does not parallelise. The blocking lane holds the same behaviour
/// through the selector tests and the split-pane probe in App.Tests.</para>
/// <para>Nothing here reads localized text. The host runs the product's own startup, which
/// re-points the locale at the developer's profile, so a sentence asserted in English is red on
/// a French machine. The assertions are on the DTO the ViewModel holds and on the selected entry.</para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class ToolRouteSelectorLiveInventoryTests
{
    [StaFact]
    public void GatewayEdit_ReachesTheOpenTool_AndTheDtoItDials()
    {
        FakeGatewayInventory inventory = new(Gateway("gw-1", "Paris", host: "10.0.0.1"));
        (PortScannerView view, PortScanViewModel viewModel) = Open(inventory);
        try
        {
            WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex = 1);
            Assert.Equal("10.0.0.1", viewModel.CurrentGateway?.Host);

            inventory.Publish(Gateway("gw-1", "Paris DC", host: "10.0.0.2"));
            Drain();

            Assert.Equal("10.0.0.2", viewModel.CurrentGateway?.Host);
            Assert.Equal("gw-1", SelectedGatewayId(view));
            Assert.Equal(1, WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex));
        }
        finally
        {
            WpfTestHost.Invoke(view.Dispose);
        }
    }

    [StaFact]
    public void UnrelatedSave_LeavesTheToolWithTheGatewayItHad()
    {
        FakeGatewayInventory inventory = new(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin"));
        (PortScannerView view, PortScanViewModel viewModel) = Open(inventory);
        try
        {
            WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex = 1);
            SshGatewayDto? held = viewModel.CurrentGateway;
            Assert.NotNull(held);

            inventory.Publish(Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin DC"));
            Drain();

            // The same object: the ViewModel was not handed a fresh clone for a save that did not
            // touch its gateway.
            Assert.Same(held, viewModel.CurrentGateway);
            Assert.Equal("gw-1", SelectedGatewayId(view));
        }
        finally
        {
            WpfTestHost.Invoke(view.Dispose);
        }
    }

    [StaFact]
    public void GatewayDeletion_FallsTheToolBackToDirect_AndShowsAnError()
    {
        FakeGatewayInventory inventory = new(Gateway("gw-1", "Paris"));
        (PortScannerView view, PortScanViewModel viewModel) = Open(inventory);
        try
        {
            WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex = 1);
            Assert.NotNull(viewModel.CurrentGateway);

            inventory.Publish();
            Drain();

            Assert.Null(viewModel.CurrentGateway);
            Assert.Null(SelectedGatewayId(view));
            Assert.Equal(0, WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex));
            Assert.True(viewModel.ShowError);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorText));
        }
        finally
        {
            WpfTestHost.Invoke(view.Dispose);
        }
    }

    [StaFact]
    public void DisposedTool_NoLongerListens()
    {
        FakeGatewayInventory inventory = new(Gateway("gw-1", "Paris", host: "10.0.0.1"));
        (PortScannerView view, PortScanViewModel viewModel) = Open(inventory);
        WpfTestHost.Invoke(() => view.CmbRouteVia.SelectedIndex = 1);

        WpfTestHost.Invoke(view.Dispose);
        inventory.Publish(Gateway("gw-1", "Paris", host: "10.0.0.2"));
        Drain();

        Assert.Equal("10.0.0.1", viewModel.CurrentGateway?.Host);
        Assert.Equal(0, inventory.SubscriberCount);
    }

    private static (PortScannerView View, PortScanViewModel ViewModel) Open(FakeGatewayInventory inventory)
    {
        WpfTestHost.ResetLocale();
        return WpfTestHost.Invoke(() =>
        {
            PortScannerView view = new();
            view.Initialize(new ToolContext(GatewayInventory: inventory), WpfTestHost.Localizer);
            return (view, (PortScanViewModel)view.DataContext);
        });
    }

    private static string? SelectedGatewayId(PortScannerView view)
        => WpfTestHost.Invoke(() => (view.CmbRouteVia.SelectedItem as ComboBoxItem)?.Tag is SshGatewayDto gateway
            ? gateway.Id
            : null);

    /// <summary>
    /// Waits for everything the selector queued on the host dispatcher. Below Normal on purpose:
    /// a Send-priority call would run ahead of the queued change and read the old state.
    /// </summary>
    private static void Drain()
        => WpfTestHost.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    private static SshGatewayDto Gateway(string id, string name, string host = "127.0.0.1")
        => new()
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 22,
            User = "bastion"
        };

    private sealed class FakeGatewayInventory : IGatewayInventory
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

        public void Publish(params SshGatewayDto[] gateways)
        {
            Current = gateways;
            _changed?.Invoke(Current);
        }
    }
}
