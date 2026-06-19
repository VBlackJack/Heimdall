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
using Heimdall.Core.Localization;

namespace Heimdall.App.Views.Dialogs;

public partial class FtpsCertificatePromptDialog : Window
{
    private readonly LocalizationManager? _localizer;
    private FtpsCertificatePromptDialogViewModel? _viewModel;

    public FtpsCertificatePromptDialog()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public FtpsCertificatePromptDialog(LocalizationManager localizer)
        : this()
    {
        _localizer = localizer;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAutomationNames();
        if (_viewModel?.TrustOnceIsDefault == true)
        {
            TrustOnceButton.Focus();
        }
        else
        {
            AcceptButton.Focus();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = e.NewValue as FtpsCertificatePromptDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }

        ApplyAutomationNames();
    }

    private void OnCloseRequested(bool confirmed)
    {
        DialogResult = confirmed;
    }

    private void ApplyAutomationNames()
    {
        var localizer = _localizer;
        var vm = DataContext as FtpsCertificatePromptDialogViewModel;
        if (vm is null)
        {
            return;
        }

        AutomationProperties.SetName(RejectButton, vm.RejectButtonText);
        AutomationProperties.SetName(TrustOnceButton, vm.TrustOnceButtonText);
        AutomationProperties.SetName(AcceptButton, vm.AcceptButtonText);
        AutomationProperties.SetName(
            CopyFingerprintButton,
            localizer?["FtpsCertificateCopyFingerprintButton"] ?? "Copy");
        AutomationProperties.SetName(
            PresentedFingerprintBox,
            localizer?["FtpsCertificatePresentedFingerprintLabel"] ?? "Presented certificate fingerprint");
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
