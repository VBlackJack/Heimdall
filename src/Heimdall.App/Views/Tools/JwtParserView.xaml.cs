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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Jwt;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

public partial class JwtParserView : UserControl, IToolView
{
    private readonly JwtParserViewModel _vm;
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private LocalizationManager? _localizer;
    private bool _disposed;
    public JwtParserView()
    {
        InitializeComponent();
        var services = (Application.Current as App)?.Services;
        var uiDispatcher = services?.GetRequiredService<IUiDispatcher>()
            ?? throw new InvalidOperationException("IUiDispatcher is not registered.");
        _vm = new JwtParserViewModel(
            uiDispatcher,
            services?.GetService<IJwtParserToolService>());
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _vm;
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); _vm.ParseCommand.Execute(null); };
    }
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _localizer = localizer;
        if (_localizer is not null) { _localizer.LocaleChanged += OnLocaleChanged; }
        _vm.Initialize(localizer);
        _vm.PrefillInput(context?.Argument);
        if (!string.IsNullOrWhiteSpace(_vm.InputText)) { _vm.ParseCommand.Execute(null); }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { TxtInput.Focus(); TxtInput.SelectAll(); });
    }
    public bool CanClose() => true;
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _debounceTimer.Stop();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }
    private void OnInputTextChanged(object sender, TextChangedEventArgs e) { _debounceTimer.Stop(); _debounceTimer.Start(); }
    private void OnCopyClick(object sender, RoutedEventArgs e) { if (sender is not Button { Tag: string text } button || string.IsNullOrEmpty(text)) { return; } try { Clipboard.SetText(text); CopyFeedbackHelper.ShowCopyFeedback(button); } catch (ExternalException) { } }
    private void OnHelpClick(object sender, RoutedEventArgs e) { UpdateHelpText(); HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
    private void OnLocaleChanged(string _) { if (HelpPanel.Visibility == Visibility.Visible) { UpdateHelpText(); } }
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(JwtParserViewModel.ExpirationStatus) or nameof(JwtParserViewModel.IsExpirationVisible)) { ApplyExpirationTheme(); } }
    private void ApplyExpirationTheme() { if (!_vm.IsExpirationVisible || _vm.ExpirationStatus == JwtExpirationStatus.InvalidClaim) { return; } string brushKey = _vm.ExpirationStatus switch { JwtExpirationStatus.Expired => "ErrorBrush", JwtExpirationStatus.Valid => "SuccessBrush", _ => "TextSecondaryBrush" }; if (TryFindResource(brushKey) is not SolidColorBrush brush) { return; } ExpirationBorder.Background = CreateOverlayBrush(brush.Color); ExpirationBorder.BorderBrush = brush; TxtExpiration.Foreground = TryFindResource(SemanticTextBrushes.TextKeyFor(brushKey)) as Brush ?? brush; }
    private void UpdateHelpText() => TxtHelpContent.Text = (_localizer?["ToolHelpJWT"] ?? "ToolHelpJWT").Replace("\\n", "\n", StringComparison.Ordinal);
    private static SolidColorBrush CreateOverlayBrush(System.Windows.Media.Color color) { var brush = new SolidColorBrush(color) { Opacity = 40.0 / 255.0 }; brush.Freeze(); return brush; }
}
