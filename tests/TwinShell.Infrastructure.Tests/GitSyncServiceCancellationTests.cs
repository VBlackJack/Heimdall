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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Infrastructure.Services;

namespace TwinShell.Infrastructure.Tests;

public sealed class GitSyncServiceCancellationTests
{
    [Fact]
    public void CancelOperation_WhenIdle_NeverThrows()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        ControllableSyncService syncService = new();
        using GitSyncService service = CreateService(fixture, syncService);

        System.Action idleCancel = () =>
        {
            service.CancelOperation();
            service.CancelOperation();
            service.CancelOperation();
        };

        idleCancel.Should().NotThrow();

        service.Dispose();
        System.Action disposedCancel = () => service.CancelOperation();

        disposedCancel.Should().NotThrow();
    }

    [Fact]
    public async Task CancelOperation_DuringOperation_IsThreadSafe()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        TaskCompletionSource<bool> exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControllableSyncService syncService = new()
        {
            ExportHandler = async (string _, CancellationToken cancellationToken) =>
            {
                exportStarted.TrySetResult(true);
                await releaseExport.Task.WaitAsync(cancellationToken);
                return new SyncExportResult { Success = true };
            }
        };
        using GitSyncService service = CreateService(fixture, syncService);
        Task<GitOperationResult> operationTask = service.ExportAndPushAsync("race cancellation test");
        await exportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancelTask = Task.Run(async () =>
        {
            while (!operationTask.IsCompleted)
            {
                service.CancelOperation();
                await Task.Yield();
            }

            service.CancelOperation();
        });

        try
        {
            GitOperationResult result = await operationTask.WaitAsync(TimeSpan.FromSeconds(10));

            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(GitSyncErrorCode.Cancelled);
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseExport.TrySetResult(true);
        }
    }

    [Fact]
    public async Task PullAndImport_WhenImportThrowsOperationCanceled_ReturnsCancelled()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        ControllableSyncService syncService = new()
        {
            ImportHandler = (string _, CancellationToken cancellationToken) =>
                throw new OperationCanceledException(cancellationToken)
        };
        using GitSyncService service = CreateService(fixture, syncService);

        GitOperationResult result = await service.PullAndImportAsync();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(GitSyncErrorCode.Cancelled);
        syncService.LastImportCancellationToken.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAndPush_WhenExportThrowsOperationCanceled_ReturnsCancelled()
    {
        using GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        ControllableSyncService syncService = new()
        {
            ExportHandler = (string _, CancellationToken cancellationToken) =>
                throw new OperationCanceledException(cancellationToken)
        };
        using GitSyncService service = CreateService(fixture, syncService);

        GitOperationResult result = await service.ExportAndPushAsync("cancelled export");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(GitSyncErrorCode.Cancelled);
        syncService.LastExportCancellationToken.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public void RepositoryFixture_Dispose_RemovesTheWholeTree()
    {
        GitRepositoryFixture fixture = GitRepositoryFixture.Create();
        string objectsPath = Path.Combine(fixture.LocalPath, ".git", "objects");
        Directory.EnumerateFiles(objectsPath, "*", SearchOption.AllDirectories)
            .Should().Contain(filePath => File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));

        fixture.Dispose();

        Directory.Exists(fixture.RootPath).Should().BeFalse();
    }

    [Fact]
    public void FileLockHolders_NamesTheProcessHoldingAFile()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "heimdall_lock_holder_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream held = new(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                FileLockHolders.Describe(filePath)
                    .Should().Contain(holder => holder.EndsWith("(" + Environment.ProcessId + ")", StringComparison.Ordinal));
            }

            FileLockHolders.Describe(filePath).Should().BeEmpty();
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static GitSyncService CreateService(
        GitRepositoryFixture fixture,
        ControllableSyncService syncService)
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

    /// <summary>
    /// Minimal scope factory whose scopes resolve only <see cref="ISyncService"/>
    /// (to the supplied fake) and return null for everything else, enough for
    /// <see cref="GitSyncService"/> to run import/export through a scope while the
    /// optional sync-history repository stays absent.
    /// </summary>
    internal sealed class SingleServiceScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly ISyncService _syncService;

        internal SingleServiceScopeFactory(ISyncService syncService)
            => _syncService = syncService;

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ISyncService) ? _syncService : null;

        public void Dispose()
        {
        }
    }

    internal sealed class GitRepositoryFixture : IDisposable
    {
        private GitRepositoryFixture(string rootPath, string localPath, string barePath, string branchName)
        {
            RootPath = rootPath;
            LocalPath = localPath;
            BarePath = barePath;
            BranchName = branchName;
        }

        internal string RootPath { get; }

        internal string LocalPath { get; }

        internal string BarePath { get; }

        internal string BranchName { get; }

        /// <summary>
        /// Creates the fixture in a fresh GUID directory. When Windows refuses libgit2's
        /// replace-rename of <c>.git/config.lock</c> (seen once on 2026-10-15, not reproduced
        /// in 48000 attempts since), the failure is rethrown with the processes that the
        /// Restart Manager reports as holding the file, so the next occurrence names its
        /// cause instead of leaving it to inference. It is never retried: a retry would hide
        /// the evidence while the cause is unproven.
        /// </summary>
        internal static GitRepositoryFixture Create()
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall_git_sync_" + Guid.NewGuid().ToString("N"));
            string localPath = Path.Combine(rootPath, "local");
            string barePath = Path.Combine(rootPath, "remote.git");
            try
            {
                string branchName = InitializeRepositories(localPath, barePath);
                return new GitRepositoryFixture(rootPath, localPath, barePath, branchName);
            }
            catch (LibGit2SharpException ex) when (ex.Message.Contains(LockfileRenameFailure, StringComparison.Ordinal))
            {
                string holders = DescribeHolders(localPath, barePath);
                DeleteTree(rootPath);
                throw new InvalidOperationException(ex.Message + " | holders: " + holders, ex);
            }
            catch
            {
                DeleteTree(rootPath);
                throw;
            }
        }

        private const string LockfileRenameFailure = "failed to rename lockfile";

        private static string DescribeHolders(string localPath, string barePath)
        {
            IEnumerable<string> candidates = new[] { localPath, barePath }
                .Select(repositoryPath => Path.Combine(repositoryPath, ".git", "config"))
                .Append(Path.Combine(barePath, "config"))
                .SelectMany(configPath => new[] { configPath, configPath + ".lock" });
            List<string> holders = candidates
                .SelectMany(filePath => FileLockHolders.Describe(filePath)
                    .Select(holder => Path.GetFileName(filePath) + "=" + holder))
                .ToList();
            return holders.Count == 0 ? "none reported" : string.Join(", ", holders);
        }

        private static string InitializeRepositories(string localPath, string barePath)
        {
            Directory.CreateDirectory(localPath);
            Directory.CreateDirectory(barePath);

            Repository.Init(barePath, isBare: true);
            Repository.Init(localPath);

            using Repository repository = new(localPath);
            string readmePath = Path.Combine(localPath, "README.md");
            File.WriteAllText(readmePath, "TwinShell sync test repository.");
            Commands.Stage(repository, "README.md");

            Signature signature = new(
                "TwinShell Tests",
                "twinshell-tests@example.local",
                DateTimeOffset.UtcNow);
            repository.Commit("Initial commit", signature, signature);

            Branch branch = repository.Head;
            string branchName = branch.FriendlyName;
            Remote remote = repository.Network.Remotes.Add("origin", barePath);
            repository.Network.Push(remote, "refs/heads/" + branchName, new PushOptions());
            repository.Branches.Update(
                branch,
                branchUpdater => branchUpdater.Remote = "origin",
                branchUpdater => branchUpdater.UpstreamBranch = branch.CanonicalName);

            return branchName;
        }

        public void Dispose() => DeleteTree(RootPath);

        /// <summary>
        /// Deletes a fixture tree. Git writes its object files read-only, which makes
        /// <see cref="Directory.Delete(string, bool)"/> refuse the whole tree, so the
        /// attributes are cleared first.
        /// </summary>
        internal static void DeleteTree(string rootPath)
        {
            if (!Directory.Exists(rootPath))
            {
                return;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                }

                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal sealed class ControllableSyncService : ISyncService
    {
        internal Func<string, CancellationToken, Task<SyncImportResult>> ImportHandler { get; set; } =
            (string _, CancellationToken _) => Task.FromResult(new SyncImportResult { Success = true });

        internal Func<string, CancellationToken, Task<SyncExportResult>> ExportHandler { get; set; } =
            (string _, CancellationToken _) => Task.FromResult(new SyncExportResult { Success = true });

        internal CancellationToken LastImportCancellationToken { get; private set; }

        internal CancellationToken LastExportCancellationToken { get; private set; }

        public Task<SyncExportResult> ExportDataToYamlAsync(
            string rootFolderPath,
            CancellationToken cancellationToken = default)
        {
            LastExportCancellationToken = cancellationToken;
            return ExportHandler(rootFolderPath, cancellationToken);
        }

        public Task<SyncImportResult> ImportDataFromYamlAsync(
            string rootFolderPath,
            CancellationToken cancellationToken = default)
        {
            LastImportCancellationToken = cancellationToken;
            return ImportHandler(rootFolderPath, cancellationToken);
        }

        public Task<SyncValidationResult> ValidateFolderAsync(
            string rootFolderPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncValidationResult { IsValid = true });
        }
    }

    internal sealed class FakeSettingsService : ISettingsService
    {
        internal FakeSettingsService(UserSettings settings)
        {
            CurrentSettings = settings;
        }

        public UserSettings CurrentSettings { get; private set; }

        public Task<UserSettings> LoadSettingsAsync()
            => Task.FromResult(CurrentSettings);

        public Task<bool> SaveSettingsAsync(UserSettings settings)
        {
            CurrentSettings = settings;
            return Task.FromResult(true);
        }

        public Task<UserSettings> ResetToDefaultAsync()
        {
            CurrentSettings = UserSettings.Default;
            return Task.FromResult(CurrentSettings);
        }

        public string GetSettingsFilePath()
            => "mem://settings.json";

        public bool ValidateSettings(UserSettings settings)
            => true;
    }

    internal sealed class FakeLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

        public CultureInfo[] SupportedCultures => [CultureInfo.InvariantCulture];

        public event EventHandler? LanguageChanged;

        public void ChangeLanguage(CultureInfo culture)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ChangeLanguage(string cultureCode)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetString(string key)
            => key;

        public string GetFormattedString(string key, params object[] args)
            => key;
    }
}
