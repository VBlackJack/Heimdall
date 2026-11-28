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
using Heimdall.Ssh;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Maps the typed refusals of the native OpenSSH launch to locale keys, so no English exception
/// text reaches the user.
/// </summary>
internal static class OpenSshLaunchFailureMessages
{
    /// <summary>Locale key for a refused <c>ssh.exe</c> resolution.</summary>
    public static string KeyFor(OpenSshRefusalReason? refusal) => refusal switch
    {
        OpenSshRefusalReason.NotWindowsBuild => SshLocalizationKeys.ErrorOpenSshNotWindowsBuild,
        OpenSshRefusalReason.VersionUnreadable => SshLocalizationKeys.ErrorOpenSshVersionUnreadable,
        _ => SshLocalizationKeys.ErrorOpenSshNotFound,
    };

    /// <summary>Locale key for a refused launch.</summary>
    public static string KeyFor(OpenSshLaunchFailure failure) => failure switch
    {
        OpenSshLaunchFailure.InvalidHost => SshLocalizationKeys.ErrorInvalidTargetHost,
        OpenSshLaunchFailure.InvalidPort => SshLocalizationKeys.ErrorInvalidTargetPort,
        OpenSshLaunchFailure.InvalidUsername => SshLocalizationKeys.ErrorInvalidSshUsername,
        OpenSshLaunchFailure.InvalidKnownHostsPath => SshLocalizationKeys.ErrorOpenSshLaunchArguments,
        OpenSshLaunchFailure.InvalidHostKeyAlias => SshLocalizationKeys.ErrorOpenSshLaunchArguments,
        OpenSshLaunchFailure.UnsupportedPinnedKeyType => SshLocalizationKeys.ErrorSshHostKeyTypeUnsupported,
        OpenSshLaunchFailure.NoAllowedHostKeyAlgorithm => SshLocalizationKeys.ErrorSshHostKeyTypeUnsupported,
        OpenSshLaunchFailure.PathNotAllowed => SshLocalizationKeys.ErrorOpenSshPathNotAllowed,
        OpenSshLaunchFailure.SetEnvUnsupported => SshLocalizationKeys.ErrorOpenSshSetEnvUnsupported,
        OpenSshLaunchFailure.NoCommonKexAlgorithm => SshLocalizationKeys.ErrorOpenSshNoCommonKex,
        OpenSshLaunchFailure.NoCommonCipher => SshLocalizationKeys.ErrorOpenSshNoCommonCipher,
        OpenSshLaunchFailure.NoCommonMac => SshLocalizationKeys.ErrorOpenSshNoCommonMac,
        _ => SshLocalizationKeys.ErrorOpenSshLaunchArguments,
    };
}
