# Security Notes

*Also available in French: [fr/SECURITY.md](fr/SECURITY.md).*

This document records known security considerations, limitations, and deliberate
defense-in-depth decisions in Heimdall.

## Reporting a vulnerability

Report a suspected vulnerability through GitHub private vulnerability
reporting: [open a private report](https://github.com/VBlackJack/Heimdall/security/advisories/new).
The report stays visible to you and the maintainer only. Do not file a public
issue for a security problem.

This project publishes no contact address, and mail is not a supported way to
reach the maintainer. Addresses that appear in commit metadata are an artifact
of how Git records authorship, not an invitation: they are not monitored. The
full policy, including where to raise bugs and questions, is in
[SECURITY.md](../SECURITY.md).

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

## Security overview in Settings

The `Settings > Security > Overview` section lists the security-relevant choices of
the Settings screen, one line each, with their state, and flags the documented
insecure ones: NLA off, TFTP sharing on, session transcripts on, the `Bypass` or
`Unrestricted` PowerShell execution policy, the master password on with no idle
auto-lock, update checks off, and the `known_hosts` import at startup on (it
trusts, without a prompt, keys any other program wrote to that file). Strict
server authentication off is reported but not flagged, because it is the
Windows default, which warns and asks. The card reads the pending values and
tags a line that differs from disk as unsaved, so it describes what Save would
keep, not what is running. The rule is one pure type, `SecurityPosture`, under
test; the card does not decide anything itself. It covers settings only: there
is no global WinRM skip-certificate default (that choice is per session) and no
SSH host key policy to report (an unknown or changed key always asks).

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

### Local tunnel endpoints are not authenticated

Every local endpoint a tunnel opens - the local forward, the SOCKS proxy of a
profile with `SocksProxyPort`, and the intermediate forwards of a gateway chain
- listens on a loopback address only (`LoopbackBinding` refuses anything outside
`127.0.0.1`-`127.0.0.254`), and none of them asks who is connecting. This is the
model of `ssh -L` and `ssh -D`: any process running on the same machine can
connect to these ports for as long as the tunnel is open, and reach what the
gateway reaches with the operator's authenticated session.

On a single-user workstation that is the expected trade-off. On a machine
shared by several users at once, such as a Remote Desktop Session Host used as
a jump box, the other users' processes share the loopback interface. On such a
host, prefer a plain forward to the one destination over a SOCKS proxy, and
close tunnels when the session that needed them ends.

A tunnel does not outlive what it serves. One whose connection to the gateway
is lost, on any hop, is closed and its port released (`TunnelManager` checks
every 15 seconds and on any SSH client error). A Plink tunnel process is placed
in a Windows job object marked kill-on-close (`PlinkProcessJob`), so it ends
with Heimdall's process however that ends, and a crash no longer leaves an
authenticated forward on loopback with no owner. When the job cannot be set up,
plink runs as before and the reason is logged.

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

On a changed key the prompt defaults to and focuses **Reject**: the Enter
reflex trained by the first-use prompt, where Enter accepts, must not connect
to a host whose key no longer matches the stored one. Accepting the new key or
trusting it for one session takes an explicit click. The FTPS certificate
prompt is a first-use prompt only: a changed FTPS certificate is refused
without a prompt, and trusting a replacement means removing the stored
certificate first. Replacing a key never carries the previous
public key blob over: the entry holds the new fingerprint with the caller's
blob or none, so the known_hosts export can never write back a key the user
just decided against. A mismatch resolved against a hashed entry replaces that
entry instead of leaving the old fingerprint effective beside the new one.

The reachability probe of a web console (`HttpHealthProbe`) does not check the
console's certificate. It asks for the console's root page, never the
configured path, sends no credentials, no cookies and no body, follows no
redirect and bypasses the system proxy; it reads the status line and headers
only and closes the connection rather than drain a body. A forged server can therefore lie about the dot's colour and
nothing else. Trust in a console's certificate is decided in its web tab, by
the pin the user accepts there.

The account stored for a web console is written into the console's own sign-in
form only when the profile names the fields, and only on the scheme, host and
port that profile addresses: a console which redirects to another host is left
to the user rather than handed the password. The password reaches the page as a
JavaScript string literal, is never logged, and the form is filled once per tab,
recorded in the page's session storage, so a refused account is not retried into
a lockout; Reload gives that attempt back. The fields are set through the
input's own value setter and the input and change events, which is what the
page's framework reads, and a selector matching nothing leaves that field alone.

`~/.ssh/known_hosts` import and export are explicit user actions surfaced in
`Settings > SSH & SFTP > Trusted host keys`. Import preserves conflicting
existing entries unless the user explicitly opts into replacement in a
dedicated modal; conflicts are detected through the trust service, which also
matches hashed entries, so a plain line cannot silently shadow a host already
trusted through a hashed one. A hashed token can only match the identical
token: its plain host is unknown, so it is never hashed under the stored
salts. Export writes UTF-8 without a byte order mark, which OpenSSH does not
strip, and preserves every line Heimdall did not originate (including
`@cert-authority`, `@revoked`, hashed entries, and any multi-host line whose
aliases are not all managed) verbatim.

Embedded SSH terminals support keyboard-interactive authentication, including a private
key followed by a verification code. After host-key verification, recognized password
questions can receive the saved password once. A question that names a one-time code
(one-time, OTP, verification code, token, passcode, and their French, German and Spanish
forms) never receives it, even when its wording also contains "password", as pam_oath's
"One-time password (OATH)" does. Once the server has accepted the `password` method as a
partial success, the saved password is spent for the rest of the attempt, so the second
factor that follows cannot receive it either. An outright refusal of the `password` method
does not spend it: a server with password authentication off still asks for the password
through keyboard-interactive, and gets it.

Other questions open a masked dialog showing the destination and account, even when the
server asks only one question. The server's text is quoted and attributed to the named
server, with a statement that Heimdall never asks for its master password there. Before it
is shown, `ServerPromptText.Sanitize` turns control characters and line or paragraph
separators into one space, drops format characters (bidi overrides, zero-width characters,
byte order marks) and lone surrogates, collapses whitespace and caps the text at 256
characters, so a server cannot open a paragraph of its own or reorder what is read in order
to pose as Heimdall. Responses are used for the current attempt and are not saved to the
profile or vault. Cancelling the dialog aborts authentication and is reported as a
cancellation, with no Plink retry; cancelling the connection closes the dialog.

Reaching the host keeps the normal 15-second connect timeout, which bounds the TCP connect,
the banner and the key exchange. The two-minute bound that leaves time for input applies
only to the authentication waits, from the moment the server's host key has been received.

A rejected interactive answer ends the attempt without an automatic Plink retry. This
avoids submitting another authentication attempt after a refused code. An authentication
refusal does not prove whether the key, password, code or server policy caused it.

This interactive entry applies to the SSH.NET embedded terminal path. SFTP, SSH gateway
creation and diagnostic probes retain their existing non-interactive behavior. Callers
without an interactive responder still answer a first single-question round with the
stored password unless it names a one-time code, and leave unanswered a one-time-code
question and any question asked after the password was used; the refusal that follows is reported as the unanswered
question, not as a rejected password. They therefore must not be treated as supporting
verification-code entry. Agent-forwarding sessions
that use Plink remain a separate authentication path.

An earlier Plink test returned "No supported authentication methods available". That
result is specific to the tested setup, not a general incompatibility with MFA. The
local test environment was found with PAM disabled on 2026-09-09; keyboard-interactive
and its authentication backend must both be enabled for a meaningful MFA test.

The key generator writes both files as UTF-8 without a byte order mark with LF
line endings, and creates the private key through the same restrictive-ACL
writer as the Plink password file, so Win32-OpenSSH does not refuse it as
"permissions too open". The managed VcXsrv keeps host access control on: the
only client Heimdall points at the display is the local one, and `-ac` would
let any host that can reach TCP 6000 read the forwarded windows and the
clipboard.

Plink fallback paths are also fail-closed. `PlinkHostKeyDecider` accepts a
stored fingerprint immediately, otherwise asks an injectable
`IPlinkHostKeyProbe` for the presented key and runs the normal verifier
before launching plink with `-hostkey`. If neither path can resolve a
Heimdall-trusted fingerprint, the operation returns
`SshFailureCode.HostKeyUnavailable` and refuses to fall back to PuTTY/Plink's
own cache.

Reusable tunnel identity includes the remote target, forwarding mode, and a
versioned SHA-256 gateway chain key over length-prefixed IDs, endpoints,
accounts, key paths, encrypted credentials, legacy credential mapping and agent
preference. Connection-affecting configuration edits prevent new acquisitions of
the old tunnel. A display-name-only edit preserves sharing. Existing consumers
keep their authenticated transport until they close it; editing a gateway does
not revoke those sessions. Two tenants exposing `10.0.0.5:3389` through different
bastions do not share a local tunnel.

Network tools resolve the full parent chain from current settings. Every hop
requires an existing trusted fingerprint and pins the authenticating connection.
The final client owns the parent route, including intermediate listeners and key
resources, and releases it on disposal or a failed handshake. Connection work
runs off the UI dispatcher and observes cancellation through the SSH handshake.
Chained tunnels apply the configured SSH keepalive interval at each hop.

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

Certificate revocation is acknowledged only after its settings write succeeds. A failed write
leaves the approval and its settings row visible, with an error and the option to retry. Trust
writes are serialized by the application so a delayed snapshot cannot restore a removed entry.
Folder creation and the scheduled task list write only their own field through the same merge,
so neither can write back a copy of the settings read before a revocation landed. An approval
still being written when Heimdall exits is awaited for up to five seconds, before the settings
writer is disposed; a write that overruns is logged and abandoned like the other exit steps.

External RDP launch fails if the temporary `.rdp` file cannot be created with its restricted ACL.
Credential Manager ownership checks and writes/deletes share a process-wide gate, and stale
cleanup rechecks ownership after enumeration. This coordinates Heimdall's own operations;
Windows does not provide conditional credential deletion against changes by unrelated processes.
When a launch of this same process to the same host is still within its live window and staged
the entry for another account, or for an account the entry does not name readably, a second
external launch is refused with a message instead of leaving that entry in place: mstsc would
have read it and signed in as the first account. A second launch for the same account
(compared without case) keeps the entry and proceeds, as before.

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
Force-External launch - starts `mstsc.exe` with no Heimdall-side certificate
check at all, while `RdpFileGenerator` puts the same `authentication level:i:0`
into the generated `.rdp` file that the embedded path applies to the control
when NLA is off. On that path the Windows check is relaxed and nothing replaces
it.

What changed is the password, not the check. At level 0
(`RdpAuthenticationSettings.AuthenticatesServer` is false) the handler neither
decrypts the stored password, nor writes the `TERMSRV` credential, nor fills in
the credential prompt: mstsc asks the user, and the launch carries a notice
saying why. Supplying the password there meant signing in to whatever answered
at that name or tunnel endpoint. What did not change: the external path still
has no Heimdall certificate gate. With NLA on (levels 1 and 2) the password is
staged and filled in as before, and it is mstsc that checks the server.

**The `TERMSRV` credential is session-scoped and swept.** When it is staged, the entry is written
with `CRED_PERSIST_SESSION` and a Heimdall ownership marker, released after the
configurable cleanup delay, and released at once when Heimdall exits with a
cleanup still pending. What a crash leaves behind is reclaimed by
`CredentialManagerHelper.SweepStaleOwnedCredentials`, which runs at startup and
before every external launch: it enumerates `TERMSRV/*` and deletes the
domain-password entries whose marker is older than the live-launch window,
whatever process wrote them. An entry without a Heimdall marker, or with the
older marker format that carries no timestamp, is never touched.

**Every outcome other than an explicit refusal proceeds.** `RdpCertificateProbe`
opens its own TCP connection, performs the X.224 negotiation, and reads the
certificate offered by the TLS layer. If the endpoint is unreachable, keeps
standard RDP security and offers no certificate at all, or the probe throws,
Heimdall has verified **nothing** and the connection is opened exactly as it
would have been without this feature. Refusing on an unverifiable endpoint
would turn a verification step into a new way to fail on a path that worked
before; accepting one as verified would be strictly worse than never having
built any of this.

**Approval is per owner, and per thumbprint.** An owner is either a saved
profile or a destination typed by hand, and the two are kept apart even when
they carry the same identifier: the command palette mints `adhoc-rdp-<host>` for
a typed destination, and a profile imported before that namespace was reserved
can hold the very same string. An approval given for one never silences the
question for the other, and one question never writes trust under both. The
owner is decided by the code that built the connection (the palette and the
server row mark what they mint), never by reading the identifier's shape.
Trusting adds to the set and never replaces it; re-approving a thumbprint keeps
its original timestamp. "Just this once" is held in memory for the run, per
owner, and is never written to disk. Durable entries live in `settings.json`:
`trustedRdpCertificates`, keyed by profile id, and
`trustedRdpCertificatesForTypedDestinations`, keyed by the typed host in lower
case. A typed destination is keyed by its host because that is all the user
typed, and because the server row mints a fresh identifier per launch under
which an approval could never be found again. Each entry carries the
thumbprint, when it was first trusted, and the subject and issuer the probe
read, so the settings screen can name the machine rather than show forty
hexadecimal pairs; a typed destination's row is shown under its host with a
"Quick connect" badge.

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

### WinRM sign-in

Both WinRM launches run PowerShell with `-NoExit`, which keeps the session interactive.
Before `Enter-PSSession` they define a global `prompt` function that ends the host through
`[Environment]::Exit` the first time a local prompt would be shown: exit code 1 when the
remote session was never entered, 0 once it was. A failed sign-in, an `exit` typed in the
remote session and a dropped connection therefore end the process, instead of leaving a
local prompt in a tab titled with the remote host, where broadcast, the Command Library and
macros would run on this machine. The error stays on screen and the tab shows the session as
ended; WinRM never reconnects on its own after a process exit, so a failed sign-in cannot
loop. The stored-credential launch defines the guard on its `-Command` line before invoking
the script, because the command line is not subject to the execution policy: a script the
policy refuses cannot skip the guard. Limit: under Constrained Language Mode the prompt
function cannot call `[Environment]::Exit`, and the host keeps its default prompt.

An execution policy enforced at machine or user policy scope (for example `AllSigned`)
overrides the `-ExecutionPolicy Bypass` the launch asks for and refuses the unsigned sign-in
script. That refusal is recognized from PowerShell's early output (the `about_Execution_Policies`
help topic, which every host language names untranslated) and explained with a localized
message that offers the current Windows identity, which needs no script.

