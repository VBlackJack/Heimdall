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

namespace Heimdall.Core.Recipes;

/// <summary>Maps <see cref="RecipeOutputEncoding"/> to a .NET encoding.</summary>
internal static class RecipeOutputEncodings
{
    /// <summary>
    /// The encoding of a kind: UTF-8 without a byte order mark, UTF-16 little endian, or the OEM or
    /// the ANSI code page of the machine. The OEM code page is the one of a console child (not the
    /// one of the user's culture) and the ANSI one is the system code page, both read through a native
    /// call and never through <see cref="Encoding.Default"/>. The code pages live in the code-pages
    /// provider on .NET; registering it again is harmless. When a code page cannot be obtained the
    /// result is UTF-8, never an exception.
    /// </summary>
    public static Encoding Resolve(RecipeOutputEncoding kind)
        => Resolve(kind, RecipeProgramNative.SystemAnsiCodePage(), RecipeProgramNative.OemCodePage());

    /// <summary>The same as <see cref="Resolve(RecipeOutputEncoding)"/>, with the code pages given (0 or less means unknown).</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="ansiCodePage">The system ANSI code page.</param>
    /// <param name="oemCodePage">The OEM code page.</param>
    public static Encoding Resolve(RecipeOutputEncoding kind, int ansiCodePage, int oemCodePage)
    {
        Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return kind switch
        {
            RecipeOutputEncoding.Utf8 => utf8,
            RecipeOutputEncoding.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
            RecipeOutputEncoding.Oem => FromCodePage(oemCodePage, utf8),
            RecipeOutputEncoding.Ansi => FromCodePage(ansiCodePage, utf8),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "An output encoding without a mapping."),
        };
    }

    /// <summary>
    /// True when a stream is really decoded as UTF-8 or UTF-16 (whatever name the recipe gave: <c>ansi</c> or
    /// <c>oem</c> on a machine whose code page is UTF-8, and the UTF-8 fallback included): its output may then start
    /// with one byte order mark, which is dropped.
    /// </summary>
    /// <param name="encoding">The encoding the stream is decoded with.</param>
    public static bool StripsLeadingMark(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage is Utf8CodePage or Utf16LittleEndianCodePage;
    }

    private const int Utf8CodePage = 65001;

    private const int Utf16LittleEndianCodePage = 1200;

    private static Encoding FromCodePage(int codePage, Encoding fallback)
    {
        if (codePage <= 0)
            return fallback;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }
}
