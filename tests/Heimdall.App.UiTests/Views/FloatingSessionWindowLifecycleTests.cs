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
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

[Collection(DesktopUiCollection.Name)]
public sealed class FloatingSessionWindowLifecycleTests
{
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Header_WhenSessionChangesAfterConstruction_TracksLiveSessionState()
    {
        WpfTestHost.Invoke(() =>
        {
            const string InitialTitle = "Initial session";
            const string UpdatedTitle = "Updated session";
            // Free-form reasons rather than status tokens. The converter passes a status the
            // mapping does not know through unchanged, so what the header shows is fixed by the
            // session alone and does not move with whatever locale is in force - and a pane that
            // failed really does carry its reason as free text. Tracking is what this test is for;
            // the mapping gets its own assertion at the end, where only the mapping is asserted.
            const string InitialStatus = "Host unreachable";
            const string UpdatedStatus = "Authentication failed";
            const string InitialTunnelRoute = "via gateway-a";
            const string UpdatedTunnelRoute = "via gateway-b";
            SessionTabViewModel session = new()
            {
                Title = InitialTitle,
                ConnectionType = "SSH",
                Status = InitialStatus,
                TunnelRoute = InitialTunnelRoute,
                HostControl = new Border()
            };
            FloatingSessionWindow window = new(session, WpfTestHost.Localizer, new PaneCloseArbiter());
            TextBlock sessionTitle = Assert.IsType<TextBlock>(window.FindName("SessionTitle"));
            TextBlock statusText = Assert.IsType<TextBlock>(window.FindName("StatusText"));
            TextBlock tunnelRouteText = Assert.IsType<TextBlock>(window.FindName("TunnelRouteText"));

            try
            {
                FlushDispatcher();
                Assert.Equal(InitialTitle, sessionTitle.Text);
                Assert.Equal(InitialStatus, statusText.Text);
                Assert.Equal(InitialTunnelRoute, tunnelRouteText.Text);

                session.Title = UpdatedTitle;
                session.Status = UpdatedStatus;
                session.TunnelRoute = UpdatedTunnelRoute;
                FlushDispatcher();

                Assert.Equal(UpdatedTitle, sessionTitle.Text);
                Assert.Equal(UpdatedStatus, statusText.Text);
                Assert.Equal(UpdatedTunnelRoute, tunnelRouteText.Text);
                Assert.Equal(
                    string.Format(WpfTestHost.Localizer["SessionDetachTitle"], UpdatedTitle),
                    window.Title);

                // A status the mapping does know is shown as its label, not as the raw token: the
                // detached header states a session's condition the way the tab header and the
                // status bar do, through SessionStatusDisplay. Which label depends on the locale
                // in force, so the assertion is that the mapping happened, not what it produced.
                session.Status = SessionStatusTokens.Reconnecting;
                FlushDispatcher();

                Assert.NotEqual(SessionStatusTokens.Reconnecting, statusText.Text);
                Assert.NotEmpty(statusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Close_WhenConnectedCloseIsDeclined_RestoresVisibleActiveSessionOnce()
    {
        WpfTestHost.Invoke(() =>
        {
            DecliningDialogProxy dialogProxy = new();
            IDialogService dialogService = dialogProxy.Create();
            ISplitService splitService = DispatchProxy.Create<ISplitService, RejectingSplitProxy>();
            ConnectionViewModel connection = new(
                WpfTestHost.Localizer,
                dialogService,
                splitService,
                new PaneCloseArbiter(),
                new SessionWindowService());
            MainViewModel main = CreateMainViewModel(connection);
            Window mainWindow = new()
            {
                DataContext = main
            };
            Window? previousMainWindow = Application.Current.MainWindow;
            Border hostControl = new();
            SessionTabViewModel session = new()
            {
                Title = "Connected test session",
                ConnectionType = "SSH",
                Status = "Connected",
                HostControl = hostControl
            };
            FloatingSessionWindow window = new(session, WpfTestHost.Localizer, new PaneCloseArbiter());

            try
            {
                Application.Current.MainWindow = mainWindow;

                window.Close();

                Assert.Equal(1, dialogProxy.ConfirmCallCount);
                Assert.Single(connection.ActiveSessions);
                Assert.Same(session, connection.ActiveSessions[0]);
                Assert.Same(session, connection.ActiveSession);
                Assert.True(connection.HasActiveSessions);
                Assert.Same(hostControl, session.HostControl);
            }
            finally
            {
                Application.Current.MainWindow = previousMainWindow;
                mainWindow.Close();
            }
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Reattach_WhenSessionIsAlreadyRestored_DoesNotDuplicateAndActivatesIt()
    {
        WpfTestHost.Invoke(() =>
        {
            IDialogService dialogService = DispatchProxy.Create<IDialogService, DialogDispatchProxy>();
            ISplitService splitService = DispatchProxy.Create<ISplitService, RejectingSplitProxy>();
            ConnectionViewModel connection = new(
                WpfTestHost.Localizer,
                dialogService,
                splitService,
                new PaneCloseArbiter(),
                new SessionWindowService());
            MainViewModel main = CreateMainViewModel(connection);
            Window mainWindow = new()
            {
                DataContext = main
            };
            Window? previousMainWindow = Application.Current.MainWindow;
            SessionTabViewModel session = new()
            {
                Title = "Restored test session",
                ConnectionType = "SSH",
                HostControl = new Border()
            };
            FloatingSessionWindow window = new(session, WpfTestHost.Localizer, new PaneCloseArbiter());
            connection.ActiveSessions.Add(session);

            try
            {
                Application.Current.MainWindow = mainWindow;

                window.ReattachToMainWindow();

                Assert.Single(connection.ActiveSessions);
                Assert.Same(session, connection.ActiveSessions[0]);
                Assert.Same(session, connection.ActiveSession);
                Assert.True(connection.HasActiveSessions);
            }
            finally
            {
                Application.Current.MainWindow = previousMainWindow;
                mainWindow.Close();
            }
        });
    }

    private static MainViewModel CreateMainViewModel(ConnectionViewModel connection)
    {
        MainViewModel main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        FieldInfo? connectionField = typeof(MainViewModel).GetField(
            "<Connection>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connectionField);
        connectionField.SetValue(main, connection);
        return main;
    }

    private static void FlushDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private sealed class DecliningDialogProxy
    {
        private readonly IDialogService _service;
        private readonly DialogDispatchProxy _proxy;

        public DecliningDialogProxy()
        {
            _service = DispatchProxy.Create<IDialogService, DialogDispatchProxy>();
            _proxy = (DialogDispatchProxy)_service;
        }

        public int ConfirmCallCount => _proxy.ConfirmCallCount;

        public IDialogService Create()
        {
            return _service;
        }
    }

    private class DialogDispatchProxy : DispatchProxy
    {
        public int ConfirmCallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmAsync))
            {
                ConfirmCallCount++;
                return Task.FromResult(false);
            }

            throw new InvalidOperationException(
                $"Unexpected dialog call: {targetMethod?.Name ?? "<unknown>"}.");
        }
    }

    private class RejectingSplitProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            throw new InvalidOperationException(
                $"Split cleanup must not run when close is declined: {targetMethod?.Name ?? "<unknown>"}.");
        }
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void DetachedSessions_ReportsASessionHostedByARealFloatingWindow()
    {
        WpfTestHost.Invoke(() =>
        {
            SessionWindowService service = new();
            SessionTabViewModel session = new()
            {
                ServerId = "detached",
                Title = "Detached",
                ConnectionType = "SSH",
            };

            Assert.DoesNotContain(session, service.DetachedSessions);

            FloatingSessionWindow window = new(
                session,
                WpfTestHost.Localizer,
                new PaneCloseArbiter());
            try
            {
                window.Show();

                Assert.Contains(session, service.DetachedSessions);
            }
            finally
            {
                window.Close();
            }

            // Closing is what makes the reading self-maintaining: no unregistration is performed
            // anywhere, and the session is gone from the answer regardless.
            Assert.DoesNotContain(session, service.DetachedSessions);
        });
    }

}
