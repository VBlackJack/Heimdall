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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// How a command is shortened to one line wherever a list shows it beside its title: the
/// command palette's snippet rows and the Command Library's action rows.
/// </summary>
/// <remarks>
/// One rule for both, so the same command never reads two different ways depending on where
/// the operator happened to find it.
/// </remarks>
public static class CommandPreviewText
{
    /// <summary>Longest preview kept before it is cut.</summary>
    public const int MaxLength = 96;

    /// <summary>Appended to a preview that was cut.</summary>
    public const string EllipsisGlyph = "...";

    /// <summary>
    /// The command on one line, cut at <see cref="MaxLength"/> characters with
    /// <see cref="EllipsisGlyph"/> appended when it was longer.
    /// </summary>
    public static string ToPreview(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Empty;
        }

        var line = command.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        return line.Length > MaxLength
            ? line[..MaxLength] + EllipsisGlyph
            : line;
    }
}
