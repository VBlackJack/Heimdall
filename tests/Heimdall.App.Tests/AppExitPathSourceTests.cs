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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The shape of the exit path, read from the source through the statement predicate. WPF
/// overrides cannot be driven by a test without a live application, and the ordering rule
/// of PR #311 - everything needing the application runs before the first await of OnExit -
/// was asserted for the extracted helper and for nothing else, which is how five cleanup
/// steps stayed behind that await.
/// </summary>
/// <remarks>
/// Every anchor is a whole statement of the method body it is read from, carried through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>, so a statement folded behind a
/// term that is false by construction is not mistaken for one that stands. The behaviour
/// behind each site lives in a pure helper with its own tests; what is read here is only
/// that the site goes through it, and in what order.
/// </remarks>
public sealed class AppExitPathSourceTests
{
    private const string OnExitMember = "protected override async void OnExit(ExitEventArgs e)";
    private const string OnStartupMember = "protected override async void OnStartup(StartupEventArgs e)";
    private const string UnhandledMember = "private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)";
    private const string SessionEndingMember = "private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args)";
    private const string ContainerDisposeMember = "private async Task DisposeContainerBoundedAsync()";

    private const string ReleaseRdpArtifactsStatement = "ReleaseRdpArtifacts();";
    private const string ReleaseTunnelsStatement = "ReleaseTunnels();";
    private const string StopSchedulerStatement = "StopScheduler();";
    private const string StopX11Statement = "StopX11Server();";
    private const string ReleaseSleepStatement = "ReleaseSleepPrevention();";
    private const string FlushStatement = "Core.Logging.FileLogger.Flush();";
    private const string SnapshotStatement = "await SaveSnapshotAndCloseSessionsAsync();";
    private const string ContainerDisposeStatement = "await DisposeContainerBoundedAsync();";
    private const string BoundedStepStatement = "await ExitStep.RunBoundedAsync(";
    private const string SingleInstanceStatement = "switch (SingleInstanceGuard.TryAcquire(";
    private const string SplashStatement = "var splash = CreateSplashWindow();";
    private const string SubscribeUnhandledStatement = "DispatcherUnhandledException += OnDispatcherUnhandledException;";
    private const string SubscribeSessionEndingStatement = "SessionEnding += OnSessionEnding;";
    private const string DialogDecisionStatement = "if (ShutdownDecisions.ShouldShowUnhandledExceptionDialog(IsShuttingDown))";
    private const string HandledStatement = "args.Handled = true;";
    private const string MarkShutdownStatement = "IsShuttingDown = true;";
    private const string PersistOnSessionEndStatement = "if (MainWindow is MainWindow window)";

    private const string RdpDisposeMember = "private void Dispose(DisconnectReason reason)";
    private const string RdpCompleteMember = "private void CompleteDispose(DisconnectReason reason)";
    private const string RdpTearDownMember = "private void TearDownRdpHost(DisconnectReason reason)";
    private const string RdpSequenceStatement = "DisposeSequence.Run(";
    private const string RdpTearDownStatement = "TearDownRdpHost(reason);";
    private const string RdpExecuteStatement = "RdpDisconnectTeardownSequence.Execute(this, reason);";

    private const string FloatingClosingMember = "protected override void OnClosing(CancelEventArgs e)";
    private const string FloatingClosedMember = "protected override void OnClosed(EventArgs e)";
    private const string FloatingReleaseMember = "private void ReleaseSessionOnClose(bool isShuttingDown)";
    private const string FloatingFlagStatement = "bool isShuttingDown = Application.Current is Heimdall.App.App";
    private const string FloatingPollStatement = "if (e.Cancel || !ShutdownDecisions.FloatingWindowShouldPollGuards(isShuttingDown, _closeGranted, _reattached, _session.HostControl is ICloseGuard))";
    private const string FloatingReleaseCall = "ReleaseSessionOnClose(isShuttingDown);";
    private const string FloatingRestoreStatement = "RestoreSession(vm);";
    private const string FloatingInteractiveStatement = "if (ShutdownDecisions.FloatingWindowShouldCloseSessionInteractively(isShuttingDown, _reattached))";

    private const string MainClosingMember = "protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)";
    private const string MainCensusMember = "private int CountConnectedSessionsForClose(MainViewModel vm)";
    private const string MainCensusStatement = "int connectedSessionCount = CountConnectedSessionsForClose(vm);";
    private const string MainCensusReturn = "return ShutdownDecisions.CountConnectedSessions(vm.Connection.ActiveSessions, _sessionWindowService.DetachedSessions);";

