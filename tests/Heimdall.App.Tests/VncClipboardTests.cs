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

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.App.Services;
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
/// The clipboard guard is behaviour: <see cref="VncClipboardAccess"/> is driven with delegates that
/// throw what a held clipboard throws. The page runs in WebView2 and cannot be hosted here, so its
/// encoding is pinned by reading its source, and the view's use of the guard by the statement
/// predicate. Mutants proved red, each green once restored: <c>stringToBase64(event.detail.text)</c>
/// back to <c>btoa(event.detail.text)</c>, <c>base64ToString(b64)</c> back to <c>atob(b64)</c>, the
/// catch in either guard method retyped, and either view call site pointed back at the raw
/// <c>Clipboard</c> call.
/// </remarks>
public sealed class VncClipboardTests
{
    private const string AccentedText = "caf\u00E9 \u20AC \U0001F600";

    /// <summary>
    /// What the page now sends and receives is exactly what the host's own encode and decode
    /// round-trip, for an accent, a character above U+00FF and a surrogate pair.
    /// </summary>
    [Fact]
    public void Utf8Base64_RoundTripsAccentsEuroAndEmoji()
    {
        string wire = Convert.ToBase64String(Encoding.UTF8.GetBytes(AccentedText));

        Assert.Equal(AccentedText, VncClipboardAccess.DecodePayload(wire));
    }

    /// <summary>
    /// What the old page sent for an accent (one Latin-1 byte) is not valid UTF-8: the host decodes it
    /// to the replacement character, which is the garbling this fix removes.
    /// </summary>
    [Fact]
    public void Latin1Base64_IsGarbledByTheHostDecode()
    {
        string wire = Convert.ToBase64String(Encoding.Latin1.GetBytes("caf\u00E9"));

        Assert.NotEqual("caf\u00E9", VncClipboardAccess.DecodePayload(wire));
    }

    [Fact]
    public void InvalidBase64_IsIgnored()
    {
        Assert.Null(VncClipboardAccess.DecodePayload("not base64 !"));
    }

    [Theory]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(ExternalException))]
    public void AHeldClipboard_OnWrite_IsReportedAndDoesNotThrow(Type failure)
    {
        List<Exception> reported = [];

        bool written = VncClipboardAccess.TrySetText(
            _ => throw (Exception)Activator.CreateInstance(failure)!,
            "text",
            reported.Add);

        Assert.False(written);
        Assert.Single(reported);
    }

    [Fact]
    public void AFreeClipboard_OnWrite_ReceivesTheText()
    {
        string? received = null;

        bool written = VncClipboardAccess.TrySetText(text => received = text, AccentedText, _ => Assert.Fail("no failure expected"));

        Assert.True(written);
        Assert.Equal(AccentedText, received);
    }

    [Fact]
    public void AnUnrelatedFailure_OnWrite_IsNotSwallowed()
    {
        Assert.Throws<InvalidOperationException>(
            () => VncClipboardAccess.TrySetText(_ => throw new InvalidOperationException(), "text", _ => { }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeldClipboard_OnRead_IsReportedAndDoesNotThrow(bool failsOnContains)
    {
        List<Exception> reported = [];

        string? text = VncClipboardAccess.TryGetText(
            () => failsOnContains ? throw new COMException("CLIPBRD_E_CANT_OPEN") : true,
            () => throw new COMException("CLIPBRD_E_CANT_OPEN"),
            reported.Add);

        Assert.Null(text);
        Assert.Single(reported);
    }

    [Fact]
    public void ARead_ReturnsTheTextWhenThereIsSome_AndNullWhenThereIsNone()
    {
        Assert.Equal(AccentedText, VncClipboardAccess.TryGetText(() => true, () => AccentedText, _ => Assert.Fail("no failure expected")));
        Assert.Null(VncClipboardAccess.TryGetText(() => false, () => AccentedText, _ => Assert.Fail("no failure expected")));
    }

    [Fact]
    public void TheView_ReachesTheClipboardOnlyThroughTheGuard()
    {
        string code = ViewSource.WithoutCommentsAndLiterals(ReadView());

        string send = ViewSource.HandlerBody(code, "private void SendLocalClipboardToSession()");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                send,
                "string? text = VncClipboardAccess.TryGetText(Clipboard.ContainsText, Clipboard.GetText, LogClipboardFailure);"),
            "The local clipboard is no longer read through VncClipboardAccess.");

        string write = ViewSource.HandlerBody(code, "private void HandleClipboardWrite(string base64)");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                write,
                "Dispatcher.Invoke(() => VncClipboardAccess.TrySetText(Clipboard.SetText, text, LogClipboardFailure));"),
            "The local clipboard is no longer written through VncClipboardAccess.");

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
