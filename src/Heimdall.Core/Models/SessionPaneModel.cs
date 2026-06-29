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
    /// Transient, per-pane toggle controlling whether this SFTP pane follows OSC 7
    /// current-directory notifications emitted by a sibling SSH terminal.
    /// </summary>
    [ObservableProperty]
    private bool _sftpFollowSshDirectory;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _status = "";

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