    [Fact]
    public void OnExit_ReleasesExternalResourcesBeforeTheFirstAwait()
    {
        string logic = Logic("App.xaml.cs", OnExitMember);

        // One call per constant, not a loop over them: the assertion guard associates an
        // IndexOf with the predicate by the anchor it was given, and a loop variable is not
        // that anchor.
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReleaseRdpArtifactsStatement), "the RDP artifact release is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReleaseTunnelsStatement), "the tunnel release is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, StopSchedulerStatement), "the scheduler stop is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, StopX11Statement), "the X11 stop is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReleaseSleepStatement), "the sleep release is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, FlushStatement), "the log flush is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SnapshotStatement), "the snapshot save is not a step of OnExit");

        int snapshot = logic.IndexOf(SnapshotStatement, StringComparison.Ordinal);
        Assert.True(logic.IndexOf(ReleaseRdpArtifactsStatement, StringComparison.Ordinal) < snapshot, "the RDP artifact release runs after the first await, in an application that no longer exists");
        Assert.True(logic.IndexOf(ReleaseTunnelsStatement, StringComparison.Ordinal) < snapshot, "the tunnel release runs after the first await");
        Assert.True(logic.IndexOf(StopSchedulerStatement, StringComparison.Ordinal) < snapshot, "the scheduler stop runs after the first await");
        Assert.True(logic.IndexOf(StopX11Statement, StringComparison.Ordinal) < snapshot, "the X11 stop runs after the first await");
        Assert.True(logic.IndexOf(ReleaseSleepStatement, StringComparison.Ordinal) < snapshot, "the sleep release runs after the first await");
        Assert.True(logic.IndexOf(FlushStatement, StringComparison.Ordinal) < snapshot, "the log is not flushed before the first await");
    }

    [Fact]
    public void OnExit_BoundsTheContainerDisposalAndTheHostKeyFlush()
    {
        string exit = Logic("App.xaml.cs", OnExitMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(exit, ContainerDisposeStatement), "the container disposal is not a step of OnExit");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(exit, BoundedStepStatement), "the host key flush is not a bounded step of OnExit");

        string dispose = Logic("App.xaml.cs", ContainerDisposeMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(dispose, BoundedStepStatement), "the container disposal is not bounded");

        // Absence: the unbounded form, the only unbounded await the exit path had.
        Assert.DoesNotContain("await asyncProvider.DisposeAsync();", ReadAppSource("App.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnExit_DoesNotSetAStatusNobodyCanSee()
    {
        // Every window is closed by the time OnExit runs; the "saving snapshot" status
        // was a localized string, a service lookup and a null-forgiving dereference
        // that bought nothing.
        Assert.DoesNotContain("StatusSnapshotSaving", ReadAppSource("App.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnStartup_DecidesSingleInstanceBeforeShowingTheSplash()
    {
        string logic = Logic("App.xaml.cs", OnStartupMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SingleInstanceStatement), "the single-instance decision is not a step of OnStartup");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SplashStatement), "the splash creation is not a step of OnStartup");
        Assert.True(
            logic.IndexOf(SingleInstanceStatement, StringComparison.Ordinal) < logic.IndexOf(SplashStatement, StringComparison.Ordinal),
            "a second launch must not flash a splash before handing over");
    }

    [Fact]
    public void OnStartup_WiresTheShutdownAwareHandlers()
    {
        string startup = Logic("App.xaml.cs", OnStartupMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(startup, SubscribeUnhandledStatement), "the unhandled-exception handler is not subscribed");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(startup, SubscribeSessionEndingStatement), "the session-ending handler is not subscribed");

        string unhandled = Logic("App.xaml.cs", UnhandledMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(unhandled, DialogDecisionStatement), "the dialog is shown without consulting the shutdown decision");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(unhandled, HandledStatement), "the exception is not marked handled");

        string ending = Logic("App.xaml.cs", SessionEndingMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ending, MarkShutdownStatement), "a logoff does not mark the application as shutting down");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(ending, PersistOnSessionEndStatement), "a logoff does not persist the window state");
    }

    [Fact]
    public void RdpViewDispose_RunsTheComTeardownThroughTheDisposeSequence()
    {
        string dispose = ViewSource.HandlerLogic(RdpDisposeMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(dispose, RdpSequenceStatement), "the dispose does not go through DisposeSequence, so a throwing release skips the COM teardown");

        string complete = ViewSource.HandlerLogic(RdpCompleteMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(complete, RdpTearDownStatement), "the teardown half does not tear the host down");

        string tearDown = ViewSource.HandlerLogic(RdpTearDownMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(tearDown, RdpExecuteStatement), "the host teardown does not run the canonical COM sequence");
    }

    [Fact]
    public void FloatingWindow_ConsultsTheShutdownDecisionsOnBothClosePaths()
    {
        string closing = Logic("Views/FloatingSessionWindow.xaml.cs", FloatingClosingMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(closing, FloatingFlagStatement), "OnClosing does not read the shutting-down flag from the application");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(closing, FloatingPollStatement), "OnClosing does not pass the flag to the guard-poll decision");

        string closed = Logic("Views/FloatingSessionWindow.xaml.cs", FloatingClosedMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(closed, FloatingFlagStatement), "OnClosed does not read the shutting-down flag from the application");

        string release = Logic("Views/FloatingSessionWindow.xaml.cs", FloatingReleaseMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(release, FloatingRestoreStatement), "the session is not restored to the main collection");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(release, FloatingInteractiveStatement), "the interactive close is not gated on the shutdown decision");

        // The restore precedes the decision: a session torn down on the shutdown path must
        // be back in the main collection for the exit snapshot.
        Assert.True(
            release.IndexOf(FloatingRestoreStatement, StringComparison.Ordinal) < release.IndexOf(FloatingInteractiveStatement, StringComparison.Ordinal),
            "the session must be restored before the interactive close is decided");
    }

    [Fact]
    public void MainWindowClosing_CountsDetachedSessions()
    {
        string closing = Logic("MainWindow.xaml.cs", MainClosingMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(closing, MainCensusStatement), "the close confirmation does not take the census");

        string census = Logic("MainWindow.xaml.cs", MainCensusMember);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(census, MainCensusReturn), "the census does not count detached sessions");
    }

    /// <summary>One method of an App source file, with comments and literals blanked.</summary>
    private static string Logic(string relativePath, string signature)
        => ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(ReadAppSource(relativePath)), signature);

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
