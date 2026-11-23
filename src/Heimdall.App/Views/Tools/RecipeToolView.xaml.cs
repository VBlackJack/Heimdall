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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// WPF shell for the tool a recipe becomes. The logic lives in <see cref="RecipeToolViewModel"/>;
/// this class hosts it, forwards the busy state to the tab, copies the output, and cancels a run
/// that is under way when the tab closes.
/// </summary>
public partial class RecipeToolView : UserControl, IToolView, IStaleToolView
{
    private readonly RecipeToolViewModel _viewModel;
    private Action<bool>? _setBusy;
    private LocalizationManager? _localizer;
    private bool _disposed;

    /// <summary>Creates the view of a recipe.</summary>
    /// <param name="viewModel">The view model of the recipe.</param>
    public RecipeToolView(RecipeToolViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _setBusy = context?.SetBusyAction;
        _localizer = localizer;
        _viewModel.Initialize(context, localizer);
    }

    /// <inheritdoc />
    public bool IsStale => _viewModel.CheckStale();

    /// <summary>A run under way is cancelled when the tab closes, so the tab can always close.</summary>
    public bool CanClose() => true;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _setBusy?.Invoke(false);
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecipeToolViewModel.IsRunning))
        {
            _setBusy?.Invoke(_viewModel.IsRunning);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.CopyText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_viewModel.CopyText);
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
        catch (ExternalException ex)
        {
            Core.Logging.FileLogger.Warn($"Recipe output clipboard copy failed ({ex.GetType().Name})");
            _viewModel.GeneralError = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localizer?["StatusClipboardCopyFailed"] ?? "StatusClipboardCopyFailed",
                ex.Message);
        }
    }
}
