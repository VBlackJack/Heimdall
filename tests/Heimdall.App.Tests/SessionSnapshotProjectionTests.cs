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
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// Which sessions are written to the workspace snapshot, and in what order.
/// </summary>
/// <remarks>
/// This had no coverage while it lived in the shell view model. The exclusions carry the weight:
/// the snapshot is replayed through <c>RestoreServerAsync</c> at the next launch, so an entry that
/// should not be there reopens the wrong thing rather than merely being untidy.
/// </remarks>
public sealed class SessionSnapshotProjectionTests
{
    [Fact]
    public void AConnectedSession_IsSnapshotted()
    {
        IReadOnlyList<SessionSnapshotEntry> entries =
            SessionSnapshotProjection.FromSessions([Session("srv-1", "SSH")]);

        SessionSnapshotEntry entry = Assert.Single(entries);
        Assert.Equal("srv-1", entry.ServerId);
        Assert.Equal("SSH", entry.ConnectionType);
        Assert.Equal(0, entry.Order);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ASessionWithNoServerToPointAt_IsExcluded(string serverId)
    {
        // Nothing to restore: the replay calls RestoreServerAsync with this identifier.
        Assert.Empty(SessionSnapshotProjection.FromSessions([Session(serverId, "SSH")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ASessionWithNoConnectionType_IsExcluded(string connectionType)
    {
        Assert.Empty(SessionSnapshotProjection.FromSessions([Session("srv-1", connectionType)]));
    }

    [Theory]
    [InlineData("TOOL:HASH")]
    [InlineData("tool:hash")]
    [InlineData("Tool:Hash")]
    public void AToolTab_IsExcluded_WhateverItsCase(string connectionType)
    {
        // A tool tab is not a server. The comparison is case-insensitive because that is what the
        // shell used here, and because the identifier after the prefix keeps the tool's own
        // spelling.
        Assert.Empty(SessionSnapshotProjection.FromSessions([Session("srv-1", connectionType)]));
    }

    [Fact]
    public void ATabTypedAsAToolWithNoToolBehindIt_IsAlsoExcluded()
    {
        // Documenting the existing semantics rather than changing them: the shell's own predicate
        // is a bare prefix test, so "TOOL:" with nothing after it is excluded too. Note that
        // ConnectionTypeCatalog.IsTool additionally requires something after the prefix and would
        // therefore KEEP this session, which is why that helper was not reused here.
        Assert.Empty(SessionSnapshotProjection.FromSessions([Session("srv-1", "TOOL:")]));
    }

    [Fact]
    public void TheRelativeOrderOfTheKeptSessions_IsPreserved()
    {
        IReadOnlyList<SessionSnapshotEntry> entries = SessionSnapshotProjection.FromSessions(
            [Session("srv-1", "SSH"), Session("srv-2", "RDP"), Session("srv-3", "SFTP")]);

        // The restore path sorts on Order, so this sequence is what decides the tab order the
        // operator gets back.
        Assert.Equal(["srv-1", "srv-2", "srv-3"], entries.Select(entry => entry.ServerId));
        Assert.Equal([0, 1, 2], entries.Select(entry => entry.Order));
    }

    [Fact]
    public void ExcludedSessionsLeaveNoHolesInTheOrder()
    {
        IReadOnlyList<SessionSnapshotEntry> entries = SessionSnapshotProjection.FromSessions(
        [
            Session("srv-1", "TOOL:HASH"),
            Session("srv-2", "SSH"),
            Session("", "RDP"),
            Session("srv-4", "SFTP"),
        ]);

        // Numbered after filtering. Numbering before would leave gaps that describe tabs the
        // snapshot does not contain.
        Assert.Equal(["srv-2", "srv-4"], entries.Select(entry => entry.ServerId));
        Assert.Equal([0, 1], entries.Select(entry => entry.Order));
    }

    [Fact]
    public void NoSessions_ProducesNoEntries()
    {
        Assert.Empty(SessionSnapshotProjection.FromSessions([]));
    }

    [Fact]
    public void TheShellDelegatesTheProjection()
    {
        // Wiring at the source: a reinstated inline query would keep every test above green.
        string mainViewModel = File.ReadAllText(FindShellFile("MainViewModel.cs"));

        Assert.Contains(
            "SessionSnapshotProjection.FromSessions(Connection.ActiveSessions)",
            mainViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"TOOL:\"", mainViewModel, StringComparison.Ordinal);
    }

    private static SessionTabViewModel Session(string serverId, string connectionType)
    {
        return new SessionTabViewModel
        {
            OriginalServerId = serverId,
            ConnectionType = connectionType,
        };
    }

    private static string FindShellFile(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                string[] matches = Directory.GetFiles(
                    Path.Combine(directory, "src", "Heimdall.App"),
                    fileName,
                    SearchOption.AllDirectories);
                return Assert.Single(matches);
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
