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
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using static Heimdall.App.Tests.SessionCoordinatorPreMountTests;

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
    public async Task SftpAutoOpen_AfterALocalSshSession_StillReadsTheProfileAgain()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto local = harness.CreateServer("SSH");
        local.SshUsername = "operator";
        local.SshPasswordEncrypted = "saved-password";
        await harness.Main.ConfigManager.SaveServersAsync([local]);
        await EnableSftpAutoOpenAsync(harness);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        Task<bool> restore = harness.Main.ServerList.RestoreServerAsync(local.Id, CancellationToken.None);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        // Whatever the SSH connection's own copy became, a local companion is built from the
        // profile as saved, as it always was.
        ssh.LastServer!.SshPasswordEncrypted = "changed-on-the-ssh-copy";
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await restore.WaitAsync(TestTimeout);
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "recorded", null));

        Assert.Equal("saved-password", sftp.LastServer!.SshPasswordEncrypted);
    }

    internal static SshGatewayDto GatewayIn(AppSettings settings, string gatewayId) =>
        Assert.Single(settings.SshGateways, gateway => string.Equals(gateway.Id, gatewayId, StringComparison.Ordinal));

    internal static async Task EnableSftpAutoOpenAsync(TestHarness harness)
    {
        AppSettings settings = await harness.Main.ConfigManager.LoadSettingsAsync();
        settings.SftpBrowserEnabled = true;
        settings.SftpAutoOpenOnSsh = true;
        await harness.Main.ReloadConfigurationAsync(settings);
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

/// <summary>
/// The companion SFTP pane of a shared SSH session, over real sealed passwords: the bastion
/// preflight opens them, so this class owns the protector state like every other that seals.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SharedCompanionSftpCredentialTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task SftpAutoOpen_AfterASharedSshSession_SignsInWithTheRememberedPasswords()
    {
        SharedSessionTestData data = SharedSessionTestData.Create();
        SharedSessionSecretStore? store = null;
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, data.Provider),
            sharedCredentialsFactory: (rootPath, localizer) =>
            {
                store = new SharedSessionSecretStore(Path.Combine(rootPath, SharedSessionSecretStore.FileName));
                (IDialogService refusing, _) = SharedPasswordPromptProxy.Create();
                return new SharedCredentialApplier(store, refusing, localizer, _ => 0);
            });
        // Sealed for real: the bastion preflight refuses a secret it cannot open.
        string rememberedGateway = CredentialProtector.Protect("remembered-gateway");
        string rememberedSession = CredentialProtector.Protect("remembered-session");
        await store!.RememberAsync(
            SharedSecretKind.Gateway,
            SharedSessionTestData.SourceId,
            SharedSessionSecretStore.GatewayKey(data.Gateway.Host, data.Gateway.Port, data.Gateway.User),
            rememberedGateway);
        await store.RememberAsync(
            SharedSecretKind.Session,
            SharedSessionTestData.SourceId,
            SharedSessionSecretStore.SessionKey(data.Profile.Id, data.Profile.RemoteServer, data.Profile.SshPort, data.Profile.SshUsername!),
            rememberedSession);
        await EnableSftpAutoOpenAsync(harness);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");

        Task<bool> restore = harness.Main.ServerList.RestoreServerAsync(data.Profile.Id, CancellationToken.None);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await restore.WaitAsync(TestTimeout);
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "recorded", null));

        // The companion is the second connection of the same sign-in: it carries what the SSH
        // connection was filled with. Read again from the lookup it carried nothing, and the
        // bastion hop refused it for want of an agent.
        Assert.Equal(rememberedSession, sftp.LastServer!.SshPasswordEncrypted);
        Assert.Equal(rememberedGateway, GatewayIn(sftp.LastSettings!, data.Gateway.Id).SshPasswordEncrypted);
        Assert.Equal("SFTP", sftp.LastServer.ConnectionType);
        Assert.NotEqual(ssh.LastServer!.Id, sftp.LastServer.Id);
        Assert.False(sftp.LastServer.AllowCredentialPrompt);
    }

    [Fact]
    public async Task SftpAutoOpen_AfterASharedSshSession_ReusesATypedPasswordWithoutAskingAgain()
    {
        SharedSessionTestData data = SharedSessionTestData.Create();
        SharedSessionSecretStore? store = null;
        SharedPasswordPromptProxy? prompts = null;
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, data.Provider),
            sharedCredentialsFactory: (rootPath, localizer) =>
            {
                store = new SharedSessionSecretStore(Path.Combine(rootPath, SharedSessionSecretStore.FileName));
                (IDialogService passwordDialogs, prompts) = SharedPasswordPromptProxy.Create();
                return new SharedCredentialApplier(store, passwordDialogs, localizer, _ => 0);
            });
        prompts!.Answers.Enqueue(new PasswordPromptResult("typed-gateway", Remember: false));
        prompts.Answers.Enqueue(new PasswordPromptResult("typed-session", Remember: false));
        await EnableSftpAutoOpenAsync(harness);
        ControlledProtocolHandler ssh = harness.GetHandler("SSH");
        ControlledProtocolHandler sftp = harness.GetHandler("SFTP");
        ServerItemViewModel row = (await harness.Main.ServerList.FindSharedServerItemAsync(data.Profile.Id))!;

        Task connect = harness.Main.ServerList.ConnectCommand.ExecuteAsync(row);
        await ssh.Started.Task.WaitAsync(TestTimeout);
        ssh.Result.SetResult(SuccessWithTerminalSession());
        await connect.WaitAsync(TestTimeout);
        await sftp.Started.Task.WaitAsync(TestTimeout);
        sftp.Result.SetResult(new ConnectionResult(false, "recorded", null));

        Assert.Equal("typed-session", CredentialProtector.Unprotect(sftp.LastServer!.SshPasswordEncrypted));
        Assert.Equal("typed-gateway", CredentialProtector.Unprotect(GatewayIn(sftp.LastSettings!, data.Gateway.Id).SshPasswordEncrypted));

        // Asked once, for the SSH connection; the companion asked nothing and remembered nothing.
        Assert.Equal(2, prompts.Prompts.Count);
        Assert.Null(store!.Find(
            SharedSecretKind.Session,
            SharedSessionTestData.SourceId,
            SharedSessionSecretStore.SessionKey(data.Profile.Id, data.Profile.RemoteServer, data.Profile.SshPort, data.Profile.SshUsername!)));
        Assert.Null(store.Find(
            SharedSecretKind.Gateway,
            SharedSessionTestData.SourceId,
            SharedSessionSecretStore.GatewayKey(data.Gateway.Host, data.Gateway.Port, data.Gateway.User)));

        // The passwords lived on the connection's copies only.
        Assert.Empty(await harness.Main.ConfigManager.LoadServersAsync());
        AppSettings saved = await harness.Main.ConfigManager.LoadSettingsAsync();
        Assert.DoesNotContain(saved.SshGateways, gateway => SharedSessionIds.IsShared(gateway.Id));
        Assert.Null(data.Provider.FindProfile(data.Profile.Id)!.SshPasswordEncrypted);
        Assert.Null(data.Provider.Gateways.Single().SshPasswordEncrypted);
    }
}
