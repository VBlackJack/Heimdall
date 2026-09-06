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

using Heimdall.App.ViewModels;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// The decisions the exit path takes, as pure predicates: one place for each rule, so
/// the windows and handlers that apply them cannot hold diverging copies.
/// </summary>
/// <remarks>
/// The main window already returned early on a shutting-down application; the floating
/// windows did not, and polled their close guards interactively while the application
/// was exiting. The unhandled-exception handler showed a modal dialog with no such
/// guard, so a fault during teardown kept the process alive until someone clicked it.
/// Each rule below is the one the main window already followed, made shared.
/// </remarks>
internal static class ShutdownDecisions
{
    /// <summary>The status a session pane reports while it is connected.</summary>
    private const string ConnectedStatus = "Connected";

    /// <summary>
    /// Whether a floating window may poll its close guards, which can prompt the user.
    /// Never while the application is shutting down: exit owns every session then, and
    /// a prompt would hold the process - and the update relauncher waiting on it.
    /// </summary>
    public static bool FloatingWindowShouldPollGuards(
        bool isShuttingDown,
        bool closeGranted,
        bool reattached,
        bool hostIsGuard)
        => !isShuttingDown && !closeGranted && !reattached && hostIsGuard;

    /// <summary>
    /// Whether a closing floating window should run the interactive session close
    /// itself. During shutdown it only hands the session back to the main collection,
    /// so the exit snapshot records it and the silent close tears it down.
    /// </summary>
    public static bool FloatingWindowShouldCloseSessionInteractively(bool isShuttingDown, bool reattached)
        => !isShuttingDown && !reattached;

    /// <summary>
    /// Whether an unhandled exception may be shown in a dialog. During shutdown it is
    /// logged and flushed only: a modal box on the way out stops the process from ending.
    /// </summary>
    public static bool ShouldShowUnhandledExceptionDialog(bool isShuttingDown) => !isShuttingDown;

    /// <summary>
    /// How many sessions are connected, counting the ones detached into floating
    /// windows. The close confirmation counted the main collection only, and detaching
    /// removes a session from it: three detached sessions produced a count of zero.
    /// </summary>
    public static int CountConnectedSessions(
        IEnumerable<SessionTabViewModel> attached,
        IEnumerable<SessionTabViewModel> detached)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(detached);
        return attached.Concat(detached).Count(IsConnected);
    }

    private static bool IsConnected(SessionTabViewModel session)
        => SplitTreeHelper.EnumerateLeaves(session.RootContent)
            .Any(pane => string.Equals(pane.Status, ConnectedStatus, StringComparison.Ordinal));
}
