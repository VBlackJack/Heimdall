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
using System.Windows.Automation;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins that the accent-filled button of a two-button confirmation is the one Enter presses,
/// and that the names the buttons expose are unchanged by it.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class MessageDialogConfirmStyleTests
{
    private const string AcceptLabel = "Accept";
    private const string DeclineLabel = "Decline";

    [StaFact]
    public void DeclineIsDefault_TheDeclineButtonTakesTheAccent()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                MessageDialog.ConfigureConfirmButtons(dialog, AcceptLabel, DeclineLabel, primaryIsDefault: false);

                Assert.Same(ResolveStyle(MessageDialog.PlainButtonStyleKey), dialog.BtnPrimary.Style);
                Assert.Same(ResolveStyle(MessageDialog.AccentButtonStyleKey), dialog.BtnSecondary.Style);
                Assert.True(dialog.BtnSecondary.IsDefault);
                Assert.False(dialog.BtnPrimary.IsDefault);
                Assert.Equal(AcceptLabel, AutomationProperties.GetName(dialog.BtnPrimary));
                Assert.Equal(DeclineLabel, AutomationProperties.GetName(dialog.BtnSecondary));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaFact]
    public void PrimaryIsDefault_KeepsTheStylesTheXamlDeclares()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                MessageDialog.ConfigureConfirmButtons(dialog, AcceptLabel, DeclineLabel, primaryIsDefault: true);

                Assert.Same(ResolveStyle(MessageDialog.AccentButtonStyleKey), dialog.BtnPrimary.Style);
                Assert.Same(ResolveStyle(MessageDialog.PlainButtonStyleKey), dialog.BtnSecondary.Style);
                Assert.True(dialog.BtnPrimary.IsDefault);
                Assert.Equal(AcceptLabel, AutomationProperties.GetName(dialog.BtnPrimary));
                Assert.Equal(DeclineLabel, AutomationProperties.GetName(dialog.BtnSecondary));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    private static Style ResolveStyle(string key) => (Style)Application.Current.FindResource(key);
}
