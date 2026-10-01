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

namespace Heimdall.App.Services;

/// <summary>
/// Writes a value into a PowerShell single-quoted string literal.
/// </summary>
/// <remarks>
/// PowerShell ends a single-quoted string on the ASCII apostrophe and on four typographic single
/// quotes alike, and reads any of them doubled as one literal character. Escaping only the
/// apostrophe let a value holding a typographic quote close the literal and run the rest as code
/// (measured on 2026-10-01 with powershell.exe 5.1: the apostrophe-only form exited 1, the
/// doubled form kept the value intact).
/// </remarks>
internal static class PowerShellSingleQuotedString
{
    private const char Apostrophe = '\'';
    private const char LeftSingleQuotationMark = (char)0x2018;
    private const char RightSingleQuotationMark = (char)0x2019;
    private const char SingleLow9QuotationMark = (char)0x201A;
    private const char SingleHighReversed9QuotationMark = (char)0x201B;

    /// <summary>Returns <paramref name="value"/> as a complete single-quoted literal.</summary>
    internal static string Quote(string value)
    {
        return Apostrophe + EscapeContent(value) + Apostrophe;
    }

    /// <summary>Escapes <paramref name="value"/> for use between single quotes.</summary>
    internal static string EscapeContent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder escaped = new(value.Length);
        foreach (char c in value)
        {
            escaped.Append(c);
            if (IsSingleQuote(c))
            {
                escaped.Append(c);
            }
        }

        return escaped.ToString();
    }

    private static bool IsSingleQuote(char c)
    {
        return c is Apostrophe
            or LeftSingleQuotationMark
            or RightSingleQuotationMark
            or SingleLow9QuotationMark
            or SingleHighReversed9QuotationMark;
    }
}