Stored-credential mode writes two files in the temporary folder through the same
restrictive-ACL writer: the sign-in script and, beside it, a `.blob` file holding the password
protected with DPAPI for the current user. The script reads the blob and deletes it before
anything else, and deletes itself; only the blob's path appears in the script text. That keeps
the secret-derived value out of the Microsoft-Windows-PowerShell/Operational log, where
script-block logging records the full text of every script it runs. Every cleanup path
(launch failure, process exit, kill, dispose) removes both files, and a periodic sweep removes
stale `heimdall_winrm_*` scripts and blobs. A locked vault, a stored password that cannot be
decrypted and a refused protection of the blob are each reported under their own cause.

The reachability preflight (TCP, then TLS for HTTPS) runs on direct connections only. Through
an SSH gateway it would connect to the local end of the tunnel, which accepts every connection
whatever the target does, so it is skipped and the skip is logged; an unreachable target then
shows as the `Enter-PSSession` error.

Import clears `WinRmSkipCertificateCheck` on every imported profile, with or without SSL, as it
clears `ExecutionConfirmed`: whether a host's TLS certificate need not be validated is decided on
the machine that connects, not by whoever wrote the file. The profile dialog, where the setting
is visible, is the only way to turn it back on. The dialog also refuses at save a WinRM username
the sign-in script would refuse, and drops the stored WinRM password when the profile uses the
current Windows identity, since the field that shows and clears it is hidden in that mode.

