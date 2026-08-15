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

public sealed class SftpPublicationAttributesTests
{
    private const string Target = "/srv/app/config.txt";

    // The staging mode is owner read/write, so a 0600 destination matches it exactly. That is the
    // case the previous shape got wrong: no write was emitted at all.
    private const uint StagingAndTargetMode = 0x180;

    private static readonly DateTime AccessTime = new(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime WriteTime = new(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc);

    [Fact]
    public void Replacement_WritesOnce_EvenWhenTheModeAlreadyMatches()
    {
        List<(uint Mode, DateTime? Access, DateTime? Write)> writes = [];

        SftpBrowser.ApplyPublicationAttributesBeforeCommit(
            Target,
            new SftpModePreservation.SftpPublicationAttributes(
                StagingAndTargetMode, AccessTime, WriteTime),
            tempPermissions: StagingAndTargetMode,
            applyAll: (mode, access, write) => writes.Add((mode, access, write)));

        // Exactly one write, carrying the stamps. Routing timestamps through the mode helper meant
        // an equal mode skipped the callback entirely, so nothing was applied and the read-back
        // then refused a replacement that was perfectly legitimate.
        (uint mode, DateTime? access, DateTime? write) = Assert.Single(writes);
        Assert.Equal(StagingAndTargetMode, mode);
        Assert.Equal(AccessTime, access);
        Assert.Equal(WriteTime, write);
    }

    [Fact]
    public void Replacement_WritesExactlyOnce_WhenTheModeDiffers()
    {
        List<(uint Mode, DateTime? Access, DateTime? Write)> writes = [];

        SftpBrowser.ApplyPublicationAttributesBeforeCommit(
            Target,
            new SftpModePreservation.SftpPublicationAttributes(0x1ED, AccessTime, WriteTime),
            tempPermissions: StagingAndTargetMode,
            applyAll: (mode, access, write) => writes.Add((mode, access, write)));

        // Never two: mode and stamps travel together, so no observer can see the file carrying the
        // new permissions with the old timestamps.
        (uint mode, DateTime? access, DateTime? write) = Assert.Single(writes);
        Assert.Equal(0x1EDu, mode);
        Assert.Equal(AccessTime, access);
        Assert.Equal(WriteTime, write);
    }

    [Fact]
    public void Replacement_AFailedWrite_RefusesTheCommit()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SftpBrowser.ApplyPublicationAttributesBeforeCommit(
                Target,
                new SftpModePreservation.SftpPublicationAttributes(
                    StagingAndTargetMode, AccessTime, WriteTime),
                tempPermissions: StagingAndTargetMode,
                applyAll: (_, _, _) => throw new IOException("utimes refused")));

        Assert.Contains("commit refused", exception.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public void Creation_InheritsNoTimestamps_AndKeepsTheExistingModePolicy()
    {
        List<(uint Mode, DateTime? Access, DateTime? Write)> writes = [];

        SftpBrowser.ApplyPublicationAttributesBeforeCommit(
            Target,
            new SftpModePreservation.SftpPublicationAttributes(
                0x1A4, LastAccessTimeUtc: null, LastWriteTimeUtc: null),
            tempPermissions: StagingAndTargetMode,
            applyAll: (mode, access, write) => writes.Add((mode, access, write)));

        // A creation still goes through the mode-only policy, and carries no timestamps: there is
        // no destination whose stamps could be inherited, so inventing any would date a file that
        // never existed.
        (uint mode, DateTime? access, DateTime? write) = Assert.Single(writes);
        Assert.Equal(0x1A4u, mode);
        Assert.Null(access);
        Assert.Null(write);
    }

    [Fact]
    public void Creation_SkipsTheWriteWhenTheModeAlreadyMatches()
    {
        List<uint> writes = [];

        SftpBrowser.ApplyPublicationAttributesBeforeCommit(
            Target,
            new SftpModePreservation.SftpPublicationAttributes(
                StagingAndTargetMode, LastAccessTimeUtc: null, LastWriteTimeUtc: null),
            tempPermissions: StagingAndTargetMode,
            applyAll: (mode, _, _) => writes.Add(mode));

        // Unchanged from before this lot. The creation path was never the defect, and making it
        // stricter would be an unrequested behaviour change.
        Assert.Empty(writes);
    }
}
