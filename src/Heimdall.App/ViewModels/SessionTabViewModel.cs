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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel for a single embedded session tab. Supports recursive N-pane split
/// layouts via a tree of <see cref="ISplitContent"/> nodes. The tree root is either
/// a single <see cref="SessionPaneModel"/> (no split) or a <see cref="SplitContainerModel"/>
/// (one or more splits).
/// </summary>
public partial class SessionTabViewModel : ObservableObject
{
    /// <summary>
    /// The root of the recursive split pane tree.
    /// A single <see cref="SessionPaneModel"/> when unsplit, or a
    /// <see cref="SplitContainerModel"/> when split into multiple panes.
    /// </summary>
    [ObservableProperty]
    private ISplitContent _rootContent = new SessionPaneModel();

    /// <summary>
    /// Indicates the session is performing a long-running operation
    /// (scan, export, etc.). Drives the tab header busy indicator.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderToolTip))]
    private RdpModeOverride _rdpModeOverride = RdpModeOverride.UseProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderToolTip))]
    private string _rdpModeOverrideSuffix = string.Empty;

    /// <summary>
    /// User-supplied custom tab title. When non-blank it overrides the auto title
    /// (and suppresses the RDP mode suffix). Session-lifetime only; never persisted.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderToolTip))]
    private string? _customTitle;

    /// <summary>
    /// True when the user pinned this tab. Pinned tabs sort before unpinned tabs
    /// and are excluded from "Close others" / "Close to the right". Session-lifetime
    /// only; never persisted.
    /// </summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>
    /// True when this session was started ad-hoc from the Command Palette
    /// (typing a bare IP/hostname) and is not backed by a saved server profile.
    /// </summary>
    public bool IsAdHoc { get; private set; }

    /// <summary>
    /// Snapshot of the connection DTO captured when the ad-hoc session was
    /// started. Used to seed the "Save as profile" dialog. Null for sessions
    /// backed by an existing server profile.
    /// </summary>
    public ServerProfileDto? AdHocProfileSnapshot { get; private set; }

    /// <summary>
    /// Per-tab manual override of the Tunnels panel expanded state, set by the user
    /// during the tab's lifetime. null = no manual override; falls back to profile
    /// or app default. Lives only as long as the tab; never persisted.
    /// </summary>
    [ObservableProperty]
    private bool? _tunnelsPanelManualOverride;

    /// <summary>
    /// Aggregated tunnel badge state for this tab. Computed by walking all
    /// panes via SplitTreeHelper.EnumerateLeaves and inspecting TunnelManager
    /// state for each pane's ServerId. Updated by TunnelsViewModel on tunnel
    /// open/close events and on tab structure changes.
    /// </summary>
    [ObservableProperty]
    private TunnelBadgeState _tunnelBadgeState;

    /// <summary>
    /// Returns the first leaf pane in the tree (the "primary" pane).
    /// Used for tab header display (title, icon, status).
    /// </summary>
    public SessionPaneModel PrimaryPane =>
        SplitTreeHelper.FirstLeaf(RootContent) ?? _emptyPane;

    /// <summary>
    /// Marks this tab as an ad-hoc session and stores the profile snapshot
    /// used to seed a future saved profile.
    /// </summary>
    public void MarkAsAdHoc(ServerProfileDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IsAdHoc = true;
        AdHocProfileSnapshot = snapshot;
        OnPropertyChanged(nameof(IsAdHoc));
        OnPropertyChanged(nameof(AdHocProfileSnapshot));
    }

    partial void OnRootContentChanged(ISplitContent value) => NotifyTreeDependentProperties();

    // ── Backward-compatibility shim properties ────────────────────
    // These delegate to PrimaryPane so existing consumers (tab header
    // bindings, context menu, palette) continue working during migration.
    // Setters update the primary pane in-place.

    public string ServerId
    {
        get => PrimaryPane.ServerId;
        set
        {
            PrimaryPane.ServerId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileLookupServerId));
        }
    }

    public string OriginalServerId
    {
        get => PrimaryPane.OriginalServerId;
        set
        {
            PrimaryPane.OriginalServerId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileLookupServerId));
        }
    }

    /// <summary>
    /// Profile-scoped lookup id for this tab's primary pane.
    /// See <see cref="SessionPaneModel.ProfileLookupServerId"/>.
    /// </summary>
    public string ProfileLookupServerId => PrimaryPane.ProfileLookupServerId;

    public string Title
    {
        get => PrimaryPane.Title;
        set
        {
            PrimaryPane.Title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(HeaderToolTip));
        }
    }

    public string DisplayTitle
    {
        get
        {
            // A user-supplied custom title wins verbatim, with no RDP mode suffix.
            if (!string.IsNullOrWhiteSpace(CustomTitle))
            {
                return CustomTitle;
            }

            return string.IsNullOrWhiteSpace(RdpModeOverrideSuffix)
                ? Title
                : $"{Title} {RdpModeOverrideSuffix}";
        }
    }

    public string ConnectionType
    {
        get => PrimaryPane.ConnectionType;
        set { PrimaryPane.ConnectionType = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => PrimaryPane.Status;
        set { PrimaryPane.Status = value; OnPropertyChanged(); }
    }

    public string EnvironmentColor
    {
        get => PrimaryPane.EnvironmentColor;
        set { PrimaryPane.EnvironmentColor = value; OnPropertyChanged(); }
    }

    public string TunnelRoute
    {
        get => PrimaryPane.TunnelRoute;
        set
        {
            PrimaryPane.TunnelRoute = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HeaderToolTip));
        }
    }

    public SessionDiagnostic? FailureDetails
    {
        get => PrimaryPane.FailureDetails;
        set
        {
            PrimaryPane.FailureDetails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFailureDetails));
        }
    }

    public bool HasFailureDetails => PrimaryPane.HasFailureDetails;

    public object? HostControl
    {
        get => PrimaryPane.HostControl;
        set { PrimaryPane.HostControl = value; OnPropertyChanged(); }
    }

    public string HeaderToolTip =>
        string.IsNullOrWhiteSpace(TunnelRoute)
            ? DisplayTitle
            : $"{DisplayTitle} {TunnelRoute}";

    /// <summary>
    /// True when the tree has more than one pane (root is a <see cref="SplitContainerModel"/>).
    /// Setter for backward compatibility: setting to true wraps the current root into a
    /// split container with an empty secondary pane; setting to false collapses back to
    /// the primary pane only (caller must handle secondary cleanup before this).
    /// </summary>
    public bool IsSplit
    {
        get => RootContent is SplitContainerModel;
        set
        {
            if (value == IsSplit) return;

            if (value)
            {
                // Wrap current root (primary pane) into a split container with
                // a new empty secondary pane
                var primary = PrimaryPane;
                var secondary = new SessionPaneModel();
                RootContent = new SplitContainerModel
                {
                    First = primary,
                    Second = secondary,
                    Orientation = SplitOrientation.Vertical,
                    SplitRatio = 0.5
                };
            }
            else
            {
                // Collapse back to primary pane only
                var primary = PrimaryPane;
                RootContent = primary;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Orientation of the root-level split. Returns Vertical when not split.
    /// </summary>
    public SplitOrientation SplitOrientation
    {
        get => RootContent is SplitContainerModel c ? c.Orientation : SplitOrientation.Vertical;
        set
        {
            if (RootContent is SplitContainerModel c)
            {
                c.Orientation = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Splitter ratio of the root-level split. Returns 0.5 when not split.
    /// </summary>
    public double SplitRatio
    {
        get => RootContent is SplitContainerModel c ? c.SplitRatio : 0.5;
        set
        {
            if (RootContent is SplitContainerModel c)
            {
                c.SplitRatio = value;
                OnPropertyChanged();
            }
        }
    }

    // ── Secondary pane shim properties ────────────────────────────
    // These target the SECOND leaf (if any) for backward compatibility
    // with code that references the "secondary" pane directly.

    private SessionPaneModel? SecondaryPaneOrNull =>
        RootContent is SplitContainerModel c
            ? SplitTreeHelper.FirstLeaf(c.Second)
            : null;

    public object? SecondaryHostControl
    {
        get => SecondaryPaneOrNull?.HostControl;
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.HostControl = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryServerId
    {
        get => SecondaryPaneOrNull?.ServerId ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.ServerId = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryOriginalServerId
    {
        get => SecondaryPaneOrNull?.OriginalServerId ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.OriginalServerId = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryConnectionType
    {
        get => SecondaryPaneOrNull?.ConnectionType ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.ConnectionType = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryTitle
    {
        get => SecondaryPaneOrNull?.Title ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.Title = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryStatus
    {
        get => SecondaryPaneOrNull?.Status ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.Status = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryTunnelRoute
    {
        get => SecondaryPaneOrNull?.TunnelRoute ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.TunnelRoute = value; OnPropertyChanged(); }
        }
    }

    public string SecondaryEnvironmentColor
    {
        get => SecondaryPaneOrNull?.EnvironmentColor ?? "";
        set
        {
            var pane = SecondaryPaneOrNull;
            if (pane is not null) { pane.EnvironmentColor = value; OnPropertyChanged(); }
        }
    }

    /// <summary>
    /// Notifies all shim property bindings that depend on the tree structure.
    /// Called externally after in-place tree mutations (e.g., swap) that don't
    /// change the <see cref="RootContent"/> reference.
    /// </summary>
    public void NotifyShimPropertiesChanged() => NotifyTreeDependentProperties();

    /// <summary>
    /// Shared notification for all tree-dependent shim properties.
    /// Used by both <see cref="OnRootContentChanged"/> and <see cref="NotifyShimPropertiesChanged"/>.
    /// </summary>
    private void NotifyTreeDependentProperties()
    {
        OnPropertyChanged(nameof(PrimaryPane));
        OnPropertyChanged(nameof(ServerId));
        OnPropertyChanged(nameof(OriginalServerId));
        OnPropertyChanged(nameof(ProfileLookupServerId));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(ConnectionType));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HostControl));
        OnPropertyChanged(nameof(TunnelRoute));
        OnPropertyChanged(nameof(HeaderToolTip));
        OnPropertyChanged(nameof(EnvironmentColor));
        OnPropertyChanged(nameof(FailureDetails));
        OnPropertyChanged(nameof(HasFailureDetails));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(SplitOrientation));
        OnPropertyChanged(nameof(SplitRatio));
    }

    /// <summary>
    /// Fallback pane for when the tree is empty. Per-instance to avoid shared state.
    /// </summary>
    private readonly SessionPaneModel _emptyPane = new();
}
