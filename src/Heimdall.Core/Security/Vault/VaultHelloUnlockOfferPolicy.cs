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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Pure predicate for whether the UI may offer Windows Hello vault unlock.
/// </summary>
public static class VaultHelloUnlockOfferPolicy
{
    /// <summary>
    /// Returns true when a Hello wrapper is enrolled and the periodic
    /// master-password re-authentication policy is not currently due.
    /// </summary>
    public static bool ShouldOfferHelloUnlock(AppSettings settings, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.VaultEnabled || !settings.VaultHelloEnrolled)
        {
            return false;
        }

        return !VaultHelloReauthPolicy.ShouldRequireMasterPassword(
            settings.VaultLastMasterUnlockUtc,
            settings.VaultHelloMaxDaysBeforeMasterPassword,
            nowUtc);
    }
}
