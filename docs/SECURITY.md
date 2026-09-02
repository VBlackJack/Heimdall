# Security Notes

*Also available in French: [fr/SECURITY.md](fr/SECURITY.md).*

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

### RDP server certificate trust

Windows keeps exactly **one** RDP server thumbprint per host name. Behind a
single name there is often more than one machine - a pool of domain
controllers, each with its own self-signed RDP certificate. Every connection
may land on a different member, so the stored thumbprint disagrees, Windows
asks again, and accepting overwrites the previous one. The loop never
converges, and it happens with native `mstsc` too: it is a property of
one-thumbprint-per-name storage, not a Heimdall defect.

Heimdall keeps a **set** of approved thumbprints per profile, which is what
makes the loop terminate. Each member of a pool is approved once, and the
profile then stays silent until a machine is rebuilt.

**The check runs only where nothing else checks.** `RdpCertificateGate`
verifies exclusively when the resolved `AuthenticationLevel` is `0`, which is
the level applied when NLA is off and which requires nothing of the server. At
levels 1 and 2 Windows performs its own server-authentication step and shows
its own warning; a second question about the same fact would be one prompt too
many, and a prompt users learn to click through is worse than no prompt. This
also means no connection that has verification today gains a network round
trip.

**The check is embedded-only.** `RdpCertificateGate` has call sites in
`src/Heimdall.App/Views/EmbeddedRdpView.xaml.cs` and nowhere else. A launch that
resolves to the external client - a profile whose RDP mode is `External`, or a
Force-External launch - writes the `TERMSRV` credential and starts `mstsc.exe`
with no Heimdall-side certificate check at all, while `RdpFileGenerator` puts
the same `authentication level:i:0` into the generated `.rdp` file that the
embedded path applies to the control. On that path the Windows check is relaxed
and nothing replaces it.

**Every outcome other than an explicit refusal proceeds.** `RdpCertificateProbe`
opens its own TCP connection, performs the X.224 negotiation, and reads the
certificate offered by the TLS layer. If the endpoint is unreachable, keeps
standard RDP security and offers no certificate at all, or the probe throws,
Heimdall has verified **nothing** and the connection is opened exactly as it
would have been without this feature. Refusing on an unverifiable endpoint
would turn a verification step into a new way to fail on a path that worked
before; accepting one as verified would be strictly worse than never having
built any of this.

**Approval is per profile, and per thumbprint.** Trusting adds to the set and
never replaces it; re-approving a thumbprint keeps its original timestamp.
"Just this once" is held in memory for the run and is never written to disk.
Durable entries live in `trustedRdpCertificates` in `settings.json`, keyed by
profile id, each carrying the thumbprint, when it was first trusted, and the
subject and issuer the probe read, so a future settings screen can name the
machine rather than show forty hexadecimal pairs.

**Known limitation, and the reason the set matters.** Nothing guarantees that
the ActiveX control reconnects to the same machine the probe inspected - on a
multi-member pool that is the normal case, not an edge case. The set does not
close that gap: it makes the question converge, it does not authenticate the
session. Heimdall compares only what its own probe read, and the certificate the
ActiveX control actually receives is never compared to anything; at
`AuthenticationLevel` 0 - the only level on which this check runs - the session
that follows requires nothing of the server. Where every member of the pool has
already been approved, whichever one answers presents a certificate the user has
seen; a member that joined after the last approval is accepted with no prompt
and no record. A scheme that approved a *pool* rather than individual
certificates would be worse still, because the machine actually joined could
present a certificate that was never approved, only assumed to belong. That is
why pool-shaped trust was rejected: there is no cryptographic link between two
self-signed certificates from two different machines, and this feature replaces
a Microsoft check that then lets CredSSP credentials through, so it cannot be
more permissive than the check it disables.

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

### Remote upload commit guarantees

Every remote write uploads to a unique temporary path next to the destination
first, so a truncated transfer never lands on the destination. What differs per
protocol is the commit step, and with it the guarantee an existing destination
receives.

