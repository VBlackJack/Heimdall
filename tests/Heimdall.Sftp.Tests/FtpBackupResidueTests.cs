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

using Heimdall.Core.Models;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The copy an FTP replacement sets aside, and what a later listing says about it.
/// </summary>
/// <remarks>
/// FTP cannot replace a file atomically: the existing one is moved aside, the upload is
/// moved into place, and the copy is deleted. Interrupt that between the first two moves -
/// a dropped connection, a killed application - and the user is left with nothing at the
/// destination and a file named after a GUID that nothing in the product ever mentioned.
/// Naming it is the whole change; moving it back would race another client's replacement,
/// which is happening right now in exactly the case the user most wants fixed.
/// </remarks>
public sealed class FtpBackupResidueTests
{
    [Fact]
    public void AMintedNameIsRecognisedAndYieldsTheNameItWasMadeFrom()
    {
        // The mint and the recogniser are one decision. If they ever stop agreeing, every
        // other test in this file is asserting against a convention nothing produces.
        string minted = FtpBackupResidue.CreateBackupPath("/srv/app/config.txt");
        string name = minted[(minted.LastIndexOf('/') + 1)..];

        Assert.True(FtpBackupResidue.TryGetProtectedName(name, out string original));
        Assert.Equal("config.txt", original);
    }

    [Fact]
    public void APlainBakSiblingIsNotOurs()
    {
        // A user's own backup. Telling them Heimdall left it behind would be a lie, and it
        // is the kind of lie that makes every other warning less believable.
        Assert.False(FtpBackupResidue.TryGetProtectedName("notes.txt.bak", out _));
        Assert.False(FtpBackupResidue.TryGetProtectedName("config.txt.NOTAGUID.bak", out _));
        Assert.False(FtpBackupResidue.TryGetProtectedName(".00112233445566778899aabbccddeeff.bak", out _));
    }

    [Fact]
    public void AResidueWhoseOriginalIsAbsentIsReportedAsSuch()
    {
        string residue = Residue("config.txt");

        IReadOnlyList<FtpBackupResidueFinding> found = FtpBackupResidue.Classify(
            [File(residue), File("notes.md")],
            [residue, "notes.md"]);

        FtpBackupResidueFinding finding = Assert.Single(found);
        Assert.Equal($"/srv/{residue}", finding.RemotePath);
        Assert.Equal("config.txt", finding.OriginalName);
        Assert.False(finding.OriginalIsPresent);
    }

    [Fact]
    public void AResidueWhoseOriginalIsPresentIsReportedDifferently()
    {
        // Not silence. The replacement completed and only its cleanup failed, so nothing is
        // lost - but the file is still there, holding the previous version's bytes, and the
        // user is the only one who can decide what to do with it.
        string residue = Residue("config.txt");

        IReadOnlyList<FtpBackupResidueFinding> found = FtpBackupResidue.Classify(
            [File("config.txt"), File(residue)],
            ["config.txt", residue]);

        FtpBackupResidueFinding finding = Assert.Single(found);
        Assert.True(finding.OriginalIsPresent);
    }

    [Fact]
    public void PresenceIsReadFromTheRawListingNotTheMappedOne()
    {
        // The mapper drops entries whose name fails the path guard. If presence were read
        // off the mapped list, a residue whose original is sitting right there - unmapped -
        // would be announced as the only surviving copy of a file that is not lost at all.
        string residue = Residue("config.txt");

        IReadOnlyList<FtpBackupResidueFinding> found = FtpBackupResidue.Classify(
            [File(residue)],
            ["config.txt", residue]);

        Assert.True(Assert.Single(found).OriginalIsPresent);
    }

    [Theory]
    [InlineData(RemoteEntryKind.Directory)]
    [InlineData(RemoteEntryKind.SymbolicLink)]
    public void OnlyARegularFileCanBeOneOfOurs(RemoteEntryKind kind)
    {
        // Only a regular file is ever set aside. A directory is the obvious case; the
        // symlink is the one a "not a directory" test would let through, and pointing the
        // user at it would name a file the server never made.
        string residue = Residue("config.txt");

        Assert.Empty(FtpBackupResidue.Classify([Entry(residue, kind)], [residue]));
    }

    [Fact]
    public void EachResidueIsReportedOncePerConnection()
    {
        // A browser lists the same directory constantly. Warning every time would train the
        // user to dismiss the warning, which is worse than not warning at all.
        string residue = Residue("config.txt");
        var reporter = new FtpBackupResidueReporter();

        Assert.Single(reporter.Report([File(residue)], [residue]));
        Assert.Empty(reporter.Report([File(residue)], [residue]));
    }

    [Fact]
    public void AReconnectSaysItAgain()
    {
        string residue = Residue("config.txt");
        var reporter = new FtpBackupResidueReporter();

        Assert.Single(reporter.Report([File(residue)], [residue]));
        reporter.Reset();
        Assert.Single(reporter.Report([File(residue)], [residue]));
    }

    [Fact]
    public void TheTwoStatesProduceTwoDifferentWarnings()
    {
        // The classifier telling the two apart buys nothing if the message does not. This
        // is the assertion that fails when both branches are collapsed into one call - and
        // that collapse leaves every other test in this file green.
        var missing = new FtpBackupResidueFinding("/srv/config.txt.abc.bak", "config.txt", OriginalIsPresent: false);
        var present = missing with { OriginalIsPresent = true };

        Assert.Equal(
            "WarnFtpBackupResidueOriginalMissing",
            RemoteOperationWarning.ForBackupResidue(missing).WarningKey);
        Assert.Equal(
            "WarnFtpBackupResidueOriginalPresent",
            RemoteOperationWarning.ForBackupResidue(present).WarningKey);
        Assert.Equal("/srv/config.txt.abc.bak", RemoteOperationWarning.ForBackupResidue(missing).RemotePath);
    }

    private static string Residue(string originalName)
    {
        string minted = FtpBackupResidue.CreateBackupPath($"/srv/{originalName}");
        return minted[(minted.LastIndexOf('/') + 1)..];
    }

    private static SftpFileInfo File(string name) => Entry(name, RemoteEntryKind.File);

    private static SftpFileInfo Entry(string name, RemoteEntryKind kind) => new(
        name,
        $"/srv/{name}",
        kind,
        Size: 0,
        LastModified: DateTime.UnixEpoch,
        Permissions: "rw-r--r--",
        Owner: "0",
        Group: "0");
}
