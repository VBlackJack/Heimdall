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
/// Exactly which failures are worth offering a password for, and how many attempts.
/// </summary>
/// <remarks>
/// Enumerated over the whole enum rather than spot-checked, so both directions are
/// caught: someone widening the set to match the one that gates SSH's plink fallback,
/// and someone narrowing it. Neither needs a socket, a dialog or a tunnel to detect.
/// </remarks>
public sealed class SftpPasswordPromptPolicyTests
{
    private static readonly SshFailureCode[] Allowed =
    [
        SshFailureCode.NoSupportedAuth,
        SshFailureCode.KeyboardInteractiveNoPassword,
        SshFailureCode.AuthRejected,
        SshFailureCode.PasswordRejected,
    ];

    [Fact]
    public void AllowsPasswordRetry_MatchesTheFourCodeSetExactly()
    {
        foreach (SshFailureCode code in Enum.GetValues<SshFailureCode>())
        {
            bool expected = Allowed.Contains(code);
            Assert.Equal(expected, SftpPasswordPromptPolicy.AllowsPasswordRetry(code));
        }
    }

    [Fact]
    public void AllowsPasswordRetry_ExcludesFailuresAPasswordCannotFix()
    {
        // A key or passphrase rejection belongs to the set that gates SSH's fallback,
        // because that path can also do key authentication. A password box cannot fix a
        // key, so sharing the literal would share text where the decisions differ.
        Assert.False(SftpPasswordPromptPolicy.AllowsPasswordRetry(SshFailureCode.KeyRejected));
        Assert.False(SftpPasswordPromptPolicy.AllowsPasswordRetry(SshFailureCode.PassphraseRejected));

        // Terminal even on the first attempt: retrying opens a NEW connection, which
        // resets the server's per-connection attempt counter. That is the shape that
        // walks an account into a lockout rather than away from one.
        Assert.False(SftpPasswordPromptPolicy.AllowsPasswordRetry(SshFailureCode.TooManyAuthFailures));
    }

    [Fact]
    public void MaxConnectAttempts_IsTheOriginalPlusExactlyOneRetry()
    {
        // Never a loop. Each attempt carrying a password registers more than one failed
        // authentication on the remote, so a typo costs another press of Connect rather
        // than an account.
        Assert.Equal(2, SftpPasswordPromptPolicy.MaxConnectAttempts);
    }
}