SFTP file upload replaces a destination it has observed only through an atomic
rename. `SftpAtomicUpload.CommitRename` first attempts the OpenSSH
`posix-rename@openssh.com` extension, which replaces the destination in a single
server-side operation. A failure is eligible for the plain-rename fallback only
when `SftpBrowser.IsAtomicRenameCapabilityFailure` recognizes a capability error
(`NotSupportedException`, or an `SftpException` carrying
`StatusCode.OperationUnsupported`); every other failure, permission errors
included, propagates unchanged. Once demoted, the destination is probed, and the
fallback runs only when that probe proves the destination absent: a destination
the probe reports as present raises an `InvalidOperationException` and is left
untouched, and a probe that itself fails is propagated rather than assumed.
Heimdall therefore never moves, deletes, or backs up a destination it has
observed, and never opens a window in which such a destination is missing. The
uploaded temporary file is removed by the caller's rollback path.

That guarantee is scoped to what the probe observed, and the fallback is not
transactional. Between a probe reporting the destination absent and the plain
rename that follows, another writer may create that path. The rename then lands
on a target Heimdall never saw, with whatever semantics the server applies to an
existing destination: SFTP leaves that case to the implementation, so a server
may refuse the rename or may overwrite silently. Only the `posix-rename` path is
atomic with respect to such a concurrent creation. A deployment that must exclude
that race needs a server offering the extension.

SFTP remote copy either reserves the destination exclusively or is refused. The
copy runs as a server-side command over an SSH exec channel pinned to the host key
resolved at connect time, and that command is what makes the no-overwrite contract
real: a file is staged then published with a hard link, a directory root is
reserved with `mkdir` without `-p`, and both fail if the destination already
exists. If the command cannot be used, the copy is refused and the reason is
reported; there is no second route.

There used to be one. When the server-side command was unavailable, the copy fell
back to downloading to a local temporary file and republishing through a plain
rename, re-probing the destination only after that rename failed. A server whose
rename silently overwrites the destination therefore succeeded with no exception
and no warning, which meant the documented no-overwrite contract was not honoured
on that path. The fallback has been removed rather than annotated: a copy that
cannot promise the destination is untouched is not performed at all.

Cancelling a copy raises a cancellation, not a refusal. The two are distinct
outcomes and are reported and journaled separately, so "the user stopped this" is
never presented as "this server cannot copy safely".

FTP remote copy is refused, not attempted. The copy contract is that an existing
destination is never overwritten, and FTP cannot honour it: every publish this
client offers reduces to a client-side existence check followed by a plain rename,
and RFC 959 says nothing about what a rename onto an existing destination does, so
a server that silently overwrites is conformant. Previously the FTP copy ran
through the ordinary upload, whose commit replaces an existing destination and
reports success, so any missed pre-check became silent data loss. `CopyAsync` on
the FTP browser now always throws, and the user is pointed at SFTP with a working
server-side copy command, which is the only route that reserves the destination
exclusively. FTP cut and
move make no such promise and still issue a plain rename, so they may overwrite
silently: do not rely on a move to preserve an existing destination either.

FTP upload keeps the two-step replacement and is not atomic. FluentFTP exposes no
atomic replace, so `FtpAtomicUpload.CommitRenameAsync` moves an existing
destination to a `.bak` sibling, moves the uploaded temporary file into place,
then deletes the backup. A failed commit restores the backup, and a failed
restore raises an `InvalidOperationException` carrying both the commit and the
restore error. Between the two moves the destination does not exist, so a
concurrent reader can observe a missing file and a crash can leave the payload
under the `.bak` sibling.

FTP replacement also preserves none of the replaced file's metadata. What lands
at the destination is a freshly uploaded file, carrying whatever owner, mode and
timestamps the server assigns a new upload, so the previous file's ownership,
permissions, timestamps, ACLs, extended attributes and capabilities are gone.
FTP exposes no command that would restore them, and Heimdall does not simulate a
preservation it cannot perform. A destination whose access is governed by its own
mode or ACL must not be replaced over FTP; use SFTP, whose replacement path
preserves the complete permission mode and refuses the commit when it cannot.

