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
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Constants;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;
using Fixture = TwinShell.Infrastructure.Tests.GitSyncServiceCancellationTests.GitRepositoryFixture;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins that the messages a sync returns, which the Command Library shows in its sync summary,
/// come from locale keys and carry their figures.
/// </summary>
/// <remarks>
/// They were English sentences built in the service, so "Pull completed. 1 commits merged." reached
/// a French or Spanish operator as it was. Driven against a real pair of Git repositories, under a
/// localizer that hands back the key and the arguments it was given: a literal sentence cannot
/// equal that, and a figure dropped from the call changes it.
/// </remarks>
public sealed class GitSyncServiceResultMessageTests
{
    private const string ArgumentSeparator = "|";

    [Fact]
    public async Task Pull_ReturnsThePullCompletedKey_WithTheMergedCommitCount()
    {
        using Fixture fixture = Fixture.Create();
        PushOneCommitFromAnotherClone(fixture);
        using GitSyncService service = CreateService(fixture, autoPush: false);

        GitOperationResult result = await service.PullAndImportAsync();

        result.Success.Should().BeTrue(result.ErrorDetails);
        result.CommitsMerged.Should().Be(1, "the other clone pushed one commit");
        result.Message.Should().Be(MessageKeys.GitSyncResultPullCompleted + ArgumentSeparator + "1");
    }

    /// <summary>
    /// The full sync is what the Command Library's Sync button runs; with auto-push on, its
    /// message is the one the summary dialog shows.
    /// </summary>
    [Fact]
    public async Task FullSync_ReturnsTheSyncCompleteKey_WithTheImportedAndExportedCounts()
    {
        using Fixture fixture = Fixture.Create();
        GitSyncServiceCancellationTests.ControllableSyncService syncService = new()
        {
            ImportHandler = (string _, CancellationToken _) => Task.FromResult(
                new SyncImportResult { Success = true, ActionsCreated = 2, ActionsUpdated = 1 }),
            ExportHandler = (string _, CancellationToken _) => Task.FromResult(
                new SyncExportResult { Success = true, ActionsExported = 5 }),
        };
        using GitSyncService service = CreateService(fixture, autoPush: true, syncService);

        GitOperationResult result = await service.FullSyncAsync();

        result.Success.Should().BeTrue(result.ErrorDetails);
        result.Message.Should().Be(
            MessageKeys.GitSyncResultSyncComplete + ArgumentSeparator + "3" + ArgumentSeparator + "5");
    }

    private static GitSyncService CreateService(
        Fixture fixture,
        bool autoPush,
        GitSyncServiceCancellationTests.ControllableSyncService? syncService = null)
    {
        UserSettings settings = new()
        {
            GitRepositoryPath = fixture.LocalPath,
            GitRemoteUrl = fixture.BarePath,
            GitBranch = fixture.BranchName,
            GitUserName = "TwinShell Tests",
            GitUserEmail = "twinshell-tests@example.local",
            GitAutoPush = autoPush,
        };

        return new GitSyncService(
            new GitSyncServiceCancellationTests.FakeSettingsService(settings),
            NullLogger<GitSyncService>.Instance,
            new ArgumentEchoLocalizationService(),
            new GitSyncServiceCancellationTests.SingleServiceScopeFactory(
                syncService ?? new GitSyncServiceCancellationTests.ControllableSyncService()));
    }

    /// <summary>
    /// Clones the shared repository beside the local one, commits a file and pushes it, so the
    /// local repository is one commit behind its remote.
    /// </summary>
    private static void PushOneCommitFromAnotherClone(Fixture fixture)
    {
        string otherPath = Path.Combine(fixture.RootPath, "other");
        Repository.Clone(fixture.BarePath, otherPath, new CloneOptions { BranchName = fixture.BranchName });

        using Repository other = new(otherPath);
        File.WriteAllText(Path.Combine(otherPath, "remote-edit.txt"), "edited on another machine");
        Commands.Stage(other, "remote-edit.txt");
        Signature signature = new("Other Machine", "other-machine@example.local", DateTimeOffset.UtcNow);
        other.Commit("Remote edit", signature, signature);
        other.Network.Push(other.Network.Remotes["origin"], "refs/heads/" + fixture.BranchName, new PushOptions());
    }

    /// <summary>
    /// Hands back the key, then each argument, so the test reads which message was asked for and
    /// with which figures.
    /// </summary>
    private sealed class ArgumentEchoLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

        public CultureInfo[] SupportedCultures => [CultureInfo.InvariantCulture];

        public event EventHandler? LanguageChanged;

        public void ChangeLanguage(CultureInfo culture)
            => LanguageChanged?.Invoke(this, EventArgs.Empty);

        public void ChangeLanguage(string cultureCode)
            => LanguageChanged?.Invoke(this, EventArgs.Empty);

        public string GetString(string key)
            => key;

        public string GetString(string key, string fallback)
            => key;

        public string GetFormattedString(string key, params object[] args)
            => string.Join(
                ArgumentSeparator,
                args.Select(arg => Convert.ToString(arg, CultureInfo.InvariantCulture)).Prepend(key));
    }
}
