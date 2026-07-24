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
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WpfBinding = System.Windows.Data.Binding;

namespace Heimdall.App.Behaviors;

/// <summary>
/// Raises a UI Automation live-region event after a bound text value becomes visible.
/// This covers controls that leave the automation tree while an update is pending.
/// </summary>
public static class LiveRegionBehavior
{
    public static readonly DependencyProperty AnnounceOnTargetUpdatedProperty =
        DependencyProperty.RegisterAttached(
            "AnnounceOnTargetUpdated",
            typeof(bool),
            typeof(LiveRegionBehavior),
            new PropertyMetadata(false, OnAnnounceOnTargetUpdatedChanged));

    private static readonly DependencyProperty LastAnnouncedTextProperty =
        DependencyProperty.RegisterAttached(
            "LastAnnouncedText",
            typeof(string),
            typeof(LiveRegionBehavior));

    private static readonly DependencyProperty IsAnnouncementQueuedProperty =
        DependencyProperty.RegisterAttached(
            "IsAnnouncementQueued",
            typeof(bool),
            typeof(LiveRegionBehavior));

    public static void SetAnnounceOnTargetUpdated(DependencyObject element, bool value) =>
        element.SetValue(AnnounceOnTargetUpdatedProperty, value);

    public static bool GetAnnounceOnTargetUpdated(DependencyObject element) =>
        (bool)element.GetValue(AnnounceOnTargetUpdatedProperty);

    private static void OnAnnounceOnTargetUpdatedChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            WpfBinding.RemoveTargetUpdatedHandler(element, OnTargetUpdated);
            element.IsVisibleChanged -= OnIsVisibleChanged;
            element.Loaded -= OnLoaded;
        }

        if ((bool)e.NewValue)
        {
            WpfBinding.AddTargetUpdatedHandler(element, OnTargetUpdated);
            element.IsVisibleChanged += OnIsVisibleChanged;
            element.Loaded += OnLoaded;
            QueueAnnouncement(element);
        }
        else
        {
            element.ClearValue(LastAnnouncedTextProperty);
            element.ClearValue(IsAnnouncementQueuedProperty);
        }
    }

    private static void OnTargetUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            QueueAnnouncement(element);
        }
    }

    private static void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && (bool)e.NewValue)
        {
            QueueAnnouncement(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            QueueAnnouncement(element);
        }
    }

    private static void QueueAnnouncement(FrameworkElement element)
    {
        if ((bool)element.GetValue(IsAnnouncementQueuedProperty))
        {
            return;
        }

        element.SetValue(IsAnnouncementQueuedProperty, true);
        _ = element.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => AnnounceIfChanged(element));
    }

    private static void AnnounceIfChanged(FrameworkElement element)
    {
        element.SetValue(IsAnnouncementQueuedProperty, false);
        if (!GetAnnounceOnTargetUpdated(element) || !element.IsVisible)
        {
            return;
        }

        string announcement = AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(announcement) && element is TextBlock textBlock)
        {
            announcement = textBlock.Text;
        }

        string? previous = (string?)element.GetValue(LastAnnouncedTextProperty);
        if (string.IsNullOrWhiteSpace(announcement)
            || string.Equals(announcement, previous, StringComparison.Ordinal))
        {
            return;
        }

        AutomationPeer? peer =
            FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        if (peer is null)
        {
            return;
        }

        element.SetValue(LastAnnouncedTextProperty, announcement);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
