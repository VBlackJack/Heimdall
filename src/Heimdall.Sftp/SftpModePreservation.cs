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
/// Decides how an SFTP replacement preserves the target's complete POSIX permission mode.
/// </summary>
public static class SftpModePreservation
{
    internal const uint SetUserIdBit = 0x800;
    internal const uint SetGroupIdBit = 0x400;
    internal const uint StickyBit = 0x200;
    internal const uint OwnerReadBit = 0x100;
    internal const uint OwnerWriteBit = 0x080;
    internal const uint OwnerExecuteBit = 0x040;
    internal const uint GroupReadBit = 0x020;
    internal const uint GroupWriteBit = 0x010;
    internal const uint GroupExecuteBit = 0x008;
    internal const uint OthersReadBit = 0x004;
    internal const uint OthersWriteBit = 0x002;
    internal const uint OthersExecuteBit = 0x001;

    private const uint PermissionModeMask = 0x0FFF;

    /// <summary>
    /// Returns the target mode to apply to the temporary file, or <see langword="null"/>
    /// when both complete permission modes already match.
    /// </summary>
    public static uint? ResolveModeToApply(uint targetPermissions, uint tempPermissions)
    {
        uint targetMode = GetMode(targetPermissions);
        uint tempMode = GetMode(tempPermissions);

        return targetMode == tempMode ? null : targetMode;
    }

    /// <summary>
    /// Returns whether a failed mode application must refuse the commit because the temporary
    /// file grants at least one permission or special mode bit absent from the target.
    /// </summary>
    public static bool ShouldRefuseCommitAfterApplyFailure(
        uint targetPermissions,
        uint tempPermissions)
    {
        uint targetMode = GetMode(targetPermissions);
        uint tempMode = GetMode(tempPermissions);

        return (tempMode & ~targetMode & PermissionModeMask) != 0;
    }

    internal static uint GetMode(uint permissions)
    {
        return permissions & PermissionModeMask;
    }
}
