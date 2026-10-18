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
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.Behaviors;
using WpfControl = System.Windows.Controls.Control;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Heimdall.App.Services;

/// <summary>
/// Small controller that keeps async tool shell states consistent:
/// loading, inline error, empty state, results visibility, and busy button state.
/// </summary>
/// <remarks>
/// Disabling the controls for a run takes keyboard focus away from the one that held it, and WPF
/// leaves it on the scroll viewer around them: a keyboard or screen reader user was left on an
/// unnamed pane after every lookup. The control that held focus gets it back when the run ends,
/// unless the user has moved on in the meantime.
/// </remarks>
internal sealed class ToolAsyncStateController
{
    private readonly Action<bool>? _setBusy;
    private readonly WpfProgressBar? _loadingBar;
    private readonly WpfTextBlock? _errorText;
    private readonly UIElement? _emptyState;
    private readonly UIElement? _results;
    private readonly WpfTextBlock? _statusText;
    private readonly WpfControl[] _controlsToDisable;
    private WpfControl? _focusedBeforeRun;

    public ToolAsyncStateController(
        Action<bool>? setBusy,
        WpfProgressBar? loadingBar,
        WpfTextBlock? errorText,
        UIElement? emptyState,
        UIElement? results,
        WpfTextBlock? statusText,
        params WpfControl[] controlsToDisable)
    {
        _setBusy = setBusy;
        _loadingBar = loadingBar;
        _errorText = errorText;
        _emptyState = emptyState;
        _results = results;
        _statusText = statusText;
        _controlsToDisable = controlsToDisable ?? [];
    }

    public void Reset(bool showEmptyState = true)
    {
        if (_errorText is not null)
        {
            _errorText.Text = string.Empty;
            _errorText.Visibility = Visibility.Collapsed;
        }

        if (_loadingBar is not null)
        {
            _loadingBar.Visibility = Visibility.Collapsed;
        }

        if (_results is not null)
        {
            _results.Visibility = Visibility.Collapsed;
        }

        if (_emptyState is not null)
        {
            _emptyState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_statusText is not null)
        {
            _statusText.Text = string.Empty;
        }
    }

    public void Begin(string? status = null)
    {
        _setBusy?.Invoke(true);
        _focusedBeforeRun = Array.Find(_controlsToDisable, control => control.IsKeyboardFocusWithin);
        SetControlsEnabled(false);

        if (_errorText is not null)
        {
            _errorText.Text = string.Empty;
            _errorText.Visibility = Visibility.Collapsed;
        }

        if (_loadingBar is not null)
        {
            _loadingBar.Visibility = Visibility.Visible;
        }

        if (_statusText is not null && status is not null)
        {
            _statusText.Text = status;
        }
    }

    public void ShowError(
        string message,
        string? status = null,
        bool showEmptyState = true,
        bool keepResultsVisible = false)
    {
        if (_results is not null)
        {
            _results.Visibility = keepResultsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_emptyState is not null)
        {
            _emptyState.Visibility = keepResultsVisible
                ? Visibility.Collapsed
                : (showEmptyState ? Visibility.Visible : Visibility.Collapsed);
        }

        WriteError(message);

        if (_statusText is not null && status is not null)
        {
            _statusText.Text = status;
        }
    }

    public void ShowResults(string? status = null)
    {
        if (_emptyState is not null)
        {
            _emptyState.Visibility = Visibility.Collapsed;
        }

        if (_results is not null)
        {
            _results.Visibility = Visibility.Visible;
        }

        if (_statusText is not null && status is not null)
        {
            _statusText.Text = status;
        }
    }

    public void End()
    {
        _setBusy?.Invoke(false);
        SetControlsEnabled(true);
        RestoreFocusAfterRun();

        if (_loadingBar is not null)
        {
            _loadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private void WriteError(string message)
    {
        if (_errorText is null)
        {
            return;
        }

        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        LiveRegionBehavior.Announce(_errorText);
    }

    private void RestoreFocusAfterRun()
    {
        WpfControl? previous = _focusedBeforeRun;
        _focusedBeforeRun = null;
        if (previous is null || !FocusFellBackFrom(Keyboard.FocusedElement as DependencyObject, previous))
        {
            return;
        }

        previous.Focus();
    }

    /// <summary>
    /// True when focus is nowhere, or sits on a container of <paramref name="previous"/>: where WPF
    /// puts it when the focused control is disabled. Focus anywhere else is the user's own move.
    /// </summary>
    internal static bool FocusFellBackFrom(DependencyObject? focused, DependencyObject previous)
    {
        if (focused is null)
        {
            return true;
        }

        return !ReferenceEquals(focused, previous)
            && focused is Visual container
            && previous is Visual control
            && control.IsDescendantOf(container);
    }

    private void SetControlsEnabled(bool enabled)
    {
        foreach (var control in _controlsToDisable)
        {
            control.IsEnabled = enabled;
        }
    }
}
