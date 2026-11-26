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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The open-sessions threshold: past it, a notice says what the sessions cost, and nothing is
/// refused.
/// </summary>
/// <remarks>
/// <para>The setting used to be a hard limit, checked after the connection had already been made:
/// the tab was refused and a working connection thrown away. Counting every pane on top of that
/// stopped an SSH user at 5 tabs by default. A ceiling that blocks the users with the most sessions
/// open protects nobody they would thank for it, so the setting now marks where Heimdall starts
/// to say so.</para>
/// <para>The notice is a banner in its own layout row, not a dialog: it never waits for an answer,
/// and it pushes content down rather than lying over an RDP ActiveX surface.</para>
/// </remarks>
public partial class ConnectionViewModel
{
    private const string SessionLoadNoticeKey = "SessionLoadNoticeText";

    /// <summary>The threshold last reported; 0 until a session has been opened against one.</summary>
    private int _sessionLoadThreshold;

    /// <summary>The open-session count the notice text was last built from.</summary>
    private int _sessionLoadNoticeOpenCount;

    /// <summary>Set when the user hid the notice; cleared once the count is back under the threshold.</summary>
    private bool _sessionLoadNoticeDismissed;

    /// <summary>True while the open-sessions notice is on screen.</summary>
    [ObservableProperty]
    private bool _isSessionLoadNoticeShown;

    /// <summary>What the open-sessions notice says.</summary>
    [ObservableProperty]
    private string _sessionLoadNoticeText = string.Empty;

    /// <summary>
    /// True when <paramref name="sessions"/> already hold <paramref name="threshold"/> embedded
    /// connections or more, so one more goes past it. A threshold of 0 or less never trips.
    /// </summary>
    internal static bool IsAtSessionThreshold(IEnumerable<SessionTabViewModel>? sessions, int threshold)
        => sessions is not null
            && threshold > 0
            && CountEmbeddedConnections(sessions) >= threshold;

    /// <summary>
    /// Records that a new embedded connection is being opened against <paramref name="threshold"/>,
    /// and shows the notice when it goes past it, unless the user hid the notice since the count
    /// last went past.
    /// </summary>
    internal void NoteNewConnection(int threshold)
    {
        _sessionLoadThreshold = threshold;
        if (!IsAtSessionThreshold(AllOpenSessions, threshold))
        {
            return;
        }

        int open = CountEmbeddedConnections(AllOpenSessions) + 1;
        _sessionLoadNoticeOpenCount = open;
        SessionLoadNoticeText = _localizer.Format(SessionLoadNoticeKey, open, threshold);
        if (!_sessionLoadNoticeDismissed)
        {
            IsSessionLoadNoticeShown = true;
        }
    }

    /// <summary>Rewords the notice from the counts it was built from, once a notice has been built.</summary>
    private void RefreshSessionLoadNoticeText()
    {
        if (_sessionLoadNoticeOpenCount <= 0)
        {
            return;
        }

        string text = _localizer.Format(SessionLoadNoticeKey, _sessionLoadNoticeOpenCount, _sessionLoadThreshold);
        if (!string.Equals(text, SessionLoadNoticeText, StringComparison.Ordinal))
        {
            SessionLoadNoticeText = text;
        }
    }

    [RelayCommand]
    private void DismissSessionLoadNotice()
    {
        IsSessionLoadNoticeShown = false;
        _sessionLoadNoticeDismissed = true;
    }

    /// <summary>
    /// Takes the notice down once the sessions are back under the threshold, and forgets that it
    /// was hidden, so going past again shows it again.
    /// </summary>
    /// <remarks>
    /// Runs on every change of the strip, from whatever thread made it, so it does nothing unless
    /// there is a notice to take down or a dismissal to forget. The census it would otherwise run
    /// reads the floating windows, which belongs on the UI thread: its other caller, opening a
    /// session, runs there, and an idle strip change has no reason to touch it.
    /// </remarks>
    private void RefreshSessionLoadNotice()
    {
        if (!IsSessionLoadNoticeShown && !_sessionLoadNoticeDismissed)
        {
            return;
        }

        if (_sessionLoadThreshold <= 0
            || CountEmbeddedConnections(AllOpenSessions) >= _sessionLoadThreshold)
        {
            return;
        }

        IsSessionLoadNoticeShown = false;
        _sessionLoadNoticeDismissed = false;
    }
}
