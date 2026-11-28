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

    /// <summary>Path to a private key file (PEM, OpenSSH, or PuTTY PPK format). Optional.</summary>
    public string? KeyPath { get; init; }

    /// <summary>
    /// Optional path to an OpenSSH user certificate file (<c>-cert.pub</c>) paired with <see cref="KeyPath"/>.
    /// When omitted, a sibling <c>{KeyPath}-cert.pub</c> file is automatically detected if present.
    /// </summary>
    public string? CertificatePath { get; init; }

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

    /// <summary>
    /// Fingerprint of the one agent key to offer, or null to offer every key of the agent.
    /// When set and the key is not loaded in a reachable agent, building the connection throws
    /// <see cref="SshAgentKeyNotLoadedException"/>: there is no fallback to other keys.
    /// </summary>
    public string? AgentKeyFingerprint { get; init; }

    /// <summary>
    /// When true, skips all local SSH agent authentication methods (used by key rotation verification
    /// so a new key file is validated in strict isolation without falling back to an old agent key).
    /// </summary>
    public bool DisableAgentAuthentication { get; init; }

    /// <summary>
    /// Optional notification invoked right before an SSH agent signs an authentication challenge
    /// (for example to inform the user to touch a FIDO2 hardware security key).
    /// </summary>
    public Action<ISshAgentKey>? OnBeforeAgentSign { get; init; }

    /// <summary>
    /// Optional confirmation gate invoked before an SSH agent signs an authentication challenge.
    /// Returning <see langword="false"/> aborts authentication with <see cref="SshAgentSigningDeclinedException"/>.
    /// </summary>
    public Func<ISshAgentKey, bool>? ConfirmAgentSign { get; init; }

    /// <summary>Whether to enable SSH agent forwarding.</summary>
    public bool AgentForwarding { get; init; }

    /// <summary>Whether to enable zlib compression on the SSH transport.</summary>
    public bool Compression { get; init; }

    /// <summary>Cryptographic algorithm policy applied during the SSH handshake.</summary>
    public SshCryptoPolicy CryptoPolicy { get; init; } = SshCryptoPolicy.Standard;

    /// <summary>Optional outbound proxy type (HTTP CONNECT, SOCKS5, SOCKS4) for reaching <see cref="Host"/>.</summary>
    public SshProxyType ProxyType { get; init; } = SshProxyType.None;

    /// <summary>Proxy hostname or IP address when <see cref="ProxyType"/> is enabled.</summary>
    public string? ProxyHost { get; init; }

    /// <summary>Proxy TCP port when <see cref="ProxyType"/> is enabled.</summary>
    public int ProxyPort { get; init; }

    /// <summary>Optional proxy authentication username.</summary>
    public string? ProxyUsername { get; init; }

    /// <summary>Optional plaintext proxy authentication password.</summary>
    public string? ProxyPassword { get; init; }

    /// <summary>Whether to request X11 forwarding from the server.</summary>
    public bool X11Forwarding { get; init; }

    /// <summary>Optional local SOCKS5 dynamic forwarding port (-D) attached directly to this SSH session.</summary>
    public int SocksProxyPort { get; init; }

    /// <summary>Optional remote bind port (-R) attached directly to this SSH session.</summary>
    public int RemoteBindPort { get; init; }

    /// <summary>Optional local target port for remote forwarding (-R). Defaults to <see cref="RemoteBindPort"/>.</summary>
    public int RemoteLocalPort { get; init; }

    /// <summary>Optional local port forward specs (-L) attached directly to this SSH session.</summary>
    public IReadOnlyList<string> LocalForwards { get; init; } = [];

    /// <summary>
    /// When true and <see cref="HostKeyStore"/> already holds an effective trusted entry for
    /// <see cref="HostKeyVerificationHost"/>:<see cref="HostKeyVerificationPort"/>, skips the
    /// preliminary throwaway host-key probe while still enforcing strict pinned verification on
    /// the real handshake (used by companion SFTP sessions whose parent SSH session just pinned
    /// the host key).
    /// </summary>
    public bool SkipHostKeyProbeIfTrusted { get; init; }

    /// <summary>
    /// Optional host-key verifier already resolved by a parent SSH session (such as a companion SFTP
    /// session), allowing zero-reprobe pinned verification even on dual-algorithm or CA-signed hosts.
    /// Still subject to <c>@revoked</c> enforcement against <see cref="HostKeyStore"/>.
    /// </summary>
    public PinnedFingerprintVerifier? PreResolvedHostKeyVerifier { get; init; }

    /// <summary>TCP connection timeout. Defaults to 15 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Bound on each wait of the authentication exchange when it can include a human answer,
    /// or null to use <see cref="ConnectTimeout"/> throughout.
    /// </summary>
    /// <remarks>
    /// SSH.NET applies one timeout, <c>ConnectionInfo.Timeout</c>, to every wait of the connect:
    /// the banner, the key exchange and each authentication round alike. A keyboard-interactive
    /// question held open by a person needs minutes; reaching the server does not. When this is
    /// set, the client's timeout is this value and the phase before the server's host key
    /// arrives is bounded separately by <see cref="ConnectTimeout"/>
    /// (<see cref="SshConnectionFactory.ConnectWithTransportBoundAsync"/>). The host key probe
    /// never waits for a person and always uses <see cref="ConnectTimeout"/>.
    /// </remarks>
    public TimeSpan? AuthenticationTimeout { get; init; }

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

    /// <summary>
    /// Optional, attempt-scoped responder for server authentication questions. Called on
    /// SSH.NET's authentication worker after host-key verification. Returning null cancels
    /// authentication. Responses must never be persisted or logged by the caller.
    /// </summary>
    public Func<string, string?>? KeyboardInteractiveResponder { get; init; }

    /// <summary>Host identity used for host-key verification and storage.</summary>
    public string HostKeyVerificationHost =>
        string.IsNullOrWhiteSpace(LogicalHost) ? Host : LogicalHost;

    /// <summary>Port identity used for host-key verification and storage.</summary>
    public int HostKeyVerificationPort => LogicalPort ?? Port;
}
