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
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests.Dialogs;

/// <summary>
/// The remember choice of the password dialog: offered only where a password may be kept, unticked
/// until the user ticks it, and absent from every other password question.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class PasswordInputRememberTests
{
    private const string RememberLabel = "Remember this password on this computer";

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void PlainQuestion_OffersNoRememberChoice()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            WpfDialogService service = new(WpfTestHost.Localizer, null!, null!);
            CheckBox? remember = null;
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PasswordInputDialog dialog = Application.Current.Windows.OfType<PasswordInputDialog>().Single();
                remember = Assert.IsType<CheckBox>(dialog.FindName("RememberCheckBox"));
                Assert.IsType<PasswordBox>(dialog.FindName("PasswordBox")).Password = "123456";
                Assert.IsType<Button>(dialog.FindName("OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            Task<string?> result = service.ShowPasswordInputAsync("SSH authentication", "Verification code:");

            Assert.Equal("123456", result.GetAwaiter().GetResult());
            Assert.NotNull(remember);
            Assert.Equal(Visibility.Collapsed, remember.Visibility);
        });
    }

    [StaTheory]
    [Trait("Category", "RequiresDesktop")]
    [InlineData(true)]
    [InlineData(false)]
    public void RememberQuestion_StartsUnticked_AndReturnsTheChoice(bool tick)
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            WpfDialogService service = new(WpfTestHost.Localizer, null!, null!);
            bool? startedTicked = null;
            string? accessibleName = null;
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PasswordInputDialog dialog = Application.Current.Windows.OfType<PasswordInputDialog>().Single();
                CheckBox remember = Assert.IsType<CheckBox>(dialog.FindName("RememberCheckBox"));
                Assert.Equal(Visibility.Visible, remember.Visibility);
                startedTicked = remember.IsChecked;
                accessibleName = AutomationProperties.GetName(remember);
                remember.IsChecked = tick;
                Assert.IsType<PasswordBox>(dialog.FindName("PasswordBox")).Password = "secret-1";
                Assert.IsType<Button>(dialog.FindName("OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            PasswordPromptResult? result = service
                .ShowPasswordInputWithRememberAsync("SSH password required", "Enter the password.", RememberLabel)
                .GetAwaiter()
                .GetResult();

            Assert.NotNull(result);
            Assert.Equal("secret-1", result.Password);
            Assert.Equal(tick, result.Remember);
            Assert.False(startedTicked == true);
            Assert.Equal(RememberLabel, accessibleName);
        });
    }
}