Both facts are reported together. A successful replacement of an existing
destination raises exactly one per-operation `RemoteOperationWarning` on the
session surface, naming the missing atomicity guarantee and the metadata loss in
a single message. It is raised only once the commit move has succeeded: no
warning is due when the destination was absent, when the backup move failed, or
when the commit failed and the backup was restored, because in those cases
nothing was replaced.

### FTP and FTPS transport notices

FTP is implemented on top of FluentFTP `AsyncFtpClient`. `FtpHandler`
validates the target host and port before connect. If a user connects with
credentials and TLS is disabled, `ConnectionResult.Warning` carries a
localized non-blocking cleartext warning to the status surface; it does not
block anonymous or explicit FTPS sessions. Explicit FTPS enables TLS for the
control channel and FluentFTP `DataConnectionEncryption`, so file transfers
use an encrypted data channel.

The FTPS control-channel certificate is validated and pinned by Heimdall.
The data channel has a third-party limitation in FluentFTP 54.2.0:
`FtpDataStream` installs an unconditional certificate-acceptance handler, so
Heimdall cannot verify that channel's identity. This behavior is also present
in the current upstream source. No `FtpConfig` option exposes data-channel
certificate validation, and supplying a second callback through
`ConfigureAuthentication` is rejected by .NET because `SslStream` has already
been constructed with FluentFTP's callback.

The exact user-facing guarantee is:

*FTPS ne peut etre considere comme liant l'identite du canal de donnees a
celle du canal de controle que si le serveur exige la reprise de session TLS
et qu'un transfert reel reussit sous cette politique. Sans cette exigence
serveur, la garantie est indisponible.*

.NET allows session resumption but exposes no API for Heimdall to require it
or observe whether it occurred. Active FTPS sessions therefore display a
persistent, non-blocking notice that the data channel identity is not
verified.

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

### SSH password file lifetime

When an interactive SSH session authenticates with a password, that password is written to a
short-lived file passed to the launcher as `-pwfile`, because the alternative is putting it on a
command line every process on the machine can read. The file used to live until the session ended,
so a secret needed for a handshake could sit on disk for hours.

It is now deleted on the first byte the launcher writes to stdout or stderr - **but only for a
launcher whose `-pwfile` behaviour has been measured**. In PuTTY 0.83, `-pwfile` is handled inside
`cmdline_process_param` while the command line is parsed - one line read, handle closed at once -
strictly before any network activity, so any output whatsoever comes after the password has been
read. Measured on that binary: an unreadable `-pwfile` against an unreachable host reports the file
error immediately, where a readable one against the same host instead spends the full network
timeout.

That conclusion belongs to that build and to no other. Heimdall lets the user point `PlinkPath` at
any executable, so the launcher is identified by the SHA-256 of its bytes and compared with the
measured build shipped at `Assets/Tools/plink.exe`. A different build may well print something before
it reads the file, so for any other executable - unknown bytes, an unreadable path, any failure at
all - the early deletion is withheld and the file is released at process exit, as before. Nothing is
trusted on a file name, a directory, a version resource or a string printed by `-V`: none of those
says anything about when the file is read.

Identifying the bytes is not by itself enough to describe **the image that runs**, for two separate
reasons.

The first is timing. The handler can wait on an interactive password dialog, and that wait is
unbounded; a legitimate update landing in that window would hand an unmeasured build the previous
verdict. So the password is resolved in full first, dialog included, and only then is the executable
opened once, hashed from that same handle, and - when it matches - kept open with sharing that
denies writes and deletion until the launch has been issued. Measured on a temporary copy: while
that pin is held the image still starts, while replacing it and writing to it are both refused. The
pin is released as soon as the launch returns, so a later update is not held off for the whole
session.

The second is the path. **An absolute path does not identify a file.** The open handle pins the
file, not the directories named on the way to it: a junction anywhere in the path can be deleted and
recreated pointing elsewhere while the handle is held, and the identical absolute string then
resolves to a different image. This was reproduced, not theorised - with a pinned attested plink
under `...\current\plink.exe`, repointing the `current` junction and launching the same string ran
another executable. `Path.GetFullPath` does not help: the string was already absolute and already
normalised.

