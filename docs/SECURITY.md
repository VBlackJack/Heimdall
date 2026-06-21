# Security Notes

This document records known security considerations, limitations, and deliberate
defense-in-depth decisions in Heimdall.

## Reporting a vulnerability

Report suspected vulnerabilities privately to the maintainer. This repository
does not currently publish a dedicated security email address; use the private
channel through which you obtained the source, or see `LICENSE` for maintainer
and licensing context. Do not file public issues for security problems.

## Threat model scope

Heimdall is a single-user desktop application that stores SSH and RDP
credentials locally using DPAPI plus HMAC-SHA256, and manages outbound
connections. It assumes:

- The local Windows user account is trusted. Malware running as the same user
  can observe the app's memory.
- The local disk is trusted. DPAPI-encrypted secrets are bound to the user
  profile.
- The network is untrusted end to end. TOFU host key pinning is the primary
  defense against MITM.

Out of scope: multi-user shared installations, secure boot chain, and supply
chain attacks on SSH.NET or WebView2. Track dependency exposure with
`dotnet list package --vulnerable`.

## Known limitations

### Credential lifetime in managed memory

`System.String` is immutable and lives on the GC heap. Plaintext credentials
passed to:

- `IMsTscNonScriptable.put_ClearTextPassword` for RDP,
- `PasswordAuthenticationMethod` and `KeyboardInteractiveAuthenticationMethod`
  for SSH.NET,
- `CredentialAutofill.InjectPassword` through `WM_SETTEXT`,

are briefly held as `string` instances before being passed to native code. We
zero the owning `char[]` buffers where possible and null out field references
after handoff, but the GC may retain copies until the next Gen2 collection.
`SecureString` does not provide stronger guarantees on modern Windows.

Mitigation: lock the workstation when not in use. Attackers with local memory
read primitives can scrape credentials from desktop SSH and RDP clients,
including this one.

### Pageant shared-memory DACL

`PageantClient.SendMessage` creates a named file mapping with **two layers of
hardening** against same-session userland snooping:

1. **Self-only DACL** via `SecurityAttributesScope.CreateSelfOnly` - the
   mapping handle is created with an explicit `SECURITY_ATTRIBUTES` whose SDDL
   is `D:P(A;;FA;;;<currentUserSid>)`, denying access even to other processes
   running under the **same** Windows user.
2. **Cryptographically random mapping name** - 
   `RandomNumberGenerator.GetHexString(16)` provides 64 bits of entropy in the
   mapping name, defeating opportunistic enumeration by a malicious process
   that knows Heimdall's PID.

The IPC handshake additionally verifies that the Pageant window is owned by a
process whose name is in the trusted whitelist
(`pageant`, `putty`, `plink`, `pscp`, `psftp`, `kitty`, `winscp`,
`keepassxc-proxy`, `keepassxc`) before sending any agent traffic, mitigating
window-class spoofing.

### Credential logging boundaries

RDP, SSH, SFTP, and credential-handling code paths never log usernames,
domains, password presence, password length, passwords, passphrases, or
credential edit-field contents. Connect log lines may identify the target host
and protocol only.

`CredentialAutofill.cs` is the canonical RDP example. Since `1d7c78c`, broker
enumeration diagnostics are emitted as one Debug entry per autofill attempt,
with an Info-level final outcome and Warning-level logging only when
enumeration itself throws. Those diagnostics may include OS window titles,
handles, PIDs, process names, and rejection reasons, but never edit-field
contents. Window titles may contain host-identifying data; for example,
`Enter credentials for server01.corp.local` is supplied by the OS or remote
client and is outside this layer's credential-field policy.

### TunnelManager port allocation race

`TunnelManager.GetEphemeralPort` and `TunnelManager.AllocatePort` bind an OS
ephemeral port, read its number, release it, then return it. Between release
and the actual tunnel bind, another process can claim the same port. Three
mitigations are in place:

1. A double-check in `OpenTunnelAsync` and `OpenChainedTunnelAsync`
   re-validates `IsPortTracked(localPort)` under `_registryLock` and disposes
   the session on collision.
2. `StartForwardedPortWithRetry` wraps the actual `ForwardedPortLocal.Start()`
   and `ForwardedPortDynamic.Start()` calls with a bounded retry (3 attempts,
   50 ms spacing) on `SocketException(AddressAlreadyInUse)` only. Unrelated
   socket errors and non-socket exceptions propagate immediately with no
   retry. This closes the common case where another local process held the
   port transiently.
