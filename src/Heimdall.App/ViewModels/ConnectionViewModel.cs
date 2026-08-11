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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Manages active embedded sessions (RDP, SSH, SFTP) displayed as tabs.
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly ISplitService _splitService;

    [ObservableProperty]
    private ObservableCollection<SessionTabViewModel> _activeSessions = [];

    [ObservableProperty]
    private SessionTabViewModel? _activeSession;

    [ObservableProperty]
    private bool _hasActiveSessions;

    public ConnectionViewModel(
        LocalizationManager localizer,
        IDialogService dialogService,
        ISplitService splitService)
    {
        _localizer = localizer;
        _dialogService = dialogService;
        _splitService = splitService;
    }

    /// <summary>
    /// Adds a new remote embedded session when the configured limit has not
    /// been reached. Local tool tabs, external-client tabs, and reintroduced
    /// sessions must use the three-argument overload because they do not create
    /// another remote embedded session.
    /// </summary>
    public SessionTabViewModel? AddSession(
        string serverId,
        string title,
        string connectionType,
        int maxEmbeddedSessions)
    {
        int currentEmbeddedSessions = ActiveSessions
            .SelectMany(session => Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent))
            .Count(pane => pane.HostControl is not null
                && !pane.ConnectionType.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase));

        if (currentEmbeddedSessions >= maxEmbeddedSessions)
        {
            _dialogService.ShowWarning(
                _localizer["SessionLimitReachedTitle"],
                _localizer.Format("SessionLimitReachedMessage", maxEmbeddedSessions));
            return null;
        }

        return AddSession(serverId, title, connectionType);
    }

    /// <summary>
    /// Adds an uncounted tab for a local tool, an external client, a diagnostic,
    /// or a remote session that is being reintroduced after a split/window move.
    /// </summary>
    public SessionTabViewModel AddSession(string serverId, string title, string connectionType)
    {
        var session = new SessionTabViewModel
        {
            ServerId = serverId,
            Title = title,
            ConnectionType = connectionType,
            Status = "Connecting",
        };

        _splitService.RegisterSession(session);
        ActiveSessions.Add(session);
        ActiveSession = session;
        HasActiveSessions = ActiveSessions.Count > 0;

        return session;
    }

    /// <summary>
    /// Sets the pinned state of a tab and enforces the invariant that all pinned
    /// tabs precede all unpinned tabs, each group keeping its relative order. The
    /// current selection (<see cref="ActiveSession"/>) is preserved across the
    /// reorder. No-op when the session is not in <see cref="ActiveSessions"/>.
    /// </summary>
    public void SetPinned(SessionTabViewModel session, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!ActiveSessions.Contains(session))
        {
            return;
        }

        session.IsPinned = pinned;

        var desired = OrderByPinned(ActiveSessions);
        var selected = ActiveSession;

        for (var target = 0; target < desired.Count; target++)
        {
            var current = ActiveSessions.IndexOf(desired[target]);
            if (current != target)
            {
                ActiveSessions.Move(current, target);
            }
        }

        ActiveSession = selected;
    }

    /// <summary>
    /// Stable partition: pinned sessions first, then unpinned, each group keeping
    /// its relative input order. Pure helper for <see cref="SetPinned"/>.
    /// </summary>
    internal static IReadOnlyList<SessionTabViewModel> OrderByPinned(
        IReadOnlyList<SessionTabViewModel> sessions)
    {
        var pinned = new List<SessionTabViewModel>();
        var unpinned = new List<SessionTabViewModel>();
        foreach (var session in sessions)
        {
            (session.IsPinned ? pinned : unpinned).Add(session);
        }

        pinned.AddRange(unpinned);
        return pinned;
    }

    [RelayCommand]
    private async Task CloseSession(SessionTabViewModel? session)
        => await CloseSessionAsync(session, DisconnectReason.TabClose);

    public async Task CloseSessionAsync(
        SessionTabViewModel? session,
        DisconnectReason reason,
        bool confirm = true)
    {
        if (session is null)
        {
            return;
        }

        // Check ALL panes in the split tree for connected status (not just the primary shim)
        var anyConnected = Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent)
            .Any(p => string.Equals(p.Status, "Connected", StringComparison.Ordinal));
        if (confirm && anyConnected)
        {
            var title = _localizer["ConfirmCloseSessionTitle"];
            var message = _localizer.Format("ConfirmCloseSessionMessage", session.Title);
            var confirmed = await _dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                return;
            }
        }

        CloseSessionInternal(session, reason);
    }

    /// <summary>
    /// Confirms a fixed group of connected sessions once, then closes every target
    /// without repeating the per-session confirmation. Busy tool tabs keep the
    /// existing fail-closed behavior of <see cref="ISplitService.CloseAllPanes"/>.
    /// </summary>
    internal async Task CloseSessionsAsync(
        IReadOnlyList<SessionTabViewModel> sessions,
        DisconnectReason reason)
    {
        int connectedCount = sessions.Count(session =>
            Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent)
                .Any(pane => string.Equals(pane.Status, "Connected", StringComparison.Ordinal)));

        if (connectedCount > 0)
        {
            string title = _localizer["ConfirmCloseSessionGroupTitle"];
            string message = _localizer.Format(
                "ConfirmCloseSessionGroupMessage",
                sessions.Count,
                connectedCount);
            bool confirmed = await _dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                return;
            }
        }

        foreach (SessionTabViewModel session in sessions)
        {
            await CloseSessionAsync(session, reason, confirm: false);
        }
    }

    /// <summary>
    /// Closes a session without showing a confirmation dialog.
    /// Used by <see cref="CloseAllSessions"/> to avoid multiple prompts.
    /// Delegates per-pane cleanup to <see cref="ISplitService.CloseAllPanes"/>.
    /// </summary>
    private void CloseSessionInternal(
        SessionTabViewModel session,
        DisconnectReason reason = DisconnectReason.UserAction)
    {
        if (!_splitService.CloseAllPanes(session, reason))
            return; // Blocked by a busy tool pane

        ActiveSessions.Remove(session);

        if (ActiveSession == session)
        {
            ActiveSession = ActiveSessions.LastOrDefault();
        }

        HasActiveSessions = ActiveSessions.Count > 0;
    }

    /// <summary>
    /// Closes all sessions without prompting. Used during application shutdown, when WPF can no longer create dialogs.
    /// </summary>
    public void CloseAllSessionsSilently()
    {
        foreach (var session in ActiveSessions.ToList())
        {
            CloseSessionInternal(session, DisconnectReason.UserAction);
        }
    }

    [RelayCommand]
    private async Task CloseAllSessions()
    {
        // Count connected sessions to decide whether to prompt
        int connectedCount = ActiveSessions.Count(s =>
            Core.Models.SplitTreeHelper.EnumerateLeaves(s.RootContent)
                .Any(p => string.Equals(p.Status, "Connected", StringComparison.Ordinal)));

        if (connectedCount > 0)
        {
            var title = _localizer["ConfirmCloseAllTabs"];
            var message = _localizer.Format("ConfirmCloseAllTabsMessage", connectedCount);
            var confirmed = await _dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                return;
            }
        }

        CloseAllSessionsSilently();
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        // Fullscreen toggle will be implemented in Phase 5B with the view layer
    }
}
