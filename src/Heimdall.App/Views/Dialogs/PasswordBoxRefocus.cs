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
using System.Windows.Threading;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Gives a dialog's password box the keyboard focus back, selected, when the view-model asks.
/// </summary>
/// <remarks>
/// The box is disabled while an attempt runs and a disabled element loses the focus for good, so
/// after a failed attempt the next keystroke went nowhere. The view-model decides when
/// (<see cref="PasswordRefocusSignal"/>: the box is enabled again); this only performs it, after
/// the pending binding updates have run so the box really is enabled by then.
/// </remarks>
internal static class PasswordBoxRefocus
{
    /// <summary>
    /// Focuses and selects <paramref name="box"/> each time the signal of the dialog's view-model
    /// fires. Called from the dialog's constructor: the view-model arrives later, with the data
    /// context, and is picked up then.
    /// </summary>
    internal static void Attach(Window dialog, PasswordBox box)
    {
        dialog.DataContextChanged += (_, e) =>
        {
            if (e.NewValue is IPasswordRefocusHost host)
            {
                host.PasswordRefocus.Requested += (_, _) => FocusLater(box);
            }
        };
    }

    private static void FocusLater(PasswordBox box)
    {
        box.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!box.IsEnabled)
                {
                    return;
                }

                box.Focus();
                box.SelectAll();
            });
    }
}
