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
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests.Dialogs;

[Collection(DesktopUiCollection.Name)]
public sealed class PasswordInputCancellationTests
{
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Cancellation_ClosesVisibleDialogAndClearsEnteredResponse()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            WpfDialogService service = new(WpfTestHost.Localizer, null!, null!);
            using CancellationTokenSource cancellation = new();
            PasswordInputDialog? observed = null;
            PasswordBox? entry = null;
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                observed = Application.Current.Windows.OfType<PasswordInputDialog>().Single();
                Assert.True(observed.IsVisible);
                entry = Assert.IsType<PasswordBox>(observed.FindName("PasswordBox"));
                entry.Password = "654321";
                cancellation.Cancel();
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                _ = service.ShowPasswordInputAsync("SSH authentication", "Verification code:", cancellation.Token);
            });

            Assert.NotNull(observed);
            Assert.False(observed.IsVisible);
            Assert.NotNull(entry);
            Assert.Equal(string.Empty, entry.Password);
            Assert.Null(observed.ResultPassword);
        });
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Submit_ReturnsResponseAndClearsVisiblePasswordBox()
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            WpfDialogService service = new(WpfTestHost.Localizer, null!, null!);
            PasswordBox? entry = null;
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PasswordInputDialog dialog = Application.Current.Windows.OfType<PasswordInputDialog>().Single();
                entry = Assert.IsType<PasswordBox>(dialog.FindName("PasswordBox"));
                entry.Password = "123456";
                Assert.IsType<Button>(dialog.FindName("OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            Task<string?> result = service.ShowPasswordInputAsync("SSH authentication", "Verification code:");

            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal("123456", result.GetAwaiter().GetResult());
            Assert.NotNull(entry);
            Assert.Equal(string.Empty, entry.Password);
        });
    }
}
