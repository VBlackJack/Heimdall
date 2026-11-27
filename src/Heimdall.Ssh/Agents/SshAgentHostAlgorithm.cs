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
using Renci.SshNet.Security;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// SSH.NET host algorithm that delegates signing to an SSH agent key.
/// </summary>
internal sealed class SshAgentHostAlgorithm : HostAlgorithm
{
    private readonly ISshAgentKey _key;
    private readonly SshAgentSignFlags _signFlags;
    private readonly Action<ISshAgentKey>? _onSigned;

    public SshAgentHostAlgorithm(
        string algorithmName,
        ISshAgentKey key,
        SshAgentSignFlags signFlags = SshAgentSignFlags.None,
        Action<ISshAgentKey>? onSigned = null)
        : base(algorithmName)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _signFlags = signFlags;
        _onSigned = onSigned;
    }

    public override byte[] Data => _key.PublicKeyBlob;

    public override byte[] Sign(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var signature = _key.Sign(data, _signFlags);
        _onSigned?.Invoke(_key);
        return signature;
    }

    public override bool VerifySignature(byte[] data, byte[] signature) => false;
}
