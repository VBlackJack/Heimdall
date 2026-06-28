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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Maps Windows.Security.Credentials.KeyCredentialStatus names to headless vault
/// outcomes without making Heimdall.Core depend on WinRT.
/// </summary>
public static class VaultHelloStatusMapper
{
    /// <summary>
    /// Maps a status name. <c>Success</c> returns null; all other statuses map to
    /// a fail-closed reason.
    /// </summary>
    public static VaultHelloFailureReason? MapKeyCredentialStatus(string? statusName)
    {
        return statusName switch
        {
            "Success" => null,
            "NotFound" => VaultHelloFailureReason.NotFound,
            "UserCanceled" => VaultHelloFailureReason.UserCanceled,
            "UserPrefersPassword" => VaultHelloFailureReason.UserPrefersPassword,
            "SecurityDeviceLocked" => VaultHelloFailureReason.SecurityDeviceLocked,
            _ => VaultHelloFailureReason.Unavailable,
        };
    }
}
