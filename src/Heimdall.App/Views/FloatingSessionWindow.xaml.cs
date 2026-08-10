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
using System.Windows.Media;
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
    private bool _reattached;

    /// <summary>
    /// Gets the hosted session tab view model.
    /// </summary>
    public SessionTabViewModel Session => _session;

    public FloatingSessionWindow(
        SessionTabViewModel session,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localizer);

        _session = session;
        _localizer = localizer;

        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        ApplySessionInfo();

        // Re-apply localized strings when language changes at runtime
        _localizer.LocaleChanged += OnLocaleChanged;
    }

    private void ApplySessionInfo()
    {
        Title = string.Format(_localizer["SessionDetachTitle"], _session.Title);
        SessionTitle.Text = _session.Title;
        StatusText.Text = _session.Status;
        TunnelRouteText.Text = _session.TunnelRoute;
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

        var mainWindow = Application.Current.MainWindow;
        if (mainWindow?.DataContext is not MainViewModel vm) return;

        _reattached = true;

        // Detach the host control from this window first (UIElement single-parent rule)
        SessionHost.Content = null;

        RestoreSession(vm);

        Heimdall.Core.Logging.FileLogger.Info(
            string.Format(_localizer["LogSessionReattached"], _session.Title));

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_reattached)
        {
            // The session was not reattached; dispose it properly via ConnectionViewModel
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow?.DataContext is MainViewModel vm)
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

        _localizer.LocaleChanged -= OnLocaleChanged;
        base.OnClosed(e);
    }

    private void RestoreSession(MainViewModel vm)
    {
        if (!vm.Connection.ActiveSessions.Contains(_session))
        {
            vm.Connection.ActiveSessions.Add(_session);
        }

        vm.Connection.ActiveSession = _session;
        vm.Connection.HasActiveSessions = vm.Connection.ActiveSessions.Count > 0;
    }

    private void OnLocaleChanged(string _)
    {
        Dispatcher.Invoke(() =>
        {
            Title = string.Format(_localizer["SessionDetachTitle"], _session.Title);
            ReattachButton.Content = _localizer["SessionCtxReattach"];
            System.Windows.Automation.AutomationProperties.SetName(
                ReattachButton, _localizer["SessionCtxReattach"]);
        });
    }
}
