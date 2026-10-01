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
/// Type-ahead for the sessions tree: typed letters build a prefix that moves focus to the next
/// row whose name starts with it, the way a file list does.
/// </summary>
/// <remarks>
/// Pure state and arithmetic, with the clock handed in, so a test drives it without a window.
/// </remarks>
internal sealed class TreeTypeAhead
{
    /// <summary>How long a pause lets the next letter start a new prefix.</summary>
    internal static readonly TimeSpan ResetDelay = TimeSpan.FromMilliseconds(1000);

    private readonly StringBuilder _buffer = new();
    private long _lastTickMs;

    /// <summary>The prefix typed so far.</summary>
    internal string Prefix => _buffer.ToString();

    /// <summary>Adds a typed character, starting over when the last one is too old.</summary>
    /// <param name="typed">The character the user typed.</param>
    /// <param name="nowMs">The current time in milliseconds.</param>
    /// <returns>The prefix to search for, or an empty string when the character is not searchable.</returns>
    internal string Append(char typed, long nowMs)
    {
        if (char.IsControl(typed) || (char.IsWhiteSpace(typed) && _buffer.Length == 0))
        {
            return "";
        }

        if (nowMs - _lastTickMs > ResetDelay.TotalMilliseconds)
        {
            _buffer.Clear();
        }

        _lastTickMs = nowMs;
        _buffer.Append(typed);
        return _buffer.ToString();
    }

    /// <summary>Forgets the prefix.</summary>
    internal void Reset() => _buffer.Clear();

    /// <summary>
    /// Finds the row a prefix leads to. One letter, or one letter repeated, cycles through the
    /// rows that start with it; a longer prefix stays on the current row while it still matches.
    /// </summary>
    /// <param name="names">The row names in display order.</param>
    /// <param name="currentIndex">The focused row, or -1 when none is.</param>
    /// <param name="prefix">The typed prefix.</param>
    /// <returns>The index of the match, or -1 when none starts with the prefix.</returns>
    internal static int FindMatch(IReadOnlyList<string> names, int currentIndex, string prefix)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0 || string.IsNullOrEmpty(prefix))
        {
            return -1;
        }

        bool repeatedLetter = prefix.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[0]));
        string needle = repeatedLetter ? prefix[..1] : prefix;
        int start = repeatedLetter ? currentIndex + 1 : Math.Max(currentIndex, 0);
        for (int offset = 0; offset < names.Count; offset++)
        {
            int index = (start + offset) % names.Count;
            if (names[index].StartsWith(needle, StringComparison.CurrentCultureIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
