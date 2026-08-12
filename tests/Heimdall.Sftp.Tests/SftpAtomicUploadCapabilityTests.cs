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

public sealed class SftpAtomicUploadCapabilityTests
{
    private const string FinalRemotePath = "/srv/app/config.txt";
    private const string TempRemotePath = "/srv/app/config.txt.part";

    [Fact]
    public void CommitRename_PropagatesNonDemotableFailure_WithoutFallback()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            FinalRemotePath,
            TempRemotePath
        };
        UnauthorizedAccessException atomicFailure = new("permission denied");
        List<(string Source, string Destination)> renameCalls = [];
        int existsCalls = 0;
        int predicateCalls = 0;

        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(() =>
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (_, _) => throw atomicFailure,
                plainRename: (source, destination) => renameCalls.Add((source, destination)),
                remoteExists: path =>
                {
                    existsCalls++;
                    return remote.Contains(path);
                },
                canDemoteAtomicRenameFailure: _ =>
                {
                    predicateCalls++;
                    return false;
                }));

        Assert.Same(atomicFailure, exception);
        Assert.Equal(1, predicateCalls);
        Assert.Equal(0, existsCalls);
        Assert.Empty(renameCalls);
        Assert.Contains(FinalRemotePath, remote);
        Assert.Contains(TempRemotePath, remote);
    }

    [Fact]
    public void CommitRename_DemotesCapabilityFailure_WhenDestinationIsAbsent()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            TempRemotePath
        };
        List<(string Source, string Destination)> renameCalls = [];
        int predicateCalls = 0;

        SftpAtomicUpload.CommitRename(
            TempRemotePath,
            FinalRemotePath,
            atomicRename: (_, _) => throw new NotSupportedException("posix-rename extension unavailable"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            canDemoteAtomicRenameFailure: exception =>
            {
                predicateCalls++;
                return exception is NotSupportedException;
            });

        Assert.Equal(1, predicateCalls);
        Assert.Equal((TempRemotePath, FinalRemotePath), Assert.Single(renameCalls));
        Assert.Contains(FinalRemotePath, remote);
        Assert.DoesNotContain(TempRemotePath, remote);
    }

    [Fact]
    public void CommitRename_DemotesArbitraryFailure_WhenPredicateIsOmitted()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            TempRemotePath
        };
        List<(string Source, string Destination)> renameCalls = [];

        SftpAtomicUpload.CommitRename(
            TempRemotePath,
            FinalRemotePath,
            atomicRename: (_, _) => throw new InvalidOperationException("arbitrary failure"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains);

        Assert.Equal((TempRemotePath, FinalRemotePath), Assert.Single(renameCalls));
        Assert.Contains(FinalRemotePath, remote);
        Assert.DoesNotContain(TempRemotePath, remote);
    }
}
