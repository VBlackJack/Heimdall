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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// "Copy output, secrets masked": reads the text of a terminal pane, masks its secrets, shows the
/// masked text for review and, on Copy, places exactly that text on the clipboard. The context
/// menu, the keyboard shortcut and the command palette all start it here.
/// </summary>
/// <remarks>
/// <para>
/// Offline by construction: no network, no provider, nothing written to disk. The unmasked text
/// lives in memory between the read and the end of the dialog.
/// </para>
/// <para>
/// One run at a time. The dialog is modal and runs a nested message loop, so a key pressed again
/// while it is open (auto-repeat, a queued key event) reaches this entry point again; that second
/// call does nothing instead of stacking a second dialog.
/// </para>
/// </remarks>
public sealed class MaskedTerminalCopyService
{
    private readonly IDialogService _dialogService;
    private readonly LocalizationManager _localizer;
    private readonly IClipboardService _clipboard;
    private readonly Action<string> _showStatus;
    private bool _running;

    /// <summary>Creates the service.</summary>
    /// <param name="dialogService">Shows the review dialog.</param>
    /// <param name="localizer">Words the status messages.</param>
    /// <param name="clipboard">Receives the masked text.</param>
    /// <param name="showStatus">Shows a one-line message to the user (the status bar).</param>
    public MaskedTerminalCopyService(
        IDialogService dialogService,
        LocalizationManager localizer,
        IClipboardService clipboard,
        Action<string> showStatus)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _showStatus = showStatus ?? throw new ArgumentNullException(nameof(showStatus));
    }

    /// <summary>True while a run is reading, masking or showing the dialog.</summary>
    internal bool IsRunning => _running;

    /// <summary>True when the tab has a terminal pane this command can read right now.</summary>
    /// <param name="session">The tab.</param>
    /// <param name="clickedPane">The pane the user right-clicked, if the gesture was a pointer one.</param>
    public bool CanRun(SessionTabViewModel? session, SessionPaneModel? clickedPane = null) =>
        TerminalTextSourceResolver.Resolve(session, clickedPane) is not null;

    /// <summary>
    /// Synchronous entry for gestures that cannot await (a shortcut action, a menu click): starts
    /// the run and never lets an exception out of the fire-and-forget task.
    /// </summary>
    /// <param name="session">The tab.</param>
    /// <param name="clickedPane">The pane the user right-clicked, if the gesture was a pointer one.</param>
    public void Start(SessionTabViewModel? session, SessionPaneModel? clickedPane = null)
    {
        _ = RunAsync(session, clickedPane);
    }

    /// <summary>
    /// Runs the flow. Never throws: a failure is logged and reported through the status line.
    /// </summary>
    /// <param name="session">The tab.</param>
    /// <param name="clickedPane">The pane the user right-clicked, if the gesture was a pointer one.</param>
    public async Task RunAsync(SessionTabViewModel? session, SessionPaneModel? clickedPane = null)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        ITerminalTextSource? source = null;
        try
        {
            source = TerminalTextSourceResolver.Resolve(session, clickedPane);
            if (source is null)
            {
                _showStatus(_localizer["MaskedCopyStatusNoTerminal"]);
                return;
            }

            TerminalTextReadResult read = await source.ReadTextAsync(CancellationToken.None);
            if (read.Status != TerminalTextReadStatus.Ok)
            {
                _showStatus(_localizer[StatusKey(read.Status)]);
                return;
            }

            MaskedCopyDialogViewModel viewModel = await MaskedCopyDialogViewModel.CreateAsync(
                read.Text,
                read.Truncated,
                _localizer,
                _clipboard);

            await _dialogService.ShowMaskedCopyAsync(viewModel);

            if (viewModel.Copied)
            {
                _showStatus(_localizer["MaskedCopyCopied"]);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Masked copy failed: {ex.GetType().Name}");
            _showStatus(_localizer["MaskedCopyStatusFailed"]);
        }
        finally
        {
            _running = false;
            RestoreFocus(source);
        }
    }

    private static string StatusKey(TerminalTextReadStatus status) => status switch
    {
        TerminalTextReadStatus.NotReady => "MaskedCopyStatusNotReady",
        TerminalTextReadStatus.Timeout => "MaskedCopyStatusTimeout",
        _ => "MaskedCopyStatusFailed",
    };

    private static void RestoreFocus(ITerminalTextSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.FocusTerminal();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Masked copy could not give the focus back to the terminal: {ex.GetType().Name}");
        }
    }
}
