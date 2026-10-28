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

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using Heimdall.App.UiTests.Infrastructure;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// Fixed-width columns and label columns of the main window are wide enough for their text in
/// every language the application ships.
/// </summary>
/// <remarks>
/// Spanish "Activo" in a 40 px checkbox column, "Puerto" in a 50 px one and "Fecha de compilacion"
/// in a 120 px label column were cut off. The widths are read from the markup of the main window,
/// which the blocking test lane never loads, and each text is laid out with the real header style
/// (or the label's own font size) in the real catalogue of the language.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class LocalizedColumnWidthTests
{
    private const string HeaderStyleKey = "ThemedDataGridColumnHeaderStyle";
    private const double AboutLabelFontSize = 13;

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string> Locales { get; } = new() { "en", "fr", "es" };

    /// <summary>The fixed-width grid columns that carry a text header: column name and header key.</summary>
    private static readonly (string Column, string Key)[] HeaderColumns =
    [
        ("Mw_TpColPort", "TunnelPanelColPort"),
        ("Mw_TpColLocal", "TunnelPanelColLocal"),
        ("Mw_TpColLabel", "TunnelPanelColLabel"),
        ("Mw_ScheduledColEnabled", "ScheduledColEnabled"),
        ("Mw_ScheduledColType", "ScheduledColType"),
    ];

    private static readonly string[] AboutLabelKeys =
    [
        "AboutLabelRuntime", "AboutLabelPlatform", "AboutLabelWebView2", "AboutLabelSshNet", "AboutLabelBuildDate",
    ];

    [StaTheory]
    [MemberData(nameof(Locales))]
    [Trait("Category", "RequiresDesktop")]
    public void TheFixedWidthHeaders_FitTheirText(string locale)
    {
        XDocument main = MainWindowMarkup();
        List<string> tooNarrow = [];

        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.SwitchLocale(locale);
            try
            {
                Style headerStyle = (Style)Application.Current.FindResource(HeaderStyleKey);
                foreach ((string column, string key) in HeaderColumns)
                {
                    XElement element = main.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == column);
                    double width = double.Parse((string)element.Attribute("Width")!, CultureInfo.InvariantCulture);
                    string text = WpfTestHost.Translate(key);

                    DataGridColumnHeader header = new() { Style = headerStyle, Content = text };
                    header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (header.DesiredSize.Width > width)
                    {
                        tooNarrow.Add($"{locale} {column} '{text}': needs {header.DesiredSize.Width:F0}, has {width:F0}");
                    }
                }
            }
            finally
            {
                WpfTestHost.ResetLocale();
            }
        });

        Assert.True(tooNarrow.Count == 0, string.Join(" | ", tooNarrow));
    }

    [StaTheory]
    [MemberData(nameof(Locales))]
    [Trait("Category", "RequiresDesktop")]
    public void TheAboutLabelColumn_FitsItsLabels(string locale)
    {
        XDocument main = MainWindowMarkup();
        XElement label = main.Descendants(Presentation + "TextBlock")
            .Single(e => ((string?)e.Attribute("Text"))?.Contains("AboutLabelBuildDate", StringComparison.Ordinal) == true);
        XElement grid = label.Parent!;
        XElement firstColumn = grid.Element(Presentation + "Grid.ColumnDefinitions")!.Elements().First();
        string widthText = (string)firstColumn.Attribute("Width")!;
        double width = widthText == "Auto" ? double.PositiveInfinity : double.Parse(widthText, CultureInfo.InvariantCulture);
        List<string> tooNarrow = [];

        WpfTestHost.Invoke(() =>
        {
            WpfTestHost.SwitchLocale(locale);
            try
            {
                foreach (string key in AboutLabelKeys)
                {
                    string text = WpfTestHost.Translate(key);
                    TextBlock block = new() { Text = text, FontSize = AboutLabelFontSize };
                    block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (block.DesiredSize.Width > width)
                    {
                        tooNarrow.Add($"{locale} {key} '{text}': needs {block.DesiredSize.Width:F0}, has {width:F0}");
                    }
                }
            }
            finally
            {
                WpfTestHost.ResetLocale();
            }
        });

        Assert.True(tooNarrow.Count == 0, string.Join(" | ", tooNarrow));
    }

    /// <summary>
    /// Positive control: a column far narrower than its text is reported, so the checks above
    /// cannot pass by measuring nothing.
    /// </summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheMeasure_ReportsAHeaderInAColumnThatIsTooNarrow()
    {
        double needed = WpfTestHost.Invoke(() =>
        {
            Style headerStyle = (Style)Application.Current.FindResource(HeaderStyleKey);
            DataGridColumnHeader header = new() { Style = headerStyle, Content = "Fecha de compilacion" };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return header.DesiredSize.Width;
        });

        Assert.True(needed > 40, $"A long header measured {needed:F0}.");
    }

    private static XDocument MainWindowMarkup()
        => XDocument.Load(Path.Combine(WpfTestHost.RepoRoot, "src", "Heimdall.App", "MainWindow.xaml"));
}
