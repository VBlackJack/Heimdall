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
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.Core.Models;

/// <summary>
/// Leaf node in the recursive split pane tree, representing a single session pane.
/// Each pane owns one connection (RDP, SSH, SFTP, etc.) with its own state machine key.
/// </summary>
public partial class SessionPaneModel : ObservableObject, ISplitContent
{
    /// <summary>
    /// Stable identity for addressing this pane in tree operations.
    /// Generated once at creation, never changes.
    /// </summary>
    [ObservableProperty]
    private string _paneId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The visual host control for this pane.
    /// Typed as object to keep the model free of WPF dependencies.
    /// </summary>
    [ObservableProperty]
    private object? _hostControl;

    // A host learns its pane where it is placed, not where each caller remembers to tell it. The
    // callers that forgot left SSH-family hosts with no pane, and a swap, a merge or a detach then
    // made their Reconnect and Close act on another, live session.
    partial void OnHostControlChanged(object? value)
    {
        if (value is ISessionPaneOwner owner)
        {
            owner.SetOwningPane(this);
        }
    }

    /// <summary>
    /// Session-scoped ID used for state machine keying and tunnel tracking.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileLookupServerId))]
    private string _serverId = "";

    /// <summary>
    /// The original server inventory ID, used for server lookups (reconnect,
    /// duplicate-tab, auto-SFTP).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileLookupServerId))]
    private string _originalServerId = "";

    [ObservableProperty]
    private string _connectionType = "";

    /// <summary>
    /// Transient, session-scoped flag marking this pane as a broadcast target while
    /// <see cref="BroadcastScope.SelectedPanes"/> is active. Never persisted; reset
    /// when SelectedPanes is (re-)entered. Ignored for non-terminal panes.
    /// </summary>
    [ObservableProperty]
    private bool _isBroadcastTarget;

    [ObservableProperty]
    private string? _sftpReconnectPathHint;

    /// <summary>
    /// Transient: this pane stands in for the SFTP companion of an SSH session that has not
    /// connected it yet. Set when the tab is laid out before the connect and cleared when the
    /// companion fills the pane. Never persisted. While one is pending the tab's layout commands
    /// (swap, split, merge, unsplit, detach) are refused, because the SSH session still looks its
    /// tab up by the first pane.
    /// </summary>
    [ObservableProperty]
    private bool _isPendingCompanion;

    /// <summary>
    /// Transient, per-pane toggle controlling whether this SFTP pane follows OSC 7
    /// current-directory notifications emitted by a sibling SSH terminal.
    /// </summary>
    [ObservableProperty]
    private bool _sftpFollowSshDirectory;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _status = "";

    /// <summary>
    /// How the title of a tool pane is worded again after a language change. Null for every pane
    /// that is not a tool: a server name or a name the user typed must never be reworded. Transient,
    /// never persisted.
    /// </summary>
    public Func<string>? TitleWording { get; set; }

    /// <summary>
    /// How the status of a tool pane is worded again after a language change. Null for every pane
    /// that is not a tool. Transient, never persisted.
    /// </summary>
    public Func<string>? StatusWording { get; set; }

    /// <summary>The title the wording produced last; a later, different title was set by someone else and is left alone.</summary>
    public string? LastTitleWording { get; private set; }

    /// <summary>The status the wording produced last; a later, different status was set by someone else and is left alone.</summary>
    public string? LastStatusWording { get; private set; }

    /// <summary>Registers the wording of the title, which the pane carries now.</summary>
    public void UseTitleWording(Func<string> wording)
    {
        ArgumentNullException.ThrowIfNull(wording);
        TitleWording = wording;
        LastTitleWording = Title;
    }

    /// <summary>Registers the wording of the status, which the pane carries now.</summary>
    public void UseStatusWording(Func<string> wording)
    {
        ArgumentNullException.ThrowIfNull(wording);
        StatusWording = wording;
        LastStatusWording = Status;
    }

    /// <summary>
    /// The title this pane should carry after a language change, or null when it has no wording,
    /// its title is no longer the one the wording produced, or nothing changed.
    /// </summary>
    public string? ReworkedTitle()
    {
        if (TitleWording is null || !string.Equals(Title, LastTitleWording, StringComparison.Ordinal))
        {
            return null;
        }

        string next = TitleWording();
        LastTitleWording = next;
        return string.Equals(next, Title, StringComparison.Ordinal) ? null : next;
    }

    /// <summary>
    /// The status this pane should carry after a language change, or null when it has no wording,
    /// its status is no longer the one the wording produced, or nothing changed.
    /// </summary>
    public string? ReworkedStatus()
    {
        if (StatusWording is null || !string.Equals(Status, LastStatusWording, StringComparison.Ordinal))
        {
            return null;
        }

        string next = StatusWording();
        LastStatusWording = next;
        return string.Equals(next, Status, StringComparison.Ordinal) ? null : next;
    }

    [ObservableProperty]
    private string _tunnelRoute = "";

    [ObservableProperty]
    private string _environmentColor = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailureDetails))]
    [NotifyPropertyChangedFor(nameof(HasFailureCode))]
    [NotifyPropertyChangedFor(nameof(HasFailureDetail))]
    private SessionDiagnostic? _failureDetails;

    /// <summary>
    /// Server inventory id to use for profile-scoped lookups (reconnect, duplicate-tab,
    /// merge, connection history, saved RDP defaults). Prefers <see cref="OriginalServerId"/>
    /// (the stable inventory id) and falls back to <see cref="ServerId"/> only when the
    /// original is blank (for example ad-hoc sessions never tied to a profile). This is NOT
    /// a state-machine or tunnel key; those must keep using <see cref="ServerId"/> directly.
    /// </summary>
    public string ProfileLookupServerId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OriginalServerId))
            {
                return OriginalServerId;
            }

            return ServerId;
        }
    }

    /// <summary>
    /// True when this pane has structured failure diagnostics to display.
    /// </summary>
    public bool HasFailureDetails => FailureDetails is not null;

    /// <summary>
    /// True when the diagnostic exposes a numeric protocol-specific code.
    /// </summary>
    public bool HasFailureCode => FailureDetails?.Code is not null;

    /// <summary>
    /// True when the diagnostic exposes a raw technical detail string.
    /// </summary>
    public bool HasFailureDetail => !string.IsNullOrWhiteSpace(FailureDetails?.Detail);
}
