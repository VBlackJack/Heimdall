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
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Themed dialog for opening an ad-hoc SSH local-forward tunnel.
/// </summary>
public partial class NewTunnelDialog : Window
{
    private NewTunnelDialogViewModel? _viewModel;

    public NewTunnelDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Puts the keyboard on the first field, or on Cancel when there is no gateway to fill the
    /// form with and the whole form is disabled.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
        => (_viewModel is { HasGateways: false } ? (UIElement)CancelButton : GatewayComboBox).Focus();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = e.NewValue as NewTunnelDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            DialogResult = _viewModel.Decision == true;
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }
}
