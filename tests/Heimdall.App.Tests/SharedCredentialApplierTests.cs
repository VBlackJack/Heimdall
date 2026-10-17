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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The passwords of shared sessions and shared bastions, asked, remembered per user and reused,
/// over a real provider reading a real file, so a reload and a changed file are the real ones.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SharedCredentialApplierTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    private const string SourceId = "teamfile1";
    private const string OtherSourceId = "otherfile1";
    private const string Login = "jdoe";
    private const string OtherLogin = "asmith";
    private const string GatewayPassword = "gateway-pw-5512";
    private const string SessionPassword = "session-pw-8830";

    // Anonymized: the structure of a team file, fake hosts only.
    private const string Content = """
        [Bookmarks_1]
        SubRep=PROD
        ImgNum=41
        srv-db01=#126#0%srv-db01.example.test%22%[DAS]%%0%-1%%gw1.example.test%22%[DAS]%-1#MobaFont%10#0#database
        srv-tools01=#140#0%srv-tools01.example.test%22%[DAS]%%0%-1%%%%%-1#MobaFont%10#0#tools
        """;

    private const string AnotherSession = """

        srv-web01=#140#0%srv-web01.example.test%22%[DAS]%%0%-1%%%%%-1#MobaFont%10#0#web
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-shared-credentials",
        Guid.NewGuid().ToString("N"));

    public SharedCredentialApplierTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string SourcePath => Path.Combine(_directory, "team.mxtsessions");

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- Remember or not ---

    [Fact]
    public async Task Remembered_AfterASignedInConnection_IsUsedWithoutAskingAgain_AfterTheFileIsReloaded()
    {
        Harness harness = await Harness.CreateAsync(this);
        harness.AnswerBoth(remember: true);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;

        Assert.Equal(2, harness.Prompts.Prompts.Count);
        Assert.All(harness.Prompts.RememberLabels, label => Assert.Equal(harness.Localizer["DialogRememberPasswordOnThisComputer"], label));
        Assert.Equal(harness.Localizer.Format("DialogSharedGatewayPasswordPromptMessage", Login, "gw1.example.test"), harness.Prompts.Prompts[0]);
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(Gateway(settings).SshPasswordEncrypted));
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
        Assert.Equal(0, harness.Store.CountFor(SourceId));

        await plan.SettleAsync(succeeded: true, sessionAuthenticated: true, failureCode: null, failedAtGateway: false);
        Assert.Equal(2, harness.Store.CountFor(SourceId));

        // The file changes elsewhere and is read again: the same session and bastion are found.
        await File.AppendAllTextAsync(SourcePath, AnotherSession);
        await harness.Provider.LoadAsync(harness.Settings);
        Assert.Equal(3, harness.Provider.Profiles.Count);

        (ServerProfileDto again, AppSettings againSettings) = await harness.ConnectionCopiesAsync();
        Assert.NotNull(await harness.Applier.ApplyAsync(again, againSettings, allowPrompt: true, CancellationToken.None));

        Assert.Equal(2, harness.Prompts.Prompts.Count);
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(Gateway(againSettings).SshPasswordEncrypted));
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(again.SshPasswordEncrypted));
    }

    [Fact]
    public async Task NotRemembered_IsUsedOnce_AndAskedAgainNextTime()
    {
        Harness harness = await Harness.CreateAsync(this);
        harness.AnswerBoth(remember: false);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: true, sessionAuthenticated: true, failureCode: null, failedAtGateway: false);

        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
        Assert.Equal(0, harness.Store.CountFor(SourceId));
        Assert.False(File.Exists(harness.Store.FilePath));

        harness.AnswerBoth(remember: false);
        (ServerProfileDto again, AppSettings againSettings) = await harness.ConnectionCopiesAsync();
        await harness.Applier.ApplyAsync(again, againSettings, allowPrompt: true, CancellationToken.None);
        Assert.Equal(4, harness.Prompts.Prompts.Count);
    }

    [Fact]
    public async Task FailedConnection_RemembersNothing()
    {
        Harness harness = await Harness.CreateAsync(this);
        harness.AnswerBoth(remember: true);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)SshFailureCode.NetworkTimedOut, failedAtGateway: false);

        Assert.Equal(0, harness.Store.CountFor(SourceId));
    }

    [Fact]
    public async Task SuccessWithoutSignIn_KeepsTheBastionButNotTheSession()
    {
        // A Plink terminal reports success once it started, before it signed in: the bastion was
        // crossed, so its password was right; the session's is unproven.
        Harness harness = await Harness.CreateAsync(this);
        harness.AnswerBoth(remember: true);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: true, sessionAuthenticated: false, failureCode: null, failedAtGateway: false);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.NotNull(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    // --- A changed file ---

    [Fact]
    public async Task HostChangedInTheFile_TheSessionIsAskedAgain_TheBastionIsNot()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        await File.WriteAllTextAsync(SourcePath, Content.Replace("srv-db01.example.test", "srv-db02.example.test", StringComparison.Ordinal));
        await harness.Provider.LoadAsync(harness.Settings);
        harness.Prompts.Answers.Enqueue(new PasswordPromptResult("typed-again", Remember: false));

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None);

        Assert.Equal(harness.Localizer.Format("DialogSshPasswordPromptMessage", Login + "@srv-db02.example.test"), harness.Prompts.Prompts[^1]);
        Assert.Equal("typed-again", CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(Gateway(settings).SshPasswordEncrypted));
    }

    [Fact]
    public async Task UserChangedInTheFile_BothAreAskedAgain()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();
        int asked = harness.Prompts.Prompts.Count;

        harness.Settings.MobaXtermUserVariables["DAS"] = OtherLogin;
        await harness.Provider.LoadAsync(harness.Settings);
        harness.Prompts.Answers.Enqueue(new PasswordPromptResult("gw-other", Remember: false));
        harness.Prompts.Answers.Enqueue(new PasswordPromptResult("session-other", Remember: false));

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None);

        Assert.Equal(asked + 2, harness.Prompts.Prompts.Count);
        Assert.Equal("gw-other", CredentialProtector.Unprotect(Gateway(settings).SshPasswordEncrypted));
        Assert.Equal("session-other", CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
    }

    // --- Rejected credentials ---

    [Theory]
    [InlineData(SshFailureCode.PasswordRejected)]
    [InlineData(SshFailureCode.AuthRejected)]
    [InlineData(SshFailureCode.TooManyAuthFailures)]
    public async Task RejectedAtTheSession_ForgetsTheSessionPassword_KeepsTheBastions(SshFailureCode code)
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)code, failedAtGateway: false);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.NotNull(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    [Fact]
    public async Task RejectedAtTheBastion_ForgetsTheBastionPassword_KeepsTheSession()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)SshFailureCode.PasswordRejected, failedAtGateway: true);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.Null(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    [Theory]
    [InlineData(SshFailureCode.NetworkTimedOut)]
    [InlineData(SshFailureCode.HostKeyMismatch)]
    [InlineData(SshFailureCode.Cancelled)]
    public async Task OtherFailures_ForgetNothing(SshFailureCode code)
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)code, failedAtGateway: false);
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)code, failedAtGateway: true);

        Assert.Equal(2, harness.Store.CountFor(SourceId));
    }

    // --- Refused, then retried through Plink ---

    [Theory]
    [InlineData(SshFailureCode.PasswordRejected)]
    [InlineData(SshFailureCode.AuthRejected)]
    public async Task PlinkStartedAfterTheSessionRefusedItsPassword_ForgetsTheSessionPassword_KeepsTheBastion(SshFailureCode code)
    {
        // The terminal started, so the outcome is a success; the refusal before it is what says
        // the remembered password is wrong.
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: true, sessionAuthenticated: false, failureCode: null, failedAtGateway: false, fallbackFrom: code);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.NotNull(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    [Fact]
    public async Task PlinkFailedAfterTheSessionRefusedItsPassword_ForgetsTheSessionPassword()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)SshFailureCode.Unknown, failedAtGateway: false, fallbackFrom: SshFailureCode.PasswordRejected);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.NotNull(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    [Theory]
    [InlineData(SshFailureCode.KeyboardInteractiveUnsupportedPrompt)]
    [InlineData(SshFailureCode.KeyRejected)]
    public async Task PlinkStartedAfterARefusalThatBlamesNoPassword_ForgetsNothing(SshFailureCode code)
    {
        // A second-factor question or a refused key says nothing against the password.
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: true, sessionAuthenticated: false, failureCode: null, failedAtGateway: false, fallbackFrom: code);

        Assert.Equal(2, harness.Store.CountFor(SourceId));
    }

    [Fact]
    public async Task TypedAndRemembered_PlinkStartedAfterTheSessionRefusedIt_KeepsTheBastionOnly()
    {
        Harness harness = await Harness.CreateAsync(this);
        harness.AnswerBoth(remember: true);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;
        await plan.SettleAsync(succeeded: true, sessionAuthenticated: false, failureCode: null, failedAtGateway: false, fallbackFrom: SshFailureCode.PasswordRejected);

        Assert.Equal(1, harness.Store.CountFor(SourceId));
        Assert.NotNull(harness.Store.Find(SharedSecretKind.Gateway, SourceId, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login)));
    }

    // --- When nothing is asked ---

    [Fact]
    public async Task AgentOffersKeys_NothingIsAsked()
    {
        Harness harness = await Harness.CreateAsync(this, agentKeys: 1);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        Assert.NotNull(await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None));

        Assert.Empty(harness.Prompts.Prompts);
        Assert.True(string.IsNullOrEmpty(profile.SshPasswordEncrypted));
    }

    [Fact]
    public async Task PromptNotAllowed_UsesTheRememberedPasswords_AndAsksNothing()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();
        int asked = harness.Prompts.Prompts.Count;

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        Assert.NotNull(await harness.Applier.ApplyAsync(profile, settings, allowPrompt: false, CancellationToken.None));

        Assert.Equal(asked, harness.Prompts.Prompts.Count);
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
    }

    [Fact]
    public async Task PasswordAlreadyProvided_ByTheVault_IsNeitherReplacedNorAsked()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();
        int asked = harness.Prompts.Prompts.Count;

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        string fromVault = CredentialProtector.Protect("from-the-vault");
        profile.SshPasswordEncrypted = fromVault;
        SharedCredentialApplier.SharedCredentialPlan plan = (await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;

        Assert.Equal(fromVault, profile.SshPasswordEncrypted);
        Assert.Equal(asked, harness.Prompts.Prompts.Count);

        // A vault password the server rejects is not the remembered one: that one stays.
        await plan.SettleAsync(succeeded: false, sessionAuthenticated: false, failureCode: (int)SshFailureCode.PasswordRejected, failedAtGateway: false);
        Assert.Equal(2, harness.Store.CountFor(SourceId));
    }

    [Fact]
    public async Task SessionWithoutALogin_IsNotAskedHere()
    {
        // Nothing to key a remembered password on: the SSH client asks for the login itself.
        Harness harness = await Harness.CreateAsync(this);
        var noLogin = new ServerProfileDto
        {
            Id = SharedSessionIds.PrefixFor(SourceId) + "nologin",
            ConnectionType = "SSH",
            RemoteServer = "srv-nologin01.example.test",
        };

        Assert.NotNull(await harness.Applier.ApplyAsync(noLogin, new AppSettings(), allowPrompt: true, CancellationToken.None));

        Assert.Empty(harness.Prompts.Prompts);
        Assert.True(string.IsNullOrEmpty(noLogin.SshPasswordEncrypted));
    }

    [Fact]
    public async Task RememberedPasswordThisMachineCannotOpen_IsForgotten_AndAskedAgain()
    {
        // Sent as it is, an unreadable password would refuse the connection at the gateway check
        // instead of asking: a new Windows account, or a changed protection key.
        Harness harness = await Harness.CreateAsync(this);
        string sessionKey = SharedSessionSecretStore.SessionKey(harness.ProfileId, "srv-db01.example.test", 22, Login);
        string gatewayKey = SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, Login);
        await harness.Store.RememberAsync(SharedSecretKind.Session, SourceId, sessionKey, "not-a-sealed-value");
        await harness.Store.RememberAsync(SharedSecretKind.Gateway, SourceId, gatewayKey, "not-a-sealed-value");
        harness.AnswerBoth(remember: false);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None);

        Assert.Equal(2, harness.Prompts.Prompts.Count);
        Assert.Equal(SessionPassword, CredentialProtector.Unprotect(profile.SshPasswordEncrypted));
        Assert.Equal(GatewayPassword, CredentialProtector.Unprotect(Gateway(settings).SshPasswordEncrypted));
        Assert.Equal(0, harness.Store.CountFor(SourceId));
    }

    [Fact]
    public async Task CancelledQuestion_ReturnsNoPlan_AndRemembersNothing()
    {
        Harness harness = await Harness.CreateAsync(this);

        (ServerProfileDto profile, AppSettings settings) = await harness.ConnectionCopiesAsync();
        Assert.Null(await harness.Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None));

        Assert.Single(harness.Prompts.Prompts);
        Assert.Equal(0, harness.Store.CountFor(SourceId));
    }

    [Fact]
    public async Task LocalProfile_IsLeftAlone()
    {
        Harness harness = await Harness.CreateAsync(this);
        var local = new ServerProfileDto { Id = "local-1", ConnectionType = "SSH", RemoteServer = "local.example.test", SshUsername = Login };

        Assert.NotNull(await harness.Applier.ApplyAsync(local, new AppSettings(), allowPrompt: true, CancellationToken.None));

        Assert.Empty(harness.Prompts.Prompts);
        Assert.True(string.IsNullOrEmpty(local.SshPasswordEncrypted));
    }

    // --- Where the passwords live ---

    [Fact]
    public async Task RemovedSource_ForgetsItsPasswords_AndOnlyItsOwn()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();
        await harness.Store.RememberAsync(SharedSecretKind.Session, OtherSourceId, "k", "sealed-other");
        var otherSource = new SharedSessionSourceDto { Id = OtherSourceId, Name = "Other", Path = SourcePath, Enabled = true };

        await harness.Provider.LoadAsync(new AppSettings
        {
            SharedSessionSources = [otherSource],
            MobaXtermUserVariables = new Dictionary<string, string> { ["DAS"] = Login },
        });

        Assert.Equal(0, harness.Store.CountFor(SourceId));
        Assert.Equal(1, harness.Store.CountFor(OtherSourceId));
        Assert.Equal(0, new SharedSessionSecretStore(harness.Store.FilePath).CountFor(SourceId));
    }

    [Fact]
    public async Task OfflineCopy_HoldsNoPassword()
    {
        Harness harness = await Harness.CreateAsync(this);
        await harness.RememberBothAsync();
        await harness.Provider.LoadAsync(harness.Settings);

        string[] cached = Directory.GetFiles(harness.CacheDirectory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(cached);
        string sealedSession = harness.Store.Find(
            SharedSecretKind.Session,
            SourceId,
            SharedSessionSecretStore.SessionKey(harness.ProfileId, "srv-db01.example.test", 22, Login))!;
        foreach (string file in cached)
        {
            string text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(SessionPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain(GatewayPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain(sealedSession, text, StringComparison.Ordinal);
        }
    }

    private static SshGatewayDto Gateway(AppSettings settings) =>
        Assert.Single(settings.SshGateways, gateway => SharedSessionIds.IsShared(gateway.Id));

    private sealed class Harness
    {
        private Harness(
            SharedSessionProvider provider,
            SharedSessionSecretStore store,
            SharedCredentialApplier applier,
            SharedPasswordPromptProxy prompts,
            LocalizationManager localizer,
            AppSettings settings,
            string cacheDirectory)
        {
            Provider = provider;
            Store = store;
            Applier = applier;
            Prompts = prompts;
            Localizer = localizer;
            Settings = settings;
            CacheDirectory = cacheDirectory;
        }

        public SharedSessionProvider Provider { get; }

        public SharedSessionSecretStore Store { get; }

        public SharedCredentialApplier Applier { get; }

        public SharedPasswordPromptProxy Prompts { get; }

        public LocalizationManager Localizer { get; }

        public AppSettings Settings { get; }

        public string CacheDirectory { get; }

        public string ProfileId => Provider.Profiles.Single(profile => profile.DisplayName == "srv-db01").Id;

        public static async Task<Harness> CreateAsync(SharedCredentialApplierTests owner, int agentKeys = 0)
        {
            await File.WriteAllTextAsync(owner.SourcePath, Content);
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            string cacheDirectory = Path.Combine(owner._directory, "cache");
            var store = new SharedSessionSecretStore(Path.Combine(owner._directory, SharedSessionSecretStore.FileName));
            var provider = new SharedSessionProvider(localizer, new SharedSessionSourceCache(cacheDirectory), secrets: store);
            var settings = new AppSettings
            {
                SharedSessionSources = [new SharedSessionSourceDto { Id = SourceId, Name = "Team", Path = owner.SourcePath, Enabled = true }],
                MobaXtermUserVariables = new Dictionary<string, string> { ["DAS"] = Login },
            };
            await provider.LoadAsync(settings);

            (IDialogService dialog, SharedPasswordPromptProxy prompts) = SharedPasswordPromptProxy.Create();
            var applier = new SharedCredentialApplier(store, dialog, localizer, _ => agentKeys);
            return new Harness(provider, store, applier, prompts, localizer, settings, cacheDirectory);
        }

        /// <summary>The copies a connection to srv-db01 is made from, as the provider hands them out.</summary>
        public Task<(ServerProfileDto Profile, AppSettings Settings)> ConnectionCopiesAsync()
        {
            ServerProfileDto profile = Provider.Profiles.Single(candidate => candidate.DisplayName == "srv-db01");
            return Task.FromResult((profile, new AppSettings { SshGateways = [.. Provider.Gateways] }));
        }

        public void AnswerBoth(bool remember)
        {
            Prompts.Answers.Enqueue(new PasswordPromptResult(GatewayPassword, remember));
            Prompts.Answers.Enqueue(new PasswordPromptResult(SessionPassword, remember));
        }

        /// <summary>Asks both passwords with the remember choice and settles a signed-in connection.</summary>
        public async Task RememberBothAsync()
        {
            AnswerBoth(remember: true);
            (ServerProfileDto profile, AppSettings settings) = await ConnectionCopiesAsync();
            SharedCredentialApplier.SharedCredentialPlan plan = (await Applier.ApplyAsync(profile, settings, allowPrompt: true, CancellationToken.None))!;
            await plan.SettleAsync(succeeded: true, sessionAuthenticated: true, failureCode: null, failedAtGateway: false);
            Assert.Equal(2, Store.CountFor(SourceId));
        }
    }
}
