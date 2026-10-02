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

using Heimdall.App.Localization;
using Heimdall.Core.Localization;

namespace Heimdall.App.Services;

internal static class SshFailureMessageBuilder
{
    public static string HostKeyMismatch(
        LocalizationManager localizer,
        string storedFingerprint,
        string presentedFingerprint)
    {
        string message = localizer[SshLocalizationKeys.ErrorHostKeyMismatch];
        string detail = localizer.Format(
            SshLocalizationKeys.ErrorHostKeyMismatchDetail,
            storedFingerprint,
            presentedFingerprint);

        return $"{message} {detail}";
    }

    public static string HostKeyUnavailable(LocalizationManager localizer)
    {
        string message = localizer[SshLocalizationKeys.ErrorSshHostKeyUnavailable];
        return message;
    }

    /// <summary>
    /// A host key the user declined (or a prompt that was closed) is not a cancelled
    /// connection: the message names the host whose key was refused.
    /// </summary>
    public static string HostKeyRejected(LocalizationManager localizer, string host, int port)
    {
        return localizer.Format(SshLocalizationKeys.ErrorSshHostKeyRejected, host, port);
    }

    public static string Cancelled(LocalizationManager localizer)
    {
        string message = localizer[SshLocalizationKeys.ErrorSshCancelled];
        return message;
    }
}
