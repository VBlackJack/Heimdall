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
/// Structured failure codes for SSH operations.
/// Enables callers to display targeted error messages via i18n keys
/// (e.g., "ErrorSsh{Code}"). The <see cref="FailureClassifier"/> maps
/// SSH.NET exceptions to these codes.
/// </summary>
public enum SshFailureCode
{
    /// <summary>Unclassified or unexpected error.</summary>
    Unknown,

    // ── Auth failures ──────────────────────────────────────────────────

    /// <summary>SSH agent is not running or inaccessible. Name kept for backward compatibility.</summary>
    PageantKeyUnavailable,

    /// <summary>SSH agent is running but has no identities loaded. Name kept for backward compatibility.</summary>
    PageantNoIdentities,

    /// <summary>Private key passphrase is required but not provided.</summary>
    PassphraseRequired,

    /// <summary>Private key passphrase was rejected while decrypting the key file.</summary>
    PassphraseRejected,

    /// <summary>Private key file format is unsupported or corrupt.</summary>
    KeyFileInvalid,

    /// <summary>Private key file not found or unreadable.</summary>
    KeyFileNotFound,

    /// <summary>Server rejected the SSH key.</summary>
    KeyRejected,

    /// <summary>SSH password was rejected by the server.</summary>
    PasswordRejected,

    /// <summary>SSH authentication was rejected (generic).</summary>
    AuthRejected,

    /// <summary>No supported authentication method available.</summary>
    NoSupportedAuth,

    /// <summary>Too many failed authentication attempts.</summary>
    TooManyAuthFailures,

    /// <summary>Keyboard-interactive auth required but no password provided.</summary>
    KeyboardInteractiveNoPassword,

    // ── Network failures ───────────────────────────────────────────────

    /// <summary>TCP connection refused (server down or port blocked).</summary>
    NetworkRefused,

    /// <summary>TCP connection timed out.</summary>
    NetworkTimedOut,

    /// <summary>TCP connection was reset by the remote host.</summary>
    NetworkReset,

    /// <summary>Host or network is unreachable (DNS or routing failure).</summary>
    NetworkUnreachable,

    // ── Protocol failures ──────────────────────────────────────────────

    /// <summary>SSH protocol version mismatch or error.</summary>
    ProtocolError,

    /// <summary>Server host key verification failed (TOFU mismatch).</summary>
    HostKeyMismatch,

    /// <summary>
    /// Heimdall could not resolve a host key for the gateway through any of
    /// its trusted sources. Used to refuse delegation to an external tool's
    /// local host-key cache.
    /// </summary>
    HostKeyUnavailable,

    /// <summary>Port forwarding or proxy operation failed.</summary>
    ForwardingFailed,

    // ── Session failures ───────────────────────────────────────────────

    /// <summary>Authentication or connection timed out.</summary>
    AuthTimeout,

    /// <summary>SSH session was disconnected by the server.</summary>
    SessionDisconnected,

    // ── Tunnel-specific failures ───────────────────────────────────────

    /// <summary>The requested local port is already in use.</summary>
    PortInUse,

    /// <summary>The tunnel was unexpectedly closed by the server.</summary>
    TunnelBroken,

    /// <summary>The forwarded port is listening, but another process owns it.</summary>
    TunnelPortOwnedByDifferentProcess,

    /// <summary>No process is listening on the forwarded port.</summary>
    TunnelPortNotListening,

    /// <summary>The operating system could not attest ownership of the forwarded port.</summary>
    TunnelPortOwnershipIndeterminate,

    // ── Chain-specific failures ────────────────────────────────────────

    /// <summary>Maximum gateway chain depth exceeded.</summary>
    ChainDepthExceeded,

    /// <summary>Circular dependency detected in gateway chain.</summary>
    CircularChainDependency,

    /// <summary>Operation was cancelled via CancellationToken.</summary>
    Cancelled
}
