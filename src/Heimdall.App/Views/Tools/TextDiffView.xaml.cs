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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

public partial class TextDiffView : UserControl, IToolView
{
    private readonly TextDiffViewModel _vm;
    private HeimdallThemeService? _themeService;
    private LocalizationManager? _localizer;
    private bool _disposed;

    public TextDiffView()
    {
        InitializeComponent();
        _vm = new TextDiffViewModel((Application.Current as App)?.Services?.GetService<ITextDiffToolService>());
        DataContext = _vm;
        OriginalText.PreviewKeyDown += OnDiffInputPreviewKeyDown;
        ModifiedText.PreviewKeyDown += OnDiffInputPreviewKeyDown;
        BuildDerivedBrushes();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _localizer = localizer;
        if (_localizer is not null) { _localizer.LocaleChanged += OnLocaleChanged; }

        if (_themeService is not null) { _themeService.ThemeChanged -= OnThemeChanged; }
        _themeService = (Application.Current as App)?.Services?.GetService<HeimdallThemeService>();
        if (_themeService is not null) { _themeService.ThemeChanged += OnThemeChanged; }

        _vm.Initialize(localizer);
        _vm.ApplyPrefill(context?.Argument);
        _vm.MarkInitialized();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { OriginalText.Focus(); if (!string.IsNullOrEmpty(OriginalText.Text)) { OriginalText.SelectAll(); } });
    }

    public bool CanClose() => !_vm.IsBusy;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        OriginalText.PreviewKeyDown -= OnDiffInputPreviewKeyDown;
        ModifiedText.PreviewKeyDown -= OnDiffInputPreviewKeyDown;
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        if (_themeService is not null) { _themeService.ThemeChanged -= OnThemeChanged; }
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnDiffInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _vm.DiffCommand.CanExecute(null))
        {
            _vm.DiffCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCopyDiffClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.UnifiedDiffText)) { return; }
        try
        {
            Clipboard.SetText(_vm.UnifiedDiffText);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (ExternalException) { }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) { UpdateHelpText(); HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
    private void OnLocaleChanged(string _) { if (HelpPanel.Visibility == Visibility.Visible) { UpdateHelpText(); } }
    private void OnThemeChanged(string _) => BuildDerivedBrushes();
    private void UpdateHelpText() => TxtHelpContent.Text = L("ToolHelpDIFF").Replace("\\n", "\n", StringComparison.Ordinal);
    private string L(string key) => _localizer?[key] ?? key;

    private void BuildDerivedBrushes()
    {
        var removed = (TryFindResource("ErrorBrush") as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(255, 255, 0, 0);
        var added = (TryFindResource("SuccessBrush") as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(255, 0, 180, 0);
        Resources["DiffRemovedLineBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, removed.R, removed.G, removed.B));
        Resources["DiffAddedLineBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, added.R, added.G, added.B));
        Resources["DiffRemovedWordBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(96, removed.R, removed.G, removed.B));
        Resources["DiffAddedWordBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(96, added.R, added.G, added.B));
        Resources["DiffRemovedPrefixBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, removed.R, removed.G, removed.B));
        Resources["DiffAddedPrefixBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, added.R, added.G, added.B));
    }
}
