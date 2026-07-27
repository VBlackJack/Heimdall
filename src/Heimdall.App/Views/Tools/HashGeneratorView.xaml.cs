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
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Heimdall.App.Views.Tools;

public partial class HashGeneratorView : UserControl, IToolView
{
    private readonly HashGeneratorViewModel _vm;
    private LocalizationManager? _localizer;
    private Action<bool>? _setBusy;
    private DispatcherTimer? _debounceTimer;
    private bool _suppressInputTextChanged;
    private bool _disposed;

    public HashGeneratorView()
    {
        InitializeComponent();
        _vm = new HashGeneratorViewModel((Application.Current as App)?.Services?.GetService<IHashGeneratorService>());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.CopyTextRequested += OnCopyTextRequested;
        _vm.SaveFileRequested += OnSaveFileRequested;
        InitializeDebounceTimer();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _localizer = localizer;
        _setBusy = context?.SetBusyAction;
        if (_localizer is not null) { _localizer.LocaleChanged += OnLocaleChanged; }
        _vm.Initialize(localizer);
        ApplyLocalization();
        if (!string.IsNullOrWhiteSpace(context?.Argument)) { TxtInput.Text = context.Argument; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            TxtInput.Focus();
            TxtInput.SelectAll();
        });
    }

    public bool CanClose() => !_vm.IsFileHashing;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _setBusy?.Invoke(false);
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; _localizer = null; }
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.CopyTextRequested -= OnCopyTextRequested;
        _vm.SaveFileRequested -= OnSaveFileRequested;
        _debounceTimer?.Stop();
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void InitializeDebounceTimer()
    {
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _vm.UpdateInputText(TxtInput.Text);
        };
    }

    private void ApplyLocalization()
    {
        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        AutomationProperties.SetName(TxtInput, L("ToolHashInputLabel"));
        AutomationProperties.SetName(BtnBrowseFile, L("ToolHashBtnBrowseFile"));
        AutomationProperties.SetName(BtnClearFile, L("ToolHashBtnClearFile"));
        AutomationProperties.SetName(TxtVerify, L("ToolHashVerifyLabel"));
        TxtHelpContent.Text = L("ToolHelpHASH").Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HashGeneratorViewModel.IsFileHashing)) { _setBusy?.Invoke(_vm.IsFileHashing); }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInputTextChanged || _vm.IsFileMode) { return; }
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L("ToolHashBrowseFilter") };
        if (dialog.ShowDialog() == true) { BeginFileHash(dialog.FileName); }
    }

    private void OnClearFileClick(object sender, RoutedEventArgs e)
    {
        _debounceTimer?.Stop();
        _vm.ClearFileCommand.Execute(null);
        _suppressInputTextChanged = true;
        TxtInput.Text = string.Empty;
        _suppressInputTextChanged = false;
        TxtInput.Focus();
    }

    private void OnCopyTextRequested(object? sender, string text)
    {
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }
    }

    private void OnSaveFileRequested(object? sender, SaveFileRequest request)
    {
        var dialog = new SaveFileDialog { FileName = request.DefaultName, Filter = request.Filter, DefaultExt = Path.GetExtension(request.DefaultName) };
        if (dialog.ShowDialog() != true) { return; }
        if (request.IsBinary && request.Content is byte[] bytes) { File.WriteAllBytes(dialog.FileName, bytes); return; }
        if (!request.IsBinary && request.Content is string text) { File.WriteAllText(dialog.FileName, text, Encoding.UTF8); }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) =>
        HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files &&
            files.Length > 0) { BeginFileHash(files[0]); }
    }

    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) { DropZoneBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush"); }
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e) => DropZoneBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");

    private void BeginFileHash(string filePath)
    {
        if (!_vm.HashFileCommand.CanExecute(filePath)) { return; }

        _debounceTimer?.Stop();
        _suppressInputTextChanged = true;
        TxtInput.Text = string.Empty;
        _suppressInputTextChanged = false;
        _vm.HashFileCommand.Execute(filePath);
    }

    private void OnLocaleChanged(string _) => ApplyLocalization();

    private string L(string key) => _localizer?[key] ?? key;
}
