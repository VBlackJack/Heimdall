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
/// One agent key paired with the one host algorithm it is offered under, and the
/// agent flags that make the agent sign with that algorithm.
/// </summary>
/// <param name="Key">The agent identity.</param>
/// <param name="Algorithm">The SSH public key algorithm name.</param>
/// <param name="SignFlags">The agent sign flags matching <paramref name="Algorithm"/>.</param>
internal readonly record struct SshAgentAttempt(
    ISshAgentKey Key,
    string Algorithm,
    SshAgentSignFlags SignFlags);

/// <summary>
/// Exposes exactly one (agent key, algorithm) pair to SSH.NET.
/// </summary>
/// <remarks>
/// SSH.NET flattens every source's host algorithms into one list and sends one
/// unsigned public key query per entry, so the order of that list is the order of
/// the attempts the server counts. A source that held all three RSA algorithms of
/// one key made a wrong RSA key cost three attempts before the next key was tried,
/// which exhausts a server limited to three attempts. One source per attempt lets
/// <see cref="Order"/> decide the sequence.
/// <para>
/// rsa-sha2-512 goes first, as OpenSSH does. A server that accepts only rsa-sha2-256
/// costs one more attempt per RSA key before its 256 pass.
/// </para>
/// </remarks>
internal sealed class SshAgentAlgorithmSource : IPrivateKeySource
{
    private const string RsaKeyType = "ssh-rsa";
    private const string RsaSha2_512Algorithm = "rsa-sha2-512";
    private const string RsaSha2_256Algorithm = "rsa-sha2-256";

    // First choice first: it is the pass the most keys are expected to succeed in.
    private static readonly (string Name, SshAgentSignFlags Flags)[] RsaAlgorithmPasses =
    [
        (RsaSha2_512Algorithm, SshAgentSignFlags.RsaSha2_512),
        (RsaSha2_256Algorithm, SshAgentSignFlags.RsaSha2_256),
        (RsaKeyType, SshAgentSignFlags.None),
    ];

    private readonly IReadOnlyCollection<HostAlgorithm> _algorithms;

    public SshAgentAlgorithmSource(SshAgentAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt.Key);
        ArgumentException.ThrowIfNullOrEmpty(attempt.Algorithm);
        _algorithms = [new SshAgentHostAlgorithm(attempt.Algorithm, attempt.Key, attempt.SignFlags)];
    }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => _algorithms;

    /// <summary>
    /// Orders the attempts algorithm by algorithm: a pass over every key (agent
    /// order kept) with its first-choice algorithm, then a pass with its second,
    /// then its third. A key with a single algorithm appears in the first pass
    /// only. The set of attempts equals the per-key set; only the order differs.
    /// </summary>
    internal static IReadOnlyList<SshAgentAttempt> Order(IReadOnlyList<ISshAgentKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var attempts = new List<SshAgentAttempt>();
        for (int pass = 0; pass < RsaAlgorithmPasses.Length; pass++)
        {
            foreach (ISshAgentKey key in keys)
            {
                if (IsRsa(key))
                {
                    (string name, SshAgentSignFlags flags) = RsaAlgorithmPasses[pass];
                    attempts.Add(new SshAgentAttempt(key, name, flags));
                }
                else if (pass == 0)
                {
                    attempts.Add(new SshAgentAttempt(key, key.KeyType, SshAgentSignFlags.None));
                }
            }
        }

        return attempts;
    }

    /// <summary>Builds one source per attempt, in <see cref="Order"/> order.</summary>
    internal static IPrivateKeySource[] BuildSources(IReadOnlyList<ISshAgentKey> keys)
    {
        return Order(keys)
            .Select(attempt => (IPrivateKeySource)new SshAgentAlgorithmSource(attempt))
            .ToArray();
    }

    private static bool IsRsa(ISshAgentKey key) =>
        string.Equals(key.KeyType, RsaKeyType, StringComparison.Ordinal);
}
