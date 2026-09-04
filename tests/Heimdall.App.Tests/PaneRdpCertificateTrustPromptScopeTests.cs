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
using Heimdall.Core.Certificates;

namespace Heimdall.App.Tests;

/// <summary>
/// A saved profile and a typed destination meeting one certificate at one host are two questions.
/// </summary>
/// <remarks>
/// <para>The audit's second objection to the earlier design: the coalescer keyed on host,
/// thumbprint and identifier string, so a profile and a typed destination sharing an identifier
/// and reaching the same host coalesced into ONE question, and one press wrote durable trust
/// under two owners. The split of the store separates the reads; folding the scope into the
/// prompt key is what separates the write.</para>
/// <para>The identity string is deliberately the same on both sides - a profile whose
/// identifier is literally the host - so a key that dropped the scope would collapse the two.</para>
/// </remarks>
public sealed class PaneRdpCertificateTrustPromptScopeTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);
    private const string Host = "prod.example";

    [Fact]
    public void PromptKey_DiffersByScope_WhenTheIdentityStringIsTheSame()
    {
        TrustPromptKey profile = PaneRdpCertificateTrustPrompt.BuildKey(
            Context(RdpTrustKey.ForProfile(Host), scopeId: "pane-1"));
        TrustPromptKey typed = PaneRdpCertificateTrustPrompt.BuildKey(
            Context(RdpTrustKey.ForTypedDestination(Host), scopeId: "pane-1"));

        Assert.NotEqual(profile, typed);
    }

    // The control: two panes of one typed destination are still one question, exactly as two
    // panes of one profile are.
    [Fact]
    public void PromptKey_TwoPanesOfOneTypedDestination_IsStillOneQuestion()
    {
        Assert.Equal(
            PaneRdpCertificateTrustPrompt.BuildKey(Context(RdpTrustKey.ForTypedDestination("PROD.example"), scopeId: "pane-1")),
            PaneRdpCertificateTrustPrompt.BuildKey(Context(RdpTrustKey.ForTypedDestination("prod.example"), scopeId: "pane-2")));
    }

    [Fact]
    public async Task AskAsync_AProfileAndATypedDestination_AreAskedSeparately_AndOneAnswerDoesNotSettleTheOther()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        GatedSurface profilePane = new();
        GatedSurface typedPane = new();
        using IDisposable one = registry.Register("pane-profile", profilePane);
        using IDisposable two = registry.Register("pane-typed", typedPane);

        PaneRdpCertificateTrustPrompt prompt = new(new RdpTrustQuestionCoalescer(), registry);
        Task<RdpTrustAnswer> profileAnswer = prompt.AskAsync(
            Context(RdpTrustKey.ForProfile(Host), scopeId: "pane-profile"), CancellationToken.None);
        Task<RdpTrustAnswer> typedAnswer = prompt.AskAsync(
            Context(RdpTrustKey.ForTypedDestination(Host), scopeId: "pane-typed"), CancellationToken.None);

        await profilePane.Displayed.Task.WaitAsync(CompletionTimeout);
        await typedPane.Displayed.Task.WaitAsync(CompletionTimeout);

        // The profile approves. Under one shared question the typed pane would now be
        // withdrawn and handed this approval; it must instead still be asking.
        profilePane.Answer(RdpTrustAnswer.TrustPermanently);
        Assert.Equal(RdpTrustAnswer.TrustPermanently, await profileAnswer.WaitAsync(CompletionTimeout));
        Assert.False(typedAnswer.IsCompleted);
        Assert.Equal(0, typedPane.Withdrawals);

        typedPane.Answer(RdpTrustAnswer.Refuse);
        Assert.Equal(RdpTrustAnswer.Refuse, await typedAnswer.WaitAsync(CompletionTimeout));
    }

    private static RdpCertificatePromptContext Context(RdpTrustKey key, string scopeId)
        => new("Production", Host, "SHA256:AA:BB:01", "CN=prod", 0)
        {
            TrustKey = key,
            PromptScopeId = scopeId,
        };

    /// <summary>A surface that reports when it displayed and answers when told to.</summary>
    private sealed class GatedSurface : IRdpTrustPromptSurface
    {
        private readonly TaskCompletionSource<RdpTrustAnswer> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _withdrawals;

        public TaskCompletionSource Displayed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Withdrawals => Volatile.Read(ref _withdrawals);

        public void Answer(RdpTrustAnswer answer) => _answer.TrySetResult(answer);

        public async Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            _ = Displayed.TrySetResult();
            using CancellationTokenRegistration withdrawal = cancellationToken.Register(() =>
            {
                _ = Interlocked.Increment(ref _withdrawals);
                _ = _answer.TrySetResult(RdpTrustAnswer.NotAsked);
            });
            return await _answer.Task.ConfigureAwait(false);
        }
    }
}
