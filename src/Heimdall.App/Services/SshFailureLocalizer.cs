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

using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Turns a classified connection failure into a message in the user's language.
/// </summary>
/// <remarks>
/// Lifted out of the SSH handler so both handlers can call it. Until they did, the SSH
/// path localized and the SFTP path returned the classifier's own English literal, so the
/// same server produced a French message from one protocol and an English one from the
/// other - the second, unnamed half of the divergence BL-0086 reports.
/// </remarks>
public static class SshFailureLocalizer
{
    /// <summary>Returns the failure with its message localized, or unchanged if no key matches.</summary>
    /// <param name="failure">The classified failure.</param>
    /// <param name="localizer">The active catalogue.</param>
    /// <param name="targetHost">
    /// Interpolated into the network-level messages, which are the only ones that name a
    /// host.
    /// </param>
    public static SshFailureInfo Localize(
        SshFailureInfo failure,
        LocalizationManager localizer,
        string targetHost)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(localizer);

        string message = FailureClassifier.FormatMessage(
            failure,
            key =>
            {
                if (!localizer.HasKey(key))
                {
                    return null;
                }

                object formatArgument = failure.Code is SshFailureCode.NetworkRefused
                    or SshFailureCode.NetworkTimedOut
                    or SshFailureCode.NetworkReset
                    ? targetHost
                    : failure.Message;
                return localizer.Format(key, formatArgument);
            });

        return new SshFailureInfo(
            failure.Code,
            message,
            failure.IsFatal,
            failure.OriginalException);
    }
}
