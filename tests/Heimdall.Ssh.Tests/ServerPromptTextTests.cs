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

using System.Globalization;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Pins audit 2026-09-30 S-08: text a server sends as a keyboard-interactive prompt is shown in
/// a Heimdall dialog, so it must not be able to lay itself out as something Heimdall wrote.
/// </summary>
public sealed class ServerPromptTextTests
{
    // Built from code points so no invisible character hides in this source file.
    private static readonly string RightToLeftOverride = char.ConvertFromUtf32(0x202E);
    private static readonly string ZeroWidthSpace = char.ConvertFromUtf32(0x200B);
    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);
    private static readonly string Bell = char.ConvertFromUtf32(0x07);

    [Fact]
    public void ControlCharacters_BecomeOneSpace_SoAPromptCannotStartAFakeParagraph()
    {
        string sanitized = ServerPromptText.Sanitize("Code:\r\n\r\n\r\nHeimdall: enter your master password");

        Assert.Equal("Code: Heimdall: enter your master password", sanitized);
    }

    [Fact]
    public void FormatCharacters_AreRemoved()
    {
        string sanitized = ServerPromptText.Sanitize(
            $"Pass{ZeroWidthSpace}word{RightToLeftOverride}:drowssap retsam");

        Assert.Equal("Password:drowssap retsam", sanitized);
        Assert.DoesNotContain(sanitized, static c =>
            CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control);
    }

    [Fact]
    public void LineSeparatorsAndBell_AreNeutralised()
    {
        string sanitized = ServerPromptText.Sanitize($"A{LineSeparator}B{Bell}C");

        Assert.Equal("A B C", sanitized);
    }

    [Fact]
    public void LongText_IsCappedAtTheNamedBound_WithAMarker()
    {
        string sanitized = ServerPromptText.Sanitize(new string('x', ServerPromptText.MaxLength * 4));

        Assert.Equal(ServerPromptText.MaxLength + ServerPromptText.TruncationMarker.Length, sanitized.Length);
        Assert.EndsWith(ServerPromptText.TruncationMarker, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void TextAtTheBound_IsKeptWhole()
    {
        string exact = new('y', ServerPromptText.MaxLength);

        Assert.Equal(exact, ServerPromptText.Sanitize(exact));
    }

    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        string emoji = char.ConvertFromUtf32(0x1F511);
        string text = new string('z', ServerPromptText.MaxLength - 1) + emoji + "tail";

        string sanitized = ServerPromptText.Sanitize(text);

        Assert.DoesNotContain(sanitized, static c => char.IsSurrogate(c));
        Assert.EndsWith(ServerPromptText.TruncationMarker, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryPrompt_IsUnchanged()
    {
        Assert.Equal("Verification code:", ServerPromptText.Sanitize("  Verification code: "));
    }

    [Fact]
    public void Null_IsEmpty()
    {
        Assert.Equal(string.Empty, ServerPromptText.Sanitize(null));
    }
}
