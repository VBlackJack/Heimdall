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
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Heimdall.App.Views.Tools;

public partial class CertificateGeneratorView : UserControl, IToolView
{
    private readonly CertificateGeneratorViewModel _vm;
    private readonly TimedSecretClipboard _secretClipboard = TimedSecretClipboard.CreateDefault();
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private bool _disposed;

    public CertificateGeneratorView()
    {
        InitializeComponent();
        _vm = new CertificateGeneratorViewModel(
            (Application.Current as App)?.Services?.GetService<ICertificateGeneratorService>());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.CopyTextRequested += OnCopyTextRequested;
        _vm.SaveFileRequested += OnSaveFileRequested;
        _vm.PfxPasswordRequested += OnPfxPasswordRequested;
        _vm.ValidationFocusRequested += OnValidationFocusRequested;
        CnInput.KeyDown += OnCnInputKeyDown;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        _vm.Initialize(localizer);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            CnInput.Focus();
            CnInput.SelectAll();
        });
    }

    public bool CanClose() => !_vm.IsGenerating;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _setBusy?.Invoke(false);
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.CopyTextRequested -= OnCopyTextRequested;
        _secretClipboard.Dispose();
        _vm.SaveFileRequested -= OnSaveFileRequested;
        _vm.PfxPasswordRequested -= OnPfxPasswordRequested;
        _vm.ValidationFocusRequested -= OnValidationFocusRequested;
        CnInput.KeyDown -= OnCnInputKeyDown;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CertificateGeneratorViewModel.IsGenerating):
                _setBusy?.Invoke(_vm.IsGenerating);
                break;
            case nameof(CertificateGeneratorViewModel.ValidationMessage):
                if (!string.IsNullOrWhiteSpace(_vm.ValidationMessage))
                {
                    ValidationText.BringIntoView();
                }
                break;
        }
    }

    private void OnCopyTextRequested(object? sender, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_vm.IsPrivateKeyMaterial(text))
        {
            // A private key stays out of the clipboard history and the cloud clipboard and is taken back.
            _secretClipboard.TryCopy(text);
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Clipboard locked by another process.
        }
    }

    private void OnSaveFileRequested(object? sender, SaveFileRequest request)
    {
        var dialog = new SaveFileDialog
        {
            FileName = request.DefaultName,
            Filter = request.Filter,
            DefaultExt = Path.GetExtension(request.DefaultName),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            GeneratedFileWriter.Write(dialog.FileName, request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The previous file is left as it was; the tool's export error status tells the user.
            _vm.ReportSaveFailure(ex.Message);
        }
    }

    private void OnPfxPasswordRequested(object? sender, PfxPasswordRequest request) =>
        request.ResultCallback(PromptPfxPassword(request.Title, request.Prompt));

    private void OnValidationFocusRequested(object? sender, string fieldName)
    {
        switch (fieldName)
        {
            case "Cn":
                CnInput.Focus();
                CnInput.SelectAll();
                break;
            case "ValidityDays":
                ValidityInput.Focus();
                ValidityInput.SelectAll();
                break;
        }
    }

    private void OnCnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.GenerateCommand.CanExecute(null))
        {
            _vm.GenerateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private string? PromptPfxPassword(string title, string prompt)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock
        {
            Text = prompt,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var passwordBox = new PasswordBox { Padding = new Thickness(4, 2, 4, 2) };
        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        string? result = null;
        var btnOk = new Button
        {
            Content = L("ToolCertGenBtnOk"),
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        btnOk.Click += (_, _) =>
        {
            result = passwordBox.Password;
            dialog.DialogResult = true;
        };

        var btnCancel = new Button
        {
            Content = L("ToolCertGenBtnCancel"),
            Padding = new Thickness(16, 4, 16, 4),
            IsCancel = true,
        };

        buttonPanel.Children.Add(btnOk);
        buttonPanel.Children.Add(btnCancel);
        panel.Children.Add(label);
        panel.Children.Add(passwordBox);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

        return dialog.ShowDialog() == true ? result : null;
    }

    private string L(string key) => _localizer?[key] ?? key;
}
