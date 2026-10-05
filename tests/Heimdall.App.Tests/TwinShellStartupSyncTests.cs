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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The "sync on startup" option of the command library was saved and read by nothing.
/// </summary>
public sealed class TwinShellStartupSyncTests
{
    [Fact]
    public async Task SyncOnStartup_WhenAskedAndConfigured_RefreshesTheBridgeThenRunsAFullSync()
    {
        List<string> calls = [];
        RecordingSettingsBridge bridge = new(calls);
        RecordingGitSync gitSync = new(calls, GitOperationResult.Ok("done"));

        await TwinShellBootstrapper.SyncOnStartupAsync(Configured(onStartup: true), bridge, gitSync);

        Assert.Equal(["load", "full-sync"], calls);
    }

    [Theory]
    [InlineData(false, true, "https://example.invalid/repo.git")]
    [InlineData(true, false, "https://example.invalid/repo.git")]
    [InlineData(true, true, "  ")]
    public async Task SyncOnStartup_WhenNotAskedOrNotConfigured_DoesNothing(bool onStartup, bool enabled, string url)
    {
        List<string> calls = [];
        AppSettings settings = new()
        {
            CmdLibGitSyncOnStartup = onStartup,
            CmdLibGitSyncEnabled = enabled,
            CmdLibGitSyncUrl = url,
        };

        await TwinShellBootstrapper.SyncOnStartupAsync(
            settings,
            new RecordingSettingsBridge(calls),
            new RecordingGitSync(calls, GitOperationResult.Ok()));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task SyncOnStartup_WhenTheSyncThrows_DoesNotFailTheStartup()
    {
        List<string> calls = [];
        RecordingGitSync gitSync = new(calls, null);

        await TwinShellBootstrapper.SyncOnStartupAsync(
            Configured(onStartup: true),
            new RecordingSettingsBridge(calls),
            gitSync);

        Assert.Equal(["load", "full-sync"], calls);
    }

    private static AppSettings Configured(bool onStartup) => new()
    {
        CmdLibGitSyncOnStartup = onStartup,
        CmdLibGitSyncEnabled = true,
        CmdLibGitSyncUrl = "https://example.invalid/repo.git",
    };

    private sealed class RecordingSettingsBridge(List<string> calls) : ISettingsService
    {
        public UserSettings CurrentSettings { get; } = new();

        public Task<UserSettings> LoadSettingsAsync()
        {
            calls.Add("load");
            return Task.FromResult(CurrentSettings);
        }

        public Task<bool> SaveSettingsAsync(UserSettings settings) => Task.FromResult(true);

        public Task<UserSettings> ResetToDefaultAsync() => Task.FromResult(new UserSettings());

        public string GetSettingsFilePath() => string.Empty;

        public bool ValidateSettings(UserSettings settings) => true;
    }

    /// <summary>Records the full sync; a null result makes it throw.</summary>
    private sealed class RecordingGitSync(List<string> calls, GitOperationResult? result) : IGitSyncService
    {
        public bool IsConfigured => true;

        public bool IsOperationInProgress => false;

        public string StatusMessage => string.Empty;

#pragma warning disable CS0067 // Required by the interface, never raised here.
        public event EventHandler<GitSyncStatusEventArgs>? StatusChanged;
#pragma warning restore CS0067

        public Task<GitOperationResult> InitializeRepositoryAsync() => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> PullAndImportAsync() => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> ExportAndPushAsync(string? commitMessage = null)
            => Task.FromResult(GitOperationResult.Ok());

        public Task<GitOperationResult> FullSyncAsync()
        {
            calls.Add("full-sync");
            return result is null
                ? throw new InvalidOperationException("sync failed")
                : Task.FromResult(result);
        }

        public Task<GitOperationResult> TestConnectionAsync() => Task.FromResult(GitOperationResult.Ok());

        public Task<GitRepositoryStatus> GetRepositoryStatusAsync()
            => Task.FromResult(new GitRepositoryStatus());

        public void CancelOperation()
        {
        }
    }
}
