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
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Manages active embedded sessions (RDP, SSH, SFTP) displayed as tabs.
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly ISplitService _splitService;
    private readonly IPaneCloseArbiter _closeArbiter;

    [ObservableProperty]
    private ObservableCollection<SessionTabViewModel> _activeSessions = [];

    [ObservableProperty]
    private SessionTabViewModel? _activeSession;

    [ObservableProperty]
    private bool _hasActiveSessions;

    /// <summary>
    /// Sessions whose <see cref="INotifyPropertyChanged.PropertyChanged"/> we are attached to.
    /// Kept explicitly because <see cref="NotifyCollectionChangedAction.Reset"/> carries
    /// <c>OldItems == null</c>: on a <c>Clear()</c> there is no departing-item list to
    /// unsubscribe from, so per-action bookkeeping would leak every session at once.
    /// </summary>
    private readonly HashSet<SessionTabViewModel> _accessibleNameSubscriptions = [];

    private ObservableCollection<SessionTabViewModel>? _trackedSessions;

    private bool _refreshingAccessibleNames;

    private readonly ISessionWindowService _sessionWindows;

    public ConnectionViewModel(
        LocalizationManager localizer,
        IDialogService dialogService,
        ISplitService splitService,
        IPaneCloseArbiter closeArbiter,
        ISessionWindowService sessionWindows)
    {
        _localizer = localizer;
        _dialogService = dialogService;
        _splitService = splitService;
        _closeArbiter = closeArbiter ?? throw new ArgumentNullException(nameof(closeArbiter));
        _sessionWindows = sessionWindows ?? throw new ArgumentNullException(nameof(sessionWindows));

        TrackAccessibleNames(ActiveSessions);
    }

    /// <summary>Number of sessions currently subscribed to for accessible-name refreshes.</summary>
    /// <remarks>
    /// Exposed for tests only. A leaked subscription has no observable effect on an emptied
    /// collection - the orphaned handler recomputes nothing and changes nothing - so this count
    /// is the only signal that discriminates a correct unsubscribe from a missing one.
    /// </remarks>
    internal int TrackedAccessibleNameSubscriptionCount => _accessibleNameSubscriptions.Count;

    partial void OnActiveSessionsChanged(ObservableCollection<SessionTabViewModel> value)
        => TrackAccessibleNames(value);

    private void TrackAccessibleNames(ObservableCollection<SessionTabViewModel>? sessions)
    {
        if (ReferenceEquals(_trackedSessions, sessions))
        {
            return;
        }

        if (_trackedSessions is not null)
        {
            _trackedSessions.CollectionChanged -= OnTrackedSessionsChanged;
        }

        _trackedSessions = sessions;

        if (_trackedSessions is not null)
        {
            _trackedSessions.CollectionChanged += OnTrackedSessionsChanged;
        }

        ReconcileAccessibleNameSubscriptions();
    }

    // Every mutation path funnels through here, including the four that live outside this class
    // (SessionWindowService, MainViewModel and the two in SessionCoordinator) and never call a
    // ConnectionViewModel method. Hooking the collection is what covers them.
    private void OnTrackedSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ReconcileAccessibleNameSubscriptions();

    /// <summary>
    /// Brings the subscription set back in line with the collection - subscribe to what is
    /// present and unsubscribed, unsubscribe from what is subscribed and absent - then refreshes
    /// the names. Reconciliation rather than per-action bookkeeping, so that Reset is handled
    /// like every other action.
    /// </summary>
    private void ReconcileAccessibleNameSubscriptions()
    {
        HashSet<SessionTabViewModel> current = _trackedSessions is null
            ? []
            : [.. _trackedSessions];

        foreach (SessionTabViewModel departed in _accessibleNameSubscriptions.Where(session => !current.Contains(session)).ToList())
        {
            departed.PropertyChanged -= OnTrackedSessionPropertyChanged;
            _accessibleNameSubscriptions.Remove(departed);
        }

        foreach (SessionTabViewModel arrived in current.Where(session => !_accessibleNameSubscriptions.Contains(session)))
        {
            arrived.PropertyChanged += OnTrackedSessionPropertyChanged;
            _accessibleNameSubscriptions.Add(arrived);
        }

        RefreshAccessibleNames();
    }

    private void OnTrackedSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(SessionTabViewModel.Title)
            or nameof(SessionTabViewModel.CustomTitle)
            or nameof(SessionTabViewModel.RdpModeOverrideSuffix)
            or nameof(SessionTabViewModel.DisplayTitle))
        {
            RefreshAccessibleNames();
        }
    }

    /// <summary>
    /// Gives every tab an announced name, adding an ordinal only where the displayed title
    /// collides. The ordinal is the rank in the CURRENT order of the collection, not creation
    /// order: pinning reorders the tabs, and the announced number must match what the user would
    /// count on screen. No translatable word is introduced.
    /// </summary>
    private void RefreshAccessibleNames()
    {
        if (_trackedSessions is null || _refreshingAccessibleNames)
        {
            return;
        }

        _refreshingAccessibleNames = true;
        try
        {
            Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
            foreach (SessionTabViewModel session in _trackedSessions)
            {
                occurrences[session.DisplayTitle] = occurrences.GetValueOrDefault(session.DisplayTitle) + 1;
            }

            Dictionary<string, int> assigned = new(StringComparer.Ordinal);
            foreach (SessionTabViewModel session in _trackedSessions)
            {
                string title = session.DisplayTitle;
                if (occurrences[title] <= 1)
                {
                    session.AccessibleName = title;
                    continue;
                }

                int ordinal = assigned.GetValueOrDefault(title) + 1;
                assigned[title] = ordinal;
                session.AccessibleName = $"{title} ({ordinal})";
            }
        }
        finally
        {
            _refreshingAccessibleNames = false;
        }
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
        int currentEmbeddedSessions = CountEmbeddedPanes(
            ActiveSessions.Concat(_sessionWindows.DetachedSessions));

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
    /// Counts the embedded panes across a set of session tabs.
    /// </summary>
    /// <remarks>
    /// <para>The limit is a limit on what the machine is hosting, so it has to be counted over
    /// everything hosted. Counting only this window's tabs meant detaching a session removed it
    /// from the count while it kept its ActiveX or WebView2 host alive, so detaching repeatedly
    /// let the limit be passed without bound.</para>
    /// <para>Distinct because a reattach puts the session back in the tab collection before the
    /// floating window closes, so for that moment it is legitimately in both places and must
    /// still count once. Tool panes are excluded, matching what the limit has always meant.</para>
    /// </remarks>
    internal static int CountEmbeddedPanes(IEnumerable<SessionTabViewModel> sessions) => sessions
        .Distinct()
        .SelectMany(session => Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent))
        .Count(pane => pane.HostControl is not null
            && !ConnectionTypeCatalog.IsToolConnectionType(pane.ConnectionType));

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
        ApplyPinnedOrdering();
    }

    /// <summary>
    /// Moves a session towards <paramref name="targetIndex"/> while keeping the
    /// pinned-before-unpinned invariant. The index is a request, not a command: the
    /// re-partition that follows confines the move to the session's own group, so a drag
    /// can never interleave pinned and unpinned tabs. No-op when the session is not in
    /// <see cref="ActiveSessions"/>, since a drag can only target a visible tab and an
    /// absent session means stale drag state.
    /// </summary>
    public void MoveSession(SessionTabViewModel session, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(session);

        var currentIndex = ActiveSessions.IndexOf(session);
        if (currentIndex < 0)
        {
            return;
        }

        var boundedIndex = Math.Clamp(targetIndex, 0, ActiveSessions.Count - 1);
        if (boundedIndex != currentIndex)
        {
            ActiveSessions.Move(currentIndex, boundedIndex);
        }

        ApplyPinnedOrdering();
    }

    /// <summary>
    /// Reinserts a session returning from a floating window at the boundary of the group
    /// matching its pinned state, instead of appending at the tail where a pinned tab
    /// would land after every unpinned one. No-op when the session is already present:
    /// the restore path is reachable twice for a single window.
    /// </summary>
    public void ReintroduceSession(SessionTabViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (ActiveSessions.Contains(session))
        {
            return;
        }

        ActiveSessions.Add(session);
        ApplyPinnedOrdering();
    }

    /// <summary>
    /// Re-partitions <see cref="ActiveSessions"/> through <see cref="OrderByPinned"/> and
    /// restores the current selection, which the reorder would otherwise drop.
    /// </summary>
    private void ApplyPinnedOrdering()
    {
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

    public async Task<PaneCloseResult> CloseSessionAsync(
        SessionTabViewModel? session,
        DisconnectReason reason,
        bool confirm = true,
        CloseIntent intent = CloseIntent.Interactive)
    {
        if (session is null)
        {
            return PaneCloseResult.Closed;
        }

        // Check ALL panes in the split tree for connected status (not just the primary shim)
        bool anyConnected = Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent)
            .Any(pane => ConnectionStateSets.IsConnected(pane.Status));
        if (confirm && anyConnected)
        {
            string title = _localizer["ConfirmCloseSessionTitle"];
            string message = _localizer.Format("ConfirmCloseSessionMessage", session.Title);
            bool confirmed = await _dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                return PaneCloseResult.Blocked(CloseGuardLocaleKeys.BlockedGeneric);
            }
        }

        // The connected-session confirmation above stays first and separate. A guard is not a
        // confirmation: no clearance may be issued for a close the user then declines.
        CloseRequest request = intent == CloseIntent.Silent
            ? CloseRequest.Silent(reason)
            : CloseRequest.Interactive(reason);
        try
        {
            return await CloseSessionWithRequestAsync(session, request);
        }
        finally
        {
            _closeArbiter.Release(request);
        }
    }

    /// <summary>
    /// The close cycle every path shares: poll, resolve the guards that deferred, retry once.
    /// </summary>
    /// <remarks>
    /// The retry runs EXACTLY once. It can succeed without any guard changing its answer because
    /// clearance lives in the arbiter, not in the guard - so implementers are never trusted to
    /// "answer allow next time", and a second deferral is reported as a refusal rather than
    /// looping. Everything awaited here happens outside the synchronous close primitive.
    /// </remarks>
    private async Task<PaneCloseResult> CloseSessionWithRequestAsync(
        SessionTabViewModel session,
        CloseRequest request)
    {
        PaneCloseResult result = CloseSessionInternal(session, request);
        if (result.Outcome != PaneCloseOutcome.Deferred)
        {
            ReportIfBlocked(session, result);
            return result;
        }

        IReadOnlyList<object?> hosts = LeafHosts(session);
        if (!await _closeArbiter.ResolveAsync(request, hosts))
        {
            PaneCloseResult refused = PaneCloseResult.Blocked(
                result.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric);
            ReportIfBlocked(session, refused);
            return refused;
        }

        result = CloseSessionInternal(session, request);
        if (result.Outcome == PaneCloseOutcome.Deferred)
        {
            result = PaneCloseResult.Blocked(result.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric);
        }

        ReportIfBlocked(session, result);
        return result;
    }

    private static IReadOnlyList<object?> LeafHosts(SessionTabViewModel session)
        => [.. Core.Models.SplitTreeHelper.EnumerateLeaves(session.RootContent)
                 .Select(pane => pane.HostControl)];

    private void ReportIfBlocked(SessionTabViewModel session, PaneCloseResult result)
    {
        if (result.Outcome != PaneCloseOutcome.Blocked || result.ReasonKey is null)
        {
            return;
        }

        _dialogService.ShowInfo(
            _localizer[CloseGuardLocaleKeys.BlockedTitle],
            _localizer.Format(result.ReasonKey, session.Title));
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
                .Any(pane => ConnectionStateSets.IsConnected(pane.Status)));

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
    /// Synchronously closes the exact tab whose host materialization failed, so
    /// tunnel release occurs before the connection pipeline tears down its state.
    /// </summary>
    /// <remarks>
    /// Silent: the host never materialized, so there is no work to protect and no user to ask.
    /// </remarks>
    internal void CloseFailedMaterialization(SessionTabViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!ActiveSessions.Contains(session))
        {
            return;
        }

        CloseSessionInternal(session, CloseRequest.Silent(DisconnectReason.FailedSession));
    }

    /// <summary>
    /// Closes a session without showing a confirmation dialog.
    /// Used by <see cref="CloseAllSessions"/> to avoid multiple prompts.
    /// Delegates per-pane cleanup to <see cref="ISplitService.CloseAllPanes"/>.
    /// </summary>
    private PaneCloseResult CloseSessionInternal(
        SessionTabViewModel session,
        CloseRequest request)
    {
        PaneCloseResult result = _splitService.CloseAllPanes(session, request);
        if (!result.IsClosed)
        {
            // Withheld: either terminally, or pending an asynchronous decision. Either way nothing
            // was torn down, and it is the caller's job to decide what happens next.
            return result;
        }

        ActiveSessions.Remove(session);

        if (ActiveSession == session)
        {
            ActiveSession = ActiveSessions.LastOrDefault();
        }

        HasActiveSessions = ActiveSessions.Count > 0;
        return PaneCloseResult.Closed;
    }

    /// <summary>
    /// Closes all sessions without prompting. Used during application shutdown, when WPF can no longer create dialogs.
    /// </summary>
    /// <remarks>
    /// Silent, and this is the one place where that is unambiguously right: the application is
    /// exiting, no dialog can be created any more, and a guard that withheld a pane here would
    /// leave its host undisposed rather than protect anything.
    /// <para>
    /// Reserved for application exit. The Close All command is a user gesture and drives the
    /// interactive path instead - a guard exists precisely to be consulted there.
    /// </para>
    /// </remarks>
    public void CloseAllSessionsSilently()
    {
        foreach (var session in ActiveSessions.ToList())
        {
            CloseSessionInternal(session, CloseRequest.Silent(DisconnectReason.UserAction));
        }
    }

    [RelayCommand]
    private async Task CloseAllSessions()
    {
        // Count connected sessions to decide whether to prompt
        int connectedCount = ActiveSessions.Count(s =>
            Core.Models.SplitTreeHelper.EnumerateLeaves(s.RootContent)
                .Any(pane => ConnectionStateSets.IsConnected(pane.Status)));

        if (connectedCount > 0)
        {
            string title = _localizer["ConfirmCloseAllTabs"];
            string message = _localizer.Format("ConfirmCloseAllTabsMessage", connectedCount);
            bool confirmed = await _dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                return;
            }
        }

        // A user gesture, so every session goes through the interactive driver and its guards. The
        // group confirmation above settles "do you want to close these"; it says nothing about work
        // in flight, which is what a guard is for. Routing this through the silent path would have
        // torn down a running transfer without ever asking.
        foreach (SessionTabViewModel session in ActiveSessions.ToList())
        {
            // A session that refuses does not stop the others, matching what this command did
            // before guards existed: it closed everything it could.
            await CloseSessionAsync(session, DisconnectReason.UserAction, confirm: false);
        }
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        // Fullscreen toggle will be implemented in Phase 5B with the view layer
    }
}
