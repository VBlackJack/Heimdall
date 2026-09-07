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

namespace Heimdall.Sftp;

/// <summary>
/// Owns the name an FTP upload gives the copy it sets aside, and recognises that name again
/// in a later listing.
/// </summary>
/// <remarks>
/// <para>
/// FTP has no atomic replace. Replacing a file means moving the existing one aside, moving
/// the upload into place, and deleting the copy that was set aside. Between the first two
/// moves the destination does not exist, and if the process dies there - a dropped
/// connection, a killed application, a server that refuses the second move - the user is
/// left with no file at the destination and a <c>.bak</c> sibling nobody mentions.
/// </para>
/// <para>
/// The mint and the recogniser live together on purpose. They are one decision, and two
/// texts that happen to agree today would drift the first time either is edited.
/// </para>
/// </remarks>
public static class FtpBackupResidue
{
    /// <summary>The suffix every set-aside copy carries.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Length of the GUID token, as <c>Guid.ToString("N")</c> writes it.</summary>
    private const int TokenLength = 32;

    /// <summary>
    /// The name an upload gives the copy it sets aside before publishing over it.
    /// </summary>
    public static string CreateBackupPath(string finalRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRemotePath);

        return $"{finalRemotePath}.{Guid.NewGuid():N}.bak";
    }

    /// <summary>
    /// Recognises a name this class minted, and yields the name it was made from.
    /// </summary>
    /// <remarks>
    /// Deliberately strict about the token: a user's own <c>notes.txt.bak</c> is not a
    /// residue of ours, and telling them Heimdall left it behind would be a lie. Only the
    /// exact shape - a name, a dot, thirty-two lowercase hex digits, then the suffix - is
    /// recognised.
    /// </remarks>
    public static bool TryGetProtectedName(string entryName, out string originalName)
    {
        originalName = string.Empty;

        if (string.IsNullOrEmpty(entryName) || !entryName.EndsWith(BackupSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string withoutSuffix = entryName[..^BackupSuffix.Length];
        if (withoutSuffix.Length < TokenLength + 2)
        {
            return false;
        }

        int tokenStart = withoutSuffix.Length - TokenLength;
        if (withoutSuffix[tokenStart - 1] != '.')
        {
            return false;
        }

        for (int index = tokenStart; index < withoutSuffix.Length; index++)
        {
            if (!char.IsAsciiHexDigitLower(withoutSuffix[index]))
            {
                return false;
            }
        }

        originalName = withoutSuffix[..(tokenStart - 1)];
        return originalName.Length > 0;
    }

    /// <summary>
    /// Classifies every recognised residue in one listing.
    /// </summary>
    /// <param name="entries">The listing, as the browser mapped it.</param>
    /// <param name="presentNames">
    /// Every name the RAW server listing carried. It has to be the raw one: the mapper drops
    /// entries whose name fails the path guard, so reading presence off the mapped list would
    /// report a residue whose original is sitting right there, unmapped.
    /// </param>
    /// <returns>One item per residue, saying which of the two states it is in.</returns>
    public static IReadOnlyList<FtpBackupResidueFinding> Classify(
        IReadOnlyList<SftpFileInfo> entries,
        IReadOnlyCollection<string> presentNames)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(presentNames);

        var present = new HashSet<string>(presentNames, StringComparer.Ordinal);
        var findings = new List<FtpBackupResidueFinding>();

        foreach (SftpFileInfo entry in entries)
        {
            // Only a regular file is ever set aside, so only a regular file can be one of
            // ours. Not "anything that is not a directory": a symlink named like a residue
            // is not one either, and naming it would point the user at a file the server
            // never made.
            if (!entry.IsRegularFile)
            {
                continue;
            }

            if (!TryGetProtectedName(entry.Name, out string originalName))
            {
                continue;
            }

            findings.Add(new FtpBackupResidueFinding(
                entry.FullPath,
                originalName,
                OriginalIsPresent: present.Contains(originalName)));
        }

        return findings;
    }
}

/// <summary>
/// One recognised backup residue, and the state that decides what the user is told.
/// </summary>
/// <param name="RemotePath">Full path of the residue itself.</param>
/// <param name="OriginalName">The name it was made from.</param>
/// <param name="OriginalIsPresent">
/// Whether that name is in the same listing. Absent is the interrupted replacement: the
/// residue holds the only copy. Present is a leftover whose cleanup was swallowed, and it
/// holds the previous version's bytes. Two different things to say, so they stay apart.
/// </param>
public sealed record FtpBackupResidueFinding(
    string RemotePath,
    string OriginalName,
    bool OriginalIsPresent);
