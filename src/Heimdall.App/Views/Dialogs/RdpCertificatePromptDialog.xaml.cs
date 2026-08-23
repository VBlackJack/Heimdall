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
using System.Windows.Automation;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Asks whether to trust the certificate an RDP endpoint presented.
/// </summary>
/// <remarks>
/// Deliberately empty of decisions. The wording, the reassurance line and the three
/// answers all live in <see cref="RdpCertificatePromptDialogViewModel"/>, because a WPF
/// window constructed in a test seals application-level styles onto the shared dispatcher
/// and takes unrelated tests down with it - measured at 23 during BL-0089.
/// <para>
/// The refusal button is both the default and the cancel button. Enter, Escape and the
/// title-bar cross therefore all mean the same thing, and the one answer an accidental
/// keystroke can give is the one that creates no durable trust.
/// </para>
/// </remarks>
public partial class RdpCertificatePromptDialog : Window
{
    private RdpCertificatePromptDialogViewModel? _viewModel;

    /// <summary>Creates the dialog.</summary>
    public RdpCertificatePromptDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAutomationNames();
        RefuseButton.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = e.NewValue as RdpCertificatePromptDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }

        ApplyAutomationNames();
    }

    private void OnCloseRequested(bool confirmed) => DialogResult = confirmed;

    private void ApplyAutomationNames()
    {
        if (DataContext is not RdpCertificatePromptDialogViewModel vm)
        {
            return;
        }

        AutomationProperties.SetName(RefuseButton, vm.RefuseButtonText);
        AutomationProperties.SetName(TrustOnceButton, vm.TrustOnceButtonText);
        AutomationProperties.SetName(TrustButton, vm.TrustButtonText);
        AutomationProperties.SetName(ThumbprintBox, vm.ThumbprintLabel);
    }

    /// <inheritdoc />
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
