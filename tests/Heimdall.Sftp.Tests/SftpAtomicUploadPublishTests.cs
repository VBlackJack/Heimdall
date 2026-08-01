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

using System.Reflection;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Unit tests for the publish-if-absent SFTP commit contract.
/// </summary>
public sealed class SftpAtomicUploadPublishTests
{
    [Fact]
    public void CommitPublishIfAbsent_PublishesTemp_WhenFinalIsAbsent()
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["/srv/file.txt.part"] = "new",
        };

        SftpAtomicUpload.CommitPublishIfAbsent(
            "/srv/file.txt.part",
            "/srv/file.txt",
            (source, destination) =>
            {
                string content = files[source];
                files.Remove(source);
                files.Add(destination, content);
            },
            files.ContainsKey);

        Assert.False(files.ContainsKey("/srv/file.txt.part"));
        Assert.Equal("new", files["/srv/file.txt"]);
    }

    [Fact]
    public void CommitPublishIfAbsent_HasNoAtomicRenameDelegate()
    {
        MethodInfo method = typeof(SftpAtomicUpload).GetMethod(
                nameof(SftpAtomicUpload.CommitPublishIfAbsent))
            ?? throw new InvalidOperationException("CommitPublishIfAbsent was not found.");
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Contains(parameters, parameter => parameter.Name == "plainRename");
        Assert.DoesNotContain(parameters, parameter => parameter.Name == "atomicRename");
    }

    [Fact]
    public void CommitPublishIfAbsent_NeverCreatesBackupPath()
    {
        List<(string Source, string Destination)> renames = [];

        SftpAtomicUpload.CommitPublishIfAbsent(
            "/srv/file.txt.part",
            "/srv/file.txt",
            (source, destination) => renames.Add((source, destination)),
            _ => false);

        (string Source, string Destination) rename = Assert.Single(renames);
        Assert.Equal(("/srv/file.txt.part", "/srv/file.txt"), rename);
        Assert.DoesNotContain(renames, call =>
            call.Source.EndsWith(".bak", StringComparison.Ordinal)
            || call.Destination.EndsWith(".bak", StringComparison.Ordinal));
    }

    [Fact]
    public void CommitPublishIfAbsent_RenameFailsAndFinalExists_ThrowsDestinationAlreadyExists()
    {
        InvalidOperationException renameFailure = new("rename failed");

        IOException exception = Assert.Throws<IOException>(() =>
            SftpAtomicUpload.CommitPublishIfAbsent(
                "/srv/file.txt.part",
                "/srv/file.txt",
                (_, _) => throw renameFailure,
                path => path == "/srv/file.txt"));

        Assert.Equal("Refused to copy: destination already exists: /srv/file.txt", exception.Message);
        Assert.Same(renameFailure, exception.InnerException);
    }

    [Fact]
    public void CommitPublishIfAbsent_RenameFailsAndFinalIsAbsent_RethrowsOriginalException()
    {
        InvalidOperationException renameFailure = new("rename failed");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SftpAtomicUpload.CommitPublishIfAbsent(
                "/srv/file.txt.part",
                "/srv/file.txt",
                (_, _) => throw renameFailure,
                _ => false));

        Assert.Same(renameFailure, exception);
    }

    [Fact]
    public void CommitPublishIfAbsent_RenameFails_DoesNotDeleteFinalPath()
    {
        HashSet<string> remotePaths = new(StringComparer.Ordinal)
        {
            "/srv/file.txt.part",
            "/srv/file.txt",
        };

        Assert.Throws<IOException>(() =>
            SftpAtomicUpload.CommitPublishIfAbsent(
                "/srv/file.txt.part",
                "/srv/file.txt",
                (_, _) => throw new InvalidOperationException("rename failed"),
                remotePaths.Contains));

        Assert.Contains("/srv/file.txt", remotePaths);
        Assert.Contains("/srv/file.txt.part", remotePaths);
    }
}
