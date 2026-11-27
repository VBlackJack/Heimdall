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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;
using Heimdall.Ssh;

namespace Heimdall.App.Views;

public partial class EmbeddedSshView
{
    /// <summary>Raised when the user clicks the shield button to request an elevated shell.</summary>
    public event Action? ElevateRequested;

    /// <summary>Resolves a locale key, falling back to the key name if no localizer is set.</summary>
    private string L(string key) => _localizer?[key] ?? key;

    private string FormatHostKeyMismatchMidSession(SshSessionSecurityEvent evt)
    {
        var template = L("SftpHostKeyMismatchMidSession");
        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            template,
            evt.Host,
            evt.Port,
            evt.PresentedFingerprint ?? "?",
            evt.StoredFingerprint ?? "?");
    }

    private void LocalizeButtons()
    {
        if (_localizer is null) return;

        if (!_localeChangeSubscribed)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
            _localeChangeSubscribed = true;
        }

        DisconnectButton.Content = L("BtnDisconnectSession");
        ReconnectButton.Content = L("BtnReconnectSession");
        FallbackTitleText.Text = L("EmbeddedSshFallbackTitle");
        FallbackMessageText.Text = L("EmbeddedSshFallbackMessage");
        OverlayReconnectButton.Content = L("BtnReconnectSession");
        OverlayCloseButton.Content = L("BtnCloseOverlay");
        AutoReconnectCancelButton.Content = L("BtnCancelAutoReconnect");
        if (ReconnectOverlay.Visibility != Visibility.Visible)
        {
            ReconnectMessageText.Text = L("SshDisconnectedMessage");
            Behaviors.LiveRegionBehavior.Announce(ReconnectMessageText);
        }

        OverlayViewOutputButton.Content = L("BtnViewOutputOverlay");
        ConnectingCancelButton.Content = L("BtnCancelConnect");
        AdminBadgeText.Text = L("AdminBadgeLabel");
        BroadcastBadgeText.Text = L("BroadcastBadgeLabel");

        // Tooltips
        DisconnectButton.ToolTip = L("TooltipDisconnectSession");
        ReconnectButton.ToolTip = L("TooltipReconnectSession");
        SplitButton.ToolTip = L("TooltipSplitSession");
        HealthToggleButton.ToolTip = L("TooltipToggleHealthMonitoring");
        UpdateHighlightsToggleVisual();
        UpdateSessionCryptoInfo();
        ElevateButton.ToolTip = L("TooltipElevateToRoot");

        // Accessibility: automation names for toolbar buttons
        System.Windows.Automation.AutomationProperties.SetName(DisconnectButton, L("A11yDisconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton, L("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(SplitButton, L("A11ySplitSession"));
        System.Windows.Automation.AutomationProperties.SetName(HealthToggleButton, L("A11yToggleHealthMonitoring"));
        System.Windows.Automation.AutomationProperties.SetName(HighlightsToggleButton, L("A11yToggleTerminalHighlights"));
        System.Windows.Automation.AutomationProperties.SetName(SessionInfoButton, L("A11ySessionCryptoInfo"));
        System.Windows.Automation.AutomationProperties.SetName(ElevateButton, L("A11yElevateToRoot"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayReconnectButton, L("A11yReconnectSession"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayCloseButton, L("A11yCloseOverlay"));
        System.Windows.Automation.AutomationProperties.SetName(AutoReconnectCancelButton, L("A11yCancelAutoReconnect"));
        System.Windows.Automation.AutomationProperties.SetName(OverlayViewOutputButton, L("A11yViewOutputOverlay"));
        System.Windows.Automation.AutomationProperties.SetName(ConnectingCancelButton, L("A11yCancelConnect"));
        ConnectingCancelButton.ToolTip = L("TooltipCancelConnect");

        // The status text is the live region: a fixed name would be announced instead of its
        // content ("Connected", "Error"), so the label travels as help text.
        System.Windows.Automation.AutomationProperties.SetHelpText(StatusTextBlock, L("A11yConnectionStatus"));
    }

    private void OnLocaleChanged(string locale)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => OnLocaleChanged(locale));
            return;
        }

        LocalizeButtons();

        // The badge states its numbers in words: shown, it follows the language.
        if (_pipeSizeBadgeShown && _pipeSizeNotice is { } shownNotice)
        {
            RenderPipeSizeBadge(shownNotice);
        }

        PostTerminalMessage(MsgSetLabels + TerminalHtmlLocalizer.BuildLabelsJson(key => _localizer?[key]));

        if (_healthPanelVisible)
        {
            LocalizeHealthLabels();
        }

        if (AutoReconnectOverlay.Visibility == Visibility.Visible)
        {
            int maxAttempts = ResolveMaxAutoReconnectAttempts();
            AutoReconnectMessageText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("SshAutoReconnectMessage"),
                _autoReconnectAttempt,
                maxAttempts);
            UpdateAutoReconnectCountdownText();
        }
    }

    /// <summary>Show/hide the ADMIN badge and hide the elevate button.</summary>
    public void SetElevatedIndicator(bool elevated)
    {
        AdminBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.Visibility = Visibility.Collapsed;
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        ElevateRequested?.Invoke();
    }

    /// <summary>
    /// Tells the user, with a warning badge in the header controls, that a pipe-mode session cannot
    /// follow the terminal's new width, and takes the badge down when the widths agree again.
    /// </summary>
    /// <remarks>
    /// The badge is short and keyboard reachable; the full sentence is its tooltip and its
    /// automation name. It is announced once on raise and once per distinct width on refresh. It
    /// never touches the status line, which keeps showing the session status. A finished session
    /// takes the badge down with its own status (see <see cref="UpdateStatus"/>).
    /// </remarks>
    private void ObservePipeSessionSize(TerminalSize current)
    {
        if (_pipeSizeNotice is not { } notice || _disposed)
        {
            return;
        }

        int? shownColumns = _pipeSizeBadgeShown ? _pipeNoticeColumns : null;
        bool running = _terminalSession is Heimdall.Terminal.PipeModeSession { IsRunning: true };

        switch (notice.Observe(current, shownColumns, plainStatusShown: true, sessionRunning: running))
        {
            case SizeNoticeChange.Raise:
            case SizeNoticeChange.Refresh:
                _pipeNoticeColumns = current.Columns;
                _pipeSizeBadgeShown = true;
                RenderPipeSizeBadge(notice);
                PipeSizeBadge.Visibility = Visibility.Visible;
                Behaviors.LiveRegionBehavior.Announce(PipeSizeBadgeText);
                break;
            case SizeNoticeChange.Clear:
                HidePipeSizeBadge();
                break;
        }
    }

    private void HidePipeSizeBadge()
    {
        _pipeSizeBadgeShown = false;
        PipeSizeBadge.Visibility = Visibility.Collapsed;
    }

    private void RenderPipeSizeBadge(PipeSessionSizeNotice notice)
    {
        string sentence = _localizer is null
            ? "SshPipeSessionSizeMismatchNotice"
            : _localizer.Format(
                "SshPipeSessionSizeMismatchNotice",
                _pipeNoticeColumns,
                notice.LaunchedAt.Columns);
        PipeSizeBadgeText.Text = _localizer is null
            ? "SshPipeSessionSizeMismatchBadge"
            : _localizer.Format("SshPipeSessionSizeMismatchBadge", notice.LaunchedAt.Columns);
        PipeSizeBadge.ToolTip = sentence;

        // A border has no automation peer: the text element that carries the live region is the one
        // that is announced, so it carries the full sentence while showing the short label.
        System.Windows.Automation.AutomationProperties.SetName(PipeSizeBadgeText, sentence);
    }

    private void AcquireSleepPrevention()
    {
        if (!_sleepPreventionActive)
        {
            _sleepPreventionActive = true;
            SleepPrevention.SessionStarted();
        }
    }

    private void ReleaseSleepPrevention()
    {
        if (_sleepPreventionActive)
        {
            _sleepPreventionActive = false;
            SleepPrevention.SessionEnded();
        }
    }

    private bool IsWinRmSessionTab()
    {
        return string.Equals(_sessionTab?.ConnectionType, "WINRM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The status <see cref="UpdateStatus"/> last wrote, on the pane it writes to.</summary>
    private string? CurrentPaneStatus =>
        _ownerPane is not null && !ReferenceEquals(_sessionTab?.PrimaryPane, _ownerPane)
            ? _ownerPane.Status
            : _sessionTab?.Status;

    private void UpdateStatus(
        string status,
        string? displayTextOverride = null,
        bool forceErrorBrush = false)
    {
        // A secondary pane's status is its own. Written to the tab, it landed on the primary
        // pane, and the tab header read "Disconnected" while the primary was fine.
        if (_ownerPane is not null && !ReferenceEquals(_sessionTab?.PrimaryPane, _ownerPane))
        {
            _ownerPane.Status = status;
        }
        else if (_sessionTab is not null)
        {
            _sessionTab.Status = status;
        }

        // Display localized status text while keeping internal state identifier
        var displayText = displayTextOverride ?? status switch
        {
            SessionStatusTokens.Connected => L("SshSessionStatusConnected"),
            SessionStatusTokens.Disconnected => L("SshSessionStatusDisconnected"),
            SessionStatusTokens.Error => L("SshSessionStatusError"),
            SessionStatusTokens.Connecting => L("SshSessionStatusConnecting"),
            SessionStatusTokens.RemoteSessionHandedOff => L("SshSessionStatusRemoteSessionHandedOff"),
            _ => status
        };
        StatusTextBlock.Text = displayText;
        Behaviors.LiveRegionBehavior.Announce(StatusTextBlock);

        var isDisconnected = string.Equals(status, SessionStatusTokens.Disconnected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SessionStatusTokens.Error, StringComparison.OrdinalIgnoreCase);

        // A finished session has nothing left to warn about.
        if (isDisconnected)
        {
            HidePipeSizeBadge();
            SessionInfoButton.Visibility = Visibility.Collapsed;
        }

        DisconnectButton.IsEnabled = !_disposed && !isDisconnected;
        DisconnectButton.Visibility = isDisconnected ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.Visibility = isDisconnected ? Visibility.Visible : Visibility.Collapsed;

        StatusTextBlock.SetResourceReference(
            TextBlock.ForegroundProperty,
            forceErrorBrush || string.Equals(status, SessionStatusTokens.Error, StringComparison.OrdinalIgnoreCase)
                ? "ErrorTextBrush"
                : "TextPrimaryBrush");
    }

    private void UpdateHighlightsToggleVisual()
    {
        if (_disposed)
        {
            return;
        }

        HighlightsToggleButton.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            _highlightsEnabled ? "AccentTextBrush" : "TextSecondaryBrush");
        HighlightsToggleButton.ToolTip = _highlightsEnabled
            ? L("TooltipToggleTerminalHighlightsOn")
            : L("TooltipToggleTerminalHighlightsOff");
    }

    private void UpdateSessionCryptoInfo()
    {
        if (_disposed)
        {
            return;
        }

        var diag = _session?.GetDiagnostics();
        if (diag is null)
        {
            SessionInfoButton.Visibility = Visibility.Collapsed;
            SessionInfoButton.ToolTip = L("TooltipSessionCryptoInfo");
            return;
        }

        SessionInfoButton.Visibility = Visibility.Visible;
        SessionInfoButton.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            diag.HasWeakCrypto ? "WarningTextBrush" : "TextSecondaryBrush");
        SessionInfoButton.ToolTip = L("TooltipSessionCryptoInfo")
            + Environment.NewLine
            + FormatSessionCryptoInfoSummary(diag);
    }

    private string FormatSessionCryptoInfoSummary(SshSessionDiagnostics diag)
    {
        var cipherDisplay = FormatDirectionalAlgorithm(diag.ClientToServerEncryption, diag.ServerToClientEncryption);
        var macDisplay = FormatDirectionalAlgorithm(diag.ClientToServerMac, diag.ServerToClientMac);
        var compressionDisplay = FormatDirectionalAlgorithm(diag.ClientToServerCompression, diag.ServerToClientCompression);

        var summary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L("SessionCryptoInfoSummary"),
            diag.ServerVersion,
            diag.KeyExchangeAlgorithm,
            diag.HostKeyAlgorithm,
            cipherDisplay,
            macDisplay,
            compressionDisplay,
            diag.ActiveForwardedPorts);

        return diag.HasWeakCrypto
            ? summary + Environment.NewLine + L("SessionCryptoInfoWeakWarning")
            : summary;
    }

    private static string FormatDirectionalAlgorithm(string clientToServer, string serverToClient)
        => string.Equals(clientToServer, serverToClient, StringComparison.OrdinalIgnoreCase)
            ? clientToServer
            : $"{clientToServer} / {serverToClient}";

    private void OnSessionInfoClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var diag = _session?.GetDiagnostics();
        if (diag is null)
        {
            return;
        }

        UpdateSessionCryptoInfo();
        try
        {
            Clipboard.SetText(FormatSessionCryptoInfoSummary(diag));
            Tools.CopyFeedbackHelper.ShowCopyFeedback(SessionInfoButton);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked by another process.
        }
    }

    private Brush GetBrush(string resourceKey, Brush fallback)
    {
        return TryFindResource(resourceKey) as Brush ?? fallback;
    }
}
