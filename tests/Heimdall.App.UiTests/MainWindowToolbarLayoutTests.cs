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

using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Heimdall.App.UiTests;

/// <summary>
/// The toolbar must be able to give way as the window narrows, because it is what currently forces
/// the main window's minimum width above the logical work area of a small or scaled display.
/// </summary>
/// <remarks>
/// Two oracles, deliberately: the structural one pins the real <c>MainWindow.xaml</c>, and the
/// behavioural one pins WHY that structure is the one that works. The subtlety worth freezing is
/// that a <c>WrapPanel</c> only wraps when something upstream bounds its width - inside a
/// <c>DockPanel</c>, or in an <c>Auto</c> column, it is measured with infinite width and silently
/// lays every child on one line. A refactor could keep the WrapPanel and lose the wrapping.
/// </remarks>
public sealed class MainWindowToolbarLayoutTests
{
    private const double NarrowWidth = 360;
    private const double WideWidth = 1400;

    [StaFact]
    public void ToolbarNavigation_AtNarrowWidth_WrapsInsteadOfOverflowing()
    {
        Grid toolbar = BuildToolbarLayout(out WrapPanel navigation, out _);

        toolbar.Measure(new Size(WideWidth, double.PositiveInfinity));
        double wideHeight = navigation.DesiredSize.Height;

        toolbar.Measure(new Size(NarrowWidth, double.PositiveInfinity));
        double narrowHeight = navigation.DesiredSize.Height;

        // Wrapping shows up as extra rows, and as a navigation region that stays inside what it was
        // offered rather than reporting a width the window would have to grow to satisfy.
        Assert.True(
            narrowHeight > wideHeight,
            $"Navigation did not wrap: height {narrowHeight} at {NarrowWidth}px vs {wideHeight} at {WideWidth}px.");
        Assert.True(
            navigation.DesiredSize.Width <= NarrowWidth,
            $"Navigation asked for {navigation.DesiredSize.Width}px inside {NarrowWidth}px.");
    }

    [StaFact]
    public void Toolbar_AtNarrowWidth_KeepsTheShortcutsAtFullSize()
    {
        Grid toolbar = BuildToolbarLayout(out _, out StackPanel shortcuts);

        toolbar.Measure(new Size(WideWidth, double.PositiveInfinity));
        double wideShortcuts = shortcuts.DesiredSize.Width;

        toolbar.Measure(new Size(NarrowWidth, double.PositiveInfinity));

        // The shortcuts sit in the Auto column: the navigation region is the one that gives way, so
        // the icons never shrink or clip.
        Assert.Equal(wideShortcuts, shortcuts.DesiredSize.Width);
        Assert.True(wideShortcuts > 0);
    }

    [StaFact]
    public void Toolbar_NavigationInAnAutoColumn_WouldNotWrap()
    {
        // The negative control. Same WrapPanel, same children, only the column sizing differs - and
        // it alone decides whether any wrapping happens. Without this, the test above could pass on
        // a layout that wraps for some unrelated reason.
        Grid toolbar = BuildToolbarLayout(out WrapPanel navigation, out _, navigationColumnWidth: GridLength.Auto);

        toolbar.Measure(new Size(NarrowWidth, double.PositiveInfinity));

        Assert.True(
            navigation.DesiredSize.Width > NarrowWidth,
            "An Auto column bounded the navigation region, so this control proves nothing.");
    }

    [Fact]
    public void MainWindowXaml_ToolbarKeepsTheStructureThatAllowsWrapping()
    {
        string xaml = ReadMainWindowXaml();

        // The three clauses that make wrapping possible, each named rather than inferred.
        Assert.Contains("<Grid x:Name=\"Mw_ToolbarLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<ColumnDefinition x:Name=\"Mw_ToolbarNavigationColumn\" Width=\"*\"/>",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("<WrapPanel x:Name=\"Mw_ToolbarNavigation\"", xaml, StringComparison.Ordinal);

        // And the shape that used to defeat it.
        int toolbarStart = xaml.IndexOf("<Grid x:Name=\"Mw_ToolbarLayout\"", StringComparison.Ordinal);
        int toolbarEnd = xaml.IndexOf("Global busy indicator", StringComparison.Ordinal);
        Assert.True(toolbarEnd > toolbarStart, "Toolbar block not found in MainWindow.xaml.");

        string toolbar = xaml[toolbarStart..toolbarEnd];
        Assert.DoesNotContain("DockPanel.Dock", toolbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The toolbar's layout skeleton: same container shape and same child count as the real one,
    /// with plain content so the measurement depends on the layout rather than on a theme.
    /// </summary>
    private static Grid BuildToolbarLayout(
        out WrapPanel navigation,
        out StackPanel shortcuts,
        GridLength? navigationColumnWidth = null)
    {
        navigation = new WrapPanel { Orientation = Orientation.Horizontal };
        navigation.Children.Add(new TextBlock { Text = "Heimdall", Width = 120 });
        foreach (string tab in new[] { "Sessions", "Tools", "Tunnels", "Settings", "About" })
        {
            navigation.Children.Add(new RadioButton { Content = tab, Width = 110 });
        }

        shortcuts = new StackPanel { Orientation = Orientation.Horizontal };
        for (int index = 0; index < 3; index++)
        {
            shortcuts.Children.Add(new Button { Width = 32, Height = 32 });
        }

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = navigationColumnWidth ?? new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(navigation, 0);
        Grid.SetColumn(shortcuts, 1);
        grid.Children.Add(navigation);
        grid.Children.Add(shortcuts);
        return grid;
    }

    private static string ReadMainWindowXaml()
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "MainWindow.xaml");
        Assert.True(File.Exists(path), $"MainWindow.xaml not found at {path}.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Cannot find repository root containing Heimdall.slnx.");
    }
}
