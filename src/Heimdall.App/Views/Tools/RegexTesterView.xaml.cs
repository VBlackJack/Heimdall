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
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

public partial class RegexTesterView : UserControl, IToolView
{
    private readonly RegexTesterViewModel _vm;
    private LocalizationManager? _localizer;
    private bool _disposed;

    public RegexTesterView()
    {
        InitializeComponent();
        _vm = new RegexTesterViewModel((Application.Current as App)?.Services?.GetService<IRegexTesterToolService>());
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _localizer = localizer;
        if (_localizer is not null) { _localizer.LocaleChanged += OnLocaleChanged; }
        _vm.Initialize(localizer);
        _vm.PrefillPattern(context?.Argument);
        _vm.MarkInitialized();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { PatternText.Focus(); PatternText.SelectAll(); });
    }

    public bool CanClose() => true;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_localizer is not null) { _localizer.LocaleChanged -= OnLocaleChanged; }
        _vm.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RegexTesterViewModel.HighlightSegments)) { RenderHighlight(); } }

    private void RenderHighlight()
    {
        if (_vm.HighlightSegments.Count == 0)
        {
            HighlightDisplay.Document = new FlowDocument();
            HighlightDisplay.Visibility = Visibility.Collapsed;
            return;
        }

        // Match/group highlights derive from themed brushes (accent for matches,
        // warning for named groups) with a translucent overlay opacity.
        var matchBrush = TryFindResource("AccentBrush") is SolidColorBrush accent
            ? new SolidColorBrush(accent.Color) { Opacity = 80.0 / 255.0 }
            : null;
        var groupBrush = TryFindResource("WarningBrush") is SolidColorBrush warning
            ? new SolidColorBrush(warning.Color) { Opacity = 100.0 / 255.0 }
            : null;
        var document = new FlowDocument
        {
            FontFamily = (System.Windows.Media.FontFamily)FindResource("FontFamilyMonospace"),
            FontSize = (double)FindResource("FontSizeBodyLarge"),
            PagePadding = new Thickness(0),
        };
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        foreach (var segment in _vm.HighlightSegments)
        {
            var run = new Run(segment.Text)
            {
                Foreground = Brushes.Transparent,
            };

            run.Background = segment.Kind switch
            {
                RegexHighlightKind.Match => matchBrush,
                RegexHighlightKind.NamedGroupMatch => groupBrush,
                _ => null,
            };
            paragraph.Inlines.Add(run);
        }

        document.Blocks.Add(paragraph);
        HighlightDisplay.Document = document;
        HighlightDisplay.Visibility = Visibility.Visible;
    }

    private void OnCopyMatchesClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.MatchesCopyText)) { return; }
        try
        {
            Clipboard.SetText(_vm.MatchesCopyText);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (ExternalException) { }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) { UpdateHelpText(); HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
    private void OnLocaleChanged(string _) { if (HelpPanel.Visibility == Visibility.Visible) { UpdateHelpText(); } }
    private void UpdateHelpText() => TxtHelpContent.Text = L("ToolHelpREGEX").Replace("\\n", "\n", StringComparison.Ordinal);
    private string L(string key) => _localizer?[key] ?? key;
}
