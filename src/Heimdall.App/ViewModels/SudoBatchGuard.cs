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

using Heimdall.Sftp;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Remembers, for one batch, the first sudo failure that cannot change from one entry to the next
/// (a refused password, an account not allowed to use sudo, no sudo, a missing tool, a refused
/// connection), so the batch stops calling sudo instead of repeating the same refusal.
/// </summary>
/// <remarks>
/// One instance per batch, never shared: a failure that belongs to one entry (an immutable file,
/// a refused link, a generic failure) is not recorded and the batch keeps going.
/// </remarks>
internal sealed class SudoBatchGuard
{
    /// <summary>The failure that stopped the batch, or null while sudo is still worth trying.</summary>
    public Exception? StoppingFailure { get; private set; }

    /// <summary>Whether a batch-fatal failure was recorded.</summary>
    public bool IsStopped => StoppingFailure is not null;

    /// <summary>Records <paramref name="failure"/> when it is batch-fatal and nothing stopped the batch yet.</summary>
    public void Record(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (StoppingFailure is null && IsBatchFatal(failure))
        {
            StoppingFailure = failure;
        }
    }

    /// <summary>
    /// Whether the batch stopped on the password itself (refused or unavailable). Only then does a
    /// listing through sudo, which sends the same password, share the failure; any other stop
    /// leaves sudo able to list.
    /// </summary>
    public bool StoppedOnAuthentication => StoppingFailure switch
    {
        SudoAuthenticationException auth => auth.Kind is SudoFailureKind.PasswordRejected
            or SudoFailureKind.PasswordUnavailable,
        PrivilegedWriteFailedException write => write.Kind is PrivilegedWriteFailureKind.PasswordRejected
            or PrivilegedWriteFailureKind.PasswordUnavailable,
        _ => false,
    };

    /// <summary>Whether repeating the call for another entry would fail the same way.</summary>
    internal static bool IsBatchFatal(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure switch
        {
            SudoAuthenticationException => true,
            PrivilegedWriteFailedException write => write.Kind is
                PrivilegedWriteFailureKind.PasswordRejected
                or PrivilegedWriteFailureKind.PasswordUnavailable
                or PrivilegedWriteFailureKind.NotPermitted
                or PrivilegedWriteFailureKind.SudoMissing,
            SudoToolingUnavailableException => true,
            SshAuthenticationException => true,
            HostKeyRejectedException => true,
            _ => false,
        };
    }
}

/// <summary>
/// An entry was not tried as root because an earlier one proved that sudo cannot succeed in this batch.
/// </summary>
internal sealed class SudoBatchStoppedException(Exception stoppingFailure)
    : Exception("sudo was not tried: an earlier entry showed it cannot succeed in this batch.", stoppingFailure)
{
    /// <summary>The failure that stopped the batch.</summary>
    public Exception StoppingFailure { get; } = stoppingFailure;
}
