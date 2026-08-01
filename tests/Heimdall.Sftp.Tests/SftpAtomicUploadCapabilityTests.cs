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
    [Fact]
    public void CommitRename_DemotesCapabilityFailure_WhenPredicateAllows()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> renameCalls = [];
        List<string> deleteCalls = [];
        int predicateCalls = 0;

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new NotSupportedException("extension unavailable"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            deleteRemote: path =>
            {
                deleteCalls.Add(path);
                remote.Remove(path);
            },
            canDemoteAtomicRenameFailure: exception =>
            {
                predicateCalls++;
                return exception is NotSupportedException;
            });

        Assert.Equal(1, predicateCalls);
        Assert.Equal(2, renameCalls.Count);
        Assert.Equal("/srv/app/config.txt", renameCalls[0].Source);
        Assert.StartsWith("/srv/app/config.txt.", renameCalls[0].Destination, StringComparison.Ordinal);
        Assert.EndsWith(".bak", renameCalls[0].Destination, StringComparison.Ordinal);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), renameCalls[1]);
        Assert.Equal(renameCalls[0].Destination, Assert.Single(deleteCalls));
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(renameCalls[0].Destination, remote);
    }

    [Fact]
    public void CommitRename_PropagatesNonCapabilityFailure_WithoutMutatingTarget()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        UnauthorizedAccessException atomicFailure = new("permission denied");
        List<(string Source, string Destination)> renameCalls = [];
        List<string> deleteCalls = [];
        int existsCalls = 0;

        UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(() =>
            SftpAtomicUpload.CommitRename(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                atomicRename: (_, _) => throw atomicFailure,
                plainRename: (source, destination) => renameCalls.Add((source, destination)),
                remoteExists: path =>
                {
                    existsCalls++;
                    return remote.Contains(path);
                },
                deleteRemote: path => deleteCalls.Add(path),
                canDemoteAtomicRenameFailure: _ => false));

        Assert.Same(atomicFailure, exception);
        Assert.Equal(0, existsCalls);
        Assert.Empty(renameCalls);
        Assert.Empty(deleteCalls);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
    }

    [Fact]
    public void CommitRename_DemotesArbitraryFailure_WhenPredicateIsOmitted()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> renameCalls = [];
        List<string> deleteCalls = [];

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new InvalidOperationException("arbitrary failure"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            deleteRemote: path =>
            {
                deleteCalls.Add(path);
                remote.Remove(path);
            });

        Assert.Equal(2, renameCalls.Count);
        Assert.Equal("/srv/app/config.txt", renameCalls[0].Source);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), renameCalls[1]);
        Assert.Equal(renameCalls[0].Destination, Assert.Single(deleteCalls));
        Assert.Contains("/srv/app/config.txt", remote);
    }

    [Fact]
    public void CommitRename_RejectsNonRegularTarget_BeforeFallbackMutation()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> renameCalls = [];
        List<string> deleteCalls = [];
        List<string> typeProbeCalls = [];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SftpAtomicUpload.CommitRename(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                atomicRename: (_, _) => throw new NotSupportedException("extension unavailable"),
                plainRename: (source, destination) => renameCalls.Add((source, destination)),
                remoteExists: remote.Contains,
                deleteRemote: path => deleteCalls.Add(path),
                canDemoteAtomicRenameFailure: failure => failure is NotSupportedException,
                isExistingTargetRegularFile: path =>
                {
                    typeProbeCalls.Add(path);
                    return false;
                }));

        Assert.Contains("/srv/app/config.txt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a regular file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("/srv/app/config.txt", Assert.Single(typeProbeCalls));
        Assert.Empty(renameCalls);
        Assert.Empty(deleteCalls);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
    }

    [Fact]
    public void CommitRename_ContinuesFallback_ForRegularTarget()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> renameCalls = [];
        List<string> deleteCalls = [];
        List<string> typeProbeCalls = [];

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new NotSupportedException("extension unavailable"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            deleteRemote: path =>
            {
                deleteCalls.Add(path);
                remote.Remove(path);
            },
            canDemoteAtomicRenameFailure: failure => failure is NotSupportedException,
            isExistingTargetRegularFile: path =>
            {
                typeProbeCalls.Add(path);
                return true;
            });

        Assert.Equal("/srv/app/config.txt", Assert.Single(typeProbeCalls));
        Assert.Equal(2, renameCalls.Count);
        Assert.Equal("/srv/app/config.txt", renameCalls[0].Source);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), renameCalls[1]);
        Assert.Equal(renameCalls[0].Destination, Assert.Single(deleteCalls));
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain(renameCalls[0].Destination, remote);
    }

    [Fact]
    public void CommitRename_DoesNotConsultFallbackDelegates_WhenAtomicRenameSucceeds()
    {
        List<(string Source, string Destination)> atomicRenameCalls = [];
        List<(string Source, string Destination)> plainRenameCalls = [];
        List<string> deleteCalls = [];
        int predicateCalls = 0;
        int existsCalls = 0;
        int typeProbeCalls = 0;

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (source, destination) => atomicRenameCalls.Add((source, destination)),
            plainRename: (source, destination) => plainRenameCalls.Add((source, destination)),
            remoteExists: _ =>
            {
                existsCalls++;
                return true;
            },
            deleteRemote: path => deleteCalls.Add(path),
            canDemoteAtomicRenameFailure: _ =>
            {
                predicateCalls++;
                return true;
            },
            isExistingTargetRegularFile: _ =>
            {
                typeProbeCalls++;
                return true;
            });

        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), Assert.Single(atomicRenameCalls));
        Assert.Equal(0, predicateCalls);
        Assert.Equal(0, existsCalls);
        Assert.Equal(0, typeProbeCalls);
        Assert.Empty(plainRenameCalls);
        Assert.Empty(deleteCalls);
    }
}
