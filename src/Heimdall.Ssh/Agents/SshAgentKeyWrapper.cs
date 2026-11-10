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
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Heimdall.Ssh.Agents;

/// <summary>
/// Wraps an SSH-agent identity as an SSH.NET private key source.
/// </summary>
/// <remarks>
/// Production now builds <see cref="SshAgentAlgorithmSource"/> instead, one source per
/// attempt so the attempts can be ordered across keys. This type is kept for its tests.
/// </remarks>
internal class SshAgentKeyWrapper : IPrivateKeySource
{
    private readonly IReadOnlyCollection<HostAlgorithm> _algorithms;

    public SshAgentKeyWrapper(ISshAgentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _algorithms = BuildAlgorithms(key);
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => _algorithms;

    private static IReadOnlyCollection<HostAlgorithm> BuildAlgorithms(ISshAgentKey key)
    {
        if (string.Equals(key.KeyType, "ssh-rsa", StringComparison.Ordinal))
        {
            return
            [
                new SshAgentHostAlgorithm("rsa-sha2-256", key, SshAgentSignFlags.RsaSha2_256),
                new SshAgentHostAlgorithm("rsa-sha2-512", key, SshAgentSignFlags.RsaSha2_512),
                new SshAgentHostAlgorithm("ssh-rsa", key),
            ];
        }

        return [new SshAgentHostAlgorithm(key.KeyType, key)];
    }
}
