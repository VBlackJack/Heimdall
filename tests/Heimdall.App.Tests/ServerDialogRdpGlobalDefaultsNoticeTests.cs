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

/// <summary>
/// What the RDP options card says while it is showing numbers that are not the ones in effect.
/// </summary>
/// <remarks>
/// RdpUseGlobalDefaults defaults to true, so a new RDP profile opens with the whole options panel
/// greyed out and fully populated - populated from the profile's own fields, while the session runs
/// on the global defaults. The existing banner says which set is in effect; nothing said that the
/// ticks below it are the other set, so clearing the checkbox can change what the session does
/// without a single visible control moving.
///
/// The panel is deliberately NOT rebound to resolved values: the dialog does not hold AppSettings,
/// and seeding the profile fields when the box is cleared would overwrite what the profile already
/// stores. One sentence closes what both of those were proposed to close.
/// </remarks>
public sealed class ServerDialogRdpGlobalDefaultsNoticeTests
{
    private const string NoticeText =
        "{loc:Translate ServerDialogRdpGlobalDefaultsValuesNotInEffect}";

    [Fact]
    public void TheCard_SaysTheOptionsBelowAreNotTheOnesInEffect()
    {
        XElement panel = RdpOptionsPanel();

        Assert.Single(
            panel.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == NoticeText);

        // Scoped to the card rather than to the document: the options TabControl this sentence
        // is about is a sibling in that same card, and a sentence that drifts away from the panel
        // it describes is read by nobody.
        Assert.Contains(
            panel.Elements(),
            element => element.Name.LocalName == "TabControl");
    }

    [Fact]
    public void TheNotice_AppearsOnlyWhileTheGlobalDefaultsAreInUse()
    {
        XElement notice = Notice();

        XElement trigger = Assert.Single(
            notice.Descendants(),
            element => element.Name.LocalName == "DataTrigger"
                && element.Attribute("Binding")?.Value == "{Binding RdpUseGlobalDefaults}"
                && element.Attribute("Value")?.Value == "True");

        Assert.Contains(
            trigger.Elements(),
            setter => setter.Name.LocalName == "Setter"
                && setter.Attribute("Property")?.Value == "Visibility"
                && setter.Attribute("Value")?.Value == "Visible");

        // The other half, and the one that would turn this fix into a second false statement:
        // with the box cleared the panel IS in effect, and a sentence saying otherwise would then
        // be the lie. Shown by a trigger over a collapsed default, never the reverse.
        Assert.Contains(
            notice.Descendants(),
            setter => setter.Name.LocalName == "Setter"
                && setter.Attribute("Property")?.Value == "Visibility"
                && setter.Attribute("Value")?.Value == "Collapsed");
    }

    private static XElement Notice() => Assert.Single(
        RdpOptionsPanel().Descendants(),
        element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == NoticeText);

    // The card is resolved from the banner it completes rather than by position, so a reordering
    // of the options tab does not quietly turn this suite into a scan of the whole document.
    private static XElement RdpOptionsPanel()
    {
        XElement banner = Assert.Single(
            LoadServerDialogXaml().Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value
                    == "{loc:Translate ServerDialogRdpUsingGlobalDefaultsBanner}");

        return Assert.IsType<XElement>(banner.Parent);
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }
}
