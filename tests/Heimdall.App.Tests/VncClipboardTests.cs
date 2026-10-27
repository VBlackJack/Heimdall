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
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The VNC clipboard crosses the page boundary as base64 of UTF-8 text: the host encodes and decodes
/// with <see cref="Encoding.UTF8"/>, so the page has to as well. It used to call bare btoa/atob,
/// which read and write Latin-1: accented text came back garbled and anything above U+00FF threw
/// and was lost. The host also let a clipboard held by another process surface as an unhandled
/// COMException on the UI thread.
/// </summary>
/// <remarks>
/// The page and the view are WebView2 and WPF surfaces with no test host here, so the guards read
/// their source. Mutants proved red, each green once restored: <c>stringToBase64(event.detail.text)</c>
/// back to <c>btoa(event.detail.text)</c>, <c>base64ToString(b64)</c> back to <c>atob(b64)</c>, and
/// removal of either <c>catch (ExternalException</c>.
/// </remarks>
public sealed class VncClipboardTests
{
    private const string AccentedText = "café € \U0001F600";
    private const string SendMember = "private void SendLocalClipboardToSession()";
    private const string WriteMarker = "message.StartsWith(\"clipboard-write:\"";
    private const string NextMarker = "message.StartsWith(\"credentials-required:\"";

    /// <summary>
    /// What the page now sends and receives is exactly what the host's own encode and decode
    /// round-trip, for an accent, a character above U+00FF and a surrogate pair.
    /// </summary>
    [Fact]
    public void Utf8Base64_RoundTripsAccentsEuroAndEmoji()
    {
        string wire = Convert.ToBase64String(Encoding.UTF8.GetBytes(AccentedText));

        Assert.Equal(AccentedText, Encoding.UTF8.GetString(Convert.FromBase64String(wire)));
    }

    /// <summary>
    /// What the old page sent for an accent (one Latin-1 byte) is not valid UTF-8: the host decodes it
    /// to the replacement character, which is the garbling this fix removes.
    /// </summary>
    [Fact]
    public void Latin1Base64_IsGarbledByTheHostDecode()
    {
        string wire = Convert.ToBase64String(Encoding.Latin1.GetBytes("café"));

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(wire));

        Assert.NotEqual("café", decoded);
    }

    [Fact]
    public void ThePage_EncodesAndDecodesClipboardTextAsUtf8()
    {
        string raw = ReadPage();

        Assert.Contains("stringToBase64(event.detail.text)", raw, StringComparison.Ordinal);
        Assert.Contains("rfb.clipboardPasteFrom(base64ToString(b64))", raw, StringComparison.Ordinal);

        // The only bare btoa/atob left are the byte-level halves of the helpers.
        Assert.Single(Regex.Matches(raw, @"\bbtoa\("));
        Assert.Single(Regex.Matches(raw, @"\batob\("));
    }

    [Fact]
    public void ThePage_ReusesTheTerminalPageHelpers()
    {
        string vnc = ReadPage();
        string terminal = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "terminal.html"));

        foreach (string name in new[] { "bytesToBase64", "base64ToBytes", "stringToBase64" })
        {
            Assert.Equal(FunctionText(terminal, name), FunctionText(vnc, name));
        }

        Assert.Contains("new TextDecoder().decode(base64ToBytes(base64))", vnc, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHost_GuardsTheClipboardWriteAgainstAHeldClipboard()
    {
        string raw = ReadView();
        int start = raw.IndexOf(WriteMarker, StringComparison.Ordinal);
        int end = raw.IndexOf(NextMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The clipboard-write branch was not found.");
        string branch = raw[start..end];

        AssertGuarded(branch, "Clipboard.SetText");
    }

    [Fact]
    public void TheHost_GuardsTheClipboardReadAgainstAHeldClipboard()
    {
        string logic = ViewSource.HandlerBody(ReadView(), SendMember);

        AssertGuarded(logic, "Clipboard.ContainsText");
        AssertGuarded(logic, "Clipboard.GetText");
    }

    /// <summary>The call sits after a <c>try</c> and before a <c>catch (ExternalException</c> of the same text.</summary>
    private static void AssertGuarded(string text, string call)
    {
        int tryIndex = text.IndexOf("try", StringComparison.Ordinal);
        int callIndex = text.IndexOf(call, StringComparison.Ordinal);
        int catchIndex = text.IndexOf("catch (ExternalException", StringComparison.Ordinal);

        Assert.True(tryIndex >= 0, "No try block.");
        Assert.True(callIndex > tryIndex, $"{call} is not inside a try block.");
        Assert.True(catchIndex > callIndex, $"{call} has no catch (ExternalException) after it.");
    }

    /// <summary>One <c>function name(...) { ... }</c> of a page, to its closing brace, whitespace-normalised.</summary>
    private static string FunctionText(string page, string name)
    {
        int start = page.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"function {name} not found.");
        int open = page.IndexOf('{', start);
        int depth = 0;
        for (int index = open; index < page.Length; index++)
        {
            if (page[index] == '{')
            {
                depth++;
            }
            else if (page[index] == '}' && --depth == 0)
            {
                return Regex.Replace(page[start..(index + 1)], @"\s+", " ");
            }
        }

        Assert.Fail($"function {name} never closes.");
        return string.Empty;
    }

    private static string ReadPage() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "vnc.html"));

    private static string ReadView() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedVncView.xaml.cs"));
}
