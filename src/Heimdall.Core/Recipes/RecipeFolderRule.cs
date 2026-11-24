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

namespace Heimdall.Core.Recipes;

/// <summary>Why a folder may not be added to the list of folders recipes may run programs from.</summary>
public enum RecipeFolderRefusal
{
    /// <summary>The folder is acceptable.</summary>
    None,

    /// <summary>The text is empty.</summary>
    Empty,

    /// <summary>The text is longer than <see cref="RecipeFolderRule.MaxLength"/>.</summary>
    TooLong,

    /// <summary>The text is a network path (a UNC path such as <c>\\server\share</c>).</summary>
    Unc,

    /// <summary>The text is a device path (<c>\\?\</c>, <c>\\.\</c> or <c>\??\</c>).</summary>
    Device,

    /// <summary>The text is not a full path starting with a drive letter, or holds a <c>.</c> or <c>..</c> part.</summary>
    NotAbsolute,

    /// <summary>The text holds a control or format character, a percent sign, a wildcard, a stray colon, or a part Windows would silently rename.</summary>
    ForbiddenCharacter,

    /// <summary>The text is the root of a drive (<c>D:\</c>): trusting a whole drive is never what a program folder means.</summary>
    DriveRoot,

    /// <summary>The folder is already in the list.</summary>
    AlreadyListed,

    /// <summary>The list already holds the most folders it keeps.</summary>
    ListFull,
}

/// <summary>
/// Decides which folders the user may add to the allowed program folders. Only an absolute path on
/// a local drive letter is accepted: a network path would let a recipe run a program from a share,
/// and a relative or device path does not name one place.
/// </summary>
public static class RecipeFolderRule
{
    /// <summary>Longest folder text accepted, in characters.</summary>
    public const int MaxLength = 240;

    /// <summary>The most folders the list keeps.</summary>
    public const int MaxFolders = 32;

    private const int DriveRootLength = 3;

    private const int DriveColonIndex = 1;

    private const char DriveSeparator = ':';

    private const string ForbiddenNameCharacters = "<>\"|*?";

    private static readonly char[] Separators = ['\\', '/'];

    private static readonly char[] ForbiddenNameChars = ForbiddenNameCharacters.ToCharArray();

    private static readonly string[] DevicePrefixes = [@"\\?\", @"\\.\", @"\??\", "//?/", "//./"];

    /// <summary>Checks one folder text against the rule, ignoring what the list already holds.</summary>
    /// <param name="folder">The text the user chose.</param>
    public static RecipeFolderRefusal Check(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return RecipeFolderRefusal.Empty;

        if (folder.Length > MaxLength)
            return RecipeFolderRefusal.TooLong;

        string text = folder.Trim();
        foreach (string prefix in DevicePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return RecipeFolderRefusal.Device;
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
            return RecipeFolderRefusal.Unc;

        if (text.Contains('%', StringComparison.Ordinal) || RecipeText.ContainsRefusedCharacter(text))
            return RecipeFolderRefusal.ForbiddenCharacter;

        if (!IsDriveRooted(text))
            return RecipeFolderRefusal.NotAbsolute;

        if (text[DriveRootLength..].TrimEnd(Separators).Length == 0)
            return RecipeFolderRefusal.DriveRoot;

        string[] segments = text[DriveRootLength..].Split(Separators);
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            bool last = index == segments.Length - 1;
            if (segment.Length == 0)
            {
                // A trailing separator leaves one empty last part; anywhere else it is a doubled separator.
                if (last)
                    continue;

                return RecipeFolderRefusal.ForbiddenCharacter;
            }

            if (segment is "." or "..")
                return RecipeFolderRefusal.NotAbsolute;

            if (segment.IndexOfAny(ForbiddenNameChars) >= 0
                || segment.IndexOf(DriveSeparator) >= 0
                || segment[^1] is ' ' or '.')
            {
                return RecipeFolderRefusal.ForbiddenCharacter;
            }
        }

        return RecipeFolderRefusal.None;
    }

    /// <summary>Checks one folder text against the rule and against the list it would join.</summary>
    /// <param name="folder">The text the user chose.</param>
    /// <param name="existing">The folders already in the list.</param>
    public static RecipeFolderRefusal Check(string? folder, IReadOnlyCollection<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        RecipeFolderRefusal refusal = Check(folder);
        if (refusal != RecipeFolderRefusal.None)
            return refusal;

        string normalized = Normalize(folder!);
        foreach (string item in existing)
        {
            if (string.Equals(Normalize(item), normalized, StringComparison.OrdinalIgnoreCase))
                return RecipeFolderRefusal.AlreadyListed;
        }

        return existing.Count >= MaxFolders ? RecipeFolderRefusal.ListFull : RecipeFolderRefusal.None;
    }

    /// <summary>
    /// True when a folder equals one of the roots, lies under one, or contains one: whole path segments,
    /// ignoring case and trailing separators, the same test as the program resolver uses. Adding such a
    /// folder is accepted, but it relaxes nothing for the programs under those roots, which stay checked.
    /// A null or empty root is ignored.
    /// </summary>
    /// <param name="folder">A folder text <see cref="Check(string?)"/> accepted.</param>
    /// <param name="roots">The built-in roots: the Windows folder, Program Files and Program Files (x86).</param>
    public static bool TouchesRoot(string folder, IEnumerable<string?> roots)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(roots);

        string trimmedFolder = Normalize(folder).TrimEnd(Separators);
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string trimmedRoot = root.Trim().Replace('/', '\\').TrimEnd(Separators);
            if (trimmedRoot.Length == 0)
                continue;

            if (RecipeProgramResolver.IsUnderOrEqual(trimmedFolder, trimmedRoot)
                || RecipeProgramResolver.IsUnderOrEqual(trimmedRoot, trimmedFolder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The form a folder is stored in: trimmed, with backslashes, and without a trailing separator
    /// except for a drive root. Call it only on a text <see cref="Check(string?)"/> accepted.
    /// </summary>
    /// <param name="folder">An accepted folder text.</param>
    public static string Normalize(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string text = folder.Trim().Replace('/', '\\');
        if (text.Length > DriveRootLength)
            text = text.TrimEnd('\\');

        return text.Length == DriveColonIndex + 1 ? text + '\\' : text;
    }

    private static bool IsDriveRooted(string text)
        => text.Length >= DriveRootLength
            && char.IsAsciiLetter(text[0])
            && text[DriveColonIndex] == DriveSeparator
            && text[DriveColonIndex + 1] is '\\' or '/';
}
