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
using System.Windows.Controls;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests;

/// <summary>
/// Pins the layout and the keyboard defaults of the multi-answer question, which the export
/// scope choice uses: one named button per answer, the default answer on Enter, and Escape on
/// a cancel button that answers nothing.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class MessageDialogChoiceTests
{
    [StaFact]
    public void EachAnswerGetsANamedButton_TheDefaultTakesEnter_AndCancelTakesEscape()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                dialog.ConfigureChoiceButtons(["Mine", "Filtered", "All"], 0, "Cancel");

                var panel = (Panel)dialog.FindName("ButtonPanel");
                var visible = panel.Children.OfType<Button>()
                    .Where(button => button.Visibility == Visibility.Visible)
                    .ToList();

                Assert.Equal(
                    ["Cancel", "Mine", "Filtered", "All"],
                    visible.Select(button => (string)button.Content).ToList());
                Assert.Equal(
                    ["Cancel", "Mine", "Filtered", "All"],
                    visible.Select(AutomationProperties.GetName).ToList());
                Assert.True(visible[0].IsCancel);
                Assert.Equal(["Mine"], visible.Where(button => button.IsDefault).Select(button => (string)button.Content).ToList());
                Assert.Null(dialog.ChoiceResult);
            }
            finally
            {
                dialog.Close();
            }
        });
    }
}
