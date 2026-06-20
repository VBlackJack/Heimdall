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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Embedded tool wrapping TwinShell's command library. Phase B of the
/// <c>CommandLibraryView</c> refactor: the XAML binds directly to
/// <see cref="CommandLibraryViewModel"/> state; this code-behind only keeps
/// the view-only concerns that cannot be expressed as bindings — dialog
/// plumbing, clipboard I/O, link navigation, and the "copied" flash animation
/// on the Copy/Send buttons.
/// </summary>
public partial class CommandLibraryView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private CommandLibraryViewModel? _viewModel;

    /// <summary>Creates the view. The VM is resolved later in <see cref="Initialize"/>.</summary>
    public CommandLibraryView()
    {
        InitializeComponent();
        IsVisibleChanged += OnViewIsVisibleChanged;
    }

    /// <summary>
    /// Re-evaluates the Send button state when the view becomes visible again:
    /// the split layout or pane focus may have changed which terminal sink (if
    /// any) is available since the view was last shown.
    /// </summary>
    private void OnViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _viewModel?.RefreshSendState();
        }
    }

    // ── IToolView ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;

        // Service-locator confined to one line in code-behind: the VM itself
        // never reaches into Application.Current.Services.
        var services = ((App)System.Windows.Application.Current).Services
            ?? throw new InvalidOperationException("DI container is not initialized.");
        _viewModel = services.GetRequiredService<CommandLibraryViewModel>();

        _viewModel.SendCommandHandler = context?.SendCommandAction;
        _viewModel.CanSendToTerminalProbe = context?.CanSendToTerminal;
        _viewModel.ShowActionDialogAsync = ShowActionDialogAsync;
        _viewModel.ShowSaveFileDialog = ShowSaveFileDialog;
        _viewModel.ShowOpenFileDialog = ShowOpenFileDialog;
        _viewModel.SetClipboardText = SetClipboardSafely;
        _viewModel.ShowCopyFeedback = OnCopyFeedbackRequested;
        _viewModel.LibraryReloaded += OnLibraryReloaded;

        DataContext = _viewModel;

        _viewModel.AutoSelectPlatform(context?.ConnectionType);
        _ = _viewModel.InitializeAsync(context?.TargetHost, context?.InitialActionId);
    }

    /// <summary>
    /// Preselects an action on an already-initialized view. Called by
    /// <see cref="MainViewModel.OpenToolTabAsync"/> when the Command Palette
    /// bridge focuses an existing CMDLIB tab instead of creating a new one.
    /// </summary>
    public void PreselectAction(string actionId) => _viewModel?.SelectActionById(actionId);

    /// <inheritdoc/>
    public bool CanClose() => _viewModel?.CanClose ?? true;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_viewModel is not null)
        {
            _viewModel.LibraryReloaded -= OnLibraryReloaded;
            _viewModel.Dispose();
            _viewModel = null;
        }
        GC.SuppressFinalize(this);
    }

    // ── VM callbacks ──────────────────────────────────────────────

    /// <summary>Re-focuses the search box after every library reload.</summary>
    private void OnLibraryReloaded()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                // Land focus on the search box so the user can type immediately.
                SearchBox.Focus();
            }));
    }

    /// <summary>
    /// Flashes the "copied" visual feedback on the requested button. The VM
    /// invokes this via the <c>ShowCopyFeedback</c> callback after Copy/Send.
    /// </summary>
    private void OnCopyFeedbackRequested(string target)
    {
        Button? button = target switch
        {
            "copy" => BtnCopy,
            "send" => BtnSend,
            _ => null
        };
        if (button is not null)
        {
            CopyFeedbackHelper.ShowCopyFeedback(button);
        }
    }

    /// <summary>
    /// Thin wrapper around <see cref="Clipboard.SetText"/> that swallows the
    /// External clipboard-locked exceptions Windows raises when another
    /// process owns the clipboard. Returns <c>true</c> on a successful write,
    /// <c>false</c> when the clipboard was unavailable.
    /// </summary>
    private static bool SetClipboardSafely(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    // ── Links ─────────────────────────────────────────────────────

    /// <summary>
    /// Opens external documentation links in the default browser. Kept in
    /// code-behind because <see cref="System.Diagnostics.Process"/> is a
    /// view-layer concern.
    /// </summary>
    private void OnLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            if (e.Uri.Scheme is not ("http" or "https")) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[CommandLibrary] Failed to open link: {ex.Message}");
        }
        e.Handled = true;
    }

    // ── Dialog callbacks installed on the VM ──────────────────────

    private Task<bool> ShowActionDialogAsync(CommandActionDialogViewModel vm)
    {
        var dialog = new CommandActionDialog
        {
            DataContext = vm,
            Owner = Window.GetWindow(this)
        };

        var result = dialog.ShowDialog() == true;
        return Task.FromResult(result);
    }

    private string? ShowSaveFileDialog(string defaultFileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = defaultFileName
        };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
    }

    private string? ShowOpenFileDialog(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Multiselect = false
        };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
    }
}
