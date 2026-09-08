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
using System.Windows.Controls;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;
using Heimdall.Rdp;

namespace Heimdall.App.UiTests.Views;

[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedRdpGatewayFailureTests
{
    [StaFact]
    public void AnonymousReport_UsesTypedCodesAndExcludesDisplayedDetails()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            using EmbeddedRdpView view = new();
            SetPrivateField(view, "_localizer", WpfTestHost.Localizer);
            view.SetOwningPane(new Heimdall.Core.Models.SessionPaneModel
            {
                FailureDetails = new Heimdall.Core.SessionDiagnostics.SessionDiagnostic(
                    Heimdall.Core.SessionDiagnostics.SessionFailureStage.RdpActiveXDisconnect,
                    "RdpDisconnectedMessage", 260, "private-host alice secret-value")
            });
            Assert.IsType<TextBlock>(view.FindName("ReconnectMessageText")).Text = "private-host alice secret-value";
            string report = view.BuildAnonymousReconnectReport();
            Assert.Contains("260", report);
            Assert.Contains("Heimdall", report);
            Assert.DoesNotContain("private-host", report);
            Assert.DoesNotContain("alice", report);
            Assert.DoesNotContain("secret-value", report);
            Assert.IsType<Button>(view.FindName("OverlayCopyAnonymousButton"));
        });
    }

    [StaFact]
    public void SshProfileEdit_TargetsOwningPaneAndKeepsRecoveryVisible()
    {
        WpfTestHost.Invoke(() =>
        {
            using EmbeddedSshView view = new();
            view.SetOwningPane(new Heimdall.Core.Models.SessionPaneModel
            {
                ServerId = "runtime-key",
                OriginalServerId = "saved-ssh"
            });
            Heimdall.App.Services.EmbeddedSessionManager manager =
                (Heimdall.App.Services.EmbeddedSessionManager)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(Heimdall.App.Services.EmbeddedSessionManager));
            string? edited = null;
            manager.EditServerRequestedCallback = id => edited = id;
            typeof(Heimdall.App.Services.EmbeddedSessionManager).GetMethod("WireReconnectRequested",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(EmbeddedSshView), typeof(Heimdall.App.ViewModels.SessionTabViewModel)], null)!
                .Invoke(manager, [view, new Heimdall.App.ViewModels.SessionTabViewModel { ServerId = "other-primary" }]);
            Border overlay = Assert.IsType<Border>(view.FindName("ReconnectOverlay"));
            overlay.Visibility = System.Windows.Visibility.Visible;
            Button button = Assert.IsType<Button>(view.FindName("OverlayEditProfileButton"));
            button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("saved-ssh", edited);
            Assert.Equal(System.Windows.Visibility.Visible, overlay.Visibility);
            edited = null;
            view.Dispose();
            button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert.Null(edited);
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void GatewayAttestationFailure_SetsLocalizedErrorStatusWithoutModal()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            var view = new EmbeddedRdpView();
            SetPrivateField(view, "_localizer", WpfTestHost.Localizer);
            var exception = new RdpGatewayAttestationException(
                "gateway.example.com",
                RdpGatewayAttestationStep.SettingsComparison);

            InvokePrivateMethod(view, "HandleGatewayAttestationFailure", exception);

            var statusTextBlock = Assert.IsType<TextBlock>(view.FindName("StatusTextBlock"));
            Assert.Equal("Error", GetPrivateField(view, "_sessionStatus")?.ToString());
            Assert.Contains(
                WpfTestHost.Localizer["RdpGatewayAttestationFailed"],
                statusTextBlock.Text,
                StringComparison.Ordinal);
            Assert.Contains(exception.Message, statusTextBlock.Text, StringComparison.Ordinal);
            view.Dispose();
        });
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(target, value);
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return field.GetValue(target);
    }

    private static void InvokePrivateMethod(object target, string methodName, object argument)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");
        method.Invoke(target, [argument]);
    }
}
