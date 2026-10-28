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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Turns the bytes of a MobaXterm session file into text: UTF-8 when the file starts with a UTF-8
/// byte order mark, Windows-1252 otherwise, which is what MobaXterm writes.
/// </summary>
/// <remarks>
/// The one rule for Settings > Import and for the shared session sources, so a file reads the same
/// whichever way it arrives. The byte order mark itself is not part of the text: left in, it sits
/// before the first section header and hides it from the parser.
/// </remarks>
public static class MobaXtermFileDecoder
{
    private const int LegacyCodePage = 1252;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    static MobaXtermFileDecoder()
    {
        // Windows-1252 is not built into .NET; the provider is idempotent to register.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Decodes <paramref name="bytes"/> by the MobaXterm rule.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return HasUtf8Bom(bytes)
            ? Encoding.UTF8.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length)
            : Encoding.GetEncoding(LegacyCodePage).GetString(bytes);
    }

    /// <summary>Whether <paramref name="bytes"/> starts with a UTF-8 byte order mark.</summary>
    public static bool HasUtf8Bom(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.AsSpan().StartsWith(Utf8Bom);
    }
}
