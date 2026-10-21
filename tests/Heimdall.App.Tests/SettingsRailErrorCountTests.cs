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

using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using Heimdall.App.Controls;

namespace Heimdall.App.Tests;

/// <summary>
/// The error badge on a settings rail section counts the fields of that section in error.
/// </summary>
/// <remarks>
/// The top tab badge said which tab held an error but not which of its sections, so the user
/// opened sections one by one to find the red box.
/// </remarks>
public sealed class SettingsRailErrorCountTests
{
    private const string Prefix = "Settings.";

    [Fact]
    public void ASectionCountsItsSettingsBoxesInError_EvenOnesNotOnScreen()
    {
        RunOnStaThread(() =>
        {
            StackPanel section = new();
            section.Children.Add(Box(Prefix + "BrokenText"));
            section.Children.Add(Box(Prefix + "FineText"));
            section.Children.Add(Box("Other.BrokenText"));

            // A nested section that is not selected is still counted: its content is in the
            // logical tree, which is what the walk reads.
            TabControl nested = new();
            nested.Items.Add(new TabItem { Content = new TextBlock() });
            StackPanel hidden = new();
            hidden.Children.Add(Box(Prefix + "BrokenRange"));
            nested.Items.Add(new TabItem { Content = hidden });
            section.Children.Add(nested);

            int count = SettingsRail.CountFieldErrors(
                section,
                property => property.StartsWith("Broken", StringComparison.Ordinal),
                Prefix);

            Assert.Equal(2, count);
        });
    }

    /// <summary>
    /// The sections counted and searched are the rail items that hold content, in rail order; a
    /// group heading holds none and is never one of them.
    /// </summary>
    [Fact]
    public void TheRailSectionsAreItsItemsWithContent_AndAGroupHeadingIsNotOne()
    {
        RunOnStaThread(() =>
        {
            TabControl rail = new();
            TabItem general = new() { Header = "General" };
            TabItem appearance = new() { Content = new StackPanel() };
            TabItem ssh = new() { Header = "SSH" };
            TabItem gateways = new() { Content = new StackPanel() };
            rail.Items.Add(general);
            rail.Items.Add(appearance);
            rail.Items.Add(ssh);
            rail.Items.Add(gateways);

            List<TabItem> sections = MainWindow.SettingsRailSections(rail).ToList();

            Assert.Equal([appearance, gateways], sections);
        });
    }

    /// <summary>
    /// The first section of a group brings its heading into view with it; any other section
    /// brings no heading.
    /// </summary>
    [Fact]
    public void TheFirstSectionOfAGroupNamesTheHeadingAboveIt()
    {
        RunOnStaThread(() =>
        {
            TabControl rail = new();
            TabItem general = new() { Header = "General" };
            TabItem appearance = new() { Content = new StackPanel() };
            TabItem behavior = new() { Content = new StackPanel() };
            rail.Items.Add(general);
            rail.Items.Add(appearance);
            rail.Items.Add(behavior);

            Assert.Same(general, MainWindow.SettingsRailHeadingAbove(rail, appearance));
            Assert.Null(MainWindow.SettingsRailHeadingAbove(rail, behavior));
        });
    }

    private static TextBox Box(string path)
    {
        TextBox box = new();
        BindingOperations.SetBinding(box, TextBox.TextProperty, new Binding(path));
        return box;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
