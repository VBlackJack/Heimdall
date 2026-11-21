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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

public partial class TotpGeneratorView : UserControl, IToolView
{
    private readonly TotpGeneratorViewModel _vm;
    private string _helpText = string.Empty;
    private DispatcherTimer? _timer;
    private bool _disposed;

    public TotpGeneratorView()
    {
        InitializeComponent();
        _vm = new TotpGeneratorViewModel((Application.Current as App)?.Services?.GetService<IOtpGeneratorService>());
        DataContext = _vm;
    }

    public void Initialize(ToolContext? context, Heimdall.Core.Localization.LocalizationManager? localizer)
    {
        _helpText = localizer?["ToolHelpTOTP"]?.Replace("\\n", "\n", StringComparison.Ordinal) ?? string.Empty;
        _vm.Initialize(localizer);
        _vm.PropertyChanged += OnVmPropertyChanged;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TxtSecret.Focus());
    }

    public bool CanClose() => true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.StartCommand.Execute(null);
        e.Handled = true;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TotpGeneratorViewModel.IsCodePanelVisible) && _vm.IsCodePanelVisible) StartOrRestartTimer();
    }

    private void StartOrRestartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_disposed) _vm.RefreshCode(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanCopy()) return;
        try { CopySensitive(_vm.CurrentCode); CopyFeedbackHelper.ShowCopyFeedback(sender as Button); }
        catch (Exception ex) { FileLogger.Warn($"TotpGenerator clipboard copy failed: {ex.Message}"); }
    }

    /// <summary>A one-time code must stay out of the clipboard history and the cloud clipboard.</summary>
    private static void CopySensitive(string text)
    {
        new WpfClipboardService().SetSensitiveText(text);
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        TxtHelpContent.Text = _helpText;
        HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
}
