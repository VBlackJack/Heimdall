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

using Heimdall.App.Services.Handlers;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Which refusals earn a second, interactive attempt.
/// </summary>
/// <remarks>
/// The set is written out here rather than sampled. A predicate that decides whether to spend
/// another authentication attempt against a remote account is one where both directions matter:
/// a member wrongly added spends an attempt that cannot help, and a member wrongly dropped takes
/// away the only route a host has.
/// </remarks>
public sealed class SshPlinkRetryPolicyTests
{
    /// <summary>
    /// The member this policy exists for.
    /// </summary>
    /// <remarks>
    /// P-01. The embedded client has one secret and no way to ask for another, so a server whose
    /// remaining question is a verification code is unreachable through it. Plink runs with a
    /// real console in the terminal pane and can ask.
    /// </remarks>
    [Fact]
    public void AnUnanswerableInteractiveQuestion_EarnsAPlinkRetry()
    {
        Assert.True(
            SshPlinkRetryPolicy.AllowsPlinkRetry(
                SshFailureCode.KeyboardInteractiveUnsupportedPrompt));
    }

    [Theory]
    [InlineData(SshFailureCode.AuthRejected)]
    [InlineData(SshFailureCode.KeyRejected)]
    [InlineData(SshFailureCode.PassphraseRejected)]
    [InlineData(SshFailureCode.PasswordRejected)]
    [InlineData(SshFailureCode.NoSupportedAuth)]
    [InlineData(SshFailureCode.KeyboardInteractiveNoPassword)]
    [InlineData(SshFailureCode.KeyboardInteractiveUnsupportedPrompt)]
    public void EveryRetryableRefusal_IsRetried(SshFailureCode code)
    {
        Assert.True(SshPlinkRetryPolicy.AllowsPlinkRetry(code));
    }

    /// <summary>
    /// A server already counting failures against this account gets no extra attempt.
    /// </summary>
    /// <remarks>
    /// The one exclusion that is a safety decision rather than a usefulness one: another attempt
    /// is the single thing that can make this outcome worse instead of better.
    /// </remarks>
    [Fact]
    public void TooManyAuthFailures_EarnsNoRetry()
    {
        Assert.False(SshPlinkRetryPolicy.AllowsPlinkRetry(SshFailureCode.TooManyAuthFailures));
    }

    /// <summary>
    /// Nothing outside the named set is retried.
    /// </summary>
    /// <remarks>
    /// Exhaustive over the enum rather than a sample, so a code added later is refused by
    /// default and joins the set only by a deliberate edit to the list above. The alternative,
    /// a policy that silently widens as the enum grows, is how an attempt gets spent on a
    /// failure nobody considered.
    /// </remarks>
    [Fact]
    public void NoOtherFailure_IsRetried()
    {
        SshFailureCode[] retryable =
        [
            SshFailureCode.AuthRejected,
            SshFailureCode.KeyRejected,
            SshFailureCode.PassphraseRejected,
            SshFailureCode.PasswordRejected,
            SshFailureCode.NoSupportedAuth,
            SshFailureCode.KeyboardInteractiveNoPassword,
            SshFailureCode.KeyboardInteractiveUnsupportedPrompt,
        ];

        var unexpected = Enum.GetValues<SshFailureCode>()
            .Where(code => !retryable.Contains(code))
            .Where(SshPlinkRetryPolicy.AllowsPlinkRetry)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "these refusals are retried without being named in the policy: "
            + string.Join(", ", unexpected));
    }
}