### SFTP sudo escalation and remote editing

SFTP sudo fallback is deliberately narrow. `EmbeddedSftpViewModel`
escalates only when the server refuses an operation for lack of permission
(`SftpPermissionDeniedException`, or a recursive delete refused for that
reason); generic `SshException("Failure")` messages do not trigger privileged
operations, and neither does a local `UnauthorizedAccessException`: that is
the local file system refusing, and a privileged remote transfer would fail on
the same local path. This trades occasional manual retry prompts for avoiding
sudo actions on non-permission failures.

Whether a refused operation is run again as root is a setting, **SFTP: retry refused
operations as root** (Settings, SFTP), with three values:

- **Ask first** (the default, also for an upgraded installation): one question per
  action, declining by default, that names the act and the path ("Upload as
  root" / "Do not upload"). A transfer job asks once, its folder walk included;
  a deletion or a permission change of several entries asks once; opening a
  file in an editor asks once and says that the later saves of that editor are
  also written as root. A yes to a listing question turns the pane's **sudo**
  toggle on, so the folder and its refreshes are then listed as root without
  asking again.
- **Automatic (no question)**: the earlier behaviour, root without a question, except a
  delete, which always asks. The security posture card flags this value.
- **Never**: sudo is never called; the server's refusal is shown with a line
  saying root was not tried, and a delete counts the entry as not deleted in
  its summary. The pane's **sudo** toggle is disabled.

Some listings are not started by you and never ask: the first listing when a
pane opens, a folder followed from the terminal's working directory (a path the
server prints), and the refresh that follows an operation. Under Ask first they are
not run as root, the status line says so, and the pane falls back as it does
for any refused listing. The **sudo** toggle only lists folders as root: it
never lets a write skip the question.

An organisation can cap the setting with the DWORD value
`SftpSudoFallbackMax` under `HKLM\Software\Policies\Heimdall` (0 Never, 1 Ask,
2 Automatic). The stricter of the user's choice and the cap applies, the looser
choices are disabled in Settings with a line saying the value is managed, and
the posture card shows the effective value. The value is read once, the first
time it is needed; a change takes effect after a restart.
This is not a security boundary: a user with a shell on the server can run
sudo there directly, and the server's sudoers rules remain the real control.

