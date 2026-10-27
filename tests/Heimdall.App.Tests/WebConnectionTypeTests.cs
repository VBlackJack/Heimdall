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

using System.Diagnostics;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

/// <summary>
/// The web connection type (issue #558): the handler, the external launch, the browser policy
/// and the certificate pins.
/// </summary>
public sealed class WebConnectionTypeTests
{
    [Fact]
    public async Task AUrlPastedIntoTheHost_FailsBeforeAnythingOpens()
    {
        var stateMachine = new ConnectionStateMachine();
        int launches = 0;
        WebHandler handler = CreateHandler(stateMachine, _ => { launches++; return null; });
        ServerProfileDto profile = Profile();
        // No colon: an IPv6-looking host would be refused by the bracket rule alone.
        profile.RemoteServer = "ilo.example.test/login";
        profile.WebOpenExternally = true;

        ConnectionResult result = await handler.ConnectAsync(profile, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorWebInvalidAddress", result.ErrorMessage);
        Assert.Equal(0, launches);
        Assert.Equal(ConnectionState.Error, stateMachine.GetState(profile.Id));
    }

    [Fact]
    public async Task OpeningExternally_WithNoProgram_HandsTheAddressToTheDefaultBrowser()
    {
        var stateMachine = new ConnectionStateMachine();
        ProcessStartInfo? started = null;
        var states = new List<ConnectionState>();
        stateMachine.StateChanged += change => states.Add(change.NewState);
        WebHandler handler = CreateHandler(stateMachine, info => { started = info; return null; });
        ServerProfileDto profile = Profile();
        profile.WebOpenExternally = true;

        ConnectionResult result = await handler.ConnectAsync(profile, new AppSettings(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Session);
        Assert.NotNull(started);
        Assert.Equal("https://ilo.example.test/", started!.FileName);
        Assert.True(started.UseShellExecute);

        // Nothing to watch when the browser reuses a running instance: the row goes back to idle.
        Assert.Equal(ConnectionState.Disconnected, stateMachine.GetState(profile.Id));
        Assert.Equal(
            [ConnectionState.Initializing, ConnectionState.ValidatingConfig, ConnectionState.LaunchingWeb, ConnectionState.LaunchedExternalClient, ConnectionState.Disconnected],
            states);
    }

    [Fact]
    public void OpeningExternally_WithAProgram_ExpandsUrlHostAndPort()
    {
        ServerProfileDto profile = Profile();
        profile.WebPort = 8443;
        profile.WebExternalProgram = @"C:\Legacy\firefox.exe";
        profile.WebExternalArguments = "-new-window {Url} --host {Host}:{Port}";
        Uri address = WebAddress.From(profile)!;

        ProcessStartInfo? info = WebExternalLaunch.TryCreate(profile, address, out string? error);

        Assert.Null(error);
        Assert.Equal(@"C:\Legacy\firefox.exe", info!.FileName);
        Assert.Equal("-new-window https://ilo.example.test:8443/ --host ilo.example.test:8443", info.Arguments);
    }

    // S-01. The browser's command line is built by the external tool rule: a user name that would
    // begin an argument with a dash is an option to the browser, and the launch is refused.
    [Fact]
    public void OpeningExternally_UserNameStartingWithADash_IsRefused()
    {
        ServerProfileDto profile = Profile();
        profile.WebExternalProgram = @"C:\Legacy\firefox.exe";
        profile.WebExternalArguments = "{Url} --user {User}";
        profile.WebUsername = "-profile";

        ProcessStartInfo? info = WebExternalLaunch.TryCreate(profile, WebAddress.From(profile)!, out string? error);

        Assert.Null(info);
        Assert.Equal("ErrorWebExternalArgumentRejected", error);
    }

    [Fact]
    public void OpeningExternally_WithAProgramAndNoArguments_PassesTheAddress()
    {
        ServerProfileDto profile = Profile();
        profile.WebExternalProgram = @"C:\Legacy\iexplore.exe";

        ProcessStartInfo? info = WebExternalLaunch.TryCreate(profile, WebAddress.From(profile)!, out _);

        Assert.Equal("https://ilo.example.test/", info!.Arguments);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Tools\open.bat")]
    public async Task OpeningExternally_RefusesACommandInterpreter(string program)
    {
        var stateMachine = new ConnectionStateMachine();
        int launches = 0;
        WebHandler handler = CreateHandler(stateMachine, _ => { launches++; return null; });
        ServerProfileDto profile = Profile();
        profile.WebOpenExternally = true;
        profile.WebExternalProgram = program;

        ConnectionResult result = await handler.ConnectAsync(profile, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorWebExternalShellRefused", result.ErrorMessage);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task AProgramThatCannotStart_IsReported()
    {
        var stateMachine = new ConnectionStateMachine();
        WebHandler handler = CreateHandler(
            stateMachine,
            _ => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"));
        ServerProfileDto profile = Profile();
        profile.WebOpenExternally = true;
        profile.WebExternalProgram = @"C:\Missing\browser.exe";

        ConnectionResult result = await handler.ConnectAsync(profile, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ConnectionState.Error, stateMachine.GetState(profile.Id));
    }

    [Theory]
    [InlineData("https://ilo.example.test/", true)]
    [InlineData("http://ilo.example.test/x", true)]
    [InlineData("about:blank", true)]
    [InlineData("blob:https://ilo.example.test/1234", true)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("not a uri", false)]
    [InlineData(null, false)]
    public void TheConsole_NavigatesOnlyToWebAddresses(string? uri, bool expected)
    {
        Assert.Equal(expected, WebConsolePolicy.IsNavigable(uri));
    }

    [Theory]
    [InlineData("https://ilo.example.test/x", "admin", "pw", false, true)]
    [InlineData("https://ILO.example.test/x", "admin", "pw", false, true)]
    [InlineData("https://ilo.example.test/x", "admin", "pw", true, false)]
    [InlineData("https://ilo.example.test/x", "", "pw", false, false)]
    [InlineData("https://ilo.example.test/x", null, "pw", false, false)]
    [InlineData("https://ilo.example.test/x", "admin", null, false, false)]
    [InlineData("https://ilo.example.test/x", "admin", "", false, false)]
    [InlineData("https://evil.example.test/", "admin", "pw", false, false)]
    [InlineData("http://ilo.example.test/", "admin", "pw", false, false)]
    [InlineData("https://ilo.example.test:8443/", "admin", "pw", false, false)]
    public void TheAccount_AnswersOnlyTheProfilesOwnChallengeAndOnlyOnce(
        string challenge,
        string? username,
        string? password,
        bool alreadyAnswered,
        bool expected)
    {
        var profileAddress = new Uri("https://ilo.example.test/");

        Assert.Equal(
            expected,
            WebConsolePolicy.ShouldAnswerChallenge(username, password, alreadyAnswered, new Uri(challenge), profileAddress));
    }

    /// <summary>
    /// Only failing to reach the console before it ever loaded ends the session. An HTTP error
    /// page or a dead link after the first load used to hide the console and set the row to Error.
    /// </summary>
    [Theory]
    [InlineData(false, 0, false, false, true)]
    [InlineData(false, 0, false, true, false)]
    [InlineData(false, 404, false, false, false)]
    [InlineData(false, 401, false, true, false)]
    [InlineData(false, 0, true, false, false)]
    [InlineData(true, 200, false, false, false)]
    public void ANavigationFailure_EndsTheSessionOnlyBeforeTheConsoleAnswered(
        bool isSuccess,
        int httpStatus,
        bool canceled,
        bool loadedBefore,
        bool expected)
    {
        Assert.Equal(expected, WebConsolePolicy.IsSessionFailure(isSuccess, httpStatus, canceled, loadedBefore));
    }

    [Fact]
    public void ACertificate_IsUnknownUntilTrusted()
    {
        var trust = new WebCertificateTrust();

        Assert.Equal(WebCertificateVerdict.Unknown, trust.Evaluate("ilo.example.test", 443, "SHA256:AA"));

        var persisted = new List<string>();
        trust.CertificateTrusted += (key, _) => persisted.Add(key);

        trust.Apply("ilo.example.test", 443, Entry("SHA256:AA"), FtpsCertificateDecision.TrustOnce);

        Assert.Equal(WebCertificateVerdict.Trusted, trust.Evaluate("ilo.example.test", 443, "SHA256:AA"));

        // "This session" must never reach the settings file.
        Assert.Empty(persisted);
        Assert.Equal(WebCertificateVerdict.Unknown, trust.Evaluate("ilo.example.test", 8443, "SHA256:AA"));
    }

    [Fact]
    public void ATrustedCertificate_IsPersistedAndAChangeIsSeen()
    {
        var trust = new WebCertificateTrust();
        var persisted = new List<string>();
        trust.CertificateTrusted += (key, _) => persisted.Add(key);

        trust.Apply("ILO.example.test", 443, Entry("SHA256:AA"), FtpsCertificateDecision.Accept);

        Assert.Equal(["ilo.example.test:443"], persisted);
        Assert.Equal(WebCertificateVerdict.Trusted, trust.Evaluate("ilo.example.test", 443, "SHA256:AA"));
        Assert.Equal(WebCertificateVerdict.Changed, trust.Evaluate("ilo.example.test", 443, "SHA256:BB"));
    }

    [Fact]
    public void ARejection_TrustsNothing()
    {
        var trust = new WebCertificateTrust();

        trust.Apply("ilo.example.test", 443, Entry("SHA256:AA"), FtpsCertificateDecision.Reject);

        Assert.Equal(WebCertificateVerdict.Unknown, trust.Evaluate("ilo.example.test", 443, "SHA256:AA"));
    }

    [Fact]
    public void APinLoadedFromTheSettings_IsHonoured()
    {
        var trust = new WebCertificateTrust();
        trust.LoadEntriesFromConfig([new KeyValuePair<string, FtpsCertificateEntry>("ilo.example.test:443", Entry("SHA256:AA"))]);

        Assert.Equal(WebCertificateVerdict.Trusted, trust.Evaluate("ilo.example.test", 443, "SHA256:AA"));
    }

    /// <summary>A changed certificate must not be accepted by a reflex Enter.</summary>
    [Fact]
    public void TheChangedCertificatePrompt_DoesNotAcceptOnEnter()
    {
        var prompt = new FtpsCertificatePrompt("ilo.example.test", 443, "SHA256:AA", "CN=ilo", "CN=ilo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "");
        var localizer = new LocalizationManager();

        var changed = new FtpsCertificatePromptDialogViewModel(localizer, prompt, CertificatePromptWording.WebChanged);
        var firstUse = new FtpsCertificatePromptDialogViewModel(localizer, prompt, CertificatePromptWording.WebFirstUse);
        var ftps = new FtpsCertificatePromptDialogViewModel(localizer, prompt);

        Assert.False(changed.AcceptIsDefault);
        Assert.Equal("WebCertificateChangedTitle", changed.HeaderText);
        Assert.True(firstUse.AcceptIsDefault);
        Assert.Equal("WebCertificateFirstUseTitle", firstUse.HeaderText);
        Assert.True(ftps.AcceptIsDefault);
        Assert.Equal("FtpsCertificateFirstUseTitle", ftps.HeaderText);
    }

    [Fact]
    public void TheDialog_RoundTripsEveryWebField()
    {
        var dto = new ServerProfileDto
        {
            Id = "web-1",
            DisplayName = "iLO",
            ConnectionType = "WEB",
            RemoteServer = "ilo.example.test",
            WebPort = 8443,
            WebUseHttp = false,
            WebPath = "/index.html",
            WebUsername = "admin",
            WebPasswordEncrypted = "stored",
            WebOpenExternally = true,
            WebExternalProgram = @"C:\Legacy\firefox.exe",
            WebExternalArguments = "{Url}",
        };

        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(dto);
        ServerProfileDto saved = vm.ToDto();

        Assert.Equal(8443, vm.EndpointPort);
        Assert.Equal(8443, saved.WebPort);
        Assert.False(saved.WebUseHttp);
        Assert.Equal("/index.html", saved.WebPath);
        Assert.Equal("admin", saved.WebUsername);
        Assert.Equal("stored", saved.WebPasswordEncrypted);
        Assert.True(saved.WebOpenExternally);
        Assert.Equal(@"C:\Legacy\firefox.exe", saved.WebExternalProgram);
        Assert.Equal("{Url}", saved.WebExternalArguments);
        Assert.Equal("https://ilo.example.test:8443/index.html", vm.WebAddressPreview);

        // The header named the protocol by its internal token, WEB, beside a card that says Web.
        Assert.Equal("Web", vm.ConnectionTypeDisplayName);
    }

    /// <summary>
    /// The connect path moves a row to Initializing before any handler runs; from Disconnected
    /// the state machine refuses the handler's own transitions, and a state assertion would hold
    /// for nothing.
    /// </summary>
    private static WebHandler CreateHandler(ConnectionStateMachine stateMachine, Func<ProcessStartInfo, Process?> start)
    {
        Assert.True(stateMachine.TryTransition("web-test", ConnectionState.Initializing));
        return new(stateMachine, new LocalizationManager(), start);
    }

    private static ServerProfileDto Profile() => new()
    {
        Id = "web-test",
        DisplayName = "iLO",
        ConnectionType = "WEB",
        RemoteServer = "ilo.example.test",
        WebPort = DefaultPorts.HttpsStd,
        UseDirectConnection = true,
    };

    private static FtpsCertificateEntry Entry(string fingerprint) => new(
        fingerprint,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "CN=ilo",
        "CN=ilo",
        DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddYears(1),
        FtpsCertificateSource.UserConfirmed);
}

/// <summary>
/// The embedded web session decrypts the stored HTTP password, so it shares the protector's
/// process-global state with the other members of the collection.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class WebHandlerCredentialTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task AnEmbeddedProfile_YieldsItsAddressAndItsAccount()
    {
        var stateMachine = new ConnectionStateMachine();
        Assert.True(stateMachine.TryTransition("web-test", ConnectionState.Initializing));
        WebHandler handler = new(stateMachine, new LocalizationManager(), _ => throw new InvalidOperationException("no launch expected"));
        ServerProfileDto profile = new()
        {
            Id = "web-test",
            DisplayName = "iLO",
            ConnectionType = "WEB",
            RemoteServer = "ilo.example.test",
            UseDirectConnection = true,
        };
        profile.WebPath = "/index.html";
        profile.WebUsername = "admin";
        profile.WebPasswordEncrypted = CredentialProtector.Protect("s3cret");

        ConnectionResult result = await handler.ConnectAsync(profile, new AppSettings(), CancellationToken.None);

        Assert.True(result.Success);
        WebSessionResult session = Assert.IsType<WebSessionResult>(result.Session);
        Assert.Equal("https://ilo.example.test/index.html", session.Address.AbsoluteUri);
        Assert.Equal("admin", session.Username);
        Assert.Equal("s3cret", session.Password);
        Assert.Equal(ConnectionState.LaunchingWeb, stateMachine.GetState(profile.Id));
        Assert.DoesNotContain("s3cret", session.ToString(), StringComparison.Ordinal);
    }
}
