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
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Assigns the exact pane that owns a hosted session surface.
/// </summary>
public interface ISessionPaneOwner
{
    /// <summary>
    /// Associates the host with the pane whose runtime lifecycle it represents.
    /// </summary>
    void SetOwningPane(SessionPaneModel pane);
}

/// <summary>
/// Factory for embedded session host controls (terminals, RDP, SFTP, tools).
/// </summary>
public interface IEmbeddedSessionManager
{
    /// <summary>
    /// Optional callback invoked when a terminal view broadcasts input.
    /// Parameters: (byte[] data, object? senderView).
    /// Wired by MainViewModel to relay keystrokes to all other terminals.
    /// </summary>
    Action<byte[], object?>? BroadcastCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view's Split button is clicked.
    /// Parameters: (SessionTabViewModel session).
    /// Wired by MainWindow code-behind to show the split picker context menu.
    /// </summary>
    Action<SessionTabViewModel>? SplitRequestedCallback { get; set; }

    /// <summary>
    /// Func that returns the current broadcast mode state.
    /// Wired by MainViewModel so newly created views show the badge immediately.
    /// </summary>
    Func<bool>? IsBroadcastActive { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded session view requests reconnection.
    /// Parameters: (SessionTabViewModel session, string serverId, string connectionType).
    /// Wired by MainViewModel to restart the connection using the original server.
    /// </summary>
    Action<SessionTabViewModel, string, string>? ReconnectRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded split-pane view requests reconnection.
    /// Parameters: (SessionTabViewModel session, SessionPaneModel pane).
    /// Wired by MainViewModel to reconnect only the owning pane.
    /// </summary>
    Action<SessionTabViewModel, SessionPaneModel>? ReconnectPaneRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view requests user-driven disconnect.
    /// Parameters: (SessionTabViewModel session, SessionPaneModel pane, DisconnectReason reason).
    /// Wired by MainViewModel to close the owning pane or tab through the shared lifecycle path.
    /// </summary>
    Action<SessionTabViewModel, SessionPaneModel, DisconnectReason>? DisconnectRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded view's disconnect overlay
    /// requests the tab itself be closed (not just dismissing the overlay).
    /// Parameters: (SessionTabViewModel session).
    /// Wired by <c>SessionCoordinator</c> to delegate to
    /// <c>ConnectionViewModel.CloseSessionAsync</c>.
    /// </summary>
    Action<SessionTabViewModel>? CloseRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback invoked when an embedded RDP view requests server profile editing.
    /// Parameters: (string serverId).
    /// Wired by MainViewModel to open the existing server edit flow.
    /// </summary>
    Action<string>? EditServerRequestedCallback { get; set; }

    /// <summary>
    /// Optional callback for cross-tool navigation. Allows tool views to open other tools.
    /// Parameters: (string toolId, string title, ToolContext? context).
    /// Wired by MainViewModel to delegate to <c>OpenToolTabAsync</c>.
    /// </summary>
    Func<string, string, ToolContext?, Task>? OpenToolCallback { get; set; }

    /// <summary>
    /// Creates a visual host for an embedded connection session.
    /// </summary>
    object CreateHostControl(
        SessionTabViewModel sessionTab,
        string displayName,
        string connectionType,
        ISessionResult session,
        AppSettings? settings = null,
        string? initialRemotePath = null);

    /// <summary>
    /// Tears down a hosted pane through the shared disconnect lifecycle.
    /// </summary>
    void DisconnectSession(SessionPaneModel pane, DisconnectReason reason);

    /// <summary>
    /// Creates an SSH host control in a Connecting state before the SSH session exists.
    /// </summary>
    EmbeddedSshView CreateConnectingSshHostControl(
        SessionTabViewModel sessionTab,
        string displayName,
        ServerProfileDto server,
        AppSettings? settings = null);

    /// <summary>
    /// Attaches a freshly-connected SSH or terminal session to a pre-mounted SSH host control.
    /// </summary>
    void AttachSshSession(
        SessionTabViewModel sessionTab,
        ISessionResult sessionResult,
        AppSettings? settings = null);

    /// <summary>
    /// Creates a host control for a tool tab (non-connection UI surface).
    /// </summary>
    object CreateToolControl(
        SessionTabViewModel sessionTab,
        string toolId,
        ToolContext? context,
        AppSettings? settings = null);

    /// <summary>
    /// Injects a command into the first terminal pane of the given session.
    /// Walks the session's split tree and forwards the command to the first
    /// leaf whose host control is an <see cref="ITerminalCommandSink"/>.
    /// </summary>
    /// <param name="session">The session whose terminal should receive the command.</param>
    /// <param name="command">The command to inject (forwarded as-is).</param>
    /// <returns>
    /// <c>true</c> when a terminal sink was found and the command was written;
    /// <c>false</c> when <paramref name="session"/> is <c>null</c> or no pane
    /// can accept a command (e.g. an RDP-only session).
    /// </returns>
    bool TrySendCommandToSession(SessionTabViewModel session, string command);
}
