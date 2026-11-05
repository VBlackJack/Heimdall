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
using System.Windows.Threading;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Gives the keyboard focus to an element when a view-model asks for it, then clears the request:
/// bind <c>IsRequested</c> two-way to a view-model flag.
/// </summary>
/// <remarks>
/// The focus is given once the element is loaded and after the pending layout, so a row that has
/// just been added to a list is focused once it exists on screen.
/// </remarks>
public static class FocusRequestBehavior
{
    /// <summary>Whether the element should take the keyboard focus; cleared once it has.</summary>
    public static readonly DependencyProperty IsRequestedProperty =
        DependencyProperty.RegisterAttached(
            "IsRequested",
            typeof(bool),
            typeof(FocusRequestBehavior),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsRequestedChanged));

    public static void SetIsRequested(DependencyObject element, bool value) =>
        element.SetValue(IsRequestedProperty, value);

    public static bool GetIsRequested(DependencyObject element) =>
        (bool)element.GetValue(IsRequestedProperty);

    private static void OnIsRequestedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element || eventArgs.NewValue is not true)
        {
            return;
        }

        if (element.IsLoaded)
        {
            FocusLater(element);
            return;
        }

        element.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnLoaded;
        if (GetIsRequested(element))
        {
            FocusLater(element);
        }
    }

    private static void FocusLater(FrameworkElement element) =>
        element.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                Keyboard.Focus(element);
                element.SetCurrentValue(IsRequestedProperty, false);
            });
}
