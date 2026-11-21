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
/// A WPF visual laid over a Win32 child window (the RDP ActiveX surface, a WebView2) is painted
/// under it. These pin the markup that keeps the transient toast and the loading bars out of that
/// trap, read as XML structure.
/// </summary>
public sealed class AirspaceOverlayMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheRdpToastLivesInAStaysOpenTransparentPopup()
    {
        XElement toast = ViewSource.NamedElement("TransientToast");

        XElement? popup = toast.Ancestors(Presentation + "Popup").FirstOrDefault();

        Assert.NotNull(popup);
        Assert.Equal("True", (string?)popup!.Attribute("StaysOpen"));
        Assert.Equal("True", (string?)popup.Attribute("AllowsTransparency"));
        Assert.Equal("TransientToastPopup", (string?)popup.Attribute(Xaml + "Name"));
        Assert.Equal("Custom", (string?)popup.Attribute("Placement"));
        Assert.Contains("SurfaceContainer", (string?)popup.Attribute("PlacementTarget") ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EmbeddedVncView.xaml", "VncLoadingBar", "VncWebView")]
    [InlineData("EmbeddedWebView.xaml", "LoadingBar", "ConsoleWebView")]
    public void TheLoadingBarIsNotInTheRowOfTheWebView(string file, string barName, string webViewName)
    {
        XDocument markup = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", file));

        XElement bar = ViewSource.NamedElement(markup, barName);
        XElement webView = ViewSource.NamedElement(markup, webViewName);

        string barRow = (string?)bar.Attribute("Grid.Row") ?? "0";
        string surfaceRow = (string?)webView.Parent!.Attribute("Grid.Row") ?? "0";

        Assert.Equal(Presentation + "Grid", webView.Parent!.Name);
        Assert.NotEqual(surfaceRow, barRow);
    }
}
