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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// Five dialogs opened with the keyboard on nothing: a screen reader announced the window and no
/// control, and the first Tab landed wherever the layout began. Each takes the focus when it loads,
/// on the first field or on the button that cannot do harm.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class DialogInitialFocusTests
{
    private const string User32Library = "user32.dll";

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void NewTunnelDialog_FocusesTheGatewayList()
        => AssertFocus(
            () => new NewTunnelDialog { DataContext = new NewTunnelDialogViewModel([Gateway()], WpfTestHost.Localizer) },
            "GatewayComboBox");

    /// <summary>Without a gateway the whole form is disabled, and the keyboard goes to Cancel.</summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void NewTunnelDialog_WithoutGateways_FocusesCancel()
        => AssertFocus(
            () => new NewTunnelDialog { DataContext = new NewTunnelDialogViewModel([], WpfTestHost.Localizer) },
            "CancelButton");

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void PasteConfirmDialog_FocusesCancel()
        => AssertFocus(() => new PasteConfirmDialog(), "CancelButton");

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void MaskedCopyDialog_FocusesCopy()
        => AssertFocus(() => new MaskedCopyDialog(), "CopyButton");

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TrustedHostKeyDetailsDialog_FocusesClose()
        => AssertFocus(() => new TrustedHostKeyDetailsDialog(), "CloseButton");

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SharedSessionImportReportDialog_FocusesClose()
        => AssertFocus(() => new SharedSessionImportReportDialog(), "CloseButton");

    private static SshGatewayDto Gateway()
        => new() { Id = "gw-1", Name = "Bastion", Host = "bastion.example", Port = 22, User = "ops" };

    private static void AssertFocus(Func<Window> create, string expectedName)
    {
        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.ResetLocale();
            Window dialog = create();
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = 100;
            dialog.Top = 100;
            dialog.ShowActivated = true;
            try
            {
                dialog.Show();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

                FrameworkElement expected = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName(expectedName));

                // Logical focus is set by the dialog itself and does not depend on which process owns the foreground.
                Assert.True(
                    ReferenceEquals(expected, FocusManager.GetFocusedElement(dialog)),
                    $"Expected the logical focus on {expectedName}, found "
                    + (FocusManager.GetFocusedElement(dialog) is FrameworkElement logical
                        ? $"{logical.GetType().Name} '{logical.Name}'"
                        : FocusManager.GetFocusedElement(dialog)?.GetType().Name ?? "nothing")
                    + ".");

                // Keyboard focus only holds while the dialog window has Win32 focus on this thread; that focus is
                // per thread and needs no foreground, so it is claimed here instead of relying on the desktop.
                IntPtr dialogHandle = new WindowInteropHelper(dialog).Handle;
                SetFocus(dialogHandle);
                Assert.True(
                    dialogHandle == GetFocus(),
                    "The dialog window did not take this thread's Win32 focus (GetFocus returned " + GetFocus() + ", dialog handle " + dialogHandle + ").");
                Assert.True(
                    ReferenceEquals(expected, Keyboard.FocusedElement),
                    $"Expected the keyboard on {expectedName} (enabled {expected.IsEnabled}, visible {expected.IsVisible}, focusable {expected.Focusable}), found "
                    + (Keyboard.FocusedElement is FrameworkElement found
                        ? $"{found.GetType().Name} '{found.Name}'"
                        : Keyboard.FocusedElement?.GetType().Name ?? "nothing")
                    + ".");
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [DllImport(User32Library)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport(User32Library)]
    private static extern IntPtr GetFocus();
}
