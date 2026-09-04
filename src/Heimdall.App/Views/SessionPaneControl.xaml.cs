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

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views;

/// <summary>
/// Leaf pane view in the recursive split tree. Displays a single session's host
/// control with loading and disconnected overlays. Overlay buttons route to
/// MainViewModel via visual tree traversal.
/// </summary>
public partial class SessionPaneControl : UserControl
{
    private SessionPaneModel? _model;

    public SessionPaneControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ReconnectButton.Click += OnReconnectClick;
        ClosePaneButton.Click += OnClosePaneClick;
        EditProfileButton.Click += OnEditProfileClick;
        CopyErrorButton.Click += OnCopyErrorClick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.PropertyChanged += OnModelPropertyChanged;
        }
        ApplyLocalization();
        SyncContent();
        UpdateOverlays();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        // Release the hosted UIElement so it can be reparented in a new
        // SessionPaneControl (e.g. after a swap). Without this, the old
        // control retains the WebView2/ActiveX child, blocking reparenting.
        if (!IsApplicationShuttingDown())
        {
            HostPresenter.Content = null;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // ContentPresenter/DataTemplate swaps can reuse the existing
        // SessionPaneControl instance and only change its DataContext.
        // Release any previously hosted UIElement before binding to the
        // next pane model so WebView2/ActiveX controls do not remain
        // parented to the old presenter.
        if (!IsApplicationShuttingDown())
        {
            HostPresenter.Content = null;
        }

        // Unsubscribe from previous model
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = e.NewValue as SessionPaneModel;

        // Only subscribe if we are currently loaded
        if (_model is not null && IsLoaded)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
        }

        SyncContent();
        UpdateOverlays();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionPaneModel.HostControl):
                SyncContent();
                UpdateOverlays();
                break;
            case nameof(SessionPaneModel.Status):
            case nameof(SessionPaneModel.FailureDetails):
                UpdateOverlays();
                break;
        }
    }

    private void SyncContent()
    {
        if (IsLoaded)
        {
            HostPresenter.Content = _model?.HostControl;
        }
    }

    private void UpdateOverlays()
    {
        if (!IsLoaded) return;

        if (_model is null)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DisconnectedOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var hasContent = _model.HostControl is not null;
        var hasFailureDetails = _model.HasFailureDetails;
        var hostHandlesFailureOverlay = _model.HostControl is EmbeddedRdpView or EmbeddedSshView;
        var status = _model.Status ?? "";

        // Loading: pane exists but host control not yet assigned (connection in progress)
        LoadingOverlay.Visibility = !hasContent && !hasFailureDetails
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Disconnected: show either a failed connection without hosted content,
        // or an established pane whose host later reports disconnection/error.
        DisconnectedOverlay.Visibility = !hostHandlesFailureOverlay
            && (hasFailureDetails
                || (hasContent
                    && (string.Equals(status, nameof(ConnectionState.Disconnected), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, nameof(ConnectionState.Error), StringComparison.OrdinalIgnoreCase))
                    ))
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Recomputed with the overlay rather than once at load: the owning session is found by
        // walking the split tree, which is not populated yet when this control is first created.
        UpdateEditProfileAvailability();
    }

    // ── Overlay button handlers (route to MainViewModel) ─────────

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (!TryFindOwningSession(out var vm, out var session) || _model is null)
        {
            return;
        }

        if (!session.IsSplit)
        {
            vm.Session.ReconnectSession(session);
            return;
        }

        _ = vm.ReconnectPaneAsync(session, _model.PaneId);
    }

    private void OnClosePaneClick(object sender, RoutedEventArgs e)
    {
        if (!TryFindOwningSession(out var vm, out var session) || _model is null)
        {
            return;
        }

        if (!session.IsSplit)
        {
            _ = vm.Connection.CloseSessionAsync(session, DisconnectReason.UserAction, confirm: false);
            return;
        }

        // A user gesture, so Interactive: the pane's guard may ask before anything is torn down.
        _ = vm.ClosePaneAsync(session, _model.PaneId);
    }

    /// <summary>
    /// Opens the profile whose settings just failed to connect.
    /// </summary>
    /// <remarks>
    /// This route did not exist here. Reconnect repeats whatever failed, so on a wrong username,
    /// a wrong port or a missing key the pane offered only "do it again" and "give up" - and a
    /// newcomer presses Reconnect two or three times before concluding the app cannot connect.
    /// The way to the field that is actually wrong existed already, wired to the RDP overlay
    /// alone: the one failure surface a first-time user is least likely to meet first, since SSH
    /// and SFTP are where they start.
    /// </remarks>
    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (!TryFindOwningSession(out MainViewModel? vm, out SessionTabViewModel? session))
        {
            return;
        }

        string serverId = session.ProfileLookupServerId;
        if (string.IsNullOrEmpty(serverId))
        {
            return;
        }

        _ = vm.ServerList.EditServerByIdAsync(serverId, CancellationToken.None);
    }

    /// <summary>
    /// Puts the failure on the clipboard, so it can be pasted into a ticket or a message.
    /// </summary>
    /// <remarks>
    /// Built from the bound diagnostic rather than by scraping rendered TextBlocks the way the
    /// RDP overlay does: reading the model gives the same text on every protocol, and does not
    /// silently change meaning when the layout does.
    /// </remarks>
    private void OnCopyErrorClick(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        List<string> lines = [];

        if (!string.IsNullOrWhiteSpace(_model.Title))
        {
            lines.Add(_model.Title);
        }

        if (_model.FailureDetails is { } failure)
        {
            lines.Add($"{L("SessionDiagnosticLabelStage")} {failure.Stage}");

            if (failure.Code is { } code)
            {
                lines.Add($"{L("SessionDiagnosticLabelCode")} {code}");
            }

            if (!string.IsNullOrWhiteSpace(failure.Detail))
            {
                lines.Add($"{L("SessionDiagnosticLabelDetail")} {failure.Detail}");
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another process owns the clipboard. Not worth a dialog over a copy button.
            Heimdall.Core.Logging.FileLogger.Warn($"[SessionPane] copy error failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Offers the profile edit only when there is a stored profile behind this pane.
    /// </summary>
    /// <remarks>
    /// An ad-hoc session has no saved profile to open, so the button would do nothing - which is
    /// the defect this change removes, one button over.
    /// </remarks>
    private void UpdateEditProfileAvailability()
    {
        bool editable = TryFindOwningSession(out _, out SessionTabViewModel? session)
            && !string.IsNullOrEmpty(session.ProfileLookupServerId);

        EditProfileButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLocalization()
    {
        ReconnectButton.ToolTip = L("TooltipReconnectPane");
        ClosePaneButton.ToolTip = L("TooltipClosePane");
        EditProfileButton.ToolTip = L("TooltipEditProfileOverlay");
        CopyErrorButton.ToolTip = L("TooltipCopyErrorOverlay");
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton, L("A11yReconnectPane"));
        System.Windows.Automation.AutomationProperties.SetName(ClosePaneButton, L("A11yClosePane"));
        System.Windows.Automation.AutomationProperties.SetName(EditProfileButton, L("A11yEditProfileOverlay"));
        System.Windows.Automation.AutomationProperties.SetName(CopyErrorButton, L("A11yCopyErrorOverlay"));
    }

    private string L(string key)
    {
        var vm = FindMainViewModel();
        return vm?.GetLocalizer()[key] ?? key;
    }

    private bool TryFindOwningSession(
        [NotNullWhen(true)] out MainViewModel? vm,
        [NotNullWhen(true)] out SessionTabViewModel? session)
    {
        vm = FindMainViewModel();
        session = null;
        if (vm is null || _model is null)
        {
            return false;
        }

        // Find which session owns this pane.
        foreach (var candidate in vm.Connection.ActiveSessions)
        {
            if (SplitTreeHelper.FindPane(candidate.RootContent, _model.PaneId) is not null)
            {
                session = candidate;
                return true;
            }
        }

        return false;
    }

    private static MainViewModel? FindMainViewModel() => MainViewModelLocator.FindCurrent();

    private static bool IsApplicationShuttingDown()
    {
        Application? current = Application.Current;
        if (current is null)
        {
            return true;
        }

        return current is Heimdall.App.App app && app.IsShuttingDown;
    }
}
