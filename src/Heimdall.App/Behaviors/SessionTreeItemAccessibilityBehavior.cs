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
/// Applies UI Automation metadata to recyclable session-tree containers.
/// </summary>
/// <remarks>
/// <b>This used to cover folders only, on a premise that was false.</b> It read "server
/// containers retain the metadata already supplied by their data template" - but a data
/// template sets <c>AutomationProperties.Name</c> on a <c>Border</c>, which has no automation
/// peer, so that binding is inert. The container therefore fell back to <c>ToString()</c> of
/// the bound item and announced <c>Heimdall.App.ViewModels.ServerItemViewModel</c> to a
/// screen reader. Measured through a live UI Automation client on 2026-08-23; a source-level
/// oracle would have stayed green, because the markup it looks for is present and useless.
/// <para>
/// The name of this type carried the same false premise, which is why it changed with the
/// fix: a behaviour called <c>FolderTree...</c> is one nobody thinks to check for servers.
/// </para>
/// </summary>
public static class SessionTreeItemAccessibilityBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SessionTreeItemAccessibilityBehavior),
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

        if (!GetIsEnabled(item))
        {
            Clear(item);
            return;
        }

        switch (item.DataContext)
        {
            case FolderViewModel:
                item.SetBinding(
                    AutomationProperties.NameProperty,
                    new WpfBinding(nameof(FolderViewModel.AccessibleName)));
                item.SetBinding(
                    AutomationProperties.HelpTextProperty,
                    new WpfBinding(nameof(FolderViewModel.AccessibleHelpText)));
                break;

            case ServerItemViewModel:
                // Name only. There is no server help text to bind, and binding the property
                // to nothing would be worse than leaving it cleared.
                item.SetBinding(
                    AutomationProperties.NameProperty,
                    new WpfBinding(nameof(ServerItemViewModel.AccessibleName)));
                break;

            default:
                // An item type nobody taught this behaviour about announces its class name.
                // Clearing is not a fix for that - it is what the fallback does anyway - but
                // it keeps a recycled container from carrying the previous item's identity.
                Clear(item);
                break;
        }
    }

    private static void Clear(TreeViewItem item)
    {
        item.ClearValue(AutomationProperties.NameProperty);
        item.ClearValue(AutomationProperties.HelpTextProperty);
    }
}
