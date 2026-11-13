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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// While an SSH session connects, the page is hidden and the host sends the real pane box
/// (<c>viewport:W,H</c>) so the first column count is derived from the pane, not from the window.
/// </summary>
/// <remarks>
/// <para>Cause pinned here: <c>body</c> is a flex container as wide as the window and
/// <c>#terminal</c> is a flex item. A host-given hidden box can be wider than the window (the
/// WebView2 creation box), and a flex item with the default <c>flex-shrink: 1</c> is shrunk back to
/// the window width, so the fit addon measures the window and not the pane. The result measured was
/// plink started at 149 columns while the shown surface needed 203.</para>
/// <para>Producer: <c>terminal.html</c>, the <c>#terminal</c> rule and
/// <c>handleViewportMessage</c>, fed by the host's <c>viewport:</c> message. The script runs in
/// WebView2, not in a test host, so these tests pin the text of the shipped assets only.</para>
/// </remarks>
public sealed class TerminalHtmlViewportLayoutTests
{
    private const string TerminalRuleAnchor = "#terminal {";
    private const string ViewportHandlerAnchor = "function handleViewportMessage(payload)";
    private const string FitProposeAnchor = "proposeDimensions(){";

    [Fact]
    public void TheTerminalElementIsNotShrunkByTheFlexBodyWhenTheHostBoxIsWiderThanTheWindow()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, TerminalRuleAnchor));

        Assert.Contains(
            lines,
            line => line.StartsWith("flex-shrink: 0", StringComparison.Ordinal)
                || line.StartsWith("flex: none", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why the guard above is needed: the hidden box is applied as an inline width on the element
    /// and the fit measures it, so a shrunk element yields the window's column count.
    /// </summary>
    [Fact]
    public void TheViewportMessageSizesTheTerminalElementAndFitsIt()
    {
        string[] lines = ExecutableLines(ExtractBlock(TerminalAssetsLoader.TerminalHtml, ViewportHandlerAnchor));

        Assert.Contains(lines, line => line.StartsWith("terminalElement.style.width = snappedWidth", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("terminalElement.style.height = snappedHeight", StringComparison.Ordinal));
        Assert.Contains(lines, line => line == "fitAndReportOnce(false);");
    }

    /// <summary>
    /// Producer: the vendored fit addon. It measures the computed style of the terminal's parent
    /// element, which is <c>#terminal</c>. An upgrade that changes what is measured must be rechecked
    /// against the flex rule above.
    /// </summary>
    [Fact]
    public void TheVendoredFitAddonStillMeasuresTheTerminalParentElement()
    {
        string propose = ExtractBlock(TerminalAssetsLoader.AddonFitJs, FitProposeAnchor);

        Assert.Contains("getComputedStyle(this._terminal.element.parentElement)", propose, StringComparison.Ordinal);
        Assert.Contains("getPropertyValue(\"width\")", propose, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guard: the extraction reads the rule and the functions it claims to read.
    /// </summary>
    [Fact]
    public void TheSourcesBeingReadCarryTheAnchors()
    {
        Assert.Contains(TerminalRuleAnchor, TerminalAssetsLoader.TerminalHtml, StringComparison.Ordinal);
        Assert.Contains(ViewportHandlerAnchor, TerminalAssetsLoader.TerminalHtml, StringComparison.Ordinal);
        Assert.Contains(FitProposeAnchor, TerminalAssetsLoader.AddonFitJs, StringComparison.Ordinal);
    }

    private static string[] ExecutableLines(string block) => block
        .Split('\n')
        .Select(static line => line.Trim())
        .Where(static line => line.Length > 0
            && !line.StartsWith("//", StringComparison.Ordinal)
            && !line.StartsWith("/*", StringComparison.Ordinal)
            && !line.StartsWith("*", StringComparison.Ordinal))
        .ToArray();

    private static string ExtractBlock(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor not found: {anchor}");

        int open = source.IndexOf('{', start + anchor.Length - 1);
        Assert.True(open >= 0, $"No block for: {anchor}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced block for: {anchor}");
    }
}
