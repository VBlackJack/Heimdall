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
