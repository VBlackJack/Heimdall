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
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;
using Heimdall.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

/// <summary>
/// A terminal or file pane whose session ends while the pane stays open moves its own state
/// machine entry out of Connected. The session tree, its connected filter and the status bar
/// count read that entry; it used to stay Connected until the tab was closed, while the pane
/// header said Disconnected.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="EmbeddedSessionManager.CreateHostControl"/> and the real
/// views: the defect was a missing wire between the view's end of session and the machine, so a
/// test of either side alone would pass on the defect.
/// </remarks>
[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class PaneSessionEndStateTests
{
    private const string LostReason = "connection reset by peer";

    /// <summary>
    /// What the SFTP view resolves from <c>App.Services</c>: nothing that holds the user's
    /// profile or reaches the network. Built per application, on its thread, so the dispatcher
    /// service belongs to the dispatcher the test pumps.
    /// </summary>
    private static ServiceProvider CreateHostServices() => new ServiceCollection()
        .AddSingleton<IUiDispatcher, WpfUiDispatcher>()
        .AddSingleton<IRemoteClipboardService, RemoteClipboardService>()
        .AddSingleton<IHostKeyVerifier>(RejectingHostKeyVerifier.Instance)
        .BuildServiceProvider();

    [Fact]
    public void TerminalOnly_ServerEndsTheSessionCleanly_IsDisconnected()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "ssh-clean");
            SessionTabViewModel tab = new() { RootContent = ssh.Pane };
            ssh.Mount(tab);

            ssh.Terminal.RaiseProcessExited(0);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(ssh.Pane.ServerId));
            ssh.Dispose();
        });
    }

    [Fact]
    public void TerminalOnly_LostSession_IsAnErrorCarryingTheReasonThePaneShows()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "ssh-lost");
            SessionTabViewModel tab = new() { RootContent = ssh.Pane };
            ssh.Mount(tab);

            ssh.Terminal.RaiseProcessExited(1);
            PumpDispatcher();

            ConnectionStateData? data = machine.GetStateData(ssh.Pane.ServerId);
            Assert.NotNull(data);
            Assert.Equal(ConnectionState.Error, data.CurrentState);
            Assert.False(string.IsNullOrWhiteSpace(data.ErrorMessage));
            ssh.Dispose();
        });
    }

    [Fact]
    public void TerminalOnly_UserClicksDisconnect_IsDisconnectedNotAnError()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "ssh-user");
            SessionTabViewModel tab = new() { RootContent = ssh.Pane };
            ssh.Mount(tab);

            // A killed client exits non-zero; asked for, it is still not a lost connection.
            ClickDisconnect(ssh.View);
            ssh.Terminal.RaiseProcessExited(1);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(ssh.Pane.ServerId));
            ssh.Dispose();
        });
    }

    [Fact]
    public void SftpOnly_UserClicksDisconnect_IsDisconnected()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            FilePaneFixture files = FilePaneFixture.Create(machine, "sftp-user");
            SessionTabViewModel tab = new() { RootContent = files.Pane };
            files.Mount(tab);

            ClickDisconnect(files.View);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(files.Pane.ServerId));
            files.Dispose();
        });
    }

    [Fact]
    public void SftpOnly_ConnectionLost_IsAnErrorCarryingTheReason()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            FilePaneFixture files = FilePaneFixture.Create(machine, "sftp-lost");
            SessionTabViewModel tab = new() { RootContent = files.Pane };
            files.Mount(tab);

            files.RaiseBrowserDisconnected(LostReason);
            PumpDispatcher();

            ConnectionStateData? data = machine.GetStateData(files.Pane.ServerId);
            Assert.NotNull(data);
            Assert.Equal(ConnectionState.Error, data.CurrentState);
            Assert.Contains(LostReason, data.ErrorMessage, StringComparison.Ordinal);
            files.Dispose();
        });
    }

    [Fact]
    public void Split_SftpPaneEnds_TerminalPaneStaysConnected()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "split-ssh");
            FilePaneFixture files = FilePaneFixture.Create(machine, "split-sftp");
            SessionTabViewModel tab = new()
            {
                RootContent = new SplitContainerModel
                {
                    First = ssh.Pane,
                    Second = files.Pane,
                    Orientation = SplitOrientation.Vertical
                }
            };
            ssh.Mount(tab);
            files.Mount(tab);

            files.RaiseBrowserDisconnected(null);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(files.Pane.ServerId));
            Assert.Equal(ConnectionState.Connected, machine.GetState(ssh.Pane.ServerId));

            ssh.Terminal.RaiseProcessExited(0);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(ssh.Pane.ServerId));
            ssh.Dispose();
            files.Dispose();
        });
    }

    [Fact]
    public void Split_TerminalPaneEnds_SftpPaneStaysConnected()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "split-ssh-first");
            FilePaneFixture files = FilePaneFixture.Create(machine, "split-sftp-second");
            SessionTabViewModel tab = new()
            {
                RootContent = new SplitContainerModel
                {
                    First = ssh.Pane,
                    Second = files.Pane,
                    Orientation = SplitOrientation.Vertical
                }
            };
            ssh.Mount(tab);
            files.Mount(tab);

            ssh.Terminal.RaiseProcessExited(0);
            PumpDispatcher();

            Assert.Equal(ConnectionState.Disconnected, machine.GetState(ssh.Pane.ServerId));
            Assert.Equal(ConnectionState.Connected, machine.GetState(files.Pane.ServerId));
            ssh.Dispose();
            files.Dispose();
        });
    }

    [Fact]
    public void Split_HostNotYetGivenItsPane_EndsNoSibling()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            FilePaneFixture files = FilePaneFixture.Create(machine, "split-sftp-primary");
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "split-ssh-unplaced");
            SessionTabViewModel tab = new()
            {
                RootContent = new SplitContainerModel
                {
                    First = files.Pane,
                    Second = ssh.Pane,
                    Orientation = SplitOrientation.Vertical
                }
            };
            files.Mount(tab);
            ssh.Mount(tab, placeInPane: false);

            ssh.Terminal.RaiseProcessExited(0);
            PumpDispatcher();

            // Which pane the host belongs to is unknown: the primary pane is not a safe guess.
            Assert.Equal(ConnectionState.Connected, machine.GetState(files.Pane.ServerId));
            ssh.Dispose();
            files.Dispose();
        });
    }

    [Fact]
    public void TerminalPaneTornDownBeforeItsEndArrives_IsNotTrackedAgain()
    {
        RunWithApplication(() =>
        {
            ConnectionStateMachine machine = new();
            SshPaneFixture ssh = SshPaneFixture.Create(machine, "ssh-torn-down");
            SessionTabViewModel tab = new() { RootContent = ssh.Pane };
            ssh.Mount(tab);

            // A WinRM exit or a pane close tears the entry down before the view's end, which is
            // dispatched, reaches the machine.
            ssh.Terminal.RaiseProcessExited(1);
            machine.Teardown(ssh.Pane.ServerId);
            PumpDispatcher();

            Assert.Null(machine.GetStateData(ssh.Pane.ServerId));
            ssh.Dispose();
        });
    }

    private static void ClickDisconnect(FrameworkElement view)
    {
        Button button = Assert.IsType<Button>(view.FindName("DisconnectButton"));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void SeedConnected(ConnectionStateMachine machine, string sessionId, ConnectionState launching)
    {
        Assert.True(machine.TryTransition(sessionId, ConnectionState.Initializing));
        Assert.True(machine.TryTransition(sessionId, ConnectionState.ValidatingConfig));
        Assert.True(machine.TryTransition(sessionId, launching));
        Assert.True(machine.TryTransition(sessionId, ConnectionState.Connected));
    }

    private static EmbeddedSessionManager CreateManager(ConnectionStateMachine machine)
    {
        // English strings, so a reason the pane formats can be read back from the machine.
        LocalizationManager localizer = new();
        Task.Run(() => localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), "en"))
            .GetAwaiter()
            .GetResult();
        return new EmbeddedSessionManager(
            localizer,
            new ConnectionViewModelCloseTests.TrackingDialogService(confirmResult: true),
            new Heimdall.Ssh.HostKeyStore(),
            machine,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
    }

    /// <summary>Runs the dispatcher until every operation queued so far, and those they queue, has run.</summary>
    private static void PumpDispatcher()
    {
        for (int round = 0; round < 3; round++)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static void RunWithApplication(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            // WPF queues the startup event from the App constructor, and these tests pump the
            // dispatcher: without the switch the product startup runs in the test process,
            // against the developer's own data root.
            bool suppressedBefore = App.SuppressProductLifecycle;
            App.SuppressProductLifecycle = true;
            try
            {
                Assert.Null(Application.Current);
                App application = new();
                application.InitializeComponent();
                using ServiceProvider hostServices = CreateHostServices();
                application.HostServices = hostServices;
                try
                {
                    action();
                }
                finally
                {
                    ResetApplicationSingletonForTest(application);
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                App.SuppressProductLifecycle = suppressedBefore;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static void ResetApplicationSingletonForTest(App application)
    {
        Assert.Same(application, Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(Application.Current);
    }

    private sealed class SshPaneFixture : IDisposable
    {
        private readonly EmbeddedSessionManager _manager;

        private SshPaneFixture(EmbeddedSessionManager manager, SessionPaneModel pane)
        {
            _manager = manager;
            Pane = pane;
        }

        public SessionPaneModel Pane { get; }

        public RaisingTerminalSession Terminal { get; } = new();

        public EmbeddedSshView View { get; private set; } = null!;

        public static SshPaneFixture Create(ConnectionStateMachine machine, string sessionId)
        {
            SeedConnected(machine, sessionId, ConnectionState.LaunchingSsh);
            return new SshPaneFixture(
                CreateManager(machine),
                new SessionPaneModel
                {
                    PaneId = sessionId + "-pane",
                    ServerId = sessionId,
                    OriginalServerId = "inventory-" + sessionId,
                    Title = sessionId,
                    ConnectionType = "SSH",
                    Status = "Connected"
                });
        }

        public void Mount(SessionTabViewModel tab, bool placeInPane = true)
        {
            View = Assert.IsType<EmbeddedSshView>(_manager.CreateHostControl(
                tab,
                Pane.Title,
                Pane.ConnectionType,
                new TerminalSessionResult(Terminal)));
            if (placeInPane)
            {
                Pane.HostControl = View;
            }
        }

        public void Dispose() => View.Dispose();
    }

    private sealed class FilePaneFixture : IDisposable
    {
        private readonly EmbeddedSessionManager _manager;
        private readonly FtpBrowser _browser = new();

        private FilePaneFixture(EmbeddedSessionManager manager, SessionPaneModel pane)
        {
            _manager = manager;
            Pane = pane;
        }

        public SessionPaneModel Pane { get; }

        public EmbeddedSftpView View { get; private set; } = null!;

        public static FilePaneFixture Create(ConnectionStateMachine machine, string sessionId)
        {
            SeedConnected(machine, sessionId, ConnectionState.LaunchingFtp);
            return new FilePaneFixture(
                CreateManager(machine),
                new SessionPaneModel
                {
                    PaneId = sessionId + "-pane",
                    ServerId = sessionId,
                    OriginalServerId = "inventory-" + sessionId,
                    Title = sessionId,
                    ConnectionType = "FTP",
                    Status = "Connected"
                });
        }

        public void Mount(SessionTabViewModel tab)
        {
            View = Assert.IsType<EmbeddedSftpView>(_manager.CreateHostControl(
                tab,
                Pane.Title,
                Pane.ConnectionType,
                new FtpSessionBundle(_browser)));
            Pane.HostControl = View;
        }

        /// <summary>
        /// Raises the browser's own disconnect report, as a dropped connection does. The browser
        /// is never connected here, so its disconnect methods would report nothing.
        /// </summary>
        public void RaiseBrowserDisconnected(string? errorMessage)
        {
            FieldInfo? field = typeof(FtpBrowser).GetField(
                nameof(FtpBrowser.Disconnected),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Action<string?> handler = Assert.IsType<Action<string?>>(field.GetValue(_browser), exactMatch: false);
            handler(errorMessage);
        }

        public void Dispose()
        {
            View.Dispose();
            _browser.Dispose();
        }
    }

    private sealed class RaisingTerminalSession : ITerminalSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? ProcessExited;

        public bool IsRunning { get; private set; } = true;

        public int? ProcessId => IsRunning ? 1234 : null;

        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public void RaiseProcessExited(int exitCode)
        {
            IsRunning = false;
            ProcessExited?.Invoke(exitCode);
        }
    }
}