3. Chained-tunnel intermediate local ports receive the same retry treatment.
   `ForwardedPortRemote.Start()` does not (server-side bind, different race
   surface).

Callers may still observe `SshFailureCode.PortInUse` when the port is
genuinely occupied; retry is safe at any layer.

### SSH host-key trust model

Host-key trust decisions are resolved **before** the real `Connect()` via a
pre-authentication probe (`SshConnectionFactory.ProbeHostKeyAsync` with
`NoneAuthenticationMethod`). The real connection then uses a strict,
synchronous `PinnedFingerprintVerifier` that only accepts the pre-resolved
fingerprint. SSH.NET's `HostKeyReceived` callback performs no async work, no
UI dispatch, and no `IHostKeyVerifier.VerifyAsync` call from inside it - this
invariant has a dedicated regression test
(`IHostKeyVerifierIntegrationTests.AttachHostKeyVerification_RejectsInteractiveVerifierSynchronously`).

Production runtime paths require `HostKeyStore` and `IHostKeyVerifier` at the
type level for SSH, SFTP, tunnel, sudo, and remote-edit entry points.
`RejectingHostKeyVerifier.Instance` is the safe fail-closed verifier for
tests or non-interactive contexts; `AutoAcceptHostKeyVerifier.Instance` is
reserved for explicit test flows that need first-use acceptance.
`ToolGatewayConnector` refuses to route tool traffic through a gateway that
has no pinned fingerprint yet; the user must complete a normal interactive
SSH session first so the host key is captured into `HostKeyStore` via the
confirmed-trust path.

Trust entries carry metadata (`FirstSeen`, `LastSeen`, `Algorithm`, `Source`,
`PublicKeyBase64`) via `HostKeyEntry`. Persistence is additive:
`trustedHostKeysV2` in `settings.json` holds the enriched entries; the
legacy `trustedHostKeys` string dictionary remains readable for downgrade
safety and is never rewritten from the V2 path.

`~/.ssh/known_hosts` import and export are explicit user actions surfaced in
`Settings > SSH & SFTP > Trusted host keys`. Import preserves conflicting
existing entries unless the user explicitly opts into replacement in a
dedicated modal. Export preserves every line Heimdall did not originate
(including `@cert-authority`, `@revoked`, and hashed entries that Heimdall
cannot fully consume) verbatim.

Plink fallback paths are also fail-closed. `PlinkHostKeyDecider` accepts a
stored fingerprint immediately, otherwise asks an injectable
`IPlinkHostKeyProbe` for the presented key and runs the normal verifier
before launching plink with `-hostkey`. If neither path can resolve a
Heimdall-trusted fingerprint, the operation returns
`SshFailureCode.HostKeyUnavailable` and refuses to fall back to PuTTY/Plink's
own cache.

Reusable tunnel identity includes the remote target, forwarding mode, and a
collision-safe gateway chain key (`GatewayChainKey`) derived from stable
gateway IDs and a versioned SHA-256 hash over length-prefixed chain parts.
Two tenants that both expose `10.0.0.5:3389` through different bastions do
not share a local tunnel.

Mid-session host-key failures are surfaced as typed security events, not
generic disconnect strings. `SshSessionFailureDispatcher` maps
`HostKeyRejectedException` to `SshSessionSecurityEvent`; the SSH UI blocks
auto-reconnect on host-key mismatch, and SFTP displays a security banner.
`RemoteFileEditor` separately raises `HostKeyRotatedDuringUpload` when a
sudo edit session observes a different host key during auto-upload.

The legacy byte-array overload `HostKeyStore.Verify(byte[])` remains for
backward compatibility but is `[Obsolete]`; new code must use the host/port
aware verification APIs so trust decisions remain scoped to the correct
endpoint.

### SFTP sudo escalation and remote editing

SFTP sudo fallback is deliberately narrow. `EmbeddedSftpViewModel`
escalates only for typed permission-denied exceptions
(`SftpPermissionDeniedException` and local `UnauthorizedAccessException`);
generic `SshException("Failure")` messages do not trigger privileged
operations. This trades occasional manual retry prompts for avoiding sudo
actions on non-permission failures.

