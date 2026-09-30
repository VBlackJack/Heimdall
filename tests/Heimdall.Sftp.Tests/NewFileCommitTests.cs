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

using Renci.SshNet.Common;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The commits an upload without consent to replace goes through: the SFTP version 3 rename and the
/// FTP re-check before the move. Neither may replace a destination it can see, and neither may need
/// anything beyond the transport's own file channel.
/// </summary>
public sealed class NewFileCommitTests
{
    private const string TempPath = "/srv/app/new.txt.0123.part";
    private const string FinalPath = "/srv/app/new.txt";

    [Fact]
    public void SftpCreate_PublishesWithThePlainRenameOnce_AndDoesNotProbeOnSuccess()
    {
        List<(string Temp, string Final)> renames = [];
        int probes = 0;

        SftpAtomicUpload.CommitCreate(
            TempPath,
            FinalPath,
            (temp, final) => renames.Add((temp, final)),
            _ =>
            {
                probes++;
                return false;
            });

        Assert.Equal((TempPath, FinalPath), Assert.Single(renames));
        Assert.Equal(0, probes);
    }

    // The late collision: the server refused the rename because the name is now taken. It is the typed
    // refusal the view model already reports, carrying the server's failure as its cause.
    [Fact]
    public void SftpCreate_RenameRefusedOnAnOccupiedName_IsTheTypedCollision()
    {
        SshException serverRefusal = new("Failure");

        RemoteDestinationExistsException collision = Assert.Throws<RemoteDestinationExistsException>(
            () => SftpAtomicUpload.CommitCreate(
                TempPath,
                FinalPath,
                (_, _) => throw serverRefusal,
                path => path == FinalPath));

        Assert.Equal(FinalPath, collision.RemotePath);
        Assert.Same(serverRefusal, collision.InnerException);
    }

    [Fact]
    public void SftpCreate_RenameFailedWithTheNameStillFree_PropagatesTheRenameFailure()
    {
        SftpPermissionDeniedException denied = new("Permission denied");

        SftpPermissionDeniedException thrown = Assert.Throws<SftpPermissionDeniedException>(
            () => SftpAtomicUpload.CommitCreate(TempPath, FinalPath, (_, _) => throw denied, _ => false));

        Assert.Same(denied, thrown);
    }

    [Fact]
    public void SftpCreate_ProbeFailureAfterAFailedRename_PropagatesTheRenameFailure()
    {
        SshException renameFailure = new("Failure");

        SshException thrown = Assert.Throws<SshException>(
            () => SftpAtomicUpload.CommitCreate(
                TempPath,
                FinalPath,
                (_, _) => throw renameFailure,
                _ => throw new IOException("connection reset")));

        Assert.Same(renameFailure, thrown);
    }

    [Fact]
    public async Task FtpCreate_FreeName_MovesTheTempOnce_AndNeverDeletes()
    {
        HashSet<string> remote = new(StringComparer.Ordinal) { TempPath };
        List<(string Source, string Destination)> moves = [];

        await FtpAtomicUpload.CommitCreateAsync(
            TempPath,
            FinalPath,
            (path, _) => Task.FromResult(remote.Contains(path)),
            (source, destination, _) =>
            {
                moves.Add((source, destination));
                remote.Remove(source);
                remote.Add(destination);
                return Task.FromResult(true);
            });

        Assert.Equal((TempPath, FinalPath), Assert.Single(moves));
        Assert.Contains(FinalPath, remote);
    }

    // The replacing commit moves an occupied destination aside and publishes over it. Without consent
    // the same occupied destination must be refused and left exactly where it was.
    [Fact]
    public async Task FtpCreate_OccupiedName_IsRefusedWithoutAnyMove()
    {
        HashSet<string> remote = new(StringComparer.Ordinal) { TempPath, FinalPath };
        List<(string Source, string Destination)> moves = [];

        RemoteDestinationExistsException collision = await Assert.ThrowsAsync<RemoteDestinationExistsException>(
            () => FtpAtomicUpload.CommitCreateAsync(
                TempPath,
                FinalPath,
                (path, _) => Task.FromResult(remote.Contains(path)),
                (source, destination, _) =>
                {
                    moves.Add((source, destination));
                    return Task.FromResult(true);
                }));

        Assert.Equal(FinalPath, collision.RemotePath);
        Assert.Empty(moves);
    }

    [Fact]
    public async Task FtpCreate_MoveDeclinedBecauseTheNameWasTaken_IsTheTypedCollision()
    {
        HashSet<string> remote = new(StringComparer.Ordinal) { TempPath };

        await Assert.ThrowsAsync<RemoteDestinationExistsException>(
            () => FtpAtomicUpload.CommitCreateAsync(
                TempPath,
                FinalPath,
                (path, _) => Task.FromResult(remote.Contains(path)),
                (_, destination, _) =>
                {
                    // Another client created the destination between the check and the move.
                    remote.Add(destination);
                    return Task.FromResult(false);
                }));
    }

    [Fact]
    public async Task FtpCreate_MoveDeclinedWithTheNameStillFree_IsAFailedCommit()
    {
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => FtpAtomicUpload.CommitCreateAsync(
                TempPath,
                FinalPath,
                (_, _) => Task.FromResult(false),
                (_, _, _) => Task.FromResult(false)));

        Assert.IsNotType<RemoteDestinationExistsException>(failure);
    }
}
