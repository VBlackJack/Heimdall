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
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Heimdall.App.Views;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpSendKeysFormatTests
{
    /// <summary>Virtual-key code of F11, as defined by the Win32 keyboard interface.</summary>
    private const byte VirtualKeyF11 = 0x7A;

    /// <summary>Virtual-key code of Escape, used as the positive control for the lookup.</summary>
    private const byte VirtualKeyEscape = 0x1B;

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex TranslateBinding = new(
        @"^\{\s*loc:Translate\s+(?<key>[A-Za-z0-9_]+)\s*\}$",
        RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task RdpSendKeysLabels_AreLocalizedAndNonEmpty(string locale)
    {
        var localizer = await CreateLocalizerAsync(locale);

        foreach (var key in SendKeysKeys)
        {
            var value = localizer[key];

            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(key, value);
        }
    }

    /// <summary>
    /// F11 is the only shortcut the RDP keyboard hook swallows without a modifier, so while the
    /// remote surface holds the focus the key never reaches the session. The Send Keys menu is the
    /// route that puts it back within reach; if the entry disappears, F11 becomes unreachable again.
    /// </summary>
    [Fact]
    public void TheSendKeysMenuOffersAnEntryForF11()
    {
        string[] menuKeys = SendKeysMenuHeaderKeys();

        Assert.Contains("RdpSendKeysF11", menuKeys);
    }

    /// <summary>
    /// The entry has to deliver F11 itself, not merely carry its label.
    /// </summary>
    [Fact]
    public void TheF11EntryPostsTheF11VirtualKey()
    {
        var sequences = SendKeysSequences();

        Assert.True(
            sequences.TryGetValue("RdpSendKeysF11", out byte[]? f11Keys),
            "The RDP view declares no virtual-key sequence for the F11 Send Keys entry, so the menu "
                + "entry cannot deliver anything.");
        Assert.Equal(new[] { VirtualKeyF11 }, f11Keys);

        // Positive control: the lookup really is the table the shipped entries are built from, so
        // the assertion above measures an absent entry rather than an absent table.
        Assert.True(sequences.TryGetValue("RdpSendKeysEscape", out byte[]? escapeKeys));
        Assert.Equal(new[] { VirtualKeyEscape }, escapeKeys);
    }

    /// <summary>
    /// Every Send Keys entry the menu shows is backed by a sequence: an entry whose label has no
    /// sequence opens, reports nothing and sends nothing.
    /// </summary>
    /// <remarks>
    /// The click handler each item names is not asserted here - the XAML compiler already fails
    /// the build on a handler the view does not declare, so an assertion on it could not fail.
    /// </remarks>
    [Fact]
    public void EverySendKeysMenuEntryIsBackedByASequence()
    {
        var sequences = SendKeysSequences();
        var missingSequences = new List<string>();

        foreach (XElement item in SendKeysMenuItems())
        {
            string? headerKey = TranslateKey((string?)item.Attribute("Header"));

            if (headerKey is not null
                && headerKey.StartsWith("RdpSendKeys", StringComparison.Ordinal)
                && !sequences.ContainsKey(headerKey))
            {
                missingSequences.Add(headerKey);
            }
        }

        Assert.True(
            missingSequences.Count == 0,
            "Send Keys entries with no virtual-key sequence: " + string.Join(", ", missingSequences));
    }

    private static readonly string[] SendKeysKeys =
    [
        "RdpSendKeysCtrlAltDel",
        "RdpSendKeysWindows",
        "RdpSendKeysAltTab",
        "RdpSendKeysCtrlEsc",
        "RdpSendKeysPrintScreen",
        "RdpSendKeysEscape",
        "RdpSendKeysF11"
    ];

    private static IEnumerable<XElement> SendKeysMenuItems()
        => ViewSource.NamedElement("SendKeysMenu")
            .Elements()
            .Where(element => ViewSource.TagName(element) == "MenuItem");

    private static string[] SendKeysMenuHeaderKeys()
        => SendKeysMenuItems()
            .Select(item => TranslateKey((string?)item.Attribute("Header")))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToArray();

    private static string? TranslateKey(string? attributeValue)
    {
        if (attributeValue is null)
        {
            return null;
        }

        Match match = TranslateBinding.Match(attributeValue);
        return match.Success ? match.Groups["key"].Value : null;
    }

    private static IReadOnlyDictionary<string, byte[]> SendKeysSequences()
    {
        FieldInfo? field = typeof(EmbeddedRdpView).GetField(
            "SendKeysSequences",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        Assert.True(
            field is not null,
            "EmbeddedRdpView declares no SendKeysSequences table, so no test can tell which keys the "
                + "Send Keys menu actually delivers.");

        var sequences = field!.GetValue(null) as IReadOnlyDictionary<string, byte[]>;
        Assert.True(sequences is not null, "SendKeysSequences is not a key-to-virtual-keys lookup.");
        return sequences!;
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
