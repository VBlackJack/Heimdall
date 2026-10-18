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
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class SharedPostConnectTrustTests : IAsyncLifetime
{
    private const string RemovedSourceId = "removedsource";

    private const string KeptSourceId = "keptsource";

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "heimdall-shared-trust", Guid.NewGuid().ToString("N"));

    private ConfigManager _configManager = null!;
    private SharedPostConnectTrust _trust = null!;
    private int _confirmCalls;
    private bool _confirmAnswer = true;

    public async Task InitializeAsync()
    {
        _configManager = new ConfigManager(_rootPath);
        await _configManager.InitializeAsync();
        _trust = new SharedPostConnectTrust(_configManager);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstTime_AsksAndRecordsTheCommandsLocally()
    {
        ServerProfileDto profile = SharedSessionTestData.Create().Profile;

        bool allowed = await _trust.AuthorizeAsync(profile, Confirm);

        Assert.True(allowed);
        Assert.Equal(1, _confirmCalls);
        AppSettings settings = await _configManager.LoadSettingsAsync();
        Assert.Equal(
            PostConnectFingerprint.Compute(profile.PostConnectSteps),
            settings.SharedPostConnectApprovals[profile.Id]);
        Assert.False(profile.ExecutionConfirmed);
        Assert.Empty(await _configManager.LoadServersAsync());
    }

    [Fact]
    public async Task Accepted_RunsWithoutAskingAgain()
    {
        ServerProfileDto profile = SharedSessionTestData.Create().Profile;
        await _trust.AuthorizeAsync(profile, Confirm);

        bool allowed = await _trust.AuthorizeAsync(SharedSessionTestData.Create().Profile, Confirm);

        Assert.True(allowed);
        Assert.Equal(1, _confirmCalls);
    }

    [Fact]
    public async Task SameCommandsReadAgain_WithFreshStepIds_DoNotAskAgain()
    {
        ServerProfileDto profile = SharedSessionTestData.Create().Profile;
        await _trust.AuthorizeAsync(profile, Confirm);
        ServerProfileDto reread = SharedSessionTestData.Create().Profile;
        reread.PostConnectSteps[0].Id = Guid.NewGuid().ToString();

        Assert.True(await _trust.AuthorizeAsync(reread, Confirm));
        Assert.Equal(1, _confirmCalls);
    }

    [Fact]
    public async Task CommandsChangedInTheSharedFile_AskAgain()
    {
        ServerProfileDto profile = SharedSessionTestData.Create().Profile;
        await _trust.AuthorizeAsync(profile, Confirm);
        ServerProfileDto changed = SharedSessionTestData.Create().Profile;
        changed.PostConnectSteps = [new PostConnectStep { Input = "sudo su - root" }];

        _confirmAnswer = false;
        bool allowed = await _trust.AuthorizeAsync(changed, Confirm);

        Assert.False(allowed);
        Assert.Equal(2, _confirmCalls);
        Assert.Equal(
            PostConnectFingerprint.Compute(profile.PostConnectSteps),
            (await _configManager.LoadSettingsAsync()).SharedPostConnectApprovals[profile.Id]);
    }

    [Fact]
    public async Task Declined_RunsNothingAndRecordsNothing()
    {
        _confirmAnswer = false;
        ServerProfileDto profile = SharedSessionTestData.Create().Profile;

        bool allowed = await _trust.AuthorizeAsync(profile, Confirm);

        Assert.False(allowed);
        Assert.Empty((await _configManager.LoadSettingsAsync()).SharedPostConnectApprovals);
    }

    [Fact]
    public async Task RemovingASource_ForgetsTheApprovalsOfItsSessions_AndKeepsEveryOther()
    {
        string removed = SharedSessionIds.PrefixFor(RemovedSourceId) + "srv-db01";
        string kept = SharedSessionIds.PrefixFor(KeptSourceId) + "srv-db01";
        const string notShared = "local-profile";
        await _configManager.MergeSettingAsync(settings =>
        {
            settings.SharedSessionSources = [Source(RemovedSourceId), Source(KeptSourceId)];
            settings.SharedPostConnectApprovals[removed] = "fingerprint-removed";
            settings.SharedPostConnectApprovals[kept] = "fingerprint-kept";
            settings.SharedPostConnectApprovals[notShared] = "fingerprint-local";
        });
        using SharedSessionProvider provider = new(await LocalizerAsync(), approvals: _trust);
        await provider.LoadAsync(await _configManager.LoadSettingsAsync());
        Assert.Equal(3, (await _configManager.LoadSettingsAsync()).SharedPostConnectApprovals.Count);

        await _configManager.MergeSettingAsync(settings => settings.SharedSessionSources = [Source(KeptSourceId)]);
        await provider.LoadAsync(await _configManager.LoadSettingsAsync());

        Dictionary<string, string> approvals = (await _configManager.LoadSettingsAsync()).SharedPostConnectApprovals;
        Assert.False(approvals.ContainsKey(removed));
        Assert.Equal("fingerprint-kept", approvals[kept]);
        Assert.Equal("fingerprint-local", approvals[notShared]);
    }

    [Fact]
    public async Task ForgetAllExcept_KeepsASourceTheSavedSettingsListSinceTheLoadStarted()
    {
        // The kept list comes from the settings a load started with; a source added and approved
        // since is in the saved settings, and its approvals stay.
        string addedSince = SharedSessionIds.PrefixFor(KeptSourceId) + "srv-db01";
        await _configManager.MergeSettingAsync(settings =>
        {
            settings.SharedSessionSources = [Source(KeptSourceId)];
            settings.SharedPostConnectApprovals[addedSince] = "fingerprint";
        });

        await _trust.ForgetAllExceptAsync(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal("fingerprint", (await _configManager.LoadSettingsAsync()).SharedPostConnectApprovals[addedSince]);
    }

    private static SharedSessionSourceDto Source(string id) =>
        new() { Id = id, Name = id, Path = Path.Combine(Path.GetTempPath(), id + ".mxtsessions"), Enabled = false };

    private static async Task<LocalizationManager> LocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private Task<bool> Confirm()
    {
        _confirmCalls++;
        return Task.FromResult(_confirmAnswer);
    }
}
