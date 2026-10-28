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
/// A button repeated in a data template whose only content is a glyph from the icon font needs a name:
/// the glyph is a private-use character that a screen reader reads as nothing, or as its code point.
/// </summary>
/// <remarks>
/// The batch list of the password generator drew one such button per generated password with
/// neither, while its sibling in the history list got both from a Loaded handler. A name set from
/// code is accepted (the handler is named by the markup); a button with neither route is the gap.
/// </remarks>
public sealed class IconOnlyButtonNameGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const char PrivateUseStart = '\uE000';
    private const char PrivateUseEnd = '\uF8FF';

    [Fact]
    public void EveryTemplatedIconOnlyButton_HasAName()
    {
        int iconButtons = 0;
        List<string> unnamed = [];

        foreach (string file in SourceFileEnumeration.EnumerateFiles(AppRoot(), "*.xaml"))
        {
            foreach (XElement button in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Button"))
            {
                string? content = RawContent(button);
                string? font = (string?)button.Attribute("FontFamily");
                if (content is null || font is null
                    || !font.Contains("FontFamilyIcons", StringComparison.Ordinal)
                    || content.Length != 1
                    || content[0] < PrivateUseStart
                    || content[0] > PrivateUseEnd)
                {
                    continue;
                }

                // A button in a data template is repeated once per item, so code behind cannot name
                // it by x:Name the way it names a button written once; it needs its name in the
                // markup or from a handler that every instance runs.
                if (!button.Ancestors(Presentation + "DataTemplate").Any())
                {
                    continue;
                }

                iconButtons++;

                // A Loaded handler is the other route: the localizer is reachable from code only.
                bool namedFromCode = button.Attribute("Loaded") is not null;
                bool named = button.Attribute(AutomationName) is not null || namedFromCode;
                if (!named)
                {
                    unnamed.Add($"{Path.GetRelativePath(AppRoot(), file)}:{((System.Xml.IXmlLineInfo)button).LineNumber}");
                }
            }
        }

        // The sweep must reach the icon buttons: the generator's two copy buttons are among them.
        Assert.True(iconButtons >= 2, $"the scan found only {iconButtons} icon-only buttons");
        Assert.True(
            unnamed.Count == 0,
            "icon-only buttons in a data template without a name: " + string.Join(", ", unnamed));
    }

    /// <summary>
    /// The batch row's copy button takes its name and tooltip from the same handler as the history
    /// row's, so the two cannot drift apart. The sweep above holds the name; the tooltip is the
    /// handler's and is held here.
    /// </summary>
    [Fact]
    public void TheBatchCopyButton_UsesTheHistoryCopyButtonNaming()
    {
        XDocument view = XDocument.Load(Path.Combine(AppRoot(), "Views", "Tools", "PasswordGeneratorView.xaml"));
        XElement batchCopy = Assert.Single(
            view.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("Click") == "OnBatchCopyRow");

        Assert.Equal("OnHistoryCopyButtonLoaded", (string?)batchCopy.Attribute("Loaded"));
    }

    private static readonly XName AutomationName = "AutomationProperties.Name";

    private static string? RawContent(XElement button) => (string?)button.Attribute("Content");

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
