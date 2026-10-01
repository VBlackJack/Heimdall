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

namespace Heimdall.App.Services;

/// <summary>
/// Writes a value into a PowerShell single-quoted string literal.
/// </summary>
internal static class PowerShellSingleQuotedString
{
    private const string Apostrophe = "'";
    private const string EscapedApostrophe = "''";

    /// <summary>Returns <paramref name="value"/> as a complete single-quoted literal.</summary>
    internal static string Quote(string value)
    {
        return Apostrophe + EscapeContent(value) + Apostrophe;
    }

    /// <summary>Escapes <paramref name="value"/> for use between single quotes.</summary>
    internal static string EscapeContent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace(Apostrophe, EscapedApostrophe, StringComparison.Ordinal);
    }
}
