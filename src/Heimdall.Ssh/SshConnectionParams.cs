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

using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Parameters for establishing an SSH connection.
/// Callers are responsible for decrypting secrets (e.g., via DPAPI) before
/// populating <see cref="Password"/> or <see cref="KeyPassphrase"/>;
/// this class never touches encrypted storage.
/// </summary>
public sealed class SshConnectionParams
{
    /// <summary>SSH server hostname or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>SSH server port (default 22).</summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// Logical SSH host used for host-key trust decisions when the transport
    /// endpoint differs from the server identity, such as through a local tunnel.
    /// Defaults to <see cref="Host"/>.
    /// </summary>
    public string? LogicalHost { get; init; }

    /// <summary>
    /// Logical SSH port used for host-key trust decisions when the transport
    /// endpoint differs from the server identity, such as through a local tunnel.
    /// Defaults to <see cref="Port"/>.
    /// </summary>
    public int? LogicalPort { get; init; }

    /// <summary>SSH username for authentication.</summary>
    public required string Username { get; init; }

    /// <summary>Path to a private key file (PEM or OpenSSH format). Optional.</summary>
    public string? KeyPath { get; init; }

    /// <summary>Plaintext password for SSH password authentication.</summary>
    public string? Password { get; init; }

    /// <summary>Plaintext passphrase used to decrypt <see cref="KeyPath"/>.</summary>
    public string? KeyPassphrase { get; init; }

    /// <summary>
    /// Compatibility switch for profiles saved before the dedicated key
    /// passphrase field existed. When enabled, <see cref="Password"/> is also
    /// tried as a key passphrase, then still used as a password fallback.
    /// </summary>
    public bool UseLegacyPasswordAsKeyPassphrase { get; init; }

    /// <summary>Display name used in the legacy credential mapping log entry.</summary>
    public string? LegacyCredentialName { get; init; }

    /// <summary>Preferred local SSH agent order for authentication.</summary>
    public SshAgentPreference SshAgentPreference { get; init; } = SshAgentPreference.AutoOpenSshFirst;

    /// <summary>Whether to enable SSH agent forwarding.</summary>
    public bool AgentForwarding { get; init; }

    /// <summary>Whether to enable zlib compression on the SSH transport.</summary>
    public bool Compression { get; init; }

    /// <summary>Whether to request X11 forwarding from the server.</summary>
    public bool X11Forwarding { get; init; }

    /// <summary>TCP connection timeout. Defaults to 15 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>SSH transport keepalive interval in seconds.</summary>
    public int? KeepAliveIntervalSeconds { get; init; } = AppSettings.DefaultSshKeepAliveIntervalSeconds;

    /// <summary>
    /// What the keyboard-interactive exchange of the attempt could not answer, filled
    /// in by the authentication method and read by the failure classifier so a
    /// second-factor prompt is not reported as a rejected password. One mutable
    /// record per parameter set: the copies a handler makes with <c>with</c>-style
    /// initialisers share it, which is what a classifier looking at the same attempt
    /// needs.
    /// </summary>
    public KeyboardInteractiveObservation KeyboardInteractive { get; init; } = new();

    /// <summary>Host identity used for host-key verification and storage.</summary>
    public string HostKeyVerificationHost =>
        string.IsNullOrWhiteSpace(LogicalHost) ? Host : LogicalHost;

    /// <summary>Port identity used for host-key verification and storage.</summary>
    public int HostKeyVerificationPort => LogicalPort ?? Port;
}
