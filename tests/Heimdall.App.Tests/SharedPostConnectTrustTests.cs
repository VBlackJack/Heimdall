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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class SharedPostConnectTrustTests : IAsyncLifetime
{
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

    private Task<bool> Confirm()
    {
        _confirmCalls++;
        return Task.FromResult(_confirmAnswer);
    }
}
