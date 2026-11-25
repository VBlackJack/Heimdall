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

using System.Text;
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// How the bytes a program writes become text: the four encodings, the one leading byte order mark that
/// is dropped for UTF-8 and UTF-16, the byte cap, and the fall back to UTF-8 when a code page is unknown.
/// </summary>
public sealed class RecipeOutputDecodingTests : IDisposable
{
    private const string Program = @"C:\Windows\System32\hostname.exe";

    private const int AnsiLatinCodePage = 1252;

    private const int OemLatinCodePage = 850;

    private const byte LatinSmallEAcute = 0xE9;

    private readonly ScratchFolder _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];

    private static readonly byte[] Utf16Mark = [0xFF, 0xFE];

    private static Recipe MakeRecipe(RecipeOutputEncoding encoding, RecipeStderrMode stderr = RecipeStderrMode.Separate)
        => RecipeTestData.Minimal() with { OutputEncoding = encoding, Stderr = stderr };

    private async Task<RecipeRunResult> RunAsync(Recipe recipe, byte[][] output, byte[][]? error = null)
    {
        FakeRecipeLauncher launcher = new()
        {
            OnStart = process =>
            {
                foreach (byte[] chunk in output)
                    process.Out.Feed(chunk);

                foreach (byte[] chunk in error ?? [])
                    process.Err.Feed(chunk);

                process.Finish(0);
            },
        };

        return await new RecipeRunner(launcher).RunAsync(recipe, Program, [], _scratch.Dir("work-" + Guid.NewGuid().ToString("N")));
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Utf16(string text) => new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(text);

    // ---- the encodings by themselves ----

    [Fact]
    public void Utf8IsUtf8WithoutAMark()
    {
        Encoding encoding = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Utf8, AnsiLatinCodePage, OemLatinCodePage);

        Assert.Equal(65001, encoding.CodePage);
        Assert.Empty(encoding.GetPreamble());
    }

    [Fact]
    public void Utf16LeIsLittleEndianAndNeverBigEndian()
    {
        Encoding encoding = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Utf16Le, AnsiLatinCodePage, OemLatinCodePage);

        Assert.Equal(1200, encoding.CodePage);
        Assert.Equal("a", encoding.GetString([0x61, 0x00]));
        Assert.Equal("\u6100", encoding.GetString([0x00, 0x61]));
    }

    [Fact]
    public void AnsiIsTheGivenSystemCodePage_NotUtf8()
    {
        Encoding encoding = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Ansi, AnsiLatinCodePage, OemLatinCodePage);

        Assert.Equal(AnsiLatinCodePage, encoding.CodePage);
        Assert.Equal("\u00E9", encoding.GetString([LatinSmallEAcute]));
    }

    [Fact]
    public void OemIsTheGivenOemCodePage_AndAnsiIsNotTheOemOne()
    {
        Encoding oem = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Oem, AnsiLatinCodePage, OemLatinCodePage);
        Encoding ansi = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Ansi, AnsiLatinCodePage, OemLatinCodePage);

        Assert.Equal(OemLatinCodePage, oem.CodePage);
        Assert.NotEqual(oem.CodePage, ansi.CodePage);
        Assert.Equal("\u00E9", oem.GetString([0x82]));
    }

    [Theory]
    [InlineData(RecipeOutputEncoding.Ansi)]
    [InlineData(RecipeOutputEncoding.Oem)]
    public void ACodePageThatCannotBeObtainedFallsBackToUtf8(RecipeOutputEncoding kind)
    {
        Assert.Equal(65001, RecipeOutputEncodings.Resolve(kind, 0, 0).CodePage);
        Assert.Equal(65001, RecipeOutputEncodings.Resolve(kind, -1, -1).CodePage);
        Assert.Equal(65001, RecipeOutputEncodings.Resolve(kind, 99999, 99999).CodePage);
    }

    [Fact]
    public void TheRealAnsiCodePageIsTheSystemOneAndNeverTheRuntimeDefault()
    {
        int system = RecipeProgramNative.SystemAnsiCodePage();

        Encoding encoding = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Ansi);

        Assert.True(system > 0);
        Assert.Equal(system, encoding.CodePage);
    }

    [Fact]
    public async Task AnAnsiRunDecodesWithTheSystemCodePage()
    {
        int system = RecipeProgramNative.SystemAnsiCodePage();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = [0x63, LatinSmallEAcute, 0x72, 0x74];

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Ansi), [bytes]);

        Assert.Equal(Encoding.GetEncoding(system).GetString(bytes), result.Output);
    }

    // ---- the leading mark ----

    [Fact]
    public async Task OneLeadingUtf8MarkIsDropped()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [Concat(Utf8Mark, Encoding.UTF8.GetBytes("hello"))]);

        Assert.Equal("hello", result.Output);
    }

    [Fact]
    public async Task OnlyTheFirstMarkIsDropped()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [Concat(Utf8Mark, Utf8Mark, Encoding.UTF8.GetBytes("x"))]);

        Assert.Equal("\uFEFFx", result.Output);
    }

    [Fact]
    public async Task AMarkInTheMiddleOfTheOutputIsKept()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [Concat(Encoding.UTF8.GetBytes("a"), Utf8Mark, Encoding.UTF8.GetBytes("b"))]);

        Assert.Equal("a\uFEFFb", result.Output);
    }

    [Fact]
    public async Task AMarkSplitAcrossReadsIsStillDropped()
    {
        RecipeRunResult result = await RunAsync(
            MakeRecipe(RecipeOutputEncoding.Utf8),
            [[0xEF], [0xBB], [0xBF, 0x61]]);

        Assert.Equal("a", result.Output);
    }

    [Fact]
    public async Task Utf8WithoutAMarkIsLeftAlone()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [Encoding.UTF8.GetBytes("caf\u00E9")]);

        Assert.Equal("caf\u00E9", result.Output);
    }

    [Fact]
    public async Task Utf16LittleEndianWithAMarkIsDecodedAndTheMarkDropped()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [Concat(Utf16Mark, Utf16("ab\u00E9"))]);

        Assert.Equal("ab\u00E9", result.Output);
    }

    [Fact]
    public async Task Utf16LittleEndianWithoutAMarkIsDecoded()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [Utf16("sfc says hi\r\n")]);

        Assert.Equal("sfc says hi\r\n", result.Output);
    }

    [Fact]
    public async Task AUtf16UnitSplitAcrossReadsIsDecoded()
    {
        byte[] bytes = Concat(Utf16Mark, Utf16("xyz"));

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [bytes[..1], bytes[1..4], bytes[4..]]);

        Assert.Equal("xyz", result.Output);
    }

    [Fact]
    public async Task ABigEndianMarkIsNotSniffed_ItIsJustADifferentCharacter()
    {
        byte[] bigEndianMark = [0xFE, 0xFF];

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [Concat(bigEndianMark, Utf16("a"))]);

        Assert.NotEqual("a", result.Output);
        Assert.StartsWith("\uFFFE", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RecipeOutputEncoding.Oem)]
    [InlineData(RecipeOutputEncoding.Ansi)]
    public async Task ACodePageOutputKeepsEveryByte_NoMarkIsLookedFor(RecipeOutputEncoding encoding)
    {
        // Under a single-byte code page the three bytes of a UTF-8 mark are three characters of text.
        RecipeRunResult result = await RunAsync(MakeRecipe(encoding), [Concat(Utf8Mark, [0x61])]);

        Assert.Equal(4, result.Output.Length);
        Assert.EndsWith("a", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachStreamLosesItsOwnMark_WhenStandardErrorIsMerged()
    {
        RecipeRunResult result = await RunAsync(
            MakeRecipe(RecipeOutputEncoding.Utf8, RecipeStderrMode.Merge),
            [Concat(Utf8Mark, Encoding.UTF8.GetBytes("o"))],
            [Concat(Utf8Mark, Encoding.UTF8.GetBytes("e"))]);

        Assert.Equal(2, result.Output.Length);
        Assert.DoesNotContain('\uFEFF', result.Output);
        Assert.Contains('o', result.Output);
        Assert.Contains('e', result.Output);
    }

    [Fact]
    public async Task StandardErrorIsDecodedWithTheSameEncodingAsTheOutput()
    {
        RecipeRunResult result = await RunAsync(
            MakeRecipe(RecipeOutputEncoding.Utf16Le),
            [Utf16("out")],
            [Concat(Utf16Mark, Utf16("err"))]);

        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.ErrorOutput);
    }

    [Fact]
    public async Task TheStartInfoGivesTheSameEncodingToBothStreams()
    {
        FakeRecipeLauncher launcher = new() { OnStart = process => process.Finish(0) };

        await new RecipeRunner(launcher).RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), Program, [], _scratch.Dir("work-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(launcher.StartInfo!.StandardOutputEncoding, launcher.StartInfo.StandardErrorEncoding);
        Assert.Equal(1200, launcher.StartInfo.StandardOutputEncoding!.CodePage);
    }

    // ---- the cap is in bytes ----

    [Fact]
    public async Task TheCapCountsBytes_SoUtf16KeepsHalfAsManyCharacters()
    {
        byte[] bytes = Utf16(new string('a', RecipeLimits.MaxCaptureBytes));

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [bytes]);

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes / 2, result.Output.Length);
    }

    [Fact]
    public async Task AnOddByteBeyondTheCapIsDiscardedWithTheRest()
    {
        byte[] bytes = Concat(Utf16(new string('a', RecipeLimits.MaxCaptureBytes / 2)), [0x62]);

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [bytes]);

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes / 2, result.Output.Length);
        Assert.DoesNotContain('\uFFFD', result.Output);
    }

    [Fact]
    public async Task ALoneOddByteBelowTheCapIsNeitherAnErrorNorACharacter()
    {
        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [Concat(Utf16("ab"), [0x63])]);

        Assert.False(result.Truncated);
        Assert.Equal("ab", result.Output);
    }

    [Fact]
    public async Task ASurrogatePairCutByTheCapLosesItsHalfWithoutAReplacementCharacter()
    {
        // The high surrogate is the last unit that fits; its low surrogate is beyond the cap.
        string text = new string('a', (RecipeLimits.MaxCaptureBytes / 2) - 1) + "\U0001F600";
        byte[] bytes = Utf16(text);

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf16Le), [bytes]);

        Assert.True(result.Truncated);
        Assert.Equal((RecipeLimits.MaxCaptureBytes / 2) - 1, result.Output.Length);
        Assert.DoesNotContain('\uFFFD', result.Output);
    }

    [Fact]
    public async Task AMultiByteUtf8CharacterCutByTheCapLosesItsHalfWithoutAReplacementCharacter()
    {
        byte[] bytes = Concat(Encoding.UTF8.GetBytes(new string('a', RecipeLimits.MaxCaptureBytes - 1)), Encoding.UTF8.GetBytes("\u00E9"));

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [bytes]);

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes - 1, result.Output.Length);
        Assert.DoesNotContain('\uFFFD', result.Output);
    }

    [Fact]
    public async Task ADroppedMarkStillCountsTowardTheByteCap()
    {
        byte[] bytes = Concat(Utf8Mark, Encoding.UTF8.GetBytes(new string('a', RecipeLimits.MaxCaptureBytes)));

        RecipeRunResult result = await RunAsync(MakeRecipe(RecipeOutputEncoding.Utf8), [bytes]);

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes - Utf8Mark.Length, result.Output.Length);
    }
}
