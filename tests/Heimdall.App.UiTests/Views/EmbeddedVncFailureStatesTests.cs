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

using System.Reflection;
using System.Windows;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// What a VNC pane shows when the session cannot start or ends in a failure: the error state with
/// Reconnect, the busy bar gone, and the reason the proxy gave rather than the page's generic one.
/// </summary>
/// <remarks>
/// No browser engine runs: the page's messages are handed to
/// <see cref="EmbeddedVncView.HandlePageMessage"/> as the page would send them, and the proxy's
/// reasons to <see cref="EmbeddedVncView.HandleTargetConnectFailure"/>.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedVncFailureStatesTests
{
    // R3-6: the proxy is started inside the try. Before, a Start that threw left the tab on
    // "Connecting" with a busy bar and nothing behind it.
    // Kills: the factory call moved out of the try (the exception then escapes the view).
    [StaFact]
    public void AProxyThatCannotStart_ShowsTheErrorState_WithReconnect_AndNoBusyBar()
    {
        Visibility overlay = Visibility.Collapsed;
        Visibility busyBar = Visibility.Visible;
        string? message = null;
        string? reported = null;
        Exception? escaped = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = new();
            view.ProxyFactory = (_, _) => throw new InvalidOperationException("the listener refused to start");
            view.SessionError += (_, text) => reported = text;

            try
            {
                view.InitializeSessionAsync(
                        new VncSessionResult("s1", "127.0.0.1", 5901), null!, "lab", localizer: null)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                escaped = ex;
            }

            overlay = view.ReconnectOverlay.Visibility;
            busyBar = view.VncLoadingBar.Visibility;
            message = view.ReconnectMessageText.Text;
        });

        Assert.Null(escaped);
        Assert.Equal(Visibility.Visible, overlay);
        Assert.Equal(Visibility.Collapsed, busyBar);
        Assert.Equal("ErrorVncProxyStartFailed", message);
        Assert.Equal("ErrorVncProxyStartFailed", reported);
    }

    // R3-1: each reason has its own words, since the fix differs.
    // Kills: one message for every reason.
    [StaFact]
    public void EachReasonTheProxyGives_ShowsItsOwnMessage()
    {
        Dictionary<VncTargetFailure, string?> shown = [];

        foreach (VncTargetFailure failure in Enum.GetValues<VncTargetFailure>())
        {
            WpfTestHost.Invoke(() =>
            {
                using EmbeddedVncView view = Build();
                view.HandleTargetConnectFailure(failure);
                shown[failure] = view.ReconnectMessageText.Text;
            });
        }

        Assert.Equal("ErrorVncTargetRefused", shown[VncTargetFailure.Refused]);
        Assert.Equal("ErrorVncTargetTimedOut", shown[VncTargetFailure.TimedOut]);
        Assert.Equal("ErrorVncTargetUnreachable", shown[VncTargetFailure.Unreachable]);
        Assert.Equal(shown.Count, shown.Values.Distinct().Count());
    }

    [StaFact]
    public void AReasonFromTheProxy_EndsTheAttemptWithReconnect_AndNoBusyBar()
    {
        Visibility overlay = Visibility.Collapsed;
        Visibility busyBar = Visibility.Visible;
        string? reported = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build();
            view.SessionError += (_, text) => reported = text;

            view.HandleTargetConnectFailure(VncTargetFailure.Refused);
            overlay = view.ReconnectOverlay.Visibility;
            busyBar = view.VncLoadingBar.Visibility;
        });

        Assert.Equal(Visibility.Visible, overlay);
        Assert.Equal(Visibility.Collapsed, busyBar);
        Assert.Equal("ErrorVncTargetRefused", reported);
    }

    // The page learns only that its socket closed and says so, after the proxy's own reason.
    // Kills: the page's generic "connection lost" replacing the reason the proxy knew.
    [StaFact]
    public void ThePagesGenericError_DoesNotReplaceTheReasonTheProxyGave()
    {
        string? message = null;
        string? status = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build();
            view.HandleTargetConnectFailure(VncTargetFailure.TimedOut);

            view.HandlePageMessage("error:The VNC connection was lost unexpectedly.");
            view.HandlePageMessage("disconnected:error");

            message = view.ReconnectMessageText.Text;
            status = view.StatusTextBlock.Text;
        });

        Assert.Equal("ErrorVncTargetTimedOut", message);
        Assert.Equal("ErrorVncTargetTimedOut", status);
    }

    // R3-7: an "error:" from the page ended nothing the user could see: the status line changed
    // under a busy bar that kept running, and no Reconnect was offered.
    // Kills: the handler that only updates the status line.
    [StaFact]
    public void AnErrorFromThePage_ShowsTheOverlayWithReconnect_AndHidesTheBusyBar()
    {
        Visibility overlay = Visibility.Collapsed;
        Visibility busyBar = Visibility.Visible;
        Visibility reconnect = Visibility.Collapsed;
        string? reported = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build();
            view.SessionError += (_, text) => reported = text;
            view.VncLoadingBar.Visibility = Visibility.Visible;

            view.HandlePageMessage("error:noVNC library failed to load");

            overlay = view.ReconnectOverlay.Visibility;
            busyBar = view.VncLoadingBar.Visibility;
            reconnect = view.ReconnectButton.Visibility;
        });

        Assert.Equal(Visibility.Visible, overlay);
        Assert.Equal(Visibility.Collapsed, busyBar);
        Assert.Equal(Visibility.Visible, reconnect);
        Assert.Equal("noVNC library failed to load", reported);
    }

    /// <summary>A view as InitializeSessionAsync leaves it, without the WebView2 it starts.</summary>
    private static EmbeddedVncView Build()
    {
        EmbeddedVncView view = new();
        view.PageMessageSink = _ => { };
        SetPrivateField(view, "_session", new VncSessionResult("s1", "127.0.0.1", 5901));
        SetPrivateField(view, "_passwordFlow", new VncPasswordFlow(null, null));
        return view;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        field.SetValue(target, value);
    }
}
