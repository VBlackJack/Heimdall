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
        "InputBorderBrush",
        "HighlightBrush",
        "SuccessBrush",
        "WarningBrush",
        "ErrorBrush",
        "InfoBrush",
        "SuccessTextBrush",
        "WarningTextBrush",
        "ErrorTextBrush",
        "BadgeTextBrush",
        "TextOnAccentBrush",
        "FocusIndicatorBrush",
        "ScrollBarThumbBrush",
        "ScrollBarTrackBrush",
        "TreeViewIndentGuideBrush",
        "DragDropOverlayBackground",
        "OverlayBackground",
        "ProtocolRdpBrush",
        "ProtocolSshBrush",
        "ProtocolWinRmBrush",
        "ProtocolSftpBrush",
        "ProtocolVncBrush",
        "ProtocolTelnetBrush",
        "ProtocolFtpBrush",
        "ProtocolCitrixBrush",
        "ProtocolLocalBrush",
        "RdpBadgeBrush",
        "SshBadgeBrush",
        "WinRmBadgeBrush",
        "SftpBadgeBrush",
        "VncBadgeBrush",
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
        Assert.Equal(62, brushKeys.Count);
        Assert.Equal(
            ExpectedBrushKeys.OrderBy(key => key, StringComparer.Ordinal),
            brushKeys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(colorKeys);
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
