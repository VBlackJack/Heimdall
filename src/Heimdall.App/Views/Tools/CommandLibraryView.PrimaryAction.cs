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
using System.Windows;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Gives the primary style to whichever of Copy and Send can actually run.
/// </summary>
/// <remarks>
/// Without a terminal in the tab, Send stays visible and disabled on purpose: its tooltip says
/// why, and a gesture hidden on a state read once cannot be found again when a terminal appears.
/// What changes is the weight. A filled Send that cannot run drew the eye away from Copy, the only
/// button that could. A style is not something a trigger on the button itself can swap, so the
/// view sets it, by resource reference so a theme change still reaches it.
/// </remarks>
public partial class CommandLibraryView
{
    private const string PrimaryButtonStyleKey = "PrimaryButtonStyle";
    private const string SecondaryButtonStyleKey = "SecondaryButtonStyle";

    private CommandLibraryViewModel? _primaryActionSource;

    private void OnDataContextChangedForPrimaryAction(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_primaryActionSource is not null)
        {
            _primaryActionSource.PropertyChanged -= OnViewModelPropertyChangedForPrimaryAction;
        }

        _primaryActionSource = e.NewValue as CommandLibraryViewModel;
        if (_primaryActionSource is not null)
        {
            _primaryActionSource.PropertyChanged += OnViewModelPropertyChangedForPrimaryAction;
        }

        ApplyPrimaryAction();
    }

    private void OnViewModelPropertyChangedForPrimaryAction(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandLibraryViewModel.HasTerminalSink))
        {
            ApplyPrimaryAction();
        }
    }

    private void ApplyPrimaryAction()
    {
        bool sendLeads = _primaryActionSource?.HasTerminalSink ?? false;
        BtnSend.SetResourceReference(StyleProperty, sendLeads ? PrimaryButtonStyleKey : SecondaryButtonStyleKey);
        BtnCopy.SetResourceReference(StyleProperty, sendLeads ? SecondaryButtonStyleKey : PrimaryButtonStyleKey);
    }
}