Privileged uploads split the write and cleanup commands. The `sudo tee`
write is executed separately, and removal of the `/tmp/.heimdall_*` staging
file runs from a `finally` path with an uncancelled cleanup command. Cleanup
failures are logged as warnings while preserving the original write error.

`RemoteFileEditor` tracks file-watcher upload tasks per edit session,
propagates cancellation through `CloseEdit` and `Dispose`, and observes
faults synchronously so unhandled background upload exceptions do not reach
the process-wide `UnobservedTaskException` pipeline. Sudo edit sessions
cache the `PinnedFingerprintVerifier` built at open time instead of resolving
host-key trust again on every save.

### FTP cleartext warning

FTP is implemented on top of FluentFTP `AsyncFtpClient`. `FtpHandler`
validates the target host and port before connect. If a user connects with
credentials and TLS is disabled, `ConnectionResult.Warning` carries a
localized non-blocking cleartext warning to the status surface; it does not
block anonymous or explicit FTPS sessions. Explicit FTPS enables TLS for the
control channel and FluentFTP `DataConnectionEncryption`, so file transfers
use a protected data channel.

### SSH agent identity enumeration

`ISshAgent` implementations (`PageantAgent`, `OpenSshPipeAgent`) never hold
IPC handles across requests. Every `GetIdentities` and `Sign` call opens a
new shared-memory mapping (Pageant) or named-pipe connection (OpenSSH Agent)
and disposes it before returning. Availability probes have a 250 ms timeout;
real requests have a 5 s timeout. Pipe-not-found and timeout both return
"unavailable" without raising. User preference between agents is a runtime
setting (`AppSettings.SshAgentPreference`); changes take effect on the next
connection attempt without app restart.

`OpenSshPipeAgent.SendRequest` is built on async pipe I/O
(`NamedPipeClientStream` opened with `PipeOptions.Asynchronous`) and a
linked timeout/cancellation token, replacing the best-effort `ReadTimeout`
that `NamedPipeClientStream` silently ignores in some modes.

### Host-key fingerprint comparison

`HostKeyStore.Verify` and `HostKeyTrustService.Verify` / `Trust` / `Import`
compare stored vs presented fingerprints with the shared
`HostKeyStore.ConstantTimeEquals` helper, which delegates to
`CryptographicOperations.FixedTimeEquals` after a length-equality guard
that is safe here because OpenSSH host-key fingerprints are fixed at
`SHA256:` + 43 base64 chars. Host-key fingerprints are not secret
(servers publish them, `ssh-keyscan` retrieves them, DNS SSHFP records
expose them) so this is defense-in-depth, not a load-bearing mitigation.
The pattern is local to `HostKeyStore` and should not be copied verbatim
to variable-length secret comparisons.

### known_hosts import - DoS bounds

`KnownHostsParser` enforces two hard caps when consuming externally-supplied
`known_hosts` files:

- **`MaxLineLength = 65 536`** - lines longer than 64 KB are skipped with a
  `MalformedLine` diagnostic; defends against a single giant line forcing a
  large allocation.
- **`MaxFileSizeBytes = 50 MB`** - files larger than 50 MB are refused
  outright with a typed `FileTooLarge` diagnostic. Both the core importer and
  the app-side importer stream via `StreamReader` rather than
  `File.ReadAllText`, and wrap I/O in `try/catch` so locked or unreadable
  files degrade to `FileReadError` diagnostics instead of bubbling
  exceptions to the UI.

### Subprocess argument hardening

`PlinkTunnelRunner` builds the plink argument list via
`ProcessStartInfo.ArgumentList` (no string concatenation), and the stderr
drain task is **joined** at `Stop()` time before `Process.Kill()`, so the
background reader cannot outlive the pipe it was attached to. The drain
sanitizer (`SanitizeForLog`) redacts password / passphrase single-token
assignments, token / bearer assignments to end-of-line, and `-pw` / `-pwfile`
flags so an unexpected stderr echo from plink cannot leak credentials into
the application log.

## Security testing

- Unit tests for TOFU verification:
  `tests/Heimdall.Ssh.Tests/HostKeyStoreTests.cs` and
  `tests/Heimdall.Ssh.Tests/IHostKeyVerifierIntegrationTests.cs`, including
  an anti-deadlock regression test that runs the host-key callback under a
  single-threaded `SynchronizationContext` with a slow verifier and asserts
  the callback returns under 50 ms.
