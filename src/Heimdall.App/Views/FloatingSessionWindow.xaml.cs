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
using System.Windows;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Views;

/// <summary>
/// Floating window that hosts a single detached session tab.
/// Supports reattaching the session back to the main window.
/// </summary>
public partial class FloatingSessionWindow : Window
{
    private readonly SessionTabViewModel _session;
    private readonly LocalizationManager _localizer;
    private readonly IPaneCloseArbiter _closeArbiter;
    private bool _reattached;

    /// <summary>Set once the guards have cleared this window, so the re-issued close goes through.</summary>
    private bool _closeGranted;

    /// <summary>
    /// Gets the hosted session tab view model.
    /// </summary>
    public SessionTabViewModel Session => _session;

    public FloatingSessionWindow(
        SessionTabViewModel session,
        LocalizationManager localizer,
        IPaneCloseArbiter closeArbiter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(closeArbiter);

        _session = session;
        _localizer = localizer;
        _closeArbiter = closeArbiter;

        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        ApplySessionInfo();

        // Re-apply localized strings when language changes at runtime
        _localizer.LocaleChanged += OnLocaleChanged;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void ApplySessionInfo()
    {
        ApplyWindowTitle();
        ReattachButton.Content = _localizer["SessionCtxReattach"];
        System.Windows.Automation.AutomationProperties.SetName(ReattachButton, _localizer["SessionCtxReattach"]);

        // Set connection type icon using vector geometry (mirrors converters)
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var geoConverter = new Converters.ConnectionTypeToGeometryConverter();
        var colorConverter = new Converters.ConnectionTypeToColorConverter();
        if (geoConverter.Convert(_session.ConnectionType, typeof(Geometry), null, culture) is Geometry geo)
            TypeIcon.Data = geo;
        if (colorConverter.Convert(_session.ConnectionType, typeof(Brush), null, culture) is Brush brush)
            TypeIcon.Fill = brush;

        // Attach the host control to this window's content presenter
        if (_session.HostControl is UIElement hostElement)
        {
            SessionHost.Content = hostElement;
        }
    }

    private void OnReattachClick(object sender, RoutedEventArgs e)
    {
        ReattachToMainWindow();
    }

    /// <summary>
    /// Transfers the session back to the main window's active sessions collection.
    /// </summary>
    public void ReattachToMainWindow()
    {
        if (_reattached) return;

        if (MainViewModelLocator.FindCurrent() is not MainViewModel vm) return;

        _reattached = true;

        // Detach the host control from this window first (UIElement single-parent rule)
        SessionHost.Content = null;

        RestoreSession(vm);

        Heimdall.Core.Logging.FileLogger.Info(
            string.Format(_localizer["LogSessionReattached"], _session.Title));

        Close();
    }

    /// <summary>
    /// Asks the hosted content's close guard before letting the window go.
    /// </summary>
    /// <remarks>
    /// The decision may need to ask the user, and no answer can be awaited from inside
    /// <c>OnClosing</c>, so the close is cancelled synchronously here and re-issued later if the
    /// guards clear it. Nothing is torn down on this path - <see cref="OnClosed"/> keeps doing that
    /// - which is why an unguarded window is waved straight through.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_closeGranted || _reattached || e.Cancel || _session.HostControl is not ICloseGuard)
        {
            return;
        }

        e.Cancel = true;
        _ = ResumeCloseAsync();
    }

    /// <summary>Runs the close protocol, then re-issues the close if it was granted.</summary>
    private async Task ResumeCloseAsync()
    {
        CloseRequest request = CloseRequest.Interactive(DisconnectReason.TabClose);
        object?[] hosts = [_session.HostControl];
        try
        {
            CloseDecision decision = _closeArbiter.Poll(request, hosts);

            if (decision.Verdict == CloseVerdict.Defer)
            {
                if (!await _closeArbiter.ResolveAsync(request, hosts))
                {
                    ReportBlocked(decision.ReasonKey);
                    return;
                }

                // The retry, exactly once. A second deferral is reported, never looped on.
                decision = _closeArbiter.Poll(request, hosts);
            }

            if (decision.Verdict != CloseVerdict.Allow)
            {
                ReportBlocked(decision.ReasonKey);
                return;
            }

            _closeGranted = true;

            // Never Close() on this stack. The confirmation returns an already-completed task, so
            // the await above can resume synchronously inside OnClosing, and closing a window from
            // its own OnClosing throws from Window.VerifyNotClosing - on every floating window, not
            // just guarded ones.
            _ = Dispatcher.BeginInvoke(Close);
        }
        finally
        {
            _closeArbiter.Release(request);
        }
    }

    private void ReportBlocked(string? reasonKey)
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.DialogService.ShowInfo(
                _localizer[CloseGuardLocaleKeys.BlockedTitle],
                _localizer.Format(reasonKey ?? CloseGuardLocaleKeys.BlockedGeneric, _session.Title));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_reattached)
        {
            // The session was not reattached; dispose it properly via ConnectionViewModel
            if (MainViewModelLocator.FindCurrent() is MainViewModel vm)
            {
                // Detach host control from this window before ConnectionViewModel disposes it
                SessionHost.Content = null;

                // Restore the session before CloseSession prompts so a declined close keeps it visible.
                RestoreSession(vm);
                vm.Connection.CloseSessionCommand.Execute(_session);
            }
            else
            {
                // Fallback: dispose host control directly if main window is unavailable
                SessionHost.Content = null;
                if (_session.HostControl is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (ObjectDisposedException) { /* Expected */ }
                }
            }
        }

        _session.PropertyChanged -= OnSessionPropertyChanged;
        _localizer.LocaleChanged -= OnLocaleChanged;
        base.OnClosed(e);
    }

    private void RestoreSession(MainViewModel vm)
    {
        // ReintroduceSession owns the present/absent decision and keeps a pinned session
        // inside the pinned group instead of appending it after every unpinned tab.
        vm.Connection.ReintroduceSession(_session);

        vm.Connection.ActiveSession = _session;
        vm.Connection.HasActiveSessions = vm.Connection.ActiveSessions.Count > 0;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && e.PropertyName != nameof(SessionTabViewModel.Title))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyWindowTitle();
            return;
        }

        Dispatcher.BeginInvoke(new Action(ApplyWindowTitle));
    }

    private void ApplyWindowTitle()
    {
        Title = string.Format(_localizer["SessionDetachTitle"], _session.Title);
    }

    private void OnLocaleChanged(string _)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyWindowTitle();
            ReattachButton.Content = _localizer["SessionCtxReattach"];
            System.Windows.Automation.AutomationProperties.SetName(
                ReattachButton, _localizer["SessionCtxReattach"]);
        });
    }
}
