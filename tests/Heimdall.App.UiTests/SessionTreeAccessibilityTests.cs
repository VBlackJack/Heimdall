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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using FlaUI.UIA3;
using Heimdall.App.Behaviors;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using WpfBinding = System.Windows.Data.Binding;

namespace Heimdall.App.UiTests;

// These tests drive real UI Automation through FlaUI: UIA3Automation, FromHandle on a live window
// handle, and cross-process LiveRegionChanged event delivery. That needs an interactive desktop
// session, which is exactly what the RequiresDesktop lane exists for, and it is contended on a CI
// runner. The trait was missing here while every comparable class in this project carries it.
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class SessionTreeAccessibilityTests
{
    [StaFact]
    public void FilterCount_LiveRegionAnnouncesLatestTextAfterBecomingVisible()
    {
        TextSource? source = null;
        TextBlock? count = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;
        WpfTestHost.Invoke(() =>
        {
            source = new TextSource { Value = "1 / 3 sessions" };
            count = new TextBlock();
            AutomationProperties.SetAutomationId(count, "FilterResultCount");
            AutomationProperties.SetLiveSetting(count, AutomationLiveSetting.Polite);
            BindingOperations.SetBinding(
                count,
                TextBlock.TextProperty,
                new WpfBinding(nameof(TextSource.Value))
                {
                    Source = source,
                    NotifyOnTargetUpdated = true
                });
            BindingOperations.SetBinding(
                count,
                AutomationProperties.NameProperty,
                new WpfBinding(nameof(TextSource.Value))
                {
                    Source = source
                });
            LiveRegionBehavior.SetAnnounceOnTargetUpdated(count, true);
            window = Show(count);
            windowHandle = new WindowInteropHelper(window).Handle;
        });
        WpfTestHost.Invoke(DrainDispatcher);

        var announcements = new ConcurrentQueue<string>();
        using var automation = new UIA3Automation();
        FlaUI.Core.AutomationElements.AutomationElement root =
            automation.FromHandle(windowHandle);
        FlaUI.Core.AutomationElements.AutomationElement countElement =
            Assert.IsAssignableFrom<FlaUI.Core.AutomationElements.AutomationElement>(
                root.FindFirstDescendant(
                    condition => condition.ByAutomationId("FilterResultCount")));
        using var eventHandler = countElement.RegisterAutomationEvent(
            automation.EventLibrary.Element.LiveRegionChangedEvent,
            FlaUI.Core.Definitions.TreeScope.Element,
            (element, _) => announcements.Enqueue(element.Name));
        try
        {
            WpfTestHost.Invoke(() => count!.Visibility = Visibility.Collapsed);
            WpfTestHost.Invoke(() => source!.Value = "2 / 3 sessions");
            WpfTestHost.Invoke(DrainDispatcher);
            Assert.Empty(announcements);

            WpfTestHost.Invoke(() => count!.Visibility = Visibility.Visible);
            WaitForAutomationEvent(announcements);

            Assert.Equal(
                AutomationLiveSetting.Polite,
                WpfTestHost.Invoke(() => AutomationProperties.GetLiveSetting(count!)));
            Assert.NotEmpty(announcements);
            Assert.All(
                announcements,
                announcement => Assert.Equal("2 / 3 sessions", announcement));
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    [StaFact]
    public void GlobalStatus_XamlContractRaisesLiveRegionEventForVisibleBindingChange()
    {
        TextSource? source = null;
        TextBlock? status = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;
        WpfTestHost.Invoke(() =>
        {
            source = new TextSource { Value = "Ready" };
            status = new TextBlock();
            AutomationProperties.SetAutomationId(status, "GlobalStatus");
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            BindingOperations.SetBinding(
                status,
                TextBlock.TextProperty,
                new WpfBinding(nameof(TextSource.Value))
                {
                    Source = source,
                    NotifyOnTargetUpdated = true
                });
            BindingOperations.SetBinding(
                status,
                AutomationProperties.NameProperty,
                new WpfBinding(nameof(TextSource.Value))
                {
                    Source = source
                });
            LiveRegionBehavior.SetAnnounceOnTargetUpdated(status, true);
            window = Show(status);
            windowHandle = new WindowInteropHelper(window).Handle;
        });
        WpfTestHost.Invoke(DrainDispatcher);

        ConcurrentQueue<string> announcements = new();
        using ManualResetEventSlim announcementReceived = new(false);
        using UIA3Automation automation = new();
        FlaUI.Core.AutomationElements.AutomationElement root =
            automation.FromHandle(windowHandle);
        FlaUI.Core.AutomationElements.AutomationElement statusElement =
            Assert.IsAssignableFrom<FlaUI.Core.AutomationElements.AutomationElement>(
                root.FindFirstDescendant(
                    condition => condition.ByAutomationId("GlobalStatus")));
        using IDisposable eventHandler = statusElement.RegisterAutomationEvent(
            automation.EventLibrary.Element.LiveRegionChangedEvent,
            FlaUI.Core.Definitions.TreeScope.Element,
            (element, _) =>
            {
                announcements.Enqueue(element.Name);
                announcementReceived.Set();
            });
        try
        {
            WpfTestHost.Invoke(() => source!.Value = "Connection failed");
            WpfTestHost.Invoke(DrainDispatcher);

            Assert.True(
                announcementReceived.Wait(TimeSpan.FromSeconds(3)),
                "The visible global status did not raise LiveRegionChanged.");
            Assert.Equal(
                AutomationLiveSetting.Polite,
                WpfTestHost.Invoke(() => AutomationProperties.GetLiveSetting(status!)));
            Assert.NotEmpty(announcements);
            Assert.All(
                announcements,
                announcement => Assert.Equal("Connection failed", announcement));
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }

        AssertGlobalStatusXamlContract();
    }

    [StaFact]
    public void FolderContainer_ExposesLocalizedIdentityAndResolvesKeyboardFocus()
    {
        var folder = new FolderViewModel
        {
            Name = "Production",
            FullPath = "Production"
        };
        TreeView? tree = null;
        TreeViewItem? container = null;
        Window? window = null;
        IntPtr windowHandle = IntPtr.Zero;
        WpfTestHost.Invoke(() =>
        {
            tree = new TreeView
            {
                Width = 320,
                Height = 240,
                ItemsSource = new[] { folder }
            };
            var itemStyle = new Style(typeof(TreeViewItem));
            itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, true));
            itemStyle.Setters.Add(new Setter(
                FolderTreeItemAccessibilityBehavior.IsEnabledProperty,
                true));
            tree.ItemContainerStyle = itemStyle;

            window = Show(tree);
            container = Assert.IsType<TreeViewItem>(
                tree.ItemContainerGenerator.ContainerFromItem(folder));
            AutomationProperties.SetAutomationId(container, "ProductionFolder");
            window.Activate();
            Keyboard.Focus(container);
            windowHandle = new WindowInteropHelper(window).Handle;
        });

        try
        {
            AutomationElement root = AutomationElement.FromHandle(windowHandle);
            AutomationElement? folderElement = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    "ProductionFolder"));

            Assert.NotNull(folderElement);
            folderElement.SetFocus();
            WaitForKeyboardFocus(folderElement);
            TreeViewItem? focused = WpfTestHost.Invoke(() =>
                TreeInteractionState.FindFocusedTreeViewItem(
                    tree!,
                    Keyboard.FocusedElement as DependencyObject));

            Assert.Same(container, focused);
            Assert.Equal("Production, folder", folderElement.Current.Name);
            Assert.StartsWith(
                "Folder.",
                folderElement.Current.HelpText,
                StringComparison.Ordinal);
            Assert.True(folderElement.Current.IsKeyboardFocusable);
            Assert.True(folderElement.Current.HasKeyboardFocus);
        }
        finally
        {
            WpfTestHost.Invoke(() => window!.Close());
        }
    }

    private static void WaitForKeyboardFocus(AutomationElement element)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!element.Current.HasKeyboardFocus && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Width = 360,
            Height = 280,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        content.UpdateLayout();
        return window;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForAutomationEvent(ConcurrentQueue<string> announcements)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (announcements.IsEmpty && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    private static void AssertGlobalStatusXamlContract()
    {
        string sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(sourcePath);
        XNamespace presentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace behaviorsNamespace = "clr-namespace:Heimdall.App.Behaviors";
        XElement status = Assert.Single(
            document.Descendants(presentationNamespace + "TextBlock"),
            element =>
                ((string?)element.Attribute("Text"))?.StartsWith(
                    "{Binding StatusText",
                    StringComparison.Ordinal) == true);

        Assert.Equal(
            "{Binding StatusText, NotifyOnTargetUpdated=True}",
            (string?)status.Attribute("Text"));
        Assert.Equal(
            "{Binding StatusText}",
            (string?)status.Attribute("AutomationProperties.Name"));
        Assert.Equal(
            "Polite",
            (string?)status.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal(
            "True",
            (string?)status.Attribute(
                behaviorsNamespace + "LiveRegionBehavior.AnnounceOnTargetUpdated"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class TextSource : INotifyPropertyChanged
    {
        private string _value = "";

        public string Value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal))
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
