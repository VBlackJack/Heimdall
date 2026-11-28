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

using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.OpenSsh;

internal sealed class OpenSshAgentKey : ISshAgentKey
{
    private readonly OpenSshPipeAgent _agent;

    public OpenSshAgentKey(
        OpenSshPipeAgent agent,
        byte[] publicKeyBlob,
        string comment,
        string keyType)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        PublicKeyBlob = publicKeyBlob ?? throw new ArgumentNullException(nameof(publicKeyBlob));
        Comment = comment ?? throw new ArgumentNullException(nameof(comment));
        KeyType = keyType ?? throw new ArgumentNullException(nameof(keyType));
    }

    public string Comment { get; }
    public string KeyType { get; }
    public byte[] PublicKeyBlob { get; }

    public byte[] Sign(byte[] data, SshAgentSignFlags flags)
    {
        int? timeoutMs = ISshAgentKey.IsSecurityKeyType(KeyType)
            ? ISshAgentKey.SecurityKeySignTimeoutMilliseconds
            : null;
        return _agent.Sign(PublicKeyBlob, data, flags, timeoutMs);
    }

    public byte[] Sign(byte[] data, SshAgentSignFlags flags, int timeoutMilliseconds)
    {
        return _agent.Sign(PublicKeyBlob, data, flags, timeoutMilliseconds > 0 ? timeoutMilliseconds : null);
    }
}
