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
using Heimdall.App.ViewModels;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Converts a genuine inline-editor keyboard-focus loss into a commit request.
/// </summary>
public static class InlineRenameFocusBehavior
{
    /// <summary>Enables focus-loss commit requests on an inline rename editor.</summary>
    public static readonly DependencyProperty CommitOnLostKeyboardFocusProperty =
        DependencyProperty.RegisterAttached(
            "CommitOnLostKeyboardFocus",
            typeof(bool),
            typeof(InlineRenameFocusBehavior),
            new PropertyMetadata(false, OnCommitOnLostKeyboardFocusChanged));

    /// <summary>Raised when an enabled, active inline editor loses keyboard focus.</summary>
    public static readonly RoutedEvent CommitRequestedEvent =
        EventManager.RegisterRoutedEvent(
            "CommitRequested",
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(InlineRenameFocusBehavior));

    /// <summary>Sets whether focus loss requests an inline rename commit.</summary>
    public static void SetCommitOnLostKeyboardFocus(DependencyObject element, bool value) =>
        element.SetValue(CommitOnLostKeyboardFocusProperty, value);

    /// <summary>Gets whether focus loss requests an inline rename commit.</summary>
    public static bool GetCommitOnLostKeyboardFocus(DependencyObject element) =>
        (bool)element.GetValue(CommitOnLostKeyboardFocusProperty);

    /// <summary>Adds a handler for inline rename commit requests.</summary>
    public static void AddCommitRequestedHandler(
        DependencyObject element,
        RoutedEventHandler handler) =>
        ((UIElement)element).AddHandler(CommitRequestedEvent, handler);

    /// <summary>Removes a handler for inline rename commit requests.</summary>
    public static void RemoveCommitRequestedHandler(
        DependencyObject element,
        RoutedEventHandler handler) =>
        ((UIElement)element).RemoveHandler(CommitRequestedEvent, handler);

    private static void OnCommitOnLostKeyboardFocusChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not WpfTextBox editor)
        {
            return;
        }

        if ((bool)eventArgs.OldValue)
        {
            editor.LostKeyboardFocus -= OnLostKeyboardFocus;
        }

        if ((bool)eventArgs.NewValue)
        {
            editor.LostKeyboardFocus += OnLostKeyboardFocus;
        }
    }

    private static void OnLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs eventArgs)
    {
        if (sender is not WpfTextBox
            {
                IsEnabled: true,
                DataContext: IInlineRenameNode { IsEditing: true },
            } editor
            || !GetCommitOnLostKeyboardFocus(editor))
        {
            return;
        }

        editor.RaiseEvent(new RoutedEventArgs(CommitRequestedEvent, editor));
    }
}
