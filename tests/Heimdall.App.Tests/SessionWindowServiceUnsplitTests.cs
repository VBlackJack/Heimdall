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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void DetachSessionToFloatingWindow_HostlessTab_RefusesWithoutRemovingTab()
    {
        RunOnStaThread(() =>
        {
            using TestHarness harness = TestHarness.Create();
            var openedSessions = new List<SessionTabViewModel>();
            SessionWindowService service = CreateSessionWindowServiceForDetachTests(
                openedSessions.Add);
            SessionTabViewModel hostless = harness.Main.Connection.AddSession(
                "external-rdp",
                "External RDP",
                "RDP");
            Exception? hostlessException = Record.Exception(
                () => service.DetachSessionToFloatingWindow(hostless, harness.Main));

            Assert.Contains(hostless, harness.Main.Connection.ActiveSessions);
            Assert.Empty(openedSessions);
            Assert.Null(hostlessException);

            SessionTabViewModel hosted = harness.Main.Connection.AddSession(
                "embedded-rdp",
                "Embedded RDP",
                "RDP");
            var host = new System.Windows.Controls.Border();
            hosted.HostControl = host;
            Exception? hostedException = Record.Exception(
                () => service.DetachSessionToFloatingWindow(hosted, harness.Main));

            Assert.Null(hostedException);
            Assert.DoesNotContain(hosted, harness.Main.Connection.ActiveSessions);
            Assert.Same(host, hosted.HostControl);
            Assert.Contains(hosted, openedSessions);
        });
    }

    private static SessionWindowService CreateSessionWindowServiceForDetachTests(
        Action<SessionTabViewModel> onWindowOpened)
    {
        Type presenterType = typeof(Action<SessionTabViewModel, LocalizationManager>);
        ConstructorInfo? constructor = typeof(SessionWindowService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [presenterType],
            modifiers: null);
        if (constructor is null)
        {
            return new SessionWindowService();
        }

        var presenter = new Action<SessionTabViewModel, LocalizationManager>(
            (session, _) => onWindowOpened(session));
        return Assert.IsType<SessionWindowService>(constructor.Invoke([presenter]));
    }

    [Fact]
    public void UnsplitSession_NullSession_Throws()
    {
        using TestHarness harness = TestHarness.Create();
        SessionWindowService service = new SessionWindowService();

        Assert.Throws<ArgumentNullException>(() => service.UnsplitSession(null!, harness.Main));
    }

    [Fact]
    public void UnsplitSession_NullViewModel_Throws()
    {
        using TestHarness harness = TestHarness.Create();
        SessionWindowService service = new SessionWindowService();
        SessionTabViewModel session = new SessionTabViewModel
        {
            Title = "S",
            ConnectionType = "SSH"
        };

        Assert.Throws<ArgumentNullException>(() => service.UnsplitSession(session, null!));
    }

    [Fact]
    public void UnsplitSession_NotSplitSession_IsNoOp()
    {
        using TestHarness harness = TestHarness.Create();
        SessionWindowService service = new SessionWindowService();
        SessionTabViewModel session = harness.Main.Connection.AddSession("srv-1", "Primary", "SSH");
        int countBefore = harness.Main.Connection.ActiveSessions.Count;

        service.UnsplitSession(session, harness.Main);

        Assert.Equal(countBefore, harness.Main.Connection.ActiveSessions.Count);
        Assert.False(session.IsSplit);
    }

    [Fact]
    public void UnsplitSession_SplitWithConnectedSecondary_RestoresSecondaryAsIndependentTab()
    {
        using TestHarness harness = TestHarness.Create();
        SessionWindowService service = new SessionWindowService();
        SessionTabViewModel session = harness.Main.Connection.AddSession("srv-primary", "Primary", "SSH");
        object secondaryHost = new object();
        SessionPaneModel primaryPane = new SessionPaneModel
        {
            PaneId = "primary",
            ServerId = "srv-primary",
            ConnectionType = "SSH"
        };
        SessionPaneModel secondaryPane = new SessionPaneModel
        {
            PaneId = "secondary",
            ServerId = "srv-secondary",
            OriginalServerId = "orig-secondary",
            ConnectionType = "RDP",
            Title = "Secondary",
            Status = "Connected",
            TunnelRoute = "gw-1",
            EnvironmentColor = "#FF0000",
            HostControl = secondaryHost
        };
        session.RootContent = new SplitContainerModel
        {
            First = primaryPane,
            Second = secondaryPane
        };
        Assert.True(session.IsSplit);
        int countBefore = harness.Main.Connection.ActiveSessions.Count;

        service.UnsplitSession(session, harness.Main);

        Assert.False(session.IsSplit);
        Assert.Equal(countBefore + 1, harness.Main.Connection.ActiveSessions.Count);
        SessionTabViewModel restored = harness.Main.Connection.ActiveSessions[^1];
        Assert.Equal("srv-secondary", restored.ServerId);
        Assert.Equal("orig-secondary", restored.OriginalServerId);
        Assert.Same(secondaryHost, restored.HostControl);
        Assert.Equal("Connected", restored.Status);
        Assert.Equal("gw-1", restored.TunnelRoute);
        Assert.Equal("#FF0000", restored.EnvironmentColor);
    }

    [Fact]
    public void UnsplitSession_SplitWithConnectingSecondary_CleansOrphanWithoutRestoringTab()
    {
        using TestHarness harness = TestHarness.Create();
        SessionWindowService service = new SessionWindowService();
        SessionTabViewModel session = harness.Main.Connection.AddSession("srv-primary", "Primary", "SSH");
        SessionPaneModel primaryPane = new SessionPaneModel
        {
            PaneId = "primary",
            ServerId = "srv-primary",
            ConnectionType = "SSH"
        };
        SessionPaneModel connectingSecondary = new SessionPaneModel
        {
            PaneId = "secondary",
            ServerId = "srv-connecting",
            ConnectionType = "RDP",
            Title = "Connecting",
            HostControl = null
        };
        session.RootContent = new SplitContainerModel
        {
            First = primaryPane,
            Second = connectingSecondary
        };
        int countBefore = harness.Main.Connection.ActiveSessions.Count;

        service.UnsplitSession(session, harness.Main);

        Assert.Equal(countBefore, harness.Main.Connection.ActiveSessions.Count);
        Assert.False(session.IsSplit);
    }
}
