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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The session coordinator's reads of a shared profile, driven through the shell harness.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task ManualReconnect_SharedSession_IsFoundThroughTheLookupAndConnects()
    {
        SharedSessionTestData shared = SharedSessionTestData.Create();
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, shared.Provider));
        SessionTabViewModel source = harness.Main.Connection.AddSession(
            "source-runtime",
            shared.DirectProfile.DisplayName,
            "SSH");
        source.OriginalServerId = shared.DirectProfile.Id;
        ControlledProtocolHandler handler = harness.GetHandler("SSH");
        Action<SessionTabViewModel, string, string> callback = Assert.IsType<Action<SessionTabViewModel, string, string>>(
            harness.EmbeddedSessionManager.ReconnectRequestedCallback);

        EmbeddedSessionManager.ForwardReconnectRequest(source, ReconnectRequestContext.Manual, callback);

        // Reaching the handler is the proof: the reconnect looked the identifier up, found the
        // shared profile, and restored it. Before the lookup it answered "server not found".
        await handler.Started.Task.WaitAsync(TestTimeout);
        handler.Result.SetResult(new ConnectionResult(false, "recorded", null));
        Assert.NotEqual(harness.Main.GetLocalizer()["ErrorServerNotFound"], harness.Main.StatusText);
    }

    [Fact]
    public async Task SftpAutoOpen_AfterASharedSshSession_FindsTheSharedProfile()
    {
        SharedSessionTestData data = SharedSessionTestData.Create();
        ServerProfileDto withLogin = data.DirectProfile.CloneFaithfully();
        withLogin.SshUsername = "jdoe";
        var provider = new StubSharedSessionProvider([withLogin], [data.Gateway]);
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, provider));
        AppSettings settings = await harness.Main.ConfigManager.LoadSettingsAsync();
        settings.SftpBrowserEnabled = true;
        settings.SftpAutoOpenOnSsh = true;
        await harness.Main.ReloadConfigurationAsync(settings);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        Task<bool> restore = harness.Main.ServerList.RestoreServerAsync(withLogin.Id, CancellationToken.None);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await restore.WaitAsync(TestTimeout);

        // The companion pane asks for the profile by the shared identifier; without the lookup it
        // read servers.json, found nothing, and skipped the pane.
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "recorded", null));
    }

    [Fact]
    public async Task AuthorizePostConnect_SharedProfile_AsksOnceThenAgainOnlyWhenTheCommandsChange()
    {
        SharedSessionTestData shared = SharedSessionTestData.Create();
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, shared.Provider));

        Assert.True(await harness.Main.Session.AuthorizePostConnectAsync(shared.Profile, 1));
        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Contains(SharedSessionTestData.LoginCommand, harness.DialogService.LastConfirmMessage, StringComparison.Ordinal);

        Assert.True(await harness.Main.Session.AuthorizePostConnectAsync(shared.Provider.FindProfile(shared.Profile.Id)!, 1));
        Assert.Equal(1, harness.DialogService.ConfirmCallCount);

        ServerProfileDto changed = shared.Provider.FindProfile(shared.Profile.Id)!;
        changed.PostConnectSteps = [new PostConnectStep { Input = "sudo su - root" }];
        harness.DialogService.ConfirmResult = false;

        Assert.False(await harness.Main.Session.AuthorizePostConnectAsync(changed, 1));
        Assert.Equal(2, harness.DialogService.ConfirmCallCount);

        // The approval is the user's, kept locally; the shared profile itself is never written.
        AppSettings saved = await harness.Main.ConfigManager.LoadSettingsAsync();
        Assert.Equal(
            PostConnectFingerprint.Compute(shared.Profile.PostConnectSteps),
            saved.SharedPostConnectApprovals[shared.Profile.Id]);
        Assert.Empty(await harness.Main.ConfigManager.LoadServersAsync());
    }

    [Fact]
    public async Task AuthorizePostConnect_LocalImportedProfile_KeepsTheProfileFlag()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto local = harness.CreateServer("SSH");
        local.PostConnectSteps = [new PostConnectStep { Input = "id" }];
        local.ExecutionConfirmed = true;

        Assert.True(await harness.Main.Session.AuthorizePostConnectAsync(local, 1));
        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
        Assert.Empty((await harness.Main.ConfigManager.LoadSettingsAsync()).SharedPostConnectApprovals);
    }
}
