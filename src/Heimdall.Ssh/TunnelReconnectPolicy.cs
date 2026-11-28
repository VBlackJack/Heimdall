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

using Heimdall.Ssh.Agents;
using Renci.SshNet.Common;

namespace Heimdall.Ssh;

/// <summary>
/// Bounded exponential backoff and fail-closed classification policy for opt-in SSH tunnel
/// auto-reconnection. Security, host-key, authentication, and user cancellation errors never retry.
/// </summary>
public static class TunnelReconnectPolicy
{
    public const int DefaultMaxAttempts = 5;
    public const int BaseDelaySeconds = 2;
    public const int MaxDelaySeconds = 32;

    /// <summary>Returns the bounded exponential delay for 1-based attempt number (<c>2s..32s</c>).</summary>
    public static TimeSpan GetBackoffDelay(int attempt)
    {
        if (attempt <= 1)
        {
            return TimeSpan.FromSeconds(BaseDelaySeconds);
        }

        int shift = Math.Min(attempt - 1, 4);
        int seconds = Math.Min(MaxDelaySeconds, BaseDelaySeconds << shift);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Returns <see langword="true"/> only for transient transport drops; fails closed for host-key
    /// mismatches/revocations, authentication errors, user cancellations, or invalid chains/configs.
    /// </summary>
    public static bool ShouldRetry(Exception? exception, SshFailureCode? failureCode = null)
    {
        if (failureCode is { } code && IsPermanentFailureCode(code))
        {
            return false;
        }

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < 6; depth++, current = current.InnerException)
        {
            if (current is OperationCanceledException
                or HostKeyRejectedException
                or SshAuthenticationException
                or SshAgentKeyNotLoadedException
                or SshAgentSigningDeclinedException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPermanentFailureCode(SshFailureCode code) => code switch
    {
        SshFailureCode.HostKeyMismatch => true,
        SshFailureCode.HostKeyUnavailable => true,
        SshFailureCode.HostKeyRevoked => true,
        SshFailureCode.HostCertificateInvalid => true,
        SshFailureCode.Cancelled => true,
        SshFailureCode.AgentKeyNotLoaded => true,
        SshFailureCode.AgentSigningDeclined => true,
        SshFailureCode.CredentialUnreadable => true,
        SshFailureCode.PassphraseRequired => true,
        SshFailureCode.PassphraseRejected => true,
        SshFailureCode.KeyFileInvalid => true,
        SshFailureCode.KeyFileNotFound => true,
        SshFailureCode.KeyRejected => true,
        SshFailureCode.PasswordRejected => true,
        SshFailureCode.AuthRejected => true,
        SshFailureCode.NoSupportedAuth => true,
        SshFailureCode.TooManyAuthFailures => true,
        SshFailureCode.KeyboardInteractiveNoPassword => true,
        SshFailureCode.KeyboardInteractiveUnsupportedPrompt => true,
        SshFailureCode.ChainDepthExceeded => true,
        SshFailureCode.CircularChainDependency => true,
        SshFailureCode.PageantKeyUnavailable => true,
        SshFailureCode.PageantNoIdentities => true,
        _ => false
    };
}
