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

/// <summary>
/// One permission entry that allows something on a file or a folder, reduced to what the program
/// check needs. Entries that deny are never carried: an allow that a deny cancels is still counted,
/// which errs on the side of refusing.
/// </summary>
/// <param name="Sid">The security identifier of the principal, in its text form.</param>
/// <param name="AccessMask">The rights granted, as the Windows access mask of a file or folder.</param>
/// <param name="InheritOnly">True when the entry only applies to what is created inside, not to the item itself.</param>
public sealed record RecipeAccessRule(string Sid, int AccessMask, bool InheritOnly);

/// <summary>
/// Decides whether the permissions of a file or folder let someone who is not an administrator
/// change what a program would run: replace the file, add or delete entries, or take over the
/// permissions. Reading and executing never count.
/// </summary>
public static class RecipeAccessPolicy
{
    /// <summary>The local system account.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>The Administrators group.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>The Windows installer service account, which owns the system files.</summary>
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>The placeholder principal that stands for whoever creates an item inside a folder.</summary>
    public const string CreatorOwnerSid = "S-1-3-0";

    /// <summary>Write the content of a file, or create files in a folder.</summary>
    public const int WriteData = 0x2;

    /// <summary>Append to a file, or create sub-folders in a folder.</summary>
    public const int AppendData = 0x4;

    /// <summary>Delete the files and folders inside a folder.</summary>
    public const int DeleteSubdirectoriesAndFiles = 0x40;

    /// <summary>Delete the item.</summary>
    public const int Delete = 0x10000;

    /// <summary>Change the permissions of the item.</summary>
    public const int ChangePermissions = 0x40000;

    /// <summary>Take ownership of the item.</summary>
    public const int TakeOwnership = 0x80000;

    /// <summary>The generic write right, which a permission entry may still carry unmapped.</summary>
    public const int GenericWrite = 0x40000000;

    /// <summary>The generic all-rights right, which a permission entry may still carry unmapped.</summary>
    public const int GenericAll = 0x10000000;

    /// <summary>
    /// The rights that count as a way to change what runs. Changing attributes alone is not in the
    /// set: it is granted too widely to mean anything.
    /// </summary>
    public const int ModifyingRights =
        WriteData | AppendData | DeleteSubdirectoriesAndFiles | Delete | ChangePermissions | TakeOwnership | GenericWrite | GenericAll;

    private static readonly string[] TrustedSids = [SystemSid, AdministratorsSid, TrustedInstallerSid];

    /// <summary>
    /// True when some entry gives a modifying right to a principal other than the system, the
    /// administrators and the installer service. An entry for the creator-owner placeholder that only
    /// applies to what is created inside the item gives nothing on the item itself.
    /// </summary>
    /// <param name="rules">The entries that allow something.</param>
    public static bool GrantsModificationToStandardUsers(IReadOnlyList<RecipeAccessRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (RecipeAccessRule rule in rules)
        {
            if ((rule.AccessMask & ModifyingRights) == 0)
                continue;

            if (Array.Exists(TrustedSids, sid => string.Equals(sid, rule.Sid, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (rule.InheritOnly && string.Equals(rule.Sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }

        return false;
    }
}
