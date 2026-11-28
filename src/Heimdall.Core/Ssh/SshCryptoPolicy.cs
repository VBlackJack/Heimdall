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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Controls which SSH transport algorithms (KEX, host key, cipher, MAC) Heimdall offers during handshake.
/// </summary>
public enum SshCryptoPolicy
{
    /// <summary>Default SSH.NET algorithm preferences (post-quantum and modern algorithms preferred, legacy fallback allowed).</summary>
    Standard = 0,

    /// <summary>Strict modern cryptography only (ANSSI / SecNumCloud posture: removes SHA-1, CBC, 3DES, RC4, Blowfish, DSS, and legacy ssh-rsa).</summary>
    StrictModern = 1,

    /// <summary>Full compatibility mode preserving legacy algorithms for older network appliances.</summary>
    LegacyCompatible = 2
}

/// <summary>
/// Resolves the effective <see cref="SshCryptoPolicy"/> from an optional per-profile override and global setting.
/// </summary>
public static class SshCryptoPolicyResolver
{
    public static SshCryptoPolicy Resolve(SshCryptoPolicy? profileOverride, SshCryptoPolicy globalDefault)
        => profileOverride ?? globalDefault;
}
