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
using System.Windows.Input;

namespace Heimdall.App.Controls;

/// <summary>
/// Decides when the edit form takes the keyboard: once, when a new edit begins, and never when the
/// form merely becomes visible again (a filter hiding and showing the row, the sidebar collapsed
/// and expanded), where the next keystroke would replace the selected name.
/// </summary>
internal sealed class EditFocusGate
{
    private bool _pending;

    /// <summary>A new edit has begun: the next time the form can be seen, it takes the focus.</summary>
    public void Arm() => _pending = true;

    /// <summary>The edit is gone: nothing is owed any more.</summary>
    public void Disarm() => _pending = false;

    /// <summary>Whether the form takes the focus now; true at most once per <see cref="Arm"/>.</summary>
    public bool TryConsume(bool isVisible)
    {
        if (!_pending || !isVisible)
        {
            return false;
        }

        _pending = false;
        return true;
    }
}

/// <summary>
/// The edit form of the properties panel. Every rule and every transition lives in its view model;
/// this code only puts the cursor in the name box when a new edit begins.
/// </summary>
public partial class SessionPropertiesEditView : UserControl
{
    private readonly EditFocusGate _focusGate = new();

    public SessionPropertiesEditView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ViewModels.SessionPropertiesEditViewModel)
        {
            _focusGate.Arm();
            FocusNameIfOwed();
        }
        else
        {
            _focusGate.Disarm();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            FocusNameIfOwed();
        }
    }

    private void FocusNameIfOwed()
    {
        if (!_focusGate.TryConsume(IsVisible))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Spe_NameBox.Focus();
            Keyboard.Focus(Spe_NameBox);
            Spe_NameBox.SelectAll();
        });
    }
}
