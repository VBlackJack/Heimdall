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

namespace Heimdall.Ssh;

/// <summary>
/// Thrown when the user declines an interactive SSH agent signing confirmation prompt.
/// </summary>
public sealed class SshAgentSigningDeclinedException : Exception
{
    public SshAgentSigningDeclinedException(string host, int port, string fingerprint)
        : base($"SSH agent signing declined by user for {host}:{port} ({fingerprint}).")
    {
        Host = host;
        Port = port;
        Fingerprint = fingerprint;
    }

    public string Host { get; }
    public int Port { get; }
    public string Fingerprint { get; }
}
