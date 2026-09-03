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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// What the certificate question shows, and what each button answers.
/// </summary>
/// <remarks>
/// No window is constructed anywhere here. Building a WPF <c>Window</c> in a test seals
/// application-level styles onto the shared dispatcher and took 23 unrelated tests down
/// during BL-0089, which is why the question holds no decisions of its own to test.
/// </remarks>
public sealed class RdpCertificatePromptDialogViewModelTests
{
    private const string TunnelledOrigin = "dc-pool.example.com:3389 via localhost:53211";

    [Fact]
    public async Task Message_NamesTheProfileAndTheAddressThatAnswered()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);

        // An alarm the user cannot place is an alarm they dismiss.
        Assert.Contains("DC pool", vm.Message, StringComparison.Ordinal);
        Assert.Contains("dc-pool.example.com", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_TunnelledProfile_NamesTheMachine_NotTheLocalEndOfTheTunnel()
    {
        // The security defect this pins, and it was live on master. The verification target of
        // an SSH-tunnelled profile is 127.0.0.1, and the question used to show exactly that.
        // Two tunnelled profiles both named "Production", both connecting, produced two
        // questions reading "Production" ... 127.0.0.1, and a certificate could be approved
        // for the wrong machine.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            host: "127.0.0.1",
            origin: new RdpTrustPromptOrigin(TunnelledOrigin, null, "Production", "Heimdall"));

        Assert.Contains("dc-pool.example.com", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", vm.Message, StringComparison.Ordinal);
        Assert.Equal(TunnelledOrigin, vm.RemoteEndpoint);
    }

    [Fact]
    public async Task Route_TwoProfilesBehindDifferentGateways_ReadDifferently()
    {
        // The case the whole lot exists for, and the one the endpoint alone could not carry.
        // Two saved profiles, both named "Production", both reaching "srv01:3389", one through
        // Paris and one through Berlin: two physically different machines behind one short
        // name. Their endpoint text differs only by an ephemeral local tunnel port the user has
        // never seen and cannot map to a gateway, so approving the Paris fingerprint wrote
        // durable trust into the Berlin profile.
        RdpCertificatePromptDialogViewModel paris = await CreateAsync(
            alreadyTrusted: 0,
            host: "127.0.0.1",
            origin: new RdpTrustPromptOrigin(
                "srv01:3389 via localhost:53211", "gw-paris", "Production", "Heimdall"));
        RdpCertificatePromptDialogViewModel berlin = await CreateAsync(
            alreadyTrusted: 0,
            host: "127.0.0.1",
            origin: new RdpTrustPromptOrigin(
                "srv01:3389 via localhost:53212", "gw-berlin", "Production", "Heimdall"));

        Assert.True(paris.HasRoute);
        Assert.True(berlin.HasRoute);
        Assert.Equal("gw-paris", paris.Route);
        Assert.Equal("gw-berlin", berlin.Route);
        Assert.NotEqual(paris.Route, berlin.Route);
    }

    [Fact]
    public async Task Route_DirectProfile_ShowsNoRouteLineAtAll()
    {
        // A direct connection reaches the machine itself. An empty "Reached through" caption
        // would be a question whose own text looks broken.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(
                "srv01:3389", null, "Production", "Heimdall"));

        Assert.Null(vm.Route);
        Assert.False(vm.HasRoute);
    }

    [Fact]
    public async Task RemoteEndpoint_WithNoOrigin_FallsBackToTheAddressThatWasDialled()
    {
        // The fallback is the behaviour that shipped before any of this, not an improvement:
        // a caller that cannot say which machine it reached says which address it dialled.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            host: "127.0.0.1",
            origin: null);

        Assert.Equal("127.0.0.1", vm.RemoteEndpoint);
    }

    [Fact]
    public async Task OwnerText_NamesTheTabAndTheWindowHoldingTheQuestion()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(
                TunnelledOrigin, null, "Production", "Paris datacentre"));

        Assert.True(vm.HasOwnerText);
        Assert.Contains("Production", vm.OwnerText!, StringComparison.Ordinal);
        Assert.Contains("Paris datacentre", vm.OwnerText!, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", vm.OwnerText!, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", vm.OwnerText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerText_WithNothingToName_IsNotShownAtAll()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(TunnelledOrigin, null, null, "   "));

        Assert.Null(vm.OwnerText);
        Assert.False(vm.HasOwnerText);
    }

    [Fact]
    public async Task OwnerText_TwoSessionsOfOneName_AreNamedApartByTheAnnouncedOrdinal()
    {
        // What the owner line was expected to do and did not. It was fed DisplayTitle, which
        // this repository documents as identical by construction for two sessions of one
        // profile - so it read the same twice in exactly the case two same-named sessions were
        // the problem. It is fed the announced name now, which carries the ordinal
        // ConnectionViewModel already assigns to colliding titles.
        RdpCertificatePromptDialogViewModel first = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(
                TunnelledOrigin, null, "Production (1)", "Production - Detached"));
        RdpCertificatePromptDialogViewModel second = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(
                TunnelledOrigin, null, "Production (2)", "Production - Detached"));

        Assert.NotEqual(first.OwnerText, second.OwnerText);
        Assert.Contains("Production (1)", first.OwnerText!, StringComparison.Ordinal);
        Assert.Contains("Production (2)", second.OwnerText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerText_ADetachedWindowThatOnlyDecoratesTheTabName_AddsNoClause()
    {
        // The rule the type documented and did not implement. A detached window titles itself
        // with the SessionDetachTitle format - "Production" becomes "Production - Detached" -
        // so the two strings never matched, the window clause always stood, and two same-named
        // detached sessions read character for character alike at twice the length.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(
            alreadyTrusted: 0,
            origin: new RdpTrustPromptOrigin(
                TunnelledOrigin, null, "Production", "Production - Detached"));

        Assert.True(vm.HasOwnerText);
        Assert.Contains("Production", vm.OwnerText!, StringComparison.Ordinal);
        Assert.DoesNotContain("Detached", vm.OwnerText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyTrustedText_FirstCertificateEver_IsNotShownAtAll()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);

        Assert.Null(vm.AlreadyTrustedText);
        Assert.False(vm.HasAlreadyTrustedText);
    }

    [Fact]
    public async Task AlreadyTrustedText_Several_CarriesTheCount()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 3);

        Assert.True(vm.HasAlreadyTrustedText);
        Assert.Contains("3", vm.AlreadyTrustedText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyTrustedText_ExactlyOne_ReadsDifferentlyFromTheirPlural()
    {
        RdpCertificatePromptDialogViewModel one = await CreateAsync(alreadyTrusted: 1);
        RdpCertificatePromptDialogViewModel many = await CreateAsync(alreadyTrusted: 2);

        // Two sentences, not one sentence with a number substituted into it.
        Assert.NotEqual(one.AlreadyTrustedText, many.AlreadyTrustedText);
        Assert.DoesNotContain("{0}", one.AlreadyTrustedText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answer_BeforeAnyButtonIsPressed_IsNull()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);

        // The contract the session depends on: null is not an answer, and it is not a refusal
        // either. A question the pane took off the screen without one was answered by nobody,
        // which the session settles as RdpTrustAnswer.NotAsked - the connection stops without
        // telling anyone they declined something they were never shown.
        Assert.Null(vm.Answer);
    }

    [Fact]
    public async Task ThreeButtons_GiveThreeDifferentAnswers()
    {
        RdpCertificatePromptDialogViewModel trust = await CreateAsync(alreadyTrusted: 0);
        RdpCertificatePromptDialogViewModel once = await CreateAsync(alreadyTrusted: 0);
        RdpCertificatePromptDialogViewModel refuse = await CreateAsync(alreadyTrusted: 0);

        trust.TrustCommand.Execute(null);
        once.TrustOnceCommand.Execute(null);
        refuse.RefuseCommand.Execute(null);

        // Asserted as a set rather than one by one, so two commands wired to the same
        // answer is a red test and not a subtle loss of the middle option.
        Assert.Equal(
            new RdpTrustAnswer?[]
            {
                RdpTrustAnswer.TrustPermanently,
                RdpTrustAnswer.TrustForSession,
                RdpTrustAnswer.Refuse,
            },
            new[] { trust.Answer, once.Answer, refuse.Answer });
    }

    [Theory]
    [InlineData(RdpTrustAnswer.TrustPermanently)]
    [InlineData(RdpTrustAnswer.TrustForSession)]
    [InlineData(RdpTrustAnswer.Refuse)]
    public async Task Answering_RaisesTheAnswerItself_NotADialogResult(RdpTrustAnswer expected)
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);
        List<RdpTrustAnswer> raised = [];
        vm.Answered += raised.Add;

        CommandFor(vm, expected).Execute(null);

        // A boolean dialog result could never have carried three answers; the window it came
        // from mapped two of them onto true and lost the difference at the boundary.
        Assert.Equal([expected], raised);
    }

    [Fact]
    public async Task Dismissing_IsARefusal()
    {
        // Escape, and the pane being closed. Neither is approval, and the alternative is
        // opening a session nobody approved.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);
        List<RdpTrustAnswer> raised = [];
        vm.Answered += raised.Add;

        vm.RefuseFromDismissal();

        Assert.Equal(RdpTrustAnswer.Refuse, vm.Answer);
        Assert.Equal([RdpTrustAnswer.Refuse], raised);
    }

    [Fact]
    public async Task ASecondAnswerIsIgnored_SoTrustCannotArriveAfterARefusal()
    {
        // Escape landing on top of a click, or a second press. Without the latch the second
        // answer is raised too, and the session would settle on whichever arrived last - which
        // can be the approval the user had already declined by pressing Escape.
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);
        List<RdpTrustAnswer> raised = [];
        vm.Answered += raised.Add;

        vm.RefuseFromDismissal();
        vm.TrustCommand.Execute(null);

        Assert.Equal(RdpTrustAnswer.Refuse, vm.Answer);
        Assert.Equal([RdpTrustAnswer.Refuse], raised);
    }

    [Fact]
    public async Task Subject_IsOnlyOfferedWhenTheProbeReadOne()
    {
        RdpCertificatePromptDialogViewModel named = await CreateAsync(alreadyTrusted: 0, subject: "CN=dc04");
        RdpCertificatePromptDialogViewModel anonymous = await CreateAsync(alreadyTrusted: 0, subject: null);
        RdpCertificatePromptDialogViewModel blank = await CreateAsync(alreadyTrusted: 0, subject: "   ");

        Assert.True(named.HasSubject);
        Assert.False(anonymous.HasSubject);
        Assert.False(blank.HasSubject);
    }

    private static System.Windows.Input.ICommand CommandFor(
        RdpCertificatePromptDialogViewModel vm,
        RdpTrustAnswer answer) => answer switch
        {
            RdpTrustAnswer.TrustPermanently => vm.TrustCommand,
            RdpTrustAnswer.TrustForSession => vm.TrustOnceCommand,
            _ => vm.RefuseCommand,
        };

    private static async Task<RdpCertificatePromptDialogViewModel> CreateAsync(
        int alreadyTrusted,
        string? subject = "CN=dc04",
        string host = "dc-pool.example.com",
        RdpTrustPromptOrigin? origin = null)
    {
        return new RdpCertificatePromptDialogViewModel(
            await LocalizerAsync(),
            new RdpCertificatePromptContext(
                "DC pool",
                host,
                "SHA256:AA:BB:01",
                subject,
                alreadyTrusted),
            origin);
    }

    /// <summary>The shipped catalogue, plus the sentences this change adds to it.</summary>
    /// <remarks>
    /// <para><b>The owner sentences are not in <c>locales/en.json</c> yet.</b> New user-facing
    /// strings are handed to the integrator, who merges them into both catalogues; until that
    /// lands, <see cref="LocalizationManager.Format"/> returns the key rather than a sentence,
    /// and an assertion on the rendered text would be measuring the merge rather than the
    /// ViewModel. Templates are supplied here so these tests keep measuring what they are for:
    /// that the tab and the window reach the format at all. Whether the keys have landed is
    /// owned by the locale coverage guard, which is the right place for it.</para>
    /// <para>The catalogue is copied rather than edited: <c>locales/en.json</c> mixes literal
    /// UTF-8 with escapes and is not safe to re-serialize.</para>
    /// </remarks>
    private static async Task<LocalizationManager> LocalizerAsync()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "locales", "en.json");
        Dictionary<string, string> strings =
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(shipped))
            ?? [];

        // Only added where the catalogue has not caught up, so a merged key is exercised as
        // merged and this scaffolding disappears on its own.
        _ = strings.TryAdd(
            RdpTrustPromptOwnerLocaleKeys.Tab,
            "This question belongs to the tab \"{0}\".");
        _ = strings.TryAdd(
            RdpTrustPromptOwnerLocaleKeys.TabInWindow,
            "This question belongs to the tab \"{0}\", in the window \"{1}\".");
        _ = strings.TryAdd(
            RdpTrustPromptOwnerLocaleKeys.Window,
            "This question belongs to the window \"{0}\".");

        string directory = Path.Combine(
            AppContext.BaseDirectory, "locale-prompt-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "en.json"), JsonSerializer.Serialize(strings));

        LocalizationManager localizer = new();
        await localizer.LoadAsync(directory, "en");
        return localizer;
    }
}
