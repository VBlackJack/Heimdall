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

namespace Heimdall.App.Tests;

/// <summary>
/// Reads <c>EmbeddedSshView.xaml.cs</c> for the wiring tests that cannot construct the view.
/// </summary>
/// <remarks>
/// The view needs a desktop and an application resource tree to construct, so the decisions it
/// makes are pinned by pure helpers and their wiring is read from source. Every assertion built on
/// this reader works on executable lines: trimmed, with blank and comment-only lines removed, so a
/// statement that has been commented out does not keep a test green.
/// </remarks>
internal static class EmbeddedSshViewSourceReader
{
    private static readonly string[] ViewRelativePath =
        ["src", "Heimdall.App", "Views", "EmbeddedSshView.xaml.cs"];

    /// <summary>Reads the full view source.</summary>
    public static string ReadViewSource() => File.ReadAllText(
        Path.Combine([FindRepoRoot(), .. ViewRelativePath]));

    /// <summary>Returns the brace-balanced body that follows <paramref name="signature"/>.</summary>
    public static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");

        int open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, $"No body for: {signature}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced body for: {signature}");
    }

    /// <summary>Lines that can execute: trimmed, without blank or comment-only lines.</summary>
    public static string[] ExecutableLines(string body) => body
        .Split('\n')
        .Select(static line => line.Trim())
        .Where(static line => line.Length > 0
            && !line.StartsWith("//", StringComparison.Ordinal)
            && !line.StartsWith("///", StringComparison.Ordinal))
        .ToArray();

    /// <summary>
    /// The executable lines joined by one space, so a statement that spans several lines can be
    /// asserted as one string.
    /// </summary>
    public static string ExecutableText(string body) => string.Join(' ', ExecutableLines(body));

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
