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
using System.Windows.Input;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Runs a command each time an element is loaded into a window: a section that is costly to fill
/// reads its data when it is first shown rather than when the application starts. The command is
/// expected to do nothing the second time.
/// </summary>
public static class LoadWhenShownBehavior
{
    /// <summary>The command to run when the element is loaded.</summary>
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(LoadWhenShownBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    private static void OnCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        if (eventArgs.NewValue is null)
        {
            return;
        }

        element.Loaded += OnLoaded;
        if (element.IsLoaded)
        {
            Run(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Run((FrameworkElement)sender);

    private static void Run(FrameworkElement element)
    {
        ICommand? command = GetCommand(element);
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
