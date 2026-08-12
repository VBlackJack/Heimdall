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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class SftpModePreservationTests
{
    [Fact]
    public void ResolveModeToApply_ReturnsTargetMode_WhenModesDiffer()
    {
        uint? modeToApply = SftpModePreservation.ResolveModeToApply(
            targetPermissions: 0x81A0,
            tempPermissions: 0x81A4);

        Assert.Equal((uint)0x1A0, modeToApply);
    }

    [Fact]
    public void ResolveModeToApply_ReturnsNull_WhenModesMatch()
    {
        uint? modeToApply = SftpModePreservation.ResolveModeToApply(
            targetPermissions: 0x81A4,
            tempPermissions: 0x81A4);

        Assert.Null(modeToApply);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempIsMoreRestrictive()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x1ED,
            tempPermissions: 0x180);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ApplyUploadModeBeforeCommit_RefusesCommitAndLeavesExistingFinalUntouched_WhenApplyFails()
    {
        const string FinalRemotePath = "/srv/app.sh";
        const string TempRemotePath = "/srv/.app.sh.heimdall.part";
        Dictionary<string, string> remoteFiles = new(StringComparer.Ordinal)
        {
            [FinalRemotePath] = "old-content",
            [TempRemotePath] = "new-content",
        };
        bool commitCalled = false;

        Assert.Throws<InvalidOperationException>(() =>
        {
            SftpBrowser.ApplyUploadModeBeforeCommit(
                FinalRemotePath,
                targetPermissions: 0x1ED,
                tempPermissions: 0x180,
                applyMode: _ => throw new IOException("SetAttributes refused."));
            commitCalled = true;
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (source, destination) =>
                {
                    remoteFiles[destination] = remoteFiles[source];
                    remoteFiles.Remove(source);
                },
                plainRename: (_, _) => throw new InvalidOperationException("Fallback was not expected."),
                remoteExists: remoteFiles.ContainsKey);
        });

        Assert.False(commitCalled);
        Assert.Equal("old-content", remoteFiles[FinalRemotePath]);
        Assert.Equal("new-content", remoteFiles[TempRemotePath]);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsFalse_WhenModesAreEqual()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x1A0,
            tempPermissions: 0x1A0);

        Assert.False(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempAddsWorldRead()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x180,
            tempPermissions: 0x184);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempAddsGroupRead()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x180,
            tempPermissions: 0x1A0);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_IncludesSetUserSetGroupAndStickyBits()
    {
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x980));
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x580));
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x380));
    }
}
