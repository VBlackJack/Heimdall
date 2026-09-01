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

using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using Heimdall.App.UiTests.Infrastructure;

namespace Heimdall.App.UiTests;

/// <summary>
/// A keyboard save has to commit the box the caret is in.
/// </summary>
/// <remarks>
/// <para>
/// Clicking Save works without help: the button takes keyboard focus, so the text box fires
/// LostFocus and its binding commits before the command runs. A keystroke moves no focus, so ten
/// settings boxes bound with the default LostFocus trigger would have been written back at their
/// pre-edit value - and the panel's dirty flag cleared regardless, leaving every unsaved-changes
/// signal in the shell claiming there was nothing pending.
/// </para>
/// <para>
/// This lives in the UI test project because it needs a real WPF binding on a real control; it
/// deliberately shows no window, so it cannot reach the shared-dispatcher trouble that hosting one
/// causes here.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class SaveShortcutPendingEditTests
{
    [StaFact]
    public void CommitPendingEdit_PushesTheTypedTextIntoTheSource()
    {
        WpfTestHost.Invoke(() =>
        {
            Target target = new() { Value = "before" };
            TextBox box = BoundBox(target);

            // What typing does: the control's text changes, and a LostFocus binding keeps the
            // source at its old value until focus moves.
            box.Text = "after";
            Assert.Equal("before", target.Value);

            MainWindow.CommitPendingEdit(box);

            Assert.Equal("after", target.Value);
        });
    }

    /// <summary>
    /// The rule must not throw on anything else that can hold focus, or a save gesture from a
    /// checkbox or a combo would take the window down instead of saving.
    /// </summary>
    [StaFact]
    public void CommitPendingEdit_IgnoresWhatItCannotCommit()
    {
        WpfTestHost.Invoke(() =>
        {
            MainWindow.CommitPendingEdit(null);
            MainWindow.CommitPendingEdit(new CheckBox());
            MainWindow.CommitPendingEdit(new ComboBox());

            // An unbound box is the case a naive implementation dereferences.
            MainWindow.CommitPendingEdit(new TextBox { Text = "loose" });
        });
    }

    private static TextBox BoundBox(Target target)
    {
        TextBox box = new() { DataContext = target };
        box.SetBinding(
            TextBox.TextProperty,
            new Binding(nameof(Target.Value))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            });

        return box;
    }

    private sealed class Target : INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }
}
