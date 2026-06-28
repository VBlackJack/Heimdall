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
/// Coarse, non-secret reasons a Windows Hello vault unwrap could not proceed.
/// </summary>
public enum VaultHelloFailureReason
{
    /// <summary>The configured Windows Hello credential was not found.</summary>
    NotFound,

    /// <summary>The user cancelled the Hello prompt.</summary>
    UserCanceled,

    /// <summary>Windows reported that the user preferred the master password path.</summary>
    UserPrefersPassword,

    /// <summary>The Hello security device is locked.</summary>
    SecurityDeviceLocked,

    /// <summary>Hello or the required platform capability is unavailable.</summary>
    Unavailable,

    /// <summary>The wrapped data was malformed, tampered, or failed authentication.</summary>
    CryptoFailure,
}
