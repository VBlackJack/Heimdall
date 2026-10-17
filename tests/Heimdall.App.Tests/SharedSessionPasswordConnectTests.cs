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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The remembered passwords of shared sessions on the real connect path: after the Windows Hello
/// gate, behind the external credential provider, and never in servers.json, the settings or an
/// export.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SharedSessionPasswordConnectTests : IDisposable
{
    private const string Login = "jdoe";
    private const string GatewayPassword = "gateway-pw-5512";
    private const string SessionPassword = "session-pw-8830";

    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task Connect_AskedAndRemembered_ReachesTheConnection_AndIsUsedNextTimeWithoutAsking()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        AnswerBoth(fixture, remember: true);
        fixture.Handler.Answer = SignedIn;

        await ConnectAsync(fixture);

        (ServerProfileDto dispatched, AppSettings settings) = Assert.Single(fixture.Handler.Dispatched);
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(dispatched.SshPasswordEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(SharedGateway(settings).SshPasswordEncrypted));
        Assert.Equal(2, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));

        await ConnectAsync(fixture);

        Assert.Equal(2, fixture.Prompts!.Prompts.Count);
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(fixture.Handler.Dispatched[1].Server.SshPasswordEncrypted));
    }

    [Fact]
    public async Task Connect_NotRemembered_IsAskedAgain()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        fixture.Handler.Answer = SignedIn;

        AnswerBoth(fixture, remember: false);
        await ConnectAsync(fixture);
        AnswerBoth(fixture, remember: false);
        await ConnectAsync(fixture);

        Assert.Equal(4, fixture.Prompts!.Prompts.Count);
        Assert.Equal(0, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
    }

    [Fact]
    public async Task Connect_RejectedPassword_IsForgotten_AndAskedAgain()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        fixture.Handler.Answer = () => Rejected(SshFailureCode.PasswordRejected);

        await ConnectAsync(fixture);

        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        Assert.Null(fixture.Secrets.Find(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture)));
    }

    [Fact]
    public async Task Connect_RefusedThenStartedThroughPlink_TheSessionPasswordIsForgotten_AndAskedAgain()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        fixture.Handler.Answer = () => StartedThroughPlinkAfter(SshFailureCode.PasswordRejected);

        await ConnectAsync(fixture);

        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        Assert.Null(fixture.Secrets.Find(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture)));
        Assert.NotNull(fixture.Secrets.Find(SharedSecretKind.Gateway, SharedSessionTestData.SourceId, GatewayKey(fixture)));

        int asked = fixture.Prompts!.Prompts.Count;
        fixture.Prompts.Answers.Enqueue(new PasswordPromptResult(SessionPassword, Remember: false));
        await ConnectAsync(fixture);

        Assert.Equal(asked + 1, fixture.Prompts.Prompts.Count);
    }

    [Fact]
    public async Task Connect_RefusedThenThePlinkRetryFailed_TheSessionPasswordIsForgotten()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        fixture.Handler.Answer = () => Rejected(SshFailureCode.Unknown) with { FallbackFrom = SshFailureCode.PasswordRejected };

        await ConnectAsync(fixture);

        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        Assert.Null(fixture.Secrets.Find(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture)));
    }

    [Fact]
    public async Task Connect_TypedAndRemembered_RefusedThenStartedThroughPlink_OnlyTheBastionIsRemembered()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        AnswerBoth(fixture, remember: true);
        fixture.Handler.Answer = () => StartedThroughPlinkAfter(SshFailureCode.PasswordRejected);

        await ConnectAsync(fixture);

        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        Assert.NotNull(fixture.Secrets.Find(SharedSecretKind.Gateway, SharedSessionTestData.SourceId, GatewayKey(fixture)));
    }

    [Fact]
    public async Task BulkConnect_RefusedThenStartedThroughPlink_TheSessionPasswordIsForgotten()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        fixture.Handler.Answer = () => StartedThroughPlinkAfter(SshFailureCode.PasswordRejected);
        fixture.ViewModel.LoadServers(
            [fixture.Shared.Profile.CloneFaithfully()],
            await fixture.ConfigManager.LoadSettingsAsync());

        await fixture.ViewModel.ConnectServersBulkCoreAsync([.. fixture.ViewModel.Servers], CancellationToken.None);

        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        Assert.Null(fixture.Secrets.Find(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture)));
        Assert.NotNull(fixture.Secrets.Find(SharedSecretKind.Gateway, SharedSessionTestData.SourceId, GatewayKey(fixture)));
    }

    [Fact]
    public async Task Restore_UsesTheRememberedPasswords_AndNeverAsks()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        int asked = fixture.Prompts!.Prompts.Count;

        await fixture.ViewModel.RestoreServerAsync(fixture.Shared.Profile.Id, CancellationToken.None);

        Assert.Equal(asked, fixture.Prompts.Prompts.Count);
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(fixture.Handler.Dispatched[^1].Server.SshPasswordEncrypted));
    }

    [Fact]
    public async Task Connect_CancelledQuestion_DispatchesNothing_AndSaysSo()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        await ConnectAsync(fixture);

        Assert.Empty(fixture.Handler.Dispatched);
        Assert.Contains(fixture.Localizer["StatusSharedPasswordCancelled"], statuses);
    }

    [Fact]
    public async Task Connect_WindowsHelloRefused_NoRememberedPasswordIsUsed_AndNothingIsAsked()
    {
        var hello = new RefusingWindowsHello();
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(
            rememberSharedPasswords: true,
            windowsHello: hello);
        await RememberBothAsync(fixture);
        int asked = fixture.Prompts!.Prompts.Count;
        int dispatched = fixture.Handler.Dispatched.Count;
        await fixture.ConfigManager.MergeSettingAsync(settings =>
        {
            settings.RequireWindowsHelloOnConnect = true;
            settings.WindowsHelloGraceMinutes = 0;
        });

        await ConnectAsync(fixture);

        Assert.Equal(1, hello.VerifyCalls);
        Assert.Equal(dispatched, fixture.Handler.Dispatched.Count);
        Assert.Equal(asked, fixture.Prompts.Prompts.Count);
    }

    [Fact]
    public async Task Connect_ExternalProviderConfigured_ItsPasswordWinsOverTheRememberedOne()
    {
        var vault = new FixedCredentialProvider(new CredentialResult(Login, "from-the-vault"));
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(
            rememberSharedPasswords: true,
            credentialProviders: new FixedCredentialProviderFactory(vault));
        await RememberBothAsync(fixture);
        int asked = fixture.Prompts!.Prompts.Count;
        await fixture.ConfigManager.MergeSettingAsync(settings => settings.UseExternalCredentialProvider = true);

        await ConnectAsync(fixture);

        Assert.Equal(1, vault.Calls);
        ServerProfileDto dispatched = fixture.Handler.Dispatched[^1].Server;
        Assert.Equal("from-the-vault", CredentialProtector.Unprotect(dispatched.SshPasswordEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(SharedGateway(fixture.Handler.Dispatched[^1].Settings).SshPasswordEncrypted));
        Assert.Equal(asked, fixture.Prompts.Prompts.Count);
    }

    [Fact]
    public async Task BulkConnect_UsesTheRememberedPasswords_AndNeverAsks()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await RememberBothAsync(fixture);
        int asked = fixture.Prompts!.Prompts.Count;

        // The session's password is no longer remembered: a batch still asks nothing for it.
        await fixture.Secrets!.ForgetAsync(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture));
        int dispatched = fixture.Handler.Dispatched.Count;
        fixture.ViewModel.LoadServers(
            [fixture.Shared.Profile.CloneFaithfully(), fixture.Shared.DirectProfile.CloneFaithfully()],
            await fixture.ConfigManager.LoadSettingsAsync());

        await fixture.ViewModel.ConnectServersBulkCoreAsync([.. fixture.ViewModel.Servers], CancellationToken.None);

        Assert.Equal(dispatched + 2, fixture.Handler.Dispatched.Count);
        Assert.Equal(asked, fixture.Prompts.Prompts.Count);
        (ServerProfileDto bulkDispatched, AppSettings bulkSettings) = fixture.Handler.Dispatched.Last(entry => entry.Server.InventoryProfileId == fixture.Shared.Profile.Id);
        Assert.True(string.IsNullOrEmpty(bulkDispatched.SshPasswordEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(SharedGateway(bulkSettings).SshPasswordEncrypted));
    }

    [Fact]
    public async Task RememberedPasswords_NeverReachServersJson_TheSettings_OrAnExport()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(rememberSharedPasswords: true);
        await fixture.SeedLocalProfileAsync();
        await RememberBothAsync(fixture);
        await fixture.ViewModel.RestoreServerAsync(fixture.Shared.Profile.Id, CancellationToken.None);

        string sealedSession = fixture.Secrets!.Find(SharedSecretKind.Session, SharedSessionTestData.SourceId, SessionKey(fixture))!;
        string sealedGateway = fixture.Secrets.Find(SharedSecretKind.Gateway, SharedSessionTestData.SourceId, GatewayKey(fixture))!;

        AppSettings saved = await fixture.ConfigManager.LoadSettingsAsync();
        await fixture.ConfigManager.SaveSettingsAsync(saved);
        string settingsExport = SettingsTransfer.Export(saved, includeUserPaths: true, @"C:\Users\someone", out _).ToJsonString();
        string profilesExport = JsonSerializer.Serialize(
            SettingsViewModel.BuildExportConfigDocument(await fixture.ConfigManager.LoadServersAsync(), saved));

        foreach (string text in new[]
        {
            await File.ReadAllTextAsync(fixture.ConfigManager.SettingsPath),
            await File.ReadAllTextAsync(fixture.ConfigManager.ServersPath),
            settingsExport,
            profilesExport,
        })
        {
            Assert.DoesNotContain(Escaped(sealedSession), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Escaped(sealedGateway), text, StringComparison.Ordinal);
            Assert.DoesNotContain(sealedSession[..24], text, StringComparison.Ordinal);
        }

        Assert.Equal(2, fixture.Secrets.CountFor(SharedSessionTestData.SourceId));
    }

    internal static ConnectionResult SignedIn() =>
        new(true, null, new SshSessionResult(new SshShellSession()));

    internal static ConnectionResult Rejected(SshFailureCode code) =>
        new(false, "rejected", null, SshSessionDiagnosticFactory.FromClassifiedFailure(new SshFailureInfo(code, "rejected", false)));

    /// <summary>
    /// What the SSH handler returns when the embedded client was refused and the Plink retry
    /// started: a success, with the refusal it retried.
    /// </summary>
    private static ConnectionResult StartedThroughPlinkAfter(SshFailureCode refusal) =>
        new(true, null, new TerminalSessionResult(new Heimdall.Terminal.PipeModeSession()), FallbackFrom: refusal);

    internal static Task ConnectAsync(SharedSessionConnectTests.Fixture fixture) =>
        ConnectRowAsync(fixture, fixture.Shared.Profile.Id);

    private static async Task ConnectRowAsync(SharedSessionConnectTests.Fixture fixture, string profileId)
    {
        ServerItemViewModel row = (await fixture.ViewModel.FindSharedServerItemAsync(profileId))!;
        await fixture.ViewModel.ConnectCommand.ExecuteAsync(row);
    }

    internal static void AnswerBoth(SharedSessionConnectTests.Fixture fixture, bool remember)
    {
        fixture.Prompts!.Answers.Enqueue(new PasswordPromptResult(GatewayPassword, remember));
        fixture.Prompts.Answers.Enqueue(new PasswordPromptResult(SessionPassword, remember));
    }

    private static async Task RememberBothAsync(SharedSessionConnectTests.Fixture fixture)
    {
        AnswerBoth(fixture, remember: true);
        Func<ConnectionResult>? previous = fixture.Handler.Answer;
        fixture.Handler.Answer = SignedIn;
        await ConnectAsync(fixture);
        fixture.Handler.Answer = previous;
        Assert.Equal(2, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
    }

    private static string SessionKey(SharedSessionConnectTests.Fixture fixture) =>
        SharedSessionSecretStore.SessionKey(fixture.Shared.Profile.Id, fixture.Shared.Profile.RemoteServer, DefaultPorts.Ssh, Login);

    private static string GatewayKey(SharedSessionConnectTests.Fixture fixture) =>
        SharedSessionSecretStore.GatewayKey(fixture.Shared.Gateway.Host, fixture.Shared.Gateway.Port, fixture.Shared.Gateway.User);

    private static SshGatewayDto SharedGateway(AppSettings settings) =>
        Assert.Single(settings.SshGateways, gateway => SharedSessionIds.IsShared(gateway.Id));

    /// <summary>How the JSON writers spell a sealed value: '+' and '/' may come out escaped.</summary>
    private static string Escaped(string value) => JsonSerializer.Serialize(value).Trim('"');

    private sealed class RefusingWindowsHello : IWindowsHelloService
    {
        public int VerifyCalls { get; private set; }

        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<bool> VerifyAsync(string message, CancellationToken ct)
        {
            VerifyCalls++;
            return Task.FromResult(false);
        }
    }

    private sealed class FixedCredentialProvider(CredentialResult result) : ICredentialProvider
    {
        public int Calls { get; private set; }

        public string Name => "Fixed";

        public bool IsAvailable => true;

        public Task<CredentialResult?> GetCredentialAsync(
            string serverHost,
            int port,
            string? username,
            string? title,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<CredentialResult?>(result);
        }
    }

    private sealed class FixedCredentialProviderFactory(ICredentialProvider provider) : ICredentialProviderFactory
    {
        public ICredentialProvider Create(AppSettings settings) => provider;
    }
}
