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

using Renci.SshNet.Common;

namespace Heimdall.Ssh;

/// <summary>
/// Raised before any authentication method is used when a session names one agent key
/// and that key is malformed or not loaded in any reachable agent.
/// </summary>
/// <remarks>
/// There is deliberately no fallback to the other keys of the agent: the session asked for
/// exactly one key, and offering others would contradict that choice.
/// </remarks>
public sealed class SshAgentKeyNotLoadedException : SshException
{
    public SshAgentKeyNotLoadedException(string fingerprint)
        : base($"The SSH agent key {fingerprint} chosen for this session is not loaded in any reachable SSH agent.")
    {
        Fingerprint = fingerprint;
    }

    /// <summary>The fingerprint the session names, exactly as stored.</summary>
    public string Fingerprint { get; }
}