The sudo password offered is the session's SSH password; it is never typed
again and never written to a log or a command line. Without a stored password
(a key or an agent), the command runs only where sudo needs no password.

These escalations are not hidden: from the first one, a "root" badge stays in
the pane's toolbar until the pane closes, and its list names the latest
operations attempted as root with their outcome and time; directory listings as
root are counted apart, on one line. The badge starts again from zero when the
pane is rebuilt, for example after a reconnect, and the existence checks of a
rename as root are not counted.

A permission change as root never follows a symbolic link. A directory is
changed from inside it, after checking that it is still the entry that was
listed; a regular file is changed through a hard link made in a private
root-only directory created beside it and removed afterwards. A symbolic link, a
pipe, a socket, a device, or an entry that changed during the check is refused;
to change a link's target, browse to the target. This needs GNU coreutils on the
server, like the privileged transfers, and a folder where root can create that
directory and a hard link; otherwise the change is refused rather than made by a
path that could follow a link. The folders that lead to the entry and a hard link
at its name are handled as described below for the privileged transfers.

In a deletion or a permission change of several entries, a sudo failure that no
later entry can change (password refused or unavailable, account not allowed to
use sudo, sudo or a required tool missing, SSH authentication or host key
refused) stops escalation for the rest of that operation: the remaining entries
are still tried with the session's own rights, and the summary says how many
were not tried as root. The detection reads sudo's English messages. Every sudo
the browser runs is started as `env LC_ALL=C sudo` (the upload exports
`LC_ALL=C` before its sudo calls), so sudo answers in English whatever the
server's language, and a refused password stops the batch there too.

Privileged uploads are staged first: the content goes into a private 0700 directory of the
connecting user under `$TMPDIR` (default `/tmp`), in a 0600 file, and stays there until the
install finishes. A killed connection or a power loss can leave that directory, and the
content, on the server. A staged file whose size differs from the expected one is refused
before any sudo runs, so a read that stopped early is never installed. One sudo call then
installs the file into a private directory beside the destination and renames it over the
target. The install is tried first without a password and with no input; the password is
offered, once, through a pipe that carries only that line, so it can neither reach the file
nor sit unread on sudo's input. The installer refuses a staging directory that is a symbolic
link, is not owned by the caller, or can be written by other accounts, and checks, on the
descriptor it opens, that the staged file is a regular file of the caller with a single link;
the connecting user's own cleanup removes the staging directory, root never does.
Accepted replacements use atomic rename; creation-only transfers publish by an
exclusive hard link (`ln -T`, which refuses a destination that has become a
directory instead of linking inside it). Cleanup removes only the staging files.
Ordinary upload and download conflict decisions also reach the final commit: an
unoccupied target or automatic rename never authorizes replacement. How each
transport creates a new file is described under "Remote upload commit guarantees"
below.

The private directory beside the destination is verified before it is used, with the
same building blocks as the root permission change: root holds the destination's
folder, creates the directory relative to it, enters it, and checks that it belongs
to the account running the script, cannot be written by other accounts, sits in the
held folder on the same device, and is empty; the script then claims it and reaches the
destination through the held folder, which it checks again just before the rename. A
privileged read (download, open for edit) enters its private directory the same way.
A signal ends the script only after its cleanup, which removes only what that run
created. Command search keeps only the absolute entries of `PATH`, in these scripts,
the installer and the root permission change. Where the directory cannot come out
owned by root and unwritable by other accounts (NFS with root squashing, a CIFS or
vfat mount whose fixed owner is not root or whose fixed mode lets others write), the
transfer stops with "Transfer failed.", the log gives the reason, and the empty
directory stays behind. From v2026.072501 to v2026.111501 the scripts entered that
directory without checking it, so an account able to write in the destination's
folder could make a root write or read land elsewhere; earlier versions wrote and
read through the destination's name and followed a symbolic link put there.

Root reaches the folder of an entry it writes, reads, deletes, creates, renames or
changes the permissions of by walking the path from `/`, one folder at a time, holding each
folder before the next step. A symbolic link on the way is followed only when the
link belongs to root or to the account that called sudo, and the folder holding it
belongs to root or to that account and cannot be written by other accounts; its
target is walked the same way, and more than 40 links end the walk. Any other link is
refused, and the message says that a folder above the entry is reached through a link
another account controls: open the folder by its real path instead. A link owned by
another account, such as one unpacked from an archive, is refused even in a closed
folder. A folder whose mode is printed with fewer than three digits is treated as
open; on a folder with a POSIX ACL the mode bits show the ACL mask, and NFSv4 ACLs are not seen. A root
delete removes the entry from the folder it holds, by its last name only, refuses a file
whose name a folder took, and, like the root permission change, needs GNU coreutils on
the server: on BusyBox or BSD it is refused. A root folder creation and a root rename
work the same way, from the held folder, and need GNU coreutils too: a name that is
already taken is reported, never replaced, unless you chose to replace a file. The
checks that look for an existing name before a rename read the path as given (a
`!` in it is escaped); the rename itself does not.

A write, read or permission change of an entry with another hard link is refused
when its folder can be written by an account other than root, the caller and the
entry's owner; the message says so. Hard
links in folders closed to other accounts, such as package files under `/usr`,
keep working.

Limits that remain: an account that can write in the folder can remove the empty work
directory and make the transfer fail.

A file that replaces another keeps its owner, mode, timestamps and extended
attributes. A new file written as root belongs to root, with the folder's gid when
the folder is setgid, and gets mode 0644, as `sudo cp` gave it, where the file system
allows it. In a folder with a default ACL, the new file keeps what that ACL gives it
instead. A secret uploaded as a new file is therefore readable by every account until
you change its permissions; the root permission change does that without following a
link.

The privileged connection goes through the same cancellable connect as the other
SFTP connections, so Cancel reaches it during the handshake. Every privileged
command is bounded: `PrivilegedFileTransfer.ControlCommandTimeout` (ten minutes)
for a command with no known payload, plus the payload at 32 KiB/s for a transfer,
so a slow link cannot hold a command forever and a large upload is not cut short
by a fixed bound.

