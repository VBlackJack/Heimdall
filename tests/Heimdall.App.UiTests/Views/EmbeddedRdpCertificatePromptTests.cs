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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// Drives the certificate question against the real RDP pane: its markup, its bindings and its
/// answers.
/// </summary>
/// <remarks>
/// <para>Everything else about this question is measured without WPF - the session state
/// machine, the routing, the wording, the owner line. What only a live control can settle is the
/// part that was a top-level window until this lot: that the question loads inside a pane at
/// all, that each answer resolves the command it is bound to, and that answering one takes it
/// off the screen and gives the native RDP surface back. A markup file that does not load, and a
/// binding that resolves to nothing, are both invisible to every source reading and to every
/// test that builds the ViewModel by hand.</para>
/// <para><b>Bindings are flushed one expression at a time, never by pumping the dispatcher.</b>
/// Setting a DataContext detaches the binding expressions under it and schedules their
/// re-attachment on the dispatcher, so nothing is readable from them until that queue runs -
/// measured, not assumed: without either mechanism three of these four tests read empty values.
/// Draining the queue means a nested <c>Invoke</c> at a lower priority, which also runs whatever
/// the other test collections have queued on this shared dispatcher. Re-attaching one expression
/// is synchronous and waits on nothing.</para>
/// <para>No <c>Window</c> is constructed. The pane is a <c>UserControl</c>, and the shared
/// dispatcher is left as it was found.</para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedRdpCertificatePromptTests
{
    private const string TunnelEndpoint = "127.0.0.1";
    private const string LogicalHost = "dc-pool.example.com";
    private const string Thumbprint = "SHA256:AA:BB:CC:DD:01";

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheQuestionAppearsInThePaneAndNamesTheMachineRatherThanTheTunnel()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();

            Task<RdpTrustAnswer> pending = Ask(view);

            Assert.Equal(Visibility.Visible, Overlay(view).Visibility);
            Assert.False(pending.IsCompleted);

            // The airspace rule: WindowsFormsHost is a child HWND and paints over WPF whatever
            // the z-order, so a question left behind it is a question nobody can answer.
            Assert.Equal(
                Visibility.Collapsed,
                Element<WindowsFormsHost>(view, "FormsHost").Visibility);

            // The security property, on the live control. The address this pane dialled is
            // 127.0.0.1, because the session is tunnelled; the question must not identify its
            // subject that way, or two tunnelled profiles ask the same question.
            string host = Bound<TextBlock>(view, "CertificatePromptHostText", TextBlock.TextProperty).Text;
            Assert.Contains(LogicalHost, host, StringComparison.Ordinal);
            Assert.DoesNotContain(TunnelEndpoint, host, StringComparison.Ordinal);

            string message = Bound<TextBlock>(
                view, "CertificatePromptMessageText", TextBlock.TextProperty).Text;
            Assert.Contains(LogicalHost, message, StringComparison.Ordinal);
            Assert.DoesNotContain(TunnelEndpoint, message, StringComparison.Ordinal);

            Assert.Equal(
                Thumbprint,
                Bound<TextBox>(view, "CertificatePromptThumbprintBox", TextBox.TextProperty).Text);

            view.Dispose();
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheQuestionNamesTheGatewayTheSessionIsReachedThrough()
    {
        // The identity the endpoint alone could not carry, read off the live control. Two saved
        // profiles, both named "Production", both reaching "dc-pool.example.com:3389", one
        // through Paris and one through Berlin, are two physically different machines behind one
        // short name. Their endpoint text differs only by an ephemeral local tunnel port, so the
        // fingerprint approved for one was written into the other's trust set.
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();
            SetField(view, "_settings", new AppSettings
            {
                SshGateways =
                [
                    new SshGatewayDto
                    {
                        Id = "gateway-1",
                        Name = "Paris datacentre",
                        Host = "gw1.example.com",
                    },
                ],
            });

            Task<RdpTrustAnswer> pending = Ask(view);

            Assert.Equal(
                "Paris datacentre",
                Bound<TextBlock>(view, "CertificatePromptRouteText", TextBlock.TextProperty).Text);
            Assert.Equal(
                Visibility.Visible,
                Bound<TextBlock>(
                    view,
                    "CertificatePromptRouteText",
                    UIElement.VisibilityProperty).Visibility);
            Assert.Equal(
                Visibility.Visible,
                Bound<TextBlock>(
                    view,
                    "CertificatePromptRouteLabel",
                    UIElement.VisibilityProperty).Visibility);

            Assert.False(pending.IsCompleted);
            view.Dispose();
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheFingerprintIsInsideTheSameTabScopeAsTheAnswers()
    {
        // The keyboard defect, read as WPF itself sees it rather than out of the markup text.
        // The three answers sat in their own TabNavigation="Cycle" scope and
        // ShowCertificatePrompt places focus inside it on the refuse button; WPF's Cycle returns
        // focus to the first element of the scope rather than leaving it, so Tab walked
        // Do-not-connect, Just-this-once, Trust, Do-not-connect, forever. The full SHA-256
        // fingerprint the user is being asked to compare sits in a read-only TextBox that is a
        // tab stop, and it was unreachable: never focusable, never selectable, never copyable,
        // and never read out by a screen-reader user walking the question with Tab.
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();
            Task<RdpTrustAnswer> pending = Ask(view);

            // The single trap, which is what stops Tab wandering into the toolbar behind.
            Assert.Equal(
                KeyboardNavigationMode.Cycle,
                KeyboardNavigation.GetTabNavigation(Overlay(view)));

            // And nothing inside it opens a second one.
            Assert.NotEqual(
                KeyboardNavigationMode.Cycle,
                KeyboardNavigation.GetTabNavigation(
                    Element<WrapPanel>(view, "CertificatePromptAnswers")));

            TextBox fingerprint = Element<TextBox>(view, "CertificatePromptThumbprintBox");
            Assert.True(
                fingerprint.Focusable && KeyboardNavigation.GetIsTabStop(fingerprint),
                "The fingerprint box is not a tab stop, so leaving the answers' scope open buys "
                    + "nothing: Tab still walks past the one thing the question exists to have "
                    + "the user read.");

            Assert.False(pending.IsCompleted);
            view.Dispose();
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheThreeAnswersAreBoundToCommandsThatResolve()
    {
        // A button whose Command binding resolves to null is enabled, clickable and inert. That
        // is invisible to a source reading of the markup and to any test that drives the
        // ViewModel directly.
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();
            Task<RdpTrustAnswer> pending = Ask(view);

            foreach (string name in new[]
            {
                "CertificatePromptRefuseButton",
                "CertificatePromptTrustOnceButton",
                "CertificatePromptTrustButton",
            })
            {
                Assert.NotNull(Bound<Button>(view, name, ButtonBase.CommandProperty).Command);
            }

            Assert.False(pending.IsCompleted);
            view.Dispose();
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void AnsweringTakesTheQuestionOffTheScreenAndGivesTheSurfaceBack()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();
            Task<RdpTrustAnswer> pending = Ask(view);

            Button trust = Bound<Button>(
                view, "CertificatePromptTrustButton", ButtonBase.CommandProperty);
            Assert.NotNull(trust.Command);
            trust.Command.Execute(trust.CommandParameter);

            Assert.True(pending.IsCompleted);
            Assert.Equal(RdpTrustAnswer.TrustPermanently, pending.Result);
            Assert.Equal(Visibility.Collapsed, Overlay(view).Visibility);

            // Without the restore, a session whose certificate was just approved comes up
            // blank, which looks exactly like a failed connection.
            Assert.Equal(
                Visibility.Visible,
                Element<WindowsFormsHost>(view, "FormsHost").Visibility);

            view.Dispose();
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void ClosingThePaneWhileItIsAskingIsARefusal()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            EmbeddedRdpView view = CreateTunnelledPane();
            Task<RdpTrustAnswer> pending = Ask(view);

            view.Dispose();

            // The rule the window used to carry as "the title-bar cross is not an answer". The
            // alternative is a connection opened on a certificate nobody approved.
            //
            // NotAsked and not Refuse: closing a pane is something the user did to the pane, not
            // something they said about the certificate. Reporting it as a refusal is what put
            // "you did not approve the certificate this server presented" on the status line of
            // a pane whose user was shown nothing - and, while the question's display was shared
            // between panes, on the status line of a DIFFERENT pane entirely. Both values stop
            // the connection.
            Assert.True(pending.IsCompleted);
            Assert.Equal(RdpTrustAnswer.NotAsked, pending.Result);
            Assert.NotEqual(RdpTrustAnswer.Refuse, pending.Result);
            Assert.Equal(Visibility.Collapsed, Overlay(view).Visibility);
        });
    }

    private static Task<RdpTrustAnswer> Ask(EmbeddedRdpView view)
        => ((IRdpTrustPromptSurface)view).AskAsync(
            new RdpCertificatePromptContext(
                "Production", TunnelEndpoint, Thumbprint, "CN=dc04", 0)
            {
                ProfileId = "profile-1",
            },
            CancellationToken.None);

    /// <summary>A pane whose session is tunnelled, so its dialled address is not its machine.</summary>
    /// <remarks>
    /// <c>InitializeSession</c> is not called: it creates the ActiveX host and starts a
    /// connection. The three fields the question reads are set directly, which is what the
    /// sibling RDP test in this project does for the same reason.
    /// </remarks>
    private static EmbeddedRdpView CreateTunnelledPane()
    {
        EmbeddedRdpView view = new();
        SetField(view, "_localizer", WpfTestHost.Localizer);
        SetField(view, "_tunnelPort", (int?)53211);
        SetField(view, "_server", new ServerProfileDto
        {
            Id = "profile-1",
            DisplayName = "Production",
            RemoteServer = LogicalHost,
            RemotePort = 3389,
            SshGatewayId = "gateway-1",
            UseDirectConnection = false,
            LocalPort = 13389,
        });

        return view;
    }

    /// <summary>The named element, with the binding on one property already pulled.</summary>
    /// <remarks>
    /// A missing binding fails here rather than being read as a value of null, which is what a
    /// deleted <c>{Binding}</c> would otherwise look like.
    /// </remarks>
    private static T Bound<T>(EmbeddedRdpView view, string name, DependencyProperty property)
        where T : DependencyObject
    {
        T element = Element<T>(view, name);
        BindingBase? declared = BindingOperations.GetBindingBase(element, property);

        Assert.True(
            declared is not null,
            $"'{name}' declares no binding on {property.Name}, so nothing of the question "
                + "reaches it.");

        // Re-attached rather than merely refreshed. A DataContext change detaches the
        // expressions under it and schedules their re-attachment on the dispatcher, so
        // UpdateTarget on the old expression pulls from a source it no longer has; a binding
        // set now resolves against the DataContext that is already there.
        BindingOperations.ClearBinding(element, property);
        _ = BindingOperations.SetBinding(element, property, declared!);
        return element;
    }

    private static FrameworkElement Overlay(EmbeddedRdpView view)
        => Element<FrameworkElement>(view, "CertificatePromptOverlay");

    private static T Element<T>(EmbeddedRdpView view, string name)
        where T : class
    {
        object? found = view.FindName(name);
        Assert.True(found is not null, $"The RDP pane declares no element named '{name}'.");
        return Assert.IsType<T>(found, exactMatch: false);
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(target, value);
    }
}
