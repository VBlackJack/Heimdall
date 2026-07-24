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
using System.Windows.Data;
using Heimdall.App.ViewModels;
using WpfBinding = System.Windows.Data.Binding;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Applies folder-only UI Automation metadata to recyclable session-tree containers.
/// Server containers retain the metadata already supplied by their data template.
/// </summary>
public static class FolderTreeItemAccessibilityBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(FolderTreeItemAccessibilityBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TreeViewItem item)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            item.DataContextChanged -= OnDataContextChanged;
        }

        if ((bool)e.NewValue)
        {
            item.DataContextChanged += OnDataContextChanged;
        }

        ApplyMetadata(item);
    }

    private static void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            ApplyMetadata(item);
        }
    }

    private static void ApplyMetadata(TreeViewItem item)
    {
        BindingOperations.ClearBinding(item, AutomationProperties.NameProperty);
        BindingOperations.ClearBinding(item, AutomationProperties.HelpTextProperty);

        if (!GetIsEnabled(item) || item.DataContext is not FolderViewModel)
        {
            item.ClearValue(AutomationProperties.NameProperty);
            item.ClearValue(AutomationProperties.HelpTextProperty);
            return;
        }

        item.SetBinding(
            AutomationProperties.NameProperty,
            new WpfBinding(nameof(FolderViewModel.AccessibleName)));
        item.SetBinding(
            AutomationProperties.HelpTextProperty,
            new WpfBinding(nameof(FolderViewModel.AccessibleHelpText)));
    }
}