- Trust service orchestration and known_hosts round-trip:
  `tests/Heimdall.Ssh.Tests/KnownHostsImportExportTests.cs`.
- SSH agent protocol and IPC: `tests/Heimdall.Ssh.Tests/OpenSshAgentProtocolTests.cs`
  (pure protocol encoding/decoding) and
  `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` (named-pipe transport
  using a GUID-suffixed test pipe, independent of the real Windows OpenSSH
  Agent service).
- Local bind retry helper:
  `tests/Heimdall.Ssh.Tests/TunnelManagerStartRetryTests.cs`, including a
  test that holds a real TCP port via `Socket.Bind` and confirms the retry
  helper still fails closed with `AddressAlreadyInUse`.
- `TunnelManager` characterization tests and gateway-aware reuse identity:
  `tests/Heimdall.Ssh.Tests/TunnelManagerTests.cs` and
  `tests/Heimdall.App.Tests/TunnelReuseIdentityTests.cs`.
- Plink fail-closed decision coverage:
  `tests/Heimdall.App.Tests/PlinkFailClosedTests.cs`.
- Pageant `SECURITY_ATTRIBUTES` factory and self-only SDDL builder:
  `tests/Heimdall.Ssh.Tests/PageantClientTests.cs`
  (`BuildSelfOnlySddl_*`, `CreateSelfOnly_ManyAllocations_DoNotLeakOrThrow`).
- Constant-time fingerprint compare:
  `tests/Heimdall.Ssh.Tests/HostKeyStoreTests.cs`
  (`ConstantTimeEquals_*`).
- Mid-session security event dispatch and shell teardown:
  `tests/Heimdall.Ssh.Tests/SshSessionFailureDispatcherTests.cs` and
  `tests/Heimdall.Ssh.Tests/SshShellSessionTeardownTests.cs`.
- Stderr secret redaction:
  `tests/Heimdall.Ssh.Tests/PlinkTunnelRunnerTests.cs`
  (`SanitizeForLog_RedactsBearerToEndOfLine`,
  `SanitizeForLog_RedactsTokenToEndOfLine`,
  `SanitizeForLog_RedactsSingleTokenPassword`,
  `SanitizeForLog_RedactsPlinkCredentialFlags`).
- known_hosts DoS caps and graceful I/O degradation:
  `tests/Heimdall.Core.Tests/Ssh/KnownHostsParserTests.cs` and
  `tests/Heimdall.Ssh.Tests/KnownHostsImportExportTests.cs` plus
  `tests/Heimdall.App.Tests/KnownHostsImporterStreamingTests.cs`
  (`ImportFile_OversizedFile_RejectedWithoutThrowing`, line-too-long cases).
- SFTP sudo escalation, remote-edit host-key rotation, and upload task
  lifecycle:
  `tests/Heimdall.App.Tests/IsPermissionDeniedTests.cs`,
  `tests/Heimdall.App.Tests/RemoteFileEditorRotationTests.cs`, and
  `tests/Heimdall.App.Tests/RemoteFileEditorTaskTrackingTests.cs`.
- Sudo command construction and external editor launch hardening:
  `tests/Heimdall.App.Tests/SudoUploadCommandsTests.cs` and
  `tests/Heimdall.App.Tests/ResolveEditorPathTests.cs`.
- FTP parser, host/port validation, and cleartext-warning coverage:
  `tests/Heimdall.App.Tests/FtpBrowserParsingTests.cs` and
  `tests/Heimdall.App.Tests/FtpHandlerValidationTests.cs`.
- Shell-injection regression tests: `InputValidator` coverage in
  `tests/Heimdall.Core.Tests`.
- RDP file generation sanitization:
  `tests/Heimdall.Ssh.Tests/RdpFileGeneratorTests.cs`.
- CI enforces: build with zero warnings under `TreatWarningsAsErrors`,
  `dotnet format --verify-no-changes`, full test suite, JSON locale parity
  (EN and FR key sets must be identical, currently 5,489 keys each), and an
  informational `dotnet list package --vulnerable` scan.
- Dependency scan for manual review: `dotnet list Heimdall.slnx package
  --vulnerable --include-transitive`. CI emits warnings but does not gate on
  vulnerability results, since advisories occasionally include false
  positives or entries without an upgrade path.

