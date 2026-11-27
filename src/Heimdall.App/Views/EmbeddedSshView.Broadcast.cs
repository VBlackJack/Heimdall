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

namespace Heimdall.App.Views;

public partial class EmbeddedSshView
{
    private bool _broadcastSelectionDiagnosticLogged;

    /// <summary>
    /// Invoked when the user clicks this pane's broadcast target toggle. The
    /// coordinator owns the canonical per-pane state
    /// (<c>SessionPaneModel.IsBroadcastTarget</c>); the view only requests the flip
    /// and then reflects the new value via <see cref="SetBroadcastTargetState"/>.
    /// </summary>
    public Action? BroadcastTargetToggleRequested;

    /// <summary>
    /// Shows or hides the broadcast mode indicator badge in the header strip.
    /// </summary>
    public void SetBroadcastIndicator(bool active)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastIndicator(active));
            return;
        }

        BroadcastBadge.Visibility = active
            ? Visibility.Visible
            : Visibility.Collapsed;

        BroadcastBorder.BorderThickness = active
            ? new Thickness(2)
            : new Thickness(0);
    }

    private void OnBroadcastTargetToggleClick(object sender, RoutedEventArgs e)
    {
        BroadcastTargetToggleRequested?.Invoke();
    }

    /// <summary>
    /// Shows or hides the per-pane broadcast target toggle. Visible only while
    /// broadcast mode is active and the scope is SelectedPanes; otherwise the pane
    /// chrome stays uncluttered.
    /// </summary>
    public void SetBroadcastSelectionMode(bool selectionActive)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastSelectionMode(selectionActive));
            return;
        }

        BroadcastTargetToggle.Visibility = selectionActive
            ? Visibility.Visible
            : Visibility.Collapsed;

        // One-shot diagnostic so a missing per-pane toggle can be confirmed from the
        // log (does selection mode actually drive the toggle to Visible?).
        if (!_broadcastSelectionDiagnosticLogged)
        {
            _broadcastSelectionDiagnosticLogged = true;
            Core.Logging.FileLogger.Info(
                $"[Broadcast] SetBroadcastSelectionMode: selectionActive={selectionActive}, " +
                $"toggleVisibility={BroadcastTargetToggle.Visibility}");
        }

        if (selectionActive)
        {
            // In selection mode the badge/border reflect target membership and are
            // driven by SetBroadcastTargetState; start hidden until told otherwise.
            BroadcastBadge.Visibility = Visibility.Collapsed;
            BroadcastBorder.BorderThickness = new Thickness(0);
        }
    }

    /// <summary>
    /// Reflects whether this pane is currently a broadcast target: checked toggle
    /// plus a visible badge/border highlight. Only meaningful in SelectedPanes mode.
    /// </summary>
    public void SetBroadcastTargetState(bool isTarget)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => SetBroadcastTargetState(isTarget));
            return;
        }

        BroadcastTargetToggle.IsChecked = isTarget;
        BroadcastBadge.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
        BroadcastBorder.BorderThickness = isTarget ? new Thickness(2) : new Thickness(0);
    }
}
