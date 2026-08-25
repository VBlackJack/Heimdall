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
using System.Windows.Data;
using Heimdall.App.ViewModels;
using WpfBinding = System.Windows.Data.Binding;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Applies UI Automation metadata to recyclable item containers.
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
/// The name of this type has now changed twice for the same reason: a behaviour called
/// <c>FolderTree...</c> is one nobody thinks to check for servers, and one called
/// <c>SessionTree...</c> is one nobody thinks to check for a command palette. It carries no
/// container type in its name any more, and it accepts any <see cref="FrameworkElement"/>
/// container, so "does this list announce its rows?" has one answer to look up rather than
/// one per control.
/// </para>
/// <para>
/// Eligibility is a contract, not a type list: an item announces itself by implementing
/// <see cref="IAccessibleItemViewModel"/>. The previous switch over concrete view-model types
/// silently ignored anything it had not been taught, which is the same failure mode wearing a
/// different shape.
/// </para>
/// </remarks>
public static class ItemContainerAccessibilityBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ItemContainerAccessibilityBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        // Any generated container qualifies: TreeViewItem, ListBoxItem, DataGridRow. The
        // previous TreeViewItem-only guard meant that enabling this on a ListBox did nothing
        // at all, and did it quietly.
        if (dependencyObject is not FrameworkElement container)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            container.DataContextChanged -= OnDataContextChanged;
        }

        if ((bool)e.NewValue)
        {
            container.DataContextChanged += OnDataContextChanged;
        }

        ApplyMetadata(container);
    }

    private static void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement container)
        {
            ApplyMetadata(container);
        }
    }

    private static void ApplyMetadata(FrameworkElement container)
    {
        BindingOperations.ClearBinding(container, AutomationProperties.NameProperty);
        BindingOperations.ClearBinding(container, AutomationProperties.HelpTextProperty);

        if (!GetIsEnabled(container) || container.DataContext is not IAccessibleItemViewModel item)
        {
            // An item that does not implement the contract announces its class name. Clearing
            // is not a fix for that - it is what the fallback does anyway - but it keeps a
            // recycled container from carrying the previous item's identity.
            Clear(container);
            return;
        }

        container.SetBinding(
            AutomationProperties.NameProperty,
            new WpfBinding(nameof(IAccessibleItemViewModel.AccessibleName)));

        if (item.AccessibleHelpText is null)
        {
            // Binding the property to nothing would be worse than leaving it cleared.
            container.ClearValue(AutomationProperties.HelpTextProperty);
            return;
        }

        container.SetBinding(
            AutomationProperties.HelpTextProperty,
            new WpfBinding(nameof(IAccessibleItemViewModel.AccessibleHelpText)));
    }

    private static void Clear(FrameworkElement container)
    {
        container.ClearValue(AutomationProperties.NameProperty);
        container.ClearValue(AutomationProperties.HelpTextProperty);
    }
}