SFTP replacement copies the destination's POSIX GID before restoring mode and
timestamps, then verifies all of them. A failed GID change or mismatching
read-back refuses publication. Privileged listings use GNU find with NUL-separated
fields; embedded newlines cannot create fictitious rows. The listing starts with
`find -H`, so a symbolic link given as the directory to list is followed, while
links among its children are still reported as links; owners are reported by name
(`%u`, `%g`), or by number for an ID with no name. Unsupported child names are
excluded before any operation can consume them. The command given to sudo is `find`
itself with no variable assignment, so a sudoers rule limited to `find` matches it
and sudo is never asked to set a variable.

`RemoteFileEditor` tracks file-watcher upload tasks per edit session,
propagates cancellation through `CloseEdit` and `Dispose`, and observes
faults synchronously so unhandled background upload exceptions do not reach
the process-wide `UnobservedTaskException` pipeline. Sudo edit sessions
cache the `PinnedFingerprintVerifier` built at open time instead of resolving
host-key trust again on every save. A refusal no retry can change (a permission
refusal, a metadata restore the server refuses, a target it does not support) is
reported once and does not re-arm the retry timer; the next save tries again.

Both editors stage a remote file in a local working folder restricted to the
current user. When that restriction fails, the folder is removed and the open is
refused, rather than staging the file, a root-owned one read through sudo
included, in a folder other accounts on the computer could read.

External-editor opens are serialized and linked to their owner's lifetime. Closing
the owner cancels pending downloads; registration, watcher creation and editor launch
cannot resume after disposal.

### Tool packs machine policy

The built-in tools are grouped in packs (see the Pack column of [TOOLS.md](TOOLS.md)). An
organisation can forbid an optional pack on a machine with one DWORD value per pack, under the
same key as the SFTP cap, `HKLM\Software\Policies\Heimdall`:

| Value | Pack |
|---|---|
| `ToolPackEssentials` | Essentials |
| `ToolPackNetworkDiagnostics` | Network diagnostics |
| `ToolPackAdministration` | Administration |
| `ToolPackAudit` | Audit and reconnaissance |
| `ToolPackRecipes` | Recipes |

- `0` forbids the pack. `1` means no restriction. Any other number, and an absent value,
  means no policy for that pack; `2` is reserved for a later "forced on" and is treated as
  no restriction today. The Core pack has no value: it can never be forbidden.
- A forbidden pack beats the user's choice. Its tools are hidden and refused, and its switch in
  `Settings > Advanced > Tools & integrations > Tool packs` shows off and disabled, with a line
  saying the pack is set by the organisation's policy. The user's own choice stays in the
  settings file, so lifting the policy gives it back.
- Only the per-machine hive is read, because the per-user hive is writable by the user the
  policy constrains. Two readers exist, the tool availability service and the privilege
  launcher's own; each reads the values once, the first time it needs them, so a change needs a
  restart.
- Only a `REG_DWORD` equal to `0` forbids a pack. Any other type or value, and a key that cannot
  be read, means no policy.
- The privilege launcher also refuses to launch while the Audit pack is forbidden, whether it
  was reached from the tool or started with a hand-built command line.

This is a convenience for administrators, **not a security boundary**: the tools stay in the
binary and on disk, and a local administrator can change the policy. It also does not reach
everything that resembles a pack's tools. Known limits:

- The Windows services tool elevates through `runas` to start, stop and restart services. It
  belongs to the Administration pack, not to Audit, so forbidding Audit does not remove that
  elevation path.
- The server context menu sends Wake-on-LAN packets on its own, with or without the Wake-on-LAN
  tool.
- Detected external tools, such as Sysinternals PsExec, are outside the packs.
- Several network diagnostics tools (ping, DNS lookup, WHOIS, HTTP headers, traceroute and the
  certificate inspector) can run their probe on an SSH gateway the user picks, which runs
  commands on a remote host. This is not specific to the Audit pack.

### Recipes

