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

using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Views;

public partial class EmbeddedSshView
{
    /// <inheritdoc />
    public bool CanReadText =>
        !_disposed && _terminalReady && !_webViewUnavailable && TryGetCoreWebView2(out _);

    /// <inheritdoc />
    public Task<TerminalTextReadResult> ReadTextAsync(CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => ReadTextAsync(cancellationToken)).Task.Unwrap();
        }

        return _bufferReads.ReadAsync(
            CanReadText,
            PostTerminalMessage,
            TimeSpan.FromMilliseconds(TerminalBufferReadCoordinator.ReadTimeoutMilliseconds),
            cancellationToken);
    }

    /// <inheritdoc />
    public void FocusTerminal()
    {
        TerminalWebView.Focus();
        PostTerminalMessage(MsgFocus);
    }

    private void PostTerminalMessage(string message)
    {
        if (_disposed || _webViewUnavailable || IsDispatcherShuttingDown())
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            BeginInvokeIfAvailable(() => PostTerminalMessage(message));
            return;
        }

        // Output already queued by producers precedes this control message.
        DrainOutputCoalescer();

        if (_terminalReady && TryGetCoreWebView2(out var core))
        {
            try
            {
                core.PostWebMessageAsString(message);
            }
            catch (ObjectDisposedException)
            {
                DisableWebViewPosts();
            }
            catch (InvalidOperationException)
            {
                DisableWebViewPosts();
            }

            return;
        }

        if (!_webViewUnavailable && !IsDispatcherShuttingDown())
        {
            _pendingTerminalMessages.Enqueue(message);
        }
    }

    /// <summary>
    /// Logs how the report the page marked as its first shown size compares with the size it derived
    /// while hidden. The marker is the page's own statement, not read from the surface visibility.
    /// </summary>
    private void NoteSizeForShownCheck(TerminalSize size, bool shownMarker)
    {
        if (_firstShownSizeCheck.Observe(size, shownMarker) is { } result)
        {
            Core.Logging.FileLogger.Info(result.ToLogLine());
        }
    }

    private void FlushPendingTerminalMessages()
    {
        if (_disposed || !_terminalReady || !TryGetCoreWebView2(out var core))
        {
            return;
        }

        while (_pendingTerminalMessages.TryDequeue(out var message))
        {
            try
            {
                core.PostWebMessageAsString(message);
            }
            catch (ObjectDisposedException)
            {
                DisableWebViewPosts();
                return;
            }
            catch (InvalidOperationException)
            {
                DisableWebViewPosts();
                return;
            }
        }
    }

    private bool TryGetCoreWebView2([NotNullWhen(true)] out CoreWebView2? core, bool allowDuringDispose = false)
    {
        core = null;

        if ((!allowDuringDispose && _disposed) || _webViewUnavailable || IsDispatcherShuttingDown())
        {
            return false;
        }

        try
        {
            core = TerminalWebView.CoreWebView2;
            return core is not null;
        }
        catch (ObjectDisposedException)
        {
            DisableWebViewPosts();
            return false;
        }
        catch (InvalidOperationException)
        {
            DisableWebViewPosts();
            return false;
        }
    }

    private bool IsDispatcherShuttingDown() =>
        Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished;

    private void BeginInvokeIfAvailable(
        Action action,
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Normal)
    {
        if (_disposed || IsDispatcherShuttingDown())
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(action, priority);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can start shutting down between the check and BeginInvoke.
        }
    }

    private void DisableWebViewPosts()
    {
        _terminalReady = false;
        _webViewUnavailable = true;
        _bufferReads.FailPending();

        _outputCoalescer.Close();
        _pendingTerminalMessages.Clear();
    }

    private void ApplyInitialTerminalFocus()
    {
        if (_disposed || _webViewUnavailable || _initialTerminalFocusApplied)
        {
            return;
        }

        if (_terminalReady)
        {
            _initialTerminalFocusApplied = true;

            // Only steal focus on first mount if this view is actually visible
            // and no other focusable control currently has keyboard focus
            var currentFocus = System.Windows.Input.Keyboard.FocusedElement;
            var focusIsElsewhere = currentFocus is not null
                && currentFocus is not System.Windows.Window
                && !IsKeyboardFocusWithin;

            if (IsVisible && !focusIsElsewhere)
            {
                Core.Logging.FileLogger.Info("EmbeddedSSH applying initial terminal focus");
                FocusTerminal();
            }
        }
    }

    private void ShowWebViewUnavailable(string message)
    {
        if (_disposed)
        {
            return;
        }

        HideConnectingOverlay();
        StopAutoReconnectTimer();
        _webViewUnavailable = true;
        SetOverlayVisibility(AutoReconnectOverlay, Visibility.Collapsed);
        _terminalReady = false;

        _outputCoalescer.Close();
        _pendingTerminalMessages.Clear();
        FallbackPanel.Visibility = Visibility.Visible;
        FallbackMessageText.Text = message;
        UpdateStatus(SessionStatusTokens.Error);
    }

    private static bool TryParseSize(ReadOnlySpan<char> payload, out int columns, out int rows) =>
        TerminalSizeReport.TryParseSize(payload, out columns, out rows);
}
