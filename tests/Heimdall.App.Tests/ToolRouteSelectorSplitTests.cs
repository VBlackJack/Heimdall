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

using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// A tool opened into a split pane lists the gateways and follows their edits, through the real
/// session manager and the real split service.
/// </summary>
/// <remarks>
/// <para>The split path is the one that passed no settings to the session manager, so a tool in a
/// split pane used to list "Direct connection" and nothing else whatever the settings said. It is
/// also the path a tab-level test never takes: the inventory is injected in the manager's tool
/// factory, and only a test that opens the tool the way a pane does can tell that injection from
/// a seed handed over by the caller.</para>
/// <para>The tool is a probe registered for the test: a bare control that binds the shared
/// selector to a combo, exactly as the sixteen views do, and records what it is told to dial.
/// Every built-in tool is a XAML view that resolves application resources, and building a
/// throwaway application per test in this project was tried and rejected: pumping its dispatcher
/// and then tearing it down by reflection raised a hard error in the test host on the third
/// test. The real views are exercised on the shared UI-test host, in the desktop lane.</para>
/// </remarks>
public sealed class ToolRouteSelectorSplitTests
{
    private const string ProbeToolId = "EXT:TEST:ROUTEPROBE";

    [Fact]
    public void ToolInASplitPane_ListsTheGateways_AndDialsTheEditedOne()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "heimdall-route-split", Guid.NewGuid().ToString("N"));
        try
        {
            RunOnStaThread(() =>
            {
                ConfigManager configManager = new(rootPath);
                Run(configManager.InitializeAsync);
                Run(() => configManager.SaveSettingsAsync(GatewaySettings("gw-1", "Paris", host: "10.0.0.1")));

                LocalizationManager localizer = new();
                ToolRegistry toolRegistry = new();
                toolRegistry.RegisterToolForTests(ProbeDescriptor(), () => new RouteProbeToolView());
                using TunnelManager tunnelManager = new();
                EmbeddedSessionManager sessionManager = new(
                    localizer, null!, null!, null!, toolRegistry, null!, null!, null!, null!, configManager);
                SplitService splitService = new(
                    configManager,
                    localizer,
                    new ConnectionStateMachine(),
                    tunnelManager,
                    sessionManager,
                    connectionService: null!,
                    toolRegistry,
                    dialogService: null!,
                    new PaneCloseArbiter());

                SessionTabViewModel session = new();
                session.RootContent = new SessionPaneModel { ConnectionType = "SSH" };

                splitService.SplitSessionWithTool(session, ProbeToolId, SplitOrientation.Vertical);

                SplitContainerModel container = Assert.IsType<SplitContainerModel>(session.RootContent);
                SessionPaneModel pane = Assert.IsType<SessionPaneModel>(container.Second);
                RouteProbeToolView probe = Assert.IsType<RouteProbeToolView>(pane.HostControl);
                try
                {
                    // Listed from the inventory although the split path handed over no settings.
                    Assert.Equal(2, probe.Combo.Items.Count);

                    probe.Combo.SelectedIndex = 1;
                    Assert.Equal("10.0.0.1", probe.Dialled?.Host);

                    // Saved from a pool thread, as the settings editor does, so the change reaches
                    // the tool through the dispatcher and not through this call stack.
                    Run(() => configManager.SaveSettingsAsync(GatewaySettings("gw-1", "Paris", host: "10.0.0.2")));
                    Drain();

                    Assert.Equal(1, probe.Combo.SelectedIndex);
                    Assert.Equal("10.0.0.2", probe.Dialled?.Host);
                }
                finally
                {
                    probe.Dispose();
                }
            });
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
                // Test cleanup.
            }
        }
    }

    private static ToolDescriptor ProbeDescriptor() => new(
        Id: ProbeToolId,
        Category: ToolCategory.External,
        CategoryLabelKey: "ToolCategoryExternal",
        LabelKey: "Route probe",
        LabelWithArgKey: null,
        CommandPrefixes: ["routeprobe"],
        IsNetworkTool: false,
        IconResourceKey: "Geo.Tool.External",
        DescriptionKey: null,
        ExternalProviderName: "TEST");

    private static void Run(Func<Task> operation)
        => Task.Run(operation).GetAwaiter().GetResult();

    /// <summary>
    /// Runs everything the selector queued on this thread's dispatcher. Below Normal on purpose: a
    /// Send-priority call would run ahead of the queued change and read the old state.
    /// </summary>
    private static void Drain()
        => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);

    private static AppSettings GatewaySettings(string id, string name, string host)
    {
        AppSettings settings = new();
        settings.SshGateways.Add(new SshGatewayDto
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 22,
            User = "bastion"
        });

        return settings;
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

    /// <summary>
    /// The smallest tool that goes through the shared selector: a combo, the selector bound to it
    /// in <see cref="Initialize"/>, released in <see cref="Dispose"/>, and a record of what it was
    /// told to dial.
    /// </summary>
    private sealed class RouteProbeToolView : UserControl, IToolView
    {
        private GatewayRouteSelector? _routeSelector;

        public ComboBox Combo { get; } = new();

        public SshGatewayDto? Dialled { get; private set; }

        public void Initialize(ToolContext? context, LocalizationManager? localizer)
        {
            _routeSelector?.Dispose();
            _routeSelector = new GatewayRouteSelector(Combo, context, key => key, gateway => Dialled = gateway);
        }

        public void Dispose() => _routeSelector?.Dispose();
    }
}
