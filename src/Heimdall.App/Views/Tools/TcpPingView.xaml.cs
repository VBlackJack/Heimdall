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
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// WPF shell for the TCP ping tool. Business logic lives in
/// <see cref="ITcpPingService"/> and <see cref="TcpPingViewModel"/>.
/// </summary>
public partial class TcpPingView : UserControl, IToolView
{
    private readonly TcpPingService _service = new();
    private TcpPingViewModel? _vm;
    private Action<bool>? _setBusy;
    private LocalizationManager? _localizer;
    private bool _disposed;

    private string L(string key) => _localizer?[key] ?? key;

    public TcpPingView()
    {
        InitializeComponent();
        _vm = new TcpPingViewModel(_service);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshUiFromVm();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_vm is null)
        {
            return;
        }

        _setBusy = context?.SetBusyAction;
        _localizer = localizer;
        _vm.UpdateLocalizer(localizer);

        _vm.Host = string.Empty;
        _vm.Port = TcpPingViewModel.DefaultPort.ToString(CultureInfo.InvariantCulture);
        _vm.Count = TcpPingViewModel.DefaultCount.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            _vm.Host = context.TargetHost.Trim();
        }

        if (context?.TargetPort is > 0)
        {
            _vm.Port = context.TargetPort.Value.ToString(CultureInfo.InvariantCulture);
        }

        RefreshUiFromVm();

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () =>
            {
                TxtHost.Focus();
                if (!string.IsNullOrEmpty(_vm.Host))
                {
                    TxtHost.SelectAll();
                }
            });
    }

    public bool CanClose() => _vm is null || !_vm.IsBusy;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Dispose();
            _vm = null;
        }

        GC.SuppressFinalize(this);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TcpPingViewModel.IsBusy):
            case nameof(TcpPingViewModel.ShowError):
            case nameof(TcpPingViewModel.HasResults):
                RefreshUiFromVm();
                break;
        }
    }

    private void RefreshUiFromVm()
    {
        if (_vm is null)
        {
            return;
        }

        _setBusy?.Invoke(_vm.IsBusy);
        TxtHost.IsReadOnly = _vm.IsBusy;
        TxtPort.IsReadOnly = _vm.IsBusy;
        TxtCount.IsReadOnly = _vm.IsBusy;
        BtnStart.IsEnabled = _vm.StartCommand.CanExecute(null);
        BtnStop.IsEnabled = _vm.StopCommand.CanExecute(null);
        BtnCopy.IsEnabled = _vm.CopyResultsCommand.CanExecute(null);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(_vm.Results))
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.Append(_vm.Results);
            if (!string.IsNullOrWhiteSpace(_vm.SummaryText))
            {
                builder.AppendLine();
                builder.AppendLine(_vm.SummaryText);
            }

            Clipboard.SetText(builder.ToString());
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (ExternalException ex)
        {
            Core.Logging.FileLogger.Warn($"TcpPing clipboard copy failed: {ex.Message}");
            _vm.ErrorText = string.Format(L("StatusClipboardCopyFailed"), ex.Message);
            _vm.ShowError = true;
        }
    }
}
