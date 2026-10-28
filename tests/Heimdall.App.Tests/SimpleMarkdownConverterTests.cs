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

public class SimpleMarkdownConverterTests
{
    [Fact]
    public void NullInput_ReturnsEmptyString()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyString()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("");
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("# H1", "<h1>H1</h1>")]
    [InlineData("## H2", "<h2>H2</h2>")]
    [InlineData("### H3", "<h3>H3</h3>")]
    [InlineData("###### H6", "<h6>H6</h6>")]
    public void Headings_ConvertCorrectly(string input, string expected)
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment(input).Trim();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Paragraph_WrappedInPTags()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("Hello world").Trim();
        Assert.Equal("<p>Hello world</p>", result);
    }

    [Fact]
    public void Bold_AsteriskConverts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("**bold**").Trim();
        Assert.Contains("<strong>bold</strong>", result);
    }

    [Fact]
    public void Bold_UnderscoreConverts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("__bold__").Trim();
        Assert.Contains("<strong>bold</strong>", result);
    }

    [Fact]
    public void Italic_AsteriskConverts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("*italic*").Trim();
        Assert.Contains("<em>italic</em>", result);
    }

    [Fact]
    public void Italic_UnderscoreConverts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("_italic_").Trim();
        Assert.Contains("<em>italic</em>", result);
    }

    [Fact]
    public void Strikethrough_Converts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("~~deleted~~").Trim();
        Assert.Contains("<del>deleted</del>", result);
    }

    [Fact]
    public void InlineCode_Converts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("`code`").Trim();
        Assert.Contains("<code>code</code>", result);
    }

    [Fact]
    public void InlineCode_HtmlEntitiesEncoded()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("`<div>`").Trim();
        Assert.Contains("<code>&lt;div&gt;</code>", result);
    }

    [Fact]
    public void FencedCodeBlock_Converts()
    {
        var input = "```bash\necho hello\n```";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<pre><code class=\"language-bash\">", result);
        Assert.Contains("echo hello", result);
        Assert.Contains("</code></pre>", result);
    }

    [Fact]
    public void FencedCodeBlock_NoLanguage()
    {
        var input = "```\nfoo\n```";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<pre><code>", result);
        Assert.DoesNotContain("class=", result);
    }

    [Fact]
    public void FencedCodeBlock_HtmlEncoded()
    {
        var input = "```\n<script>alert(1)</script>\n```";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("&lt;script&gt;", result);
        Assert.DoesNotContain("<script>", result);
    }

    [Fact]
    public void UnorderedList_Converts()
    {
        var input = "- item 1\n- item 2";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<ul>", result);
        Assert.Contains("<li>item 1</li>", result);
        Assert.Contains("<li>item 2</li>", result);
        Assert.Contains("</ul>", result);
    }

    [Fact]
    public void OrderedList_Converts()
    {
        var input = "1. first\n2. second";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<ol>", result);
        Assert.Contains("<li>first</li>", result);
        Assert.Contains("<li>second</li>", result);
        Assert.Contains("</ol>", result);
    }

    [Fact]
    public void NestedList_Converts()
    {
        var input = "- parent\n  - child\n  - child2\n- parent2";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<ul>", result);
        Assert.Contains("<li>parent", result);
        // Nested list should be inside the parent <li>
        var parentLiIndex = result.IndexOf("<li>parent", StringComparison.Ordinal);
        var nestedUlIndex = result.IndexOf("<ul>", parentLiIndex + 1, StringComparison.Ordinal);
        var parentCloseIndex = result.IndexOf("</li>", parentLiIndex, StringComparison.Ordinal);
        Assert.True(nestedUlIndex > parentLiIndex);
        Assert.True(parentCloseIndex > nestedUlIndex);
    }

    [Fact]
    public void MixedListTypes_Converts()
    {
        var input = "- bullet\n1. ordered";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<li>bullet</li>", result);
        Assert.Contains("<li>ordered</li>", result);
    }

    [Fact]
    public void TaskList_CheckedAndUnchecked()
    {
        var input = "- [x] done\n- [ ] todo";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("checked=\"checked\"", result);
        Assert.Contains("done", result);
        Assert.Contains("todo", result);
    }

    [Fact]
    public void Blockquote_Converts()
    {
        var input = "> quoted text";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<blockquote>", result);
        Assert.Contains("quoted text", result);
        Assert.Contains("</blockquote>", result);
    }

    [Fact]
    public void HorizontalRule_Converts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("---");
        Assert.Contains("<hr />", result);
    }

    [Fact]
    public void Table_Converts()
    {
        var input = "| H1 | H2 |\n|---|---|\n| C1 | C2 |";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        Assert.Contains("<table>", result);
        Assert.Contains("<th>H1</th>", result);
        Assert.Contains("<td>C1</td>", result);
        Assert.Contains("</table>", result);
    }

    [Fact]
    public void Link_Converts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("[text](http://example.com)").Trim();
        Assert.Contains("<a href=", result);
        Assert.Contains("http://example.com", result);
        Assert.Contains(">text</a>", result);
    }

    [Fact]
    public void Image_Converts()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("![alt](image.png)").Trim();
        Assert.Contains("<img src=", result);
        Assert.Contains("image.png", result);
        Assert.Contains("alt=\"alt\"", result);
    }

    [Fact]
    public void XssInParagraph_Encoded()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("<script>alert(1)</script>").Trim();
        Assert.DoesNotContain("<script>", result);
        Assert.Contains("&lt;script&gt;", result);
    }

    [Fact]
    public void LineBreaks_ConvertToBr()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("line1\nline2").Trim();
        Assert.Contains("<br />", result);
    }

    [Fact]
    public void CrLf_NormalizedCorrectly()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("# Title\r\n\r\nParagraph").Trim();
        Assert.Contains("<h1>Title</h1>", result);
        Assert.Contains("<p>Paragraph</p>", result);
    }

    // ── Inter-note links ─────────────────────────────────────────────

    [Fact]
    public void NoteLink_ConvertedToAnchor()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("See [[My Note]]").Trim();
        Assert.Contains("note-link", result);
        Assert.Contains("#note:", result);
        Assert.Contains("My Note", result);
    }

    [Fact]
    public void NoteLink_HtmlEncoded()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("[[<script>]]").Trim();
        Assert.Contains("&lt;script&gt;", result);
        Assert.DoesNotContain("<script>", result);
    }

    [Fact]
    public void NoteLink_NotConfusedWithRegularLink()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("[text](url)").Trim();
        Assert.DoesNotContain("note-link", result);
        Assert.Contains("<a href=", result);
    }

    [Fact]
    public void NoteLink_MultipleOnSameLine()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment("See [[Note A]] and [[Note B]]").Trim();
        Assert.Contains("Note A", result);
        Assert.Contains("Note B", result);
    }

    [Theory]
    [InlineData(@"See \[\[My Note]]")]
    [InlineData(@"See \[[My Note]]")]
    [InlineData(@"See [\[My Note]]")]
    public void NoteLink_WithEscapedOpeningBrackets_IsAnAnchorWithoutTheBackslashes(string markdown)
    {
        // The editor used to save [[X]] as \[\[X]]; those notes must keep linking.
        var result = SimpleMarkdownConverter.ToHtmlFragment(markdown).Trim();

        Assert.Contains("""<a href="#note:My Note" class="note-link">My Note</a>""", result, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void NoteLink_NameExcludesTheBackslashesOfAnEscapedLink()
    {
        var match = SimpleMarkdownConverter.NoteLinkRegex().Match(@"a \[\[Procedure]] b");

        Assert.True(match.Success);
        Assert.Equal("Procedure", match.Groups[1].Value);
        Assert.Equal(@"\[\[Procedure]]", match.Value);
    }

    [Fact]
    public void NoteLink_InAnInlineCodeSpan_StaysCode()
    {
        var result = SimpleMarkdownConverter.ToHtmlFragment(@"`\[\[X]]` and `[[Y]]`").Trim();

        Assert.DoesNotContain("note-link", result, StringComparison.Ordinal);
        Assert.Contains(@"<code>\[\[X]]</code>", result, StringComparison.Ordinal);
        Assert.Contains("<code>[[Y]]</code>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedList_ThreeLevels()
    {
        var input = "- L1\n  - L2\n    - L3\n- L1b";
        var result = SimpleMarkdownConverter.ToHtmlFragment(input);
        // Should have 3 nested <ul> tags
        var ulCount = result.Split("<ul>").Length - 1;
        Assert.True(ulCount >= 3, $"Expected at least 3 <ul> tags, got {ulCount}");
    }
}
