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

namespace Heimdall.Ssh;

/// <summary>
/// Routes raw session exceptions to typed security and disconnect handlers.
/// </summary>
public static class SshSessionFailureDispatcher
{
    /// <summary>
    /// Dispatches a session failure. Security handlers fire first so callers can
    /// surface a security banner before ordinary disconnect handling runs.
    /// </summary>
    /// <param name="ex">The exception captured by the SSH/SFTP layer.</param>
    /// <param name="securityHandler">Optional handler for typed security events.</param>
    /// <param name="disconnectedHandler">Optional disconnect handler.</param>
    public static void Dispatch(
        Exception ex,
        Action<SshSessionSecurityEvent>? securityHandler,
        Action<SshSessionDisconnectInfo>? disconnectedHandler)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var failure = FailureClassifier.Classify(ex);

        if (HostKeyRejectionFinder.TryFind(ex, out HostKeyRejectedException? hostKeyRejected))
        {
            securityHandler?.Invoke(new SshSessionSecurityEvent(
                failure.Code,
                hostKeyRejected.Message,
                hostKeyRejected.Host,
                hostKeyRejected.Port,
                hostKeyRejected.Algorithm,
                hostKeyRejected.PresentedFingerprint,
                hostKeyRejected.StoredFingerprint));
        }

        disconnectedHandler?.Invoke(SshSessionDisconnectInfo.FromFailure(failure));
    }
}