So an attested lease also carries the path taken **from the handle itself**, through
`GetFinalPathNameByHandle`, with every reparse point already followed, and that is what gets
launched. Measured: the returned `\\?\`-prefixed form starts the image, and starts the attested one
even after the junction has been repointed. If that path cannot be resolved, nothing is attested:
the connection proceeds on the configured path and the password file waits for process exit.

This is **not** a defence against a hostile binary. Heimdall hands the password to whatever
executable it was pointed at, so an executable chosen to steal it has already won. What the identity
check establishes is narrower: whether a timing conclusion drawn from one measured build may be
applied to the binary actually being launched.

**The exposure is reduced, and the remaining case is narrower than first written here.** Measured
against a live OpenSSH 9.6p1 target whose forced command is `sleep 600` - a session that connects
and then says nothing at all - the launcher still writes `Using username "..."` to stderr as soon
as it has a login name. That reaches the gate through the merged output stream, and the password
file is deleted 93 ms after the connection returns, while the process is still running and process
exit has not fired. A silent remote command is therefore covered.

**A profile with no configured username is now refused rather than left exposed.** The launcher in
that case waits for a login name and writes nothing on either stream - measured: zero bytes after
three seconds, process still alive - so no first byte arrives and the password file would live until
the process exits. Heimdall therefore declines the connection before the password dialog, before any
host-key probe or trust mutation, before the launcher is identified and before the file exists,
telling the user to set a username or connect with a key.

The refusal is limited to connections that would put a password on disk: a stored password, with or
without a key, or neither password nor key, since that path goes on to ask for one. **A profile that
authenticates with a key and no password is untouched** - it never writes the file, so it has nothing
to protect here, and nothing about key-only connections is claimed by this.

Process exit remains the backstop, every path that frees the file goes through one gate so the
deletion runs exactly once, and a launch that fails or is cancelled releases on the spot.

### Subprocess argument hardening

`PlinkTunnelRunner` builds the plink argument list via
`ProcessStartInfo.ArgumentList` (no string concatenation), and the stderr
drain task is **joined** at `Stop()` time before `Process.Kill()`, so the
background reader cannot outlive the pipe it was attached to. The drain
sanitizer (`SanitizeForLog`) redacts password / passphrase single-token
assignments, token / bearer assignments to end-of-line, and `-pw` / `-pwfile`
flags so an unexpected stderr echo from plink cannot leak credentials into
the application log.

### Remote entries whose type cannot be determined

A listing classifies each remote entry: regular file, directory, symbolic link, pipe, socket, device.
When every one of those tests fails, the entry is reported as **unclassifiable** rather than as a
regular file. That kind is the enum's zero value, so an uninitialised or unmapped value is the
non-transferable one and a forgotten branch fails closed rather than open.

An unclassifiable entry is refused by the application orchestration and by the guarded SFTP path: it
is excluded from the transferable inventory, so it is not uploaded onto, not downloaded, not pasted
and not duplicated, and the SFTP upload guard refuses it explicitly. It is shown with a distinct icon
so it is not mistaken for a file. Renaming remains available, as it does for a pipe or a socket,
because a rename moves a name and neither reads nor writes the object's content.

The exact bound: this covers the SFTP listing's own classification and the FTP listing mapper. On
FTP, a permission string of nine characters is mode-only and carries no type character, so it is not
read as one; a string of ten characters or more does carry one first (the extra character of the
`-rw-r--r--+` ACL and `.` SELinux forms comes last), and a type character this build does not recognise
makes the entry unclassifiable. What this does **not** add is a type guard on the FTP
upload path itself: `EnsureUploadTargetSupported` is still called only from the SFTP browser, so an
FTP upload does not consult the destination's kind at all. That gap predates this change and applies
equally to links, pipes, sockets and devices; it is tracked separately and is not closed here.

The `ls`-based listing used for sudo browsing is unaffected: it already skips any line whose type
character is not one it recognises. Those lines are dropped from the listing entirely; they are not
classified as unclassifiable, and this skip should not be read as producing that kind.

### Cross-endpoint clipboard paste

A paste between two different remote endpoints downloads each source file and puts it on the
destination server. Every node it creates there, file and directory alike, goes through an
**exclusive** primitive: a file is staged and then published with a hard link, a directory is
reserved with `mkdir` without `-p`. Both fail when something already occupies the name, and it is
the server that decides, not the client.

A transport that cannot offer such a primitive does not paste. FTP has no commit-time operation
that fails when the destination exists (everything reduces to a client-side existence check
followed by a rename), so it declines the capability and a cross-endpoint paste into it is refused
**before** any directory is created and before any byte is sent.

That early refusal is decided per transport, not per session. An SFTP session that advertises the
capability but cannot reach its pinned exec channel refuses later, at the first node it tries to
publish: a source file may already have been fetched to a local temporary by then. Nothing is
created or replaced on the destination in that case, and the temporary is removed; what is lost is
the transfer effort, not data.

Reaching that refusal depends on the two panes being recognised as different endpoints, so the
clipboard's endpoint identity is resolved through a dedicated seam that decorators carry, not by
testing the browser's concrete type. A type test answers about the wrapper as soon as the
operations-log decorator is in place, which would give every FTP pane the same empty key and make
two *different* FTP servers look like one endpoint. Distinct FTP endpoints are therefore identified
through the decorator, and a paste between them takes the cross-endpoint path and meets the gate.

An endpoint identity that cannot be determined at all is never treated as a match either. Two
unknown identities are two servers nobody could name, so the paste is routed to the cross-endpoint
path and either publishes exclusively or is refused. Losing endpoint metadata can degrade the
experience; it cannot silently reopen an overwrite.

This closes the cross-endpoint bypass. It does not change the contract of operations that are
genuinely same-endpoint: a rename or copy within one server behaves as it always has.

Directory listings are read live from the destination rather than from what the pane last
displayed, but they are used **only** to pick a name that is not already taken. A listing is not
the authority for anything: it is already out of date the moment it returns. The guarantee comes
from the exclusive reservation of each node, never from a prior probe.

Neither a collision, nor a cancellation, nor an unconfirmed result ever authorises deleting the
source of a cut. A cancellation in particular is not proof that nothing landed: a link or a
directory creation can take effect just before the answer is lost. Heimdall asks for the destination
to be reloaded, and when the session and the listing are still available the refreshed state is what
you see; a refresh that fails changes nothing about the verdict. The source is kept and the clipboard
entry is kept either way, because a failed reload never turns an unconfirmed outcome into a success
and never authorises deleting the source.

Atomicity is per node, not transactional across a tree. A paste interrupted partway can leave a
partial tree on the destination. This is deliberate: recursively cleaning up a directory the paste
created could delete entries another party added inside it in the meantime.

#### What this does not protect against

The protection targets **accidental concurrency**: two panes, a stale view, a colleague writing in
the same directory at the same time.

It does not establish the provenance of the published content against a malicious actor who has
write permission in the same directory. Staging paths are named, and an attacker able to substitute
the staging entry between two by-name operations can have content published that this client did
not write. Cleanup by name shares that boundary. Nothing here should be read as a defence against a
party who can already write where you are writing.

The tests cover the contract, the wiring and the generated commands. **No real SFTP server is
exercised anywhere in the suite.** In particular, whether a remote `ln` or `mkdir` accepts `--` as
an end-of-options marker is a property of that server's utilities, not something demonstrated here
and not a universal guarantee. A utility that rejects these forms makes the command fail, and the
caller refuses: there is no fallback to a primitive that could replace the destination.

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
  `tests/Heimdall.Rdp.Tests/RdpFileGeneratorTests.cs`.
- CI enforces: build with zero warnings under `TreatWarningsAsErrors`,
  `dotnet format --verify-no-changes`, full test suite, JSON locale parity
  (EN and FR key sets must be identical, currently 5,489 keys each), and an
  informational `dotnet list package --vulnerable` scan.
- Dependency scan for manual review: `dotnet list Heimdall.slnx package
  --vulnerable --include-transitive`. CI emits warnings but does not gate on
  vulnerability results, since advisories occasionally include false
  positives or entries without an upgrade path.

