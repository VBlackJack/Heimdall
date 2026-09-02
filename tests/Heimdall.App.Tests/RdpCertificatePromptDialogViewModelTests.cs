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
/// during BL-0089, which is why the dialog holds no decisions to test.
/// </remarks>
public sealed class RdpCertificatePromptDialogViewModelTests
{
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

        // The contract the presenter depends on: null is not an answer, and a window
        // closed by its title-bar cross has to be read as a refusal.
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

    [Fact]
    public async Task Trusting_ClosesTheWindowAsConfirmed()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);
        bool? closedWith = null;
        vm.CloseRequested += result => closedWith = result;

        vm.TrustCommand.Execute(null);

        Assert.True(closedWith);
    }

    [Fact]
    public async Task Refusing_ClosesTheWindowAsCancelled()
    {
        RdpCertificatePromptDialogViewModel vm = await CreateAsync(alreadyTrusted: 0);
        bool? closedWith = null;
        vm.CloseRequested += result => closedWith = result;

        vm.RefuseCommand.Execute(null);

        Assert.False(closedWith);
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

    [Fact]
    public void PromptKey_DiffersByProfile_SoTwoProfilesAreAskedSeparately()
    {
        // The defect this pins: RDP trust is per profile, so coalescing two profiles'
        // questions into one dialog would name profile A and write the answer into both
        // trust sets - durable trust granted from a question the user never saw.
        Assert.NotEqual(
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-a")),
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-b")));
    }

    [Fact]
    public void PromptKey_SameProfileAndCertificate_IsTheSameQuestion()
    {
        // The other half: two tabs of one profile meeting the same certificate must
        // still be asked once, which is what the coordinator is for.
        Assert.Equal(
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-a")),
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-a")));
    }

    [Fact]
    public void PromptKey_DifferentCertificate_IsADifferentQuestion()
        => Assert.NotEqual(
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-a", "SHA256:AA:BB:01")),
            DialogRdpCertificateTrustPrompt.BuildKey(Context("profile-a", "SHA256:CC:DD:02")));

    [Fact]
    public void PromptKey_NoProfile_IsStillBuildable()
    {
        // A context without a profile must not throw its way out of the prompt: the
        // fallback loses the separation, it does not lose the question.
        TrustPromptKey key = DialogRdpCertificateTrustPrompt.BuildKey(
            new RdpCertificatePromptContext("DC pool", "dc-pool.example.com", "SHA256:AA", null, 0));

        Assert.Equal(string.Empty, key.Scope);
    }

    [Fact]
    public async Task AskAsync_TwoTabsOfOneProfileMeetingOneCertificate_AskOnce()
    {
        TaskCompletionSource<RdpTrustAnswer> answered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int displayed = 0;
        DialogRdpCertificateTrustPrompt prompt = new(
            new LocalizationManager(),
            new TrustPromptCoordinator(),
            (_, _) =>
            {
                _ = Interlocked.Increment(ref displayed);
                return answered.Task;
            });

        Task<RdpTrustAnswer> first = prompt.AskAsync(Context("profile-a"), CancellationToken.None);
        Task<RdpTrustAnswer> second = prompt.AskAsync(Context("profile-a"), CancellationToken.None);
        answered.SetResult(RdpTrustAnswer.TrustForSession);
        RdpTrustAnswer[] answers = await Task.WhenAll(first, second);

        // The defect this pins: bypassing the coordinator stacks one modal window per tab
        // for a question that has one answer, and answering the first leaves the second
        // still asking something already settled.
        Assert.Equal(1, Volatile.Read(ref displayed));
        Assert.All(answers, answer => Assert.Equal(RdpTrustAnswer.TrustForSession, answer));
    }

    [Fact]
    public async Task AskAsync_TwoProfilesMeetingOneCertificate_AreAskedSeparately()
    {
        Dictionary<string, TaskCompletionSource<RdpTrustAnswer>> gates = new(StringComparer.Ordinal)
        {
            ["profile-a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["profile-b"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        int displayed = 0;
        DialogRdpCertificateTrustPrompt prompt = new(
            new LocalizationManager(),
            new TrustPromptCoordinator(),
            (context, displayCt) =>
            {
                _ = displayCt;
                _ = Interlocked.Increment(ref displayed);
                return gates[context.ProfileId!].Task;
            });

        Task<RdpTrustAnswer> first = prompt.AskAsync(Context("profile-a"), CancellationToken.None);
        Task<RdpTrustAnswer> second = prompt.AskAsync(Context("profile-b"), CancellationToken.None);

        // The positive control for the count above, and the rule that must survive the
        // coalescing: RDP trust is per profile, so one dialog naming profile A may never
        // supply the answer for profile B. The second question waits its turn; it is not
        // answered by the first.
        gates["profile-a"].SetResult(RdpTrustAnswer.TrustPermanently);
        Assert.Equal(RdpTrustAnswer.TrustPermanently, await first);

        gates["profile-b"].SetResult(RdpTrustAnswer.Refuse);
        Assert.Equal(RdpTrustAnswer.Refuse, await second);
        Assert.Equal(2, Volatile.Read(ref displayed));
    }

    private static RdpCertificatePromptContext Context(
        string profileId,
        string thumbprint = "SHA256:AA:BB:01")
        => new("DC pool", "dc-pool.example.com", thumbprint, "CN=dc04", 0)
        {
            ProfileId = profileId,
        };

    private static async Task<RdpCertificatePromptDialogViewModel> CreateAsync(
        int alreadyTrusted,
        string? subject = "CN=dc04")
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        return new RdpCertificatePromptDialogViewModel(
            localizer,
            new RdpCertificatePromptContext(
                "DC pool",
                "dc-pool.example.com",
                "SHA256:AA:BB:01",
                subject,
                alreadyTrusted));
    }
}
