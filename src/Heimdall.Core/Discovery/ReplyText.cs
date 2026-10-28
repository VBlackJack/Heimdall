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

namespace Heimdall.Core.Discovery;

/// <summary>
/// Makes text taken from a network reply safe to store and show.
/// </summary>
/// <remarks>
/// Host names, domains and OS strings in the replies of NTLM, NetBIOS and the SSH-tunnelled smb
/// tools are chosen by the host being scanned. They reach the UI, the CSV export and the
/// clipboard report, so a control character (an ANSI escape, a carriage return that rewrites a
/// line) or a megabyte of text must not travel with them.
/// </remarks>
internal static class ReplyText
{
    /// <summary>The longest host or domain name DNS allows is 253 characters; the rest is slack.</summary>
    internal const int MaxNameLength = 256;

    /// <summary>
    /// The value without control characters, trimmed and cut to <paramref name="maxLength"/>.
    /// Null when nothing printable is left.
    /// </summary>
    internal static string? Clean(string? value, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            builder.Append(character);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
