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
using System.Xml.Linq;

using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The terminal session header lets its texts shrink and trim instead of being cut mid-letter.
/// </summary>
/// <remarks>
/// The header was a horizontal <c>StackPanel</c> with literal width caps. A horizontal stack panel
/// measures its children with unbounded width, so their ellipsis never engaged: at an 800 px
/// window the subtitle showed one letter and nothing told the user it was cut. The RDP header
/// draws its caps from shared tokens; the terminal header now uses the same ones.
/// </remarks>
public sealed class SshSessionHeaderLayoutTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] HeaderTexts =
        ["SessionTitleText", "EndpointTextBlock", "GatewayRouteText", "StatusTextBlock"];

    [Theory]
    [InlineData("SessionTitleText", "SessionHeaderTitleMaxWidth")]
    [InlineData("EndpointTextBlock", "SessionHeaderEndpointMaxWidth")]
    [InlineData("GatewayRouteText", "SessionHeaderEndpointMaxWidth")]
    [InlineData("StatusTextBlock", "SessionHeaderStatusMaxWidth")]
    public void EachHeaderTextIsCappedByItsSharedTokenAndTrims(string name, string token)
    {
        XElement text = ViewSource.NamedElement(Markup(), name);

        Assert.Equal($"{{StaticResource {token}}}", (string?)text.Attribute("MaxWidth"));
        Assert.Equal("CharacterEllipsis", (string?)text.Attribute("TextTrimming"));
    }

    [Fact]
    public void TheHeaderCarriesNoLiteralWidthCap()
    {
        XElement header = HeaderPanel();

        Assert.DoesNotContain(
            header.DescendantsAndSelf(),
            e => (string?)e.Attribute("MaxWidth") is "300" or "250");
    }

    [Fact]
    public void TheHeaderTextsSitInShrinkableGridColumns()
    {
        XElement header = HeaderPanel();

        Assert.Equal(Presentation + "Grid", header.Name);
        XElement[] columns = header
            .Elements(Presentation + "Grid.ColumnDefinitions")
            .Elements(Presentation + "ColumnDefinition")
            .ToArray();

        foreach (string name in HeaderTexts)
        {
            XElement text = ViewSource.NamedElement(Markup(), name);
            int column = int.Parse((string?)text.Attribute("Grid.Column") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            string width = (string?)columns[column].Attribute("Width") ?? "*";
            Assert.True(width.EndsWith('*'), $"{name} sits in a column of width '{width}', which cannot shrink");
        }
    }

    private static XElement HeaderPanel()
    {
        XElement title = ViewSource.NamedElement(Markup(), "SessionTitleText");
        return title.Parent!;
    }

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedSshView.xaml"));
}