A recipe (see [TOOLS.md](TOOLS.md#recipes)) is a text file, written by someone
else, that tells Heimdall to run a program already on the computer with a given
list of arguments. Importing one is a trust decision: from then on, running the
tool starts that program with those arguments, with your rights. The Recipes
pack is off by default.

**What protects you.**

- **The import preview is the real barrier.** Before anything is written,
  Heimdall shows the program as the full path it resolved, then every argument
  exactly as written in the file, one per line. Control and invisible
  characters, and every character outside printable ASCII in the program, the
  arguments and the defaults, are shown as `\uXXXX`. Every translation the file
  carries (language, name, description, field labels and field hints) is listed
  too, because the tool shows it instead of the English text when the interface
  is in that language, and so is the hint of every field and the arguments a
  switch adds when it is on and when it is off. For a replacement, each of the
  author, the name, the description, the translations, the output and its
  encoding (and the separator of a table), the way standard error is kept, the
  success exit codes, the timeout and the icon is said to be unchanged or
  changed, on a line of its own, and the line of the fields compares every
  property of every field. This model assumes the preview is read: the other
  rules below are limits on what a file can ask for, not a judgment on what it
  is for.
- **Structural rules.** The program must be a real `.exe` that Heimdall resolves
  itself (the operating system search order is never used), and its final
  location, once links are followed, must lie under the Windows folder, Program
  Files, Program Files (x86) or a folder you added. It is never started through a
  shell: the argument list is passed as is, one token being one argument, with
  no quoting, splitting or joining. No environment variable is expanded. Fixed
  text that holds a network path (two separators in a row, in either form and in
  any mix, so a backslash next to a slash counts), a URL, the system path prefix
  (a separator, two question marks and a separator), a `%`, a leading `@` or a
  character that looks like a plain one is refused at import. A choice or a
  default is held at import to the rule a typed value meets at run time (see
  below), so one that would always be refused is refused with the file. A drive
  root cannot be added as an allowed folder. A typed value is held to a common
  rule (no control character, no look-alike character, no leading space, `-`,
  `/`, `@` or backslash where it starts an argument, no network or device path in
  a `path` field) and to the validation of its type.
- **Allowed roots are checked, not trusted as a whole.** A program under the
  Windows folder, Program Files or Program Files (x86) is refused when a user
  who is not an administrator can change its file or a folder above it (up to
  that root), through its permissions or by owning it (an owner other than the
  system, the administrators or the installer service), or when those
  permissions or that owner cannot be read. A folder you added is trusted as a
  whole and is not checked, but only for what lies outside Windows and Program
  Files: adding a folder that covers part of them does not switch the check off.
  A folder that leads to the root of a drive or to a network location (a mapped
  network drive, a link to a share) is refused when you add it, and its final
  form is never trusted when a program runs.
- **Look-alike characters.** A character that Windows would turn into a plain
  ASCII one for a program that reads its command line in the ANSI code page of
  the machine (a full-width hyphen, a typographic quote, a no-break space) is
  refused in the fixed text and in typed values. The rule follows the ANSI code
  page of the machine, the one a program reads its command line in, so some
  accented capitals and ligatures can be refused in an argument on some machines
  and accepted on others.
- **A seat belt, as a second line only.** A list of names of shells,
  interpreters and proxy executables (`cmd`, `powershell`, `python`, `rundll32`
  and the others listed in [TOOLS.md](TOOLS.md#recipes)) is checked on the file
  name and on the original file name in the version resource. It stops the
  obvious mistake. It is not a boundary (see below).
- **The bytes you accepted.** The SHA-256 of the imported file is recorded. A
  recipe file that was changed afterwards, or dropped into the folder by hand,
  is not loaded.
- **The program you accepted.** The path the program resolved to at import is
  recorded and compared before each run. If the name now leads to another file,
  or to nothing, the run does not start and the recipe has to be imported again.
- **A fresh working folder.** Each run gets a new, empty working folder, removed
  afterwards. It is created under a Heimdall folder in your temporary folder, not
  in the data folder, so a program does not start next to the settings of Heimdall.
- **Bounded runs.** At most 1 MiB of output is kept per stream (2 MiB in all when
  standard error is kept apart), and a run is stopped at the timeout of the
  recipe (60 seconds by default, 600 at most). A run under way is also stopped
  when the pack is turned off or becomes forbidden.
- **The processes the program started.** The program is placed in a Windows job
  object right after it starts, and the job ends the processes it started when
  the run ends. A process the program launched in that very first instant can
  escape the job. If Windows
  refuses to create it (for example when Heimdall itself runs inside a job that
  forbids nesting), only a run that is interrupted (stopped or timed out) kills
  the process tree.
- **An open tab cannot run an old recipe.** A tab keeps the recipe it was opened
  with. Once the recipe is replaced, or removed and imported again, the tab says
  so in its header and refuses to run, and opening the tool opens a new tab
  instead of focusing that one.
- **The pack stays off until it was chosen.** The Recipes pack counts as off until
  the one-time step has run or you turned the switch on and saved, so a step that
  failed or was skipped (a settings file written by a newer Heimdall is never
  written to) does not leave it on. A settings file imported from Settings does
  not turn it on either: a file written by an earlier version leaves it as it
  is, and only a file written by this version where the pack was on turns it
  on, with a line of its own in the confirmation ("Turns the Recipes pack on").
- **The policy.** An organisation can forbid the pack (see below).

**What does not protect you.**

- These rules do not make a recipe safe. A legitimate program can do harm through
  its own arguments: one that deletes, writes, reads or sends what it is told
  to, does so. A program started by a recipe has the rights and the environment
  of Heimdall, and an empty working folder does not keep it from using any other
  folder. Nothing here is a sandbox.
- A recipe can name any file the user can reach, including through a relative
  path with `..`. Nothing confines a program to its run folder.
- The seat belt is a list of names. A copy of an interpreter under another name,
  with a neutral original file name in its version resource, passes it, and so
  does a program that is harmless by name but runs other programs or loads
  plug-ins.
- There is a window between the check of the program and its start: a file
  replaced in that interval is not detected. The comparison made before each run
  is a comparison of paths, so a program replaced in place, at the same path, is
  not detected either. A folder you add to the list is trusted as a whole only
  for programs that lie outside Windows and Program Files: any `.exe` under it
  there can be named by a recipe, while a program under Windows or Program Files
  is always checked, whatever folders you added.
- The permission check of the Windows and Program Files folders reads the
  permission rules and the owner of the file and of each folder above it. An
  untrusted owner is refused whatever the rules say: an OWNER RIGHTS entry,
  which can limit what an owner may do, is deliberately not honoured. It is a
  limit on the obvious case, not a guarantee that nobody else can change the
  file.
- Only the system, the Administrators of the machine and the installer service
  are trusted. A right to write, or an owner, that is any other account makes
  the program refused, the administrators of a domain or a service account
  included, even when an administrator set it up that way on purpose. A deny
  entry of the permission list is not counted, and a conditional entry is read
  as if its condition always held (measured on a test file). Both err on the
  side of refusing.
- The look-alike check asks how the ANSI code page of the machine would write
  each character, and it always refuses the block of half-width and full-width
  forms. On a machine whose ANSI code page uses two bytes for some characters
  (Japanese, Chinese, Korean), it does not see a character whose second byte is
  the byte of a backslash, which a program that reads its command line byte by
  byte may take for one, and the half-width katakana and Hangul of that block
  are refused on every machine. Nothing in this version adapts the rule to those
  code pages.
- The folder rule reads the text of the path, and then the path it leads to once
  links and mapped drives are followed: a mapped network drive, a link to a
  share and a link to a drive root are refused. A folder whose final form cannot
  be resolved (it is missing, its drive is absent) is accepted as written, and
  what it leads to is checked again when a program runs.
- Behavior with some unusual path forms (short 8.3 names, substituted drives) has
  not been verified by a real test, and nothing is promised about it. That ending
  a run also ends the processes the program started was verified by one real
  test with a stock program, and nothing is promised beyond it.
- A table shown by a tool is neutralized when it is copied, whether it is the
  output of a recipe as CSV or JSON or the table of an external tool: a cell
  that starts with `=`, `+`, `-`, `@` or a double quote gets a leading
  apostrophe, so a spreadsheet does not read it as a formula, and a tab or a
  line break inside a cell becomes a space. A value that starts with `-` or `+`
  (a negative number, a time offset, a phone number) is therefore copied with a
  leading apostrophe and pastes as text. Plain text output is copied as it is.
- A recipe is not signed and not reviewed by the project. The author is a text
  the file claims, and Heimdall shows it as unverified. A community catalogue is
  not part of this version: a recipe comes from whoever gives you the file.

**Machine policy.** The value `ToolPackRecipes`, a `REG_DWORD` under
`HKLM\Software\Policies\Heimdall` (the key of the other pack values, see
[Tool packs machine policy](#tool-packs-machine-policy)), set to `0`, forbids
the pack. The pack's switch then shows off and disabled with a line saying it is
set by the organisation's policy; recipe tools are not listed and are refused
when something tries to run them; the **Import a recipe...** button is disabled
and an import is refused. The recipe files already on disk stay where they are.
As for the other packs, this is a convenience for administrators, not a security
boundary.

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

Creating a new file is a separate commit. A transfer whose conflict check found the
name free, and for which no replacement was consented to, never goes through the
replacing rename. SFTP commits it with the protocol version 3 rename
(`RenameFile` with `isPosix: false`, `SSH_FXP_RENAME`), which the protocol specifies
to fail on an existing name; measured on OpenSSH 10.2p1, it leaves an existing file
intact and publishes onto a free name. A failed rename is classified by a probe
afterwards: an occupied destination becomes a typed collision, and anything else
propagates the rename failure. This needs no exec channel, so it works on accounts
restricted to `internal-sftp`, on Windows OpenSSH and behind SFTP gateways, and it
rests on the server honouring the version 3 semantics. FTP checks the destination
again immediately before moving the uploaded file into place, refuses an occupied
one and never moves it aside; a file created between that check and the move can
still be overwritten, because FTP offers no rename that refuses an existing name.
Pasting between two endpoints keeps the stricter server-side publish described
under "Cross-endpoint clipboard paste".

SFTP remote copy either reserves the destination exclusively or is refused. The
copy runs as a server-side command over an SSH exec channel pinned to the host key
resolved at connect time, and that command is what makes the no-overwrite contract
real: a file is staged then published with a hard link, a directory root is
reserved with `mkdir` without `-p`, and both fail if the destination already
exists. If the command cannot be used, the copy is refused and the reason is
reported; there is no second route.

The SFTP browser's commands on the exec channel are read by the account's login
shell, which may be csh, tcsh, zsh or fish. They set the C locale with `env LC_ALL=C`
rather than a leading `LC_ALL=C`, the copy and publish scripts run through `sh -c` with
the paths passed as parameters, and a `!` in a path is written so that csh and tcsh
cannot expand it.

The copy command runs under `umask 077`, so the staging file is private to the account
from its creation until `cp -p` applies the source's mode at the end; a 0600 source
used to be readable by every local user of the server for the whole copy. A
directory root whose `mkdir` fails ends the command at once, so the cleanup of a
failed copy can only remove a tree this command created, never one another client
had just created under the same name. The publish link is `ln -T`, which refuses a
destination that has become a directory instead of linking inside it and reporting
success; an `ln` without `-T` (an older BusyBox) rejects the option, and the copy is
refused.

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
under the `.bak` sibling. Heimdall does not resume from that state: a
`<name>.<guid>.bak` sibling left by an interrupted commit is the previous
file, to be renamed back by hand.

Heimdall does now name it. Listing a directory reports each `.bak` sibling it
recognises as its own, once per connection, saying which of two things it is: a
copy whose original is missing from the listing, which holds the only contents
of the file that was being replaced, or a copy whose original is present, which
holds the version that was replaced and should have been deleted afterwards.
The report is only a report. Heimdall does not move the file back, because that
rename would race another client committing a replacement at the same instant -
which is the very situation an orphaned copy suggests - and the user is the one
who can tell the two apart.

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
validates the target host and port before connect. If TLS is disabled,
`ConnectionResult.Warning` carries a localized non-blocking cleartext warning
to the status surface, for anonymous sessions too: no credential travels then,
but every file still does, in clear. Explicit FTPS sessions get no warning.
The TLS floor is pinned to 1.2 and 1.3. Explicit FTPS enables TLS for the
control channel and FluentFTP `DataConnectionEncryption`, so file transfers
use an encrypted data channel.

The FTPS control-channel certificate is validated and pinned by Heimdall.
A pin the system validated is refused, without a prompt, when its chain's
revocation status can no longer be determined: the one adversary a pin exists
against holds a stolen, since-revoked key and blocks OCSP and CRL. A
self-signed pin the user confirmed never had a checkable status and keeps its
behaviour. A refused pin is reported as such, in different words from a Reject
click. The data channel has a third-party limitation in FluentFTP 54.2.0:
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

A session can name the one agent key it offers (`ServerProfileDto.SshAgentKeyFingerprint`,
a `SHA256:` fingerprint). The key is looked up in the agents Heimdall is set to use before
any tunnel or prompt, with a bounded wait off the calling thread; an agent that does not
answer in time holds nothing that can be offered. A key found in no agent is refused with
no fallback to the other keys. The choice limits agent keys only: it does not affect a
stored password, a key file or keyboard-interactive sign-in. Gateway hops and tunnels do not
receive it and still offer every agent key. The value is local to the machine: the shared
profile sanitizer drops it, every import clears it and the settings export strips it. With
agent forwarding on, the remote host can still use every key of the agent, whatever key the
session names. On the Plink path (agent forwarding with Pageant, retry after an embedded
client failure) the key is handed to Plink as a one-line public key file, only when the key
is in Pageant, of a type Plink can parse, no key file is configured and Plink is the
bundled build; otherwise the session is refused. External PuTTY mode with a chosen key is
refused.

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

**An empty password answer writes no file.** When a session has a user name but no stored password and no key, the password prompt can be answered with nothing, to sign in with Kerberos or a Pageant key. Heimdall then writes no password file and starts Plink in batch mode (`-batch`), so no prompt can wait on a hidden console and no password is sent. If the server accepts neither method, Plink exits at once and the terminal says why.

Process exit remains the backstop, every path that frees the file goes through one gate so the
deletion runs exactly once, and a launch that fails or is cancelled releases on the spot.

### Imported profiles that run something

A profile from a file can start a program on this computer (local shell executable, arguments, working directory, elevation; a web console's external program; a Citrix ICA file) or type post-connect commands into a session. Import sets `ExecutionConfirmed` to false, and while it is false Heimdall lists every value it would run, the post-connect commands included, as the dialog notice does (for a session whose only such value is a Citrix ICA file, it names that file in one sentence), and asks before running it. Accepting at connect saves the approval, and the default answer, which is also the one taken while Heimdall is closing, is "Do not run". Editing the profile does not change that: the session dialog shows a notice above its tabs listing every such value, the post-connect steps included even for a protocol whose dialog does not show them, and the profile is marked trusted only by the notice's "Allow to run" button followed by Save, or by accepting that prompt. A profile created in the dialog is trusted.

### Subprocess argument hardening

`PlinkTunnelRunner` builds the plink argument list via
`ProcessStartInfo.ArgumentList` (no string concatenation), and the stderr
drain task is **joined** at `Stop()` time before `Process.Kill()`, so the
background reader cannot outlive the pipe it was attached to. The drain
sanitizer (`SanitizeForLog`) redacts password / passphrase single-token
assignments, token / bearer assignments to end-of-line, and `-pw` / `-pwfile`
flags so an unexpected stderr echo from plink cannot leak credentials into
the application log.

### In-app update source and relaunch

The repository the updater checks is a compile-time constant (`UpdateSource`),
not a setting. It used to be a pair of strings in the user's `settings.json`,
unvalidated and shown in no interface: anyone able to write that file could
point the updater at a repository of their own, and the SHA-256 check passed
because it is computed against that repository's own checksum file. SHA-256 is
an integrity control against a corrupt transfer, not an authenticity control;
until the installer is signed, the source is the only authenticity control, so
it is pinned. Asset and checksum URLs taken from the release JSON are refused
unless they are `https` on `github.com`, `githubusercontent.com` or a subdomain
of either.

The detached relauncher runs under Windows PowerShell through an in-memory
bootstrap that re-reads the script under a deny-write handle and verifies its
pinned SHA-256 before executing it. The bootstrap has its own failure path: a
tampered or unparseable script is refused, recorded as a preparation failure,
and the application is relaunched. The script refuses to run the installer
while the application is still running (an installer started over a live
process force-closes it mid-session), passes the installer the directory of
the running copy, and records a declined elevation prompt as a cancellation
rather than a failure. An installer is only offered to a copy the installer
registered in place; a portable archive or an MSI deployment is shown the
release page instead.

Known residual risk, recorded rather than fixed: on the elevated path the
installer runs as administrator from a staging directory the unprivileged
user can write. The installer file itself is held under a deny-write lease
from verification through launch, so it cannot be swapped; a sibling file
planted beside it could still be resolved by the installer's own loader.

The framing this paragraph used to end on was wrong and is corrected here.
"UAC is not a security boundary" does not settle it, because elevation is
chosen only when the install directory is not writable - which is the
per-machine install. When the person running Heimdall is not an administrator,
the consent they see is an over-the-shoulder credential prompt and the
installer runs as a *different, more privileged account*. That is a boundary
Microsoft does service.

Two remedies were measured on 2026-09-07 and both are recorded because their
costs are the reason this stays a residual risk rather than a fix. A DACL
granting only Administrators and SYSTEM is rewritten by its own unprivileged
creator, who owns the directory and keeps `WRITE_DAC` (measured twice
unelevated, against a passing positive control). A `Deny` entry on `OWNER
RIGHTS` does hold - and its real cost is not that it can be bypassed but that
it takes away the updater's own ability to remove the staging directory and
delete its script in the `finally`, which would trade a hardening item for a
directory left behind after every update.

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

### Opening a link outside Heimdall

Three surfaces hand a URL to the shell: the update banner, from a GitHub release feed; the
terminal, when a remote host prints an `open-url` message; and the diagram editor, when a cell in
a possibly imported file carries a link. All three end at `Process.Start` with
`UseShellExecute = true`, which is the call that lets Windows pick a handler by scheme, so a
`javascript:` or a `file:` reaching it is a handler Heimdall never chose.

One decision stands in front of all three. `ExternalUrlPolicy.TryResolveLaunchable` parses the
string as an **absolute** URI, requires the scheme to be `http` or `https`, and hands back the
parsed absolute form. The shell receives that resolved string, never the text that arrived. Two
consequences worth stating: a relative or malformed URL is refused before parsing succeeds, and
the scheme comparison is ordinal because `Uri` has already lowercased what it parsed.

**The terminal surface is the one that matters most.** Its URL comes from whatever the machine you
are connected to chose to print, so it is the only one of the three whose input an attacker
selects. It was also the surface furthest from anyone's attention: the barrier was found while
looking at the update banner.

The decision used to be spelled three times, once per site, and the class comment described that
duplication as reusing "the canonical open-url pattern from the views". Repairing one copy would
have left the other two, which is the failure this repository has met more than once. The three
sites now call the one predicate, and each call is pinned by a guard that reads the source,
because none of the three is reachable from a test.

What this does not do: it decides on the scheme alone. A well-formed `https` URL to a hostile host
is launched, as it would be from any browser. Nothing here inspects the destination.

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

### Masked copy of terminal output

**Copy output, secrets masked** (Ctrl+Shift+M) copies the text of a terminal session after pattern-based masking, so it can be pasted into a ticket or a chat. It runs entirely on the machine: no network, no outside service, nothing written to disk. It fails closed: if a rule times out, nothing is copied, and the text reaches the clipboard only when you press **Copy** in the preview. The rules are patterns and miss what they do not describe (user names, paths, a password typed where no prompt precedes it); session logs are not masked.

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
  `dotnet format --verify-no-changes`, the blocking test lane (the suite minus
  the cases marked `CIUnstable` or `RequiresDesktop`, which run in two further
  lanes that report their failures without turning the run red), JSON locale
  parity (every catalogue holds exactly the English key set, currently 6,700+
  keys each), and an informational `dotnet list package --vulnerable` scan.
- Dependency scan for manual review: `dotnet list Heimdall.slnx package
  --vulnerable --include-transitive`. CI emits warnings but does not gate on
  vulnerability results, since advisories occasionally include false
  positives or entries without an upgrade path.

