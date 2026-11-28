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
/// Public identity loaded in an SSH agent.
/// </summary>
public interface ISshAgentKey
{
    /// <summary>Extended IPC sign timeout for hardware security keys requiring physical touch (30 s).</summary>
    public const int SecurityKeySignTimeoutMilliseconds = 30_000;

    string Comment { get; }
    string KeyType { get; }
    byte[] PublicKeyBlob { get; }
    byte[] Sign(byte[] data, SshAgentSignFlags flags);

    /// <summary>Signs data with an explicit IPC timeout (used for FIDO2 hardware keys waiting on user touch).</summary>
    byte[] Sign(byte[] data, SshAgentSignFlags flags, int timeoutMilliseconds) => Sign(data, flags);

    /// <summary>True when this identity is backed by a FIDO2 hardware security key (<c>sk-*</c>).</summary>
    bool IsSecurityKey => IsSecurityKeyType(KeyType);

    /// <summary>Returns true when <paramref name="keyType"/> is an OpenSSH FIDO2/U2F security key or certificate type.</summary>
    public static bool IsSecurityKeyType(string? keyType) => keyType is
        "sk-ssh-ed25519@openssh.com"
        or "sk-ecdsa-sha2-nistp256@openssh.com"
        or "sk-ssh-ed25519-cert-v01@openssh.com"
        or "sk-ecdsa-sha2-nistp256-cert-v01@openssh.com";
}
