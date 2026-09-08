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

using System.Globalization;

namespace Heimdall.Sftp.Tests;

public sealed class SftpAuditHardeningTests
{
    [Fact]
    public async Task Ftp_ExclusiveUploadIsRefusedBeforeTransportWork()
    {
        using IRemoteBrowser browser = new FtpBrowser();
        await Assert.ThrowsAsync<RemoteNoClobberPublishUnavailableException>(
            async () => await browser.UploadFileAsync("synthetic-source", "/target", overwrite: false));
        Assert.False(browser.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalCommit_LateCollisionRequiresReplacementConsent(bool overwrite)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Heimdall-Publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "target");
        string temp = AtomicLocalFile.CreateTempPath(target);
        try
        {
            File.WriteAllText(temp, "incoming");
            File.WriteAllText(target, "concurrent writer");
            if (overwrite) AtomicLocalFile.Commit(temp, target, overwrite);
            else Assert.Throws<LocalDestinationExistsException>(() => AtomicLocalFile.Commit(temp, target, overwrite));
            Assert.Equal(overwrite ? "incoming" : "concurrent writer", File.ReadAllText(target));
        }
        finally
        {
            File.Delete(temp);
            File.Delete(target);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(FileConflictResolutionChoice.Replace, true)]
    [InlineData(FileConflictResolutionChoice.AutoRename, false)]
    [InlineData(FileConflictResolutionChoice.Skip, false)]
    public void Planner_PreservesExplicitConsent(FileConflictResolutionChoice choice, bool overwrite)
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("source", "target")], _ => true, StringComparer.Ordinal);
        FileConflictResolvedItem item = Assert.Single(FileConflictPlanner.Resolve(
            analysis, [new FileConflictDecision(0, choice)], _ => false, StringComparer.Ordinal));
        Assert.Equal(overwrite, item.Overwrite);
    }

    [Fact]
    public void Planner_NewTargetDoesNotAuthorizeReplacement()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("source", "target")], _ => false, StringComparer.Ordinal);
        Assert.False(Assert.Single(FileConflictPlanner.Resolve(analysis, [], _ => false, StringComparer.Ordinal)).Overwrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacement_ReadbackMustConfirmGroup(bool serverHonorsGroup)
    {
        const int targetGroup = 12345;
        bool committed = false;
        SftpModePreservation.SftpPublicationAttributes desired = new(0x5A0, DateTime.UnixEpoch, DateTime.UnixEpoch, targetGroup);
        SftpModePreservation.SftpPublicationAttributes applied = default;
        using MemoryStream source = new([1, 2, 3]);
        SftpModePreservation.StagedUploadOperations operations = new(
            (_, _) => new MemoryStream(), () => 0x180, _ => { }, () => desired,
            value => applied = value with { GroupId = serverHonorsGroup ? targetGroup : 0 },
            () => applied, () => committed = true);
        if (serverHonorsGroup) SftpModePreservation.RunStagedUpload(operations, source, _ => { }, default);
        else Assert.Throws<IOException>(() => SftpModePreservation.RunStagedUpload(operations, source, _ => { }, default));
        Assert.Equal(serverHonorsGroup, committed);
        Assert.Equal(desired.Mode, applied.Mode);
    }

    [Theory]
    [InlineData("report -> old.txt")]
    [InlineData("file with spaces")]
    [InlineData(" leading and trailing ")]
    [InlineData("quote'file")]
    public void SudoListing_PreservesExactName(string name)
    {
        SftpFileInfo entry = Assert.Single(SudoDirectoryListing.Parse(Record("f", name), "/srv"));
        Assert.Equal(name, entry.Name);
        Assert.Equal("/srv/" + name, entry.FullPath);
        Assert.Equal("-rw-r-----", entry.Permissions);
        Assert.Equal("12345", entry.Group);
    }

    [Theory]
    [InlineData("prefix\n-rw-r--r-- 1 root root 7 2026-09-08 12:00 victim")]
    [InlineData("../outside")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad\tname")]
    public void SudoListing_UnsafeNameCannotCreateAnotherEntry(string name)
    {
        IReadOnlyList<SftpFileInfo> entries = SudoDirectoryListing.Parse(Record("f", name) + Record("f", "safe"), "/srv");
        Assert.Equal("safe", Assert.Single(entries).Name);
    }

    [Theory]
    [InlineData("d", RemoteEntryKind.Directory)]
    [InlineData("l", RemoteEntryKind.SymbolicLink)]
    [InlineData("p", RemoteEntryKind.Fifo)]
    [InlineData("s", RemoteEntryKind.Socket)]
    [InlineData("b", RemoteEntryKind.Device)]
    [InlineData("c", RemoteEntryKind.Device)]
    public void SudoListing_PreservesEntryKind(string kind, RemoteEntryKind expected)
        => Assert.Equal(expected, Assert.Single(SudoDirectoryListing.Parse(Record(kind, "entry"), "/")).Kind);

    [Theory]
    [InlineData("f\0")]
    [InlineData("plain ls output")]
    public void SudoListing_RejectsIncompleteResponse(string output)
        => Assert.Throws<InvalidDataException>(() => SudoDirectoryListing.Parse(output, "/"));

    [Fact]
    public void SudoListing_EmptyDirectoryAndInvariantNumbers()
    {
        Assert.Empty(SudoDirectoryListing.Parse("", "/"));
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            SftpFileInfo file = Assert.Single(SudoDirectoryListing.Parse(Record("f", "entry"), "/"));
            Assert.Equal(7, file.Size);
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(1234.5).ToLocalTime(), file.LastModified);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static string Record(string kind, string name)
        => string.Join('\0', kind, "640", "0", "12345", "7", "1234.5", name, "");
}
