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

using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Constants;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;
using static TwinShell.Infrastructure.Tests.GitSyncServiceCancellationTests;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// A pull that conflicts takes the remote side and puts the local library back on top of it,
/// and a push never runs on a repository left in the middle of a merge.
/// </summary>
/// <remarks>
/// A conflicted pull used to return with the merge half-done: the next push staged everything,
/// conflict markers included, and committed a merge that reverted the remote side.
/// </remarks>
public sealed class GitSyncServiceMergeSafetyTests
{
    private const string ConflictedFile = "README.md";

    private const string ExportedFile = "library.json";

    [Fact]
    public async Task APullThatConflicts_TakesTheRemote_ImportsIt_AndPushesTheLibraryBackOnTop()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        CreateDivergentHistory(fixture);
        bool imported = false;
        ControllableSyncService syncService = new()
        {
            ImportHandler = (string _, CancellationToken _) =>
            {
                imported = true;
                return Task.FromResult(new SyncImportResult { Success = true });
            },
            ExportHandler = (string folder, CancellationToken _) =>
            {
                File.WriteAllText(Path.Combine(folder, ExportedFile), "the library, from the database");
                return Task.FromResult(new SyncExportResult { Success = true, ActionsExported = 1 });
            }
        };
        using GitSyncService service = CreateService(fixture, syncService);

        GitOperationResult result = await service.PullAndImportAsync();

        result.Success.Should().BeTrue();
        result.ConflictedFiles.Should().Equal(ConflictedFile);
        imported.Should().BeTrue();
        using Repository repo = new(fixture.LocalPath);
        repo.Info.CurrentOperation.Should().Be(CurrentOperation.None);
        repo.RetrieveStatus().IsDirty.Should().BeFalse();
        File.ReadAllText(Path.Combine(fixture.LocalPath, ConflictedFile)).Should().Be("remote side");
        repo.Head.Tip.Parents.Single().Message.Should().StartWith("Teammate edit", "the export is committed on top of the remote side");
        using Repository remote = new(fixture.BarePath);
        remote.Branches[fixture.BranchName].Tip.Sha.Should().Be(repo.Head.Tip.Sha, "the result is pushed");
    }

    [Fact]
    public async Task APush_OnARepositoryInTheMiddleOfAMerge_IsRefused_BeforeAnyExport()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        CreateDivergentHistory(fixture);
        using (Repository repo = new(fixture.LocalPath))
        {
            Remote remote = repo.Network.Remotes["origin"];
            Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(spec => spec.Specification), new FetchOptions(), null);
            MergeResult merge = repo.Merge(repo.Head.TrackedBranch.Tip, Signature(), new MergeOptions { FailOnConflict = false });
            merge.Status.Should().Be(MergeStatus.Conflicts);
            repo.Info.CurrentOperation.Should().Be(CurrentOperation.Merge);
        }

        bool exported = false;
        ControllableSyncService syncService = new()
        {
            ExportHandler = (string _, CancellationToken _) =>
            {
                exported = true;
                return Task.FromResult(new SyncExportResult { Success = true });
            }
        };
        using GitSyncService service = CreateService(fixture, syncService);

        GitOperationResult result = await service.ExportAndPushAsync("must not run");

        result.Success.Should().BeFalse();
        result.Message.Should().Be(MessageKeys.GitSyncResultOperationInProgress);
        exported.Should().BeFalse();
    }

    /// <summary>
    /// Leaves the local branch one commit ahead and one behind its remote, both commits
    /// rewriting the same line of the same file. Returns the local commit.
    /// </summary>
    private static Commit CreateDivergentHistory(GitRepositoryFixture fixture)
    {
        string otherPath = Path.Combine(fixture.RootPath, "teammate");
        Repository.Clone(fixture.BarePath, otherPath, new CloneOptions { BranchName = fixture.BranchName });
        using (Repository teammate = new(otherPath))
        {
            File.WriteAllText(Path.Combine(otherPath, ConflictedFile), "remote side");
            Commands.Stage(teammate, ConflictedFile);
            teammate.Commit("Teammate edit", Signature(), Signature());
            teammate.Network.Push(teammate.Network.Remotes["origin"], "refs/heads/" + fixture.BranchName, new PushOptions());
        }

        using Repository local = new(fixture.LocalPath);
        File.WriteAllText(Path.Combine(fixture.LocalPath, ConflictedFile), "local side");
        Commands.Stage(local, ConflictedFile);
        return local.Commit("Local edit", Signature(), Signature());
    }

    private static Signature Signature()
        => new("TwinShell Tests", "twinshell-tests@example.local", DateTimeOffset.UtcNow);

    private static GitSyncService CreateService(GitRepositoryFixture fixture, ControllableSyncService syncService)
    {
        UserSettings settings = new()
        {
            GitRepositoryPath = fixture.LocalPath,
            GitRemoteUrl = fixture.BarePath,
            GitBranch = fixture.BranchName,
            GitUserName = "TwinShell Tests",
            GitUserEmail = "twinshell-tests@example.local"
        };

        return new GitSyncService(
            new FakeSettingsService(settings),
            NullLogger<GitSyncService>.Instance,
            new FakeLocalizationService(),
            new SingleServiceScopeFactory(syncService));
    }
}
