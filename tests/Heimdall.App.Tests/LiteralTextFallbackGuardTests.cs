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
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The macro editor dialog and the Notes editor's loading veil keep their text in the catalogues.
/// </summary>
/// <remarks>
/// The macro editor took an optional localizer and answered a missing one with English sentences
/// written in the code; every caller passes one, so the sentences were never shown and drifted
/// from the catalogue wording. The Notes editor's loading veil showed a literal ellipsis in every
/// language. The dialog now requires its localizer (held by MacroEditorDialogLocalizerTests in the UI
/// tests) and the veil reads a catalogue key.
/// </remarks>
public sealed class LiteralTextFallbackGuardTests
{
    private static readonly Regex LiteralFallbackArm = new(
        "\\]\\s*\\?\\?\\s*\\$?\"",
        RegexOptions.None,
        matchTimeout: TimeSpan.FromSeconds(5));

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void TheMacroEditorDialog_KeepsNoSentenceBehindAFallbackArm()
    {
        string code = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", "MacroEditorDialog.xaml.cs"));

        Assert.Empty(LiteralFallbackArm.Matches(code).Select(m => m.Value));
    }

    [Fact]
    public void TheFallbackPattern_RecognisesTheShapeItForbids()
    {
        // Positive control: a pattern that matches nothing would keep the guard above green.
        Assert.Matches(LiteralFallbackArm, "var title = _localizer?[\"Key\"] ?? \"Delete macro\";");
        Assert.Matches(LiteralFallbackArm, "var title = _localizer?[\"Key\"] ?? $\"Delete {name}\";");
        Assert.DoesNotMatch(LiteralFallbackArm, "var title = _localizer[\"Key\"];");
    }

    [Fact]
    public void TheNotesEditorLoadingVeil_ShowsNoLiteralText()
    {
        XDocument markup = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools", "MilkdownEditorControl.xaml"));

        Assert.Empty(LiteralTexts(markup));
    }

    [Fact]
    public void TheLiteralTextScan_FindsAFixedText()
    {
        XDocument inline = XDocument.Parse(
            $"<Grid xmlns=\"{Presentation}\"><TextBlock Text=\"...\"/><TextBlock Text=\"{{Binding Name}}\"/></Grid>");

        Assert.Single(LiteralTexts(inline));
    }

    private static IEnumerable<string> LiteralTexts(XDocument markup)
        => markup.Descendants(Presentation + "TextBlock")
            .Select(e => (string?)e.Attribute("Text"))
            .Where(text => !string.IsNullOrEmpty(text) && !text.StartsWith('{'))
            .Select(text => text!);
}
