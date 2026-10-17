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
/// The regex tester draws its match highlights on a layer over the test text. The layer was
/// opaque and drew its own copy of the text, so once a pattern matched, the text being typed,
/// its caret and its selection were hidden under a copy in another font size.
/// </summary>
public sealed class RegexHighlightOverlayTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheHighlightLayerLetsTheTestTextShowThrough()
    {
        XElement overlay = Named("HighlightDisplay");

        Assert.Equal("Transparent", (string?)overlay.Attribute("Background"));
        Assert.Equal("Transparent", (string?)overlay.Attribute("Foreground"));
        Assert.Equal("False", (string?)overlay.Attribute("IsHitTestVisible"));
        Assert.Equal("False", (string?)overlay.Attribute("Focusable"));
    }

    [Fact]
    public void TheHighlightLayerLaysOutTheTextExactlyAsTheTestTextDoes()
    {
        XElement overlay = Named("HighlightDisplay");
        XElement input = Named("TestText");

        Assert.Equal((string?)input.Attribute("FontFamily"), (string?)overlay.Attribute("FontFamily"));
        Assert.Equal((string?)input.Attribute("FontSize"), (string?)overlay.Attribute("FontSize"));
        Assert.Equal((string?)input.Attribute("Padding"), (string?)overlay.Attribute("Padding"));
        Assert.Equal((string?)input.Attribute("BorderThickness"), (string?)overlay.Attribute("BorderThickness"));
    }

    private static XElement Named(string name) =>
        Assert.Single(
            XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools", "RegexTesterView.xaml")).Descendants(),
            e => (string?)e.Attribute(Xaml + "Name") == name);
}
