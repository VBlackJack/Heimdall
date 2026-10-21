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

namespace Heimdall.App.Tests;

public sealed class HeimdallThemeBridgeTests
{
    private static readonly string[] ExpectedBrushKeys =
    [
        "BackgroundBrush",
        "SurfaceBrush",
        "CardBrush",
        "AccentBrush",
        "AccentHoverBrush",
        "AccentPressedBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextTertiaryBrush",
        "TextDisabledBrush",
        "BorderBrush",
        "BorderStrongBrush",
        "InputBorderBrush",
        "HighlightBrush",
        "SuccessBrush",
        "WarningBrush",
        "ErrorBrush",
        "InfoBrush",
        "SuccessTextBrush",
        "WarningTextBrush",
        "ErrorTextBrush",
        "InfoTextBrush",
        "AccentTextBrush",
        "BadgeTextBrush",
        "TextOnAccentBrush",
        "FocusIndicatorBrush",
        "ScrollBarThumbBrush",
        "ScrollBarTrackBrush",
        "TreeViewIndentGuideBrush",
        "TreeRowSelectedBrush",
        "TreeRowDropTargetBrush",
        "DragDropOverlayBackground",
        "OverlayBackground",
        "ProtocolRdpBrush",
        "ProtocolSshBrush",
        "ProtocolWinRmBrush",
        "ProtocolSftpBrush",
        "ProtocolVncBrush",
        "ProtocolWebBrush",
        "ProtocolTelnetBrush",
        "ProtocolFtpBrush",
        "ProtocolCitrixBrush",
        "ProtocolLocalBrush",
        "RdpBadgeBrush",
        "SshBadgeBrush",
        "WinRmBadgeBrush",
        "SftpBadgeBrush",
        "VncBadgeBrush",
        "WebBadgeBrush",
        "FtpBadgeBrush",
        "CitrixBadgeBrush",
        "TelnetBadgeBrush",
        "LocalBadgeBrush",
        "ToolBadgeBrush",
        "ToolNetworkBrush",
        "ToolSecurityBrush",
        "ToolEncodingBrush",
        "ToolSystemBrush",
        "ToolExternalBrush",
        "JwtHeaderBrush",
        "JwtPayloadBrush",
        "JwtSignatureBrush",
        "FileScriptBrush",
        "FileConfigBrush",
        "FileDocumentBrush",
        "FileArchiveBrush",
        "FileExecutableBrush",
        "FileImageBrush",
        "BroadcastActiveBrush",
    ];

    [Fact]
    public void HeimdallThemeBridge_DeclaresExpectedBrushesWithoutDuplicates()
    {
        XDocument document = XDocument.Load(GetBridgePath());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        List<XElement> keyedElements = document.Root!
            .Elements()
            .Where(element => element.Attribute(xaml + "Key") is not null)
            .ToList();
        List<string> duplicateKeys = keyedElements
            .Select(element => element.Attribute(xaml + "Key")!.Value)
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        List<string> brushKeys = keyedElements
            .Where(element => element.Name == presentation + "SolidColorBrush")
            .Select(element => element.Attribute(xaml + "Key")!.Value)
            .ToList();
        List<string> colorKeys = keyedElements
            .Where(element => element.Name == presentation + "Color")
            .Select(element => element.Attribute(xaml + "Key")!.Value)
            .ToList();

        Assert.Empty(duplicateKeys);
        Assert.Equal(69, brushKeys.Count);
        Assert.Equal(
            ExpectedBrushKeys.OrderBy(key => key, StringComparer.Ordinal),
            brushKeys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(colorKeys);
    }

    /// <summary>
    /// The modal scrim dims the window with the window's own background colour: black at 70% left
    /// a light theme under a dark veil with dark text on it. A literal colour here fails.
    /// </summary>
    [Fact]
    public void OverlayBackground_IsTheBackgroundColourAtSeventyPercent()
    {
        XElement overlay = KeyedBrush("OverlayBackground");

        Assert.Equal("{DynamicResource BackgroundColor}", overlay.Attribute("Color")?.Value);
        Assert.Equal("0.70", overlay.Attribute("Opacity")?.Value);
    }

    /// <summary>Every brush of the bridge derives from a theme colour; none spells one.</summary>
    [Fact]
    public void HeimdallThemeBridge_DeclaresNoLiteralColour()
    {
        XDocument document = XDocument.Load(GetBridgePath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        List<string> literal = document.Root!
            .Elements()
            .Where(element => element.Attribute("Color") is { } color
                && !color.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal))
            .Select(element => $"{element.Attribute(xaml + "Key")?.Value} = {element.Attribute("Color")!.Value}")
            .ToList();

        Assert.Empty(literal);
    }

    private static XElement KeyedBrush(string key)
    {
        XDocument document = XDocument.Load(GetBridgePath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        return Assert.Single(
            document.Root!.Elements(),
            element => element.Attribute(xaml + "Key")?.Value == key);
    }

    private static string GetBridgePath()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, "Heimdall.slnx");
            if (File.Exists(solutionPath))
            {
                return Path.Combine(
                    directory.FullName,
                    "src",
                    "Heimdall.App",
                    "Themes",
                    "HeimdallThemeBridge.xaml");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Heimdall repository root.");
    }
}
