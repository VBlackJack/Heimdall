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
/// The password of a VNC session whose profile cannot hold one, on the constructed view: what the
/// failure overlay offers after each answer of the server, what reaches the page, and when the
/// password is kept or dropped.
/// </summary>
/// <remarks>
/// The page's messages are handed to <see cref="EmbeddedVncView.HandlePageMessage"/> as the page
/// would send them, and what the view sends back is recorded through
/// <see cref="EmbeddedVncView.PageMessageSink"/>. No browser engine runs: the server's answers are
/// the page's messages.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedVncPasswordEntryTests
{
    private const string Typed = "typed-pw-5120";

    // The connection stays open while the user types: ending it counts as a failed sign-in on
    // TigerVNC, which blocks the address after a few (measured on the lab container, 2026-10-20).
    // Kills: the dead end kept for a shared session, the "disconnect:" still sent, or the entry
    // reset when the server ends the wait itself.
    [StaFact]
    public void ASharedSession_AskedForAPassword_OffersToTypeIt_AndKeepsTheConnectionOpen()
    {
        var keeper = new RecordingKeeper();
        List<string> sent = [];
        Visibility afterRequest = Visibility.Collapsed;
        Visibility afterDisconnect = Visibility.Collapsed;
        string? message = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, sent);

            view.HandlePageMessage("credentials-required:[\"password\"]");
            afterRequest = view.OverlayEnterPasswordButton.Visibility;
            message = view.ReconnectMessageText.Text;

            view.HandlePageMessage("disconnected:clean");
            afterDisconnect = view.OverlayEnterPasswordButton.Visibility;
        });

        Assert.DoesNotContain("disconnect:", sent);
        Assert.Equal(Visibility.Visible, afterRequest);
        Assert.Equal(Visibility.Visible, afterDisconnect);
        Assert.Equal("ErrorVncSharedPasswordRequired", message);
    }

    // A profile of the user's own holds its password: the overlay sends the user to it, and
    // nothing here will answer the server, so the attempt ends.
    // Kills: the entry offered without a keeper, or a connection left open that nothing answers.
    [StaFact]
    public void AnOwnProfile_AskedForAPassword_OffersNoEntry_AndEndsTheAttempt()
    {
        List<string> sent = [];
        Visibility entry = Visibility.Visible;
        string? message = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper: null, sent);

            view.HandlePageMessage("credentials-required:[\"password\"]");
            entry = view.OverlayEnterPasswordButton.Visibility;
            message = view.ReconnectMessageText.Text;
        });

        Assert.Equal(Visibility.Collapsed, entry);
        Assert.Equal("ErrorVncPasswordRequired", message);
        Assert.Contains("disconnect:", sent);
    }

    // Heimdall has no VNC username: a request for one keeps the dead end even on a shared session.
    // Kills: the entry offered for every credential request.
    [StaFact]
    public void ASharedSession_AskedForAUsername_OffersNoEntry()
    {
        Visibility entry = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(new RecordingKeeper(), []);

            view.HandlePageMessage("credentials-required:[\"username\",\"password\"]");
            entry = view.OverlayEnterPasswordButton.Visibility;
        });

        Assert.Equal(Visibility.Collapsed, entry);
    }

    // The typed password answers the server still waiting for it, with no new connection, and is
    // kept once the session connects. Kills: the answer sent as a new connect (one more abandoned
    // sign-in), the answer dropped, or the password kept before the server accepted it.
    [StaFact]
    public void ATypedPassword_AnswersTheWaitingServer_AndIsKeptOnceConnected()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, true));
        List<string> sent = [];
        int keptBeforeConnect = -1;
        Visibility overlay = Visibility.Visible;
        List<string> retried = [];

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, sent);
            view.SessionRetrying += retried.Add;
            view.HandlePageMessage("credentials-required:[\"password\"]");
            sent.Clear();

            InvokePrivate(view, "OnOverlayEnterPasswordClick", view.OverlayEnterPasswordButton, new RoutedEventArgs());
            keptBeforeConnect = keeper.Remembered.Count;
            overlay = view.ReconnectOverlay.Visibility;

            view.HandlePageMessage("connected:");
        });

        string answer = Assert.Single(sent);
        Assert.Equal("credentials:{\"password\":\"" + Typed + "\"}", answer);
        Assert.Equal(0, keptBeforeConnect);
        Assert.Equal(Visibility.Collapsed, overlay);
        Assert.Equal(["s1"], retried);
        Assert.Equal([Typed], keeper.Remembered);
    }

    // A server that ended the wait before the user answered gets a new connect with the password.
    // Kills: the answer sent to a connection already gone.
    [StaFact]
    public void ATypedPassword_AfterTheServerLeft_ConnectsInPlace()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, false));
        List<string> sent = [];

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, sent);
            view.HandlePageMessage("credentials-required:[\"password\"]");
            view.HandlePageMessage("disconnected:error");
            sent.Clear();

            InvokePrivate(view, "OnOverlayEnterPasswordClick", view.OverlayEnterPasswordButton, new RoutedEventArgs());
        });

        Assert.DoesNotContain(sent, message => message.StartsWith("credentials:", StringComparison.Ordinal));
        string connect = Assert.Single(sent, message => message.StartsWith("connect:", StringComparison.Ordinal));
        Assert.Contains("\"password\":\"" + Typed + "\"", connect, StringComparison.Ordinal);
    }

    // The server can leave between the answer leaving the view and reaching the page: the page
    // says so, and the answer goes into a new connect, the overlay its disconnect raised taken down.
    // Kills: a late answer lost, or the overlay left over the new attempt.
    [StaFact]
    public void AnAnswerThatArrivesLate_ConnectsInPlace()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, false));
        List<string> sent = [];
        Visibility overlay = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, sent);
            view.HandlePageMessage("credentials-required:[\"password\"]");
            InvokePrivate(view, "OnOverlayEnterPasswordClick", view.OverlayEnterPasswordButton, new RoutedEventArgs());
            view.HandlePageMessage("disconnected:error");
            sent.Clear();

            view.HandlePageMessage("credentials-late:");
            overlay = view.ReconnectOverlay.Visibility;
        });

        string connect = Assert.Single(sent, message => message.StartsWith("connect:", StringComparison.Ordinal));
        Assert.Contains("\"password\":\"" + Typed + "\"", connect, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, overlay);
    }

    // A retry that then fails for another reason (a server blocking the address) shows the overlay
    // without the entry; its words may not still say to use it. Seen on the lab TigerVNC.
    // Kills: the retry leaving the request's words on the overlay.
    [StaFact]
    public void ARetryThatFailsForAnotherReason_DoesNotKeepThePasswordWords()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, false));
        string? message = null;
        Visibility entry = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, []);
            view.HandlePageMessage("credentials-required:[\"password\"]");
            InvokePrivate(view, "OnOverlayEnterPasswordClick", view.OverlayEnterPasswordButton, new RoutedEventArgs());

            view.HandlePageMessage("error:The VNC connection was lost unexpectedly.");
            view.HandlePageMessage("disconnected:error");
            message = view.ReconnectMessageText.Text;
            entry = view.OverlayEnterPasswordButton.Visibility;
        });

        Assert.Equal("VncDisconnectedOverlay", message);
        Assert.Equal(Visibility.Collapsed, entry);
    }

    // A cancelled question leaves the overlay as it was and sends nothing.
    // Kills: a cancel that reconnects with no password.
    [StaFact]
    public void ACancelledQuestion_SendsNothing()
    {
        var keeper = new RecordingKeeper(answer: null);
        List<string> sent = [];
        Visibility overlay = Visibility.Collapsed;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, sent);
            view.HandlePageMessage("credentials-required:[\"password\"]");
            sent.Clear();

            InvokePrivate(view, "OnOverlayEnterPasswordClick", view.OverlayEnterPasswordButton, new RoutedEventArgs());
            overlay = view.ReconnectOverlay.Visibility;
        });

        Assert.Empty(sent);
        Assert.Equal(Visibility.Visible, overlay);
    }

    // Kills: a refused password kept, or no way to type another one after the refusal.
    [StaFact]
    public void ARefusalAfterThePassword_DropsIt_AndOffersToTypeAnother()
    {
        var keeper = new RecordingKeeper();
        Visibility entry = Visibility.Collapsed;
        string? message = null;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, []);

            view.HandlePageMessage("security-failure:{\"afterPassword\":true,\"status\":1,\"reason\":\"Authentication failure\"}");
            entry = view.OverlayEnterPasswordButton.Visibility;
            message = view.ReconnectMessageText.Text;
        });

        Assert.Equal(1, keeper.Forgotten);
        Assert.Equal(Visibility.Visible, entry);
        Assert.Equal("ErrorVncSharedPasswordRejected", message);
    }

    // TigerVNC refuses an address after too many failures before any password: the kept one stays.
    // Kills: forgetting on every security failure.
    [StaFact]
    public void ARefusalBeforeThePassword_KeepsIt_AndOffersNoEntry()
    {
        var keeper = new RecordingKeeper();
        Visibility entry = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = Build(keeper, []);

            view.HandlePageMessage("security-failure:{\"afterPassword\":false,\"status\":1,\"reason\":\"Too many security failures\"}");
            entry = view.OverlayEnterPasswordButton.Visibility;
        });

        Assert.Equal(0, keeper.Forgotten);
        Assert.Equal(Visibility.Collapsed, entry);
    }

    // The overlay is the only one over a VNC pane, so it carries Edit profile where a profile can
    // be edited. Kills: the button never shown, or shown where the manager said no.
    [StaFact]
    public void TheOverlay_OffersEditProfile_OnlyWhereTheManagerAllowsIt()
    {
        Visibility allowed = Visibility.Collapsed;
        Visibility refused = Visibility.Visible;

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView own = Build(keeper: null, []);
            own.EditProfileAvailable = () => true;
            own.HandlePageMessage("credentials-required:[\"password\"]");
            allowed = own.OverlayEditProfileButton.Visibility;

            using EmbeddedVncView shared = Build(new RecordingKeeper(), []);
            shared.EditProfileAvailable = () => false;
            shared.HandlePageMessage("credentials-required:[\"password\"]");
            refused = shared.OverlayEditProfileButton.Visibility;
        });

        Assert.Equal(Visibility.Visible, allowed);
        Assert.Equal(Visibility.Collapsed, refused);
    }

    /// <summary>A view as InitializeSessionAsync leaves it, without the WebView2 it starts.</summary>
    private static EmbeddedVncView Build(IVncPasswordKeeper? keeper, List<string> sent)
    {
        EmbeddedVncView view = new() { PasswordKeeper = keeper };
        view.PageMessageSink = sent.Add;
        SetPrivateField(view, "_session", new VncSessionResult("s1", "127.0.0.1", 5901));
        SetPrivateField(view, "_passwordFlow", new VncPasswordFlow(null, keeper));
        SetPrivateField(view, "_proxy", new WebSocketVncProxy("127.0.0.1", 5901));
        SetPrivateField(view, "_webViewReady", true);
        return view;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        field.SetValue(target, value);
    }

    private static void InvokePrivate(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        method.Invoke(target, arguments);
    }

    private sealed class RecordingKeeper(PasswordPromptResult? answer = null) : IVncPasswordKeeper
    {
        public List<string> Remembered { get; } = [];

        public int Forgotten { get; private set; }

        public Task<PasswordPromptResult?> AskAsync(CancellationToken cancellationToken) => Task.FromResult(answer);

        public Task RememberAsync(string password)
        {
            Remembered.Add(password);
            return Task.CompletedTask;
        }

        public Task ForgetAsync()
        {
            Forgotten++;
            return Task.CompletedTask;
        }
    }
}
