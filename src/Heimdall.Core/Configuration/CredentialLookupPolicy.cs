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

namespace Heimdall.Core.Configuration;

/// <summary>
/// The names an external credential provider can be asked for a profile's password under.
/// </summary>
public static class CredentialLookupPolicy
{
    /// <summary>
    /// The entry name a confirmed local profile is looked up by: its vault entry name, else its
    /// display name.
    /// </summary>
    public static string NameBasedTitle(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return string.IsNullOrWhiteSpace(profile.VaultEntryName)
            ? profile.DisplayName
            : profile.VaultEntryName;
    }
}
