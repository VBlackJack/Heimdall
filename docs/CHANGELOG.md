<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Changelog

All notable changes to Heimdall are documented in this file.

## 2026-08-17: a failed migration no longer leaves half of itself behind

- **Importing a legacy installation now commits settings and servers as one unit** (UXG-011). The two
  files were written in sequence, and the import reported the settings step as done the moment it
  finished. A failure on the servers step therefore returned "migration failed" while settings were
  already durably replaced, the running application had already been told about the new values, and the
  result still claimed the settings had been imported.
- **Nothing is published until both writes are durable.** On failure the runtime configuration and its
  change notification are untouched, because neither ever happened, and both files are put back to the
  exact bytes they had, or deleted again if they did not exist beforehand. Restoration writes the
  captured bytes rather than a re-serialisation of them: the existing atomic writer takes a string and
  so carries no byte-identity contract, which a restoration needs directly.
- **If a restoration itself fails**, the other is still attempted and the error says recovery was
  incomplete instead of claiming the previous state was put back.
- **An empty legacy inventory now empties a populated one.** It was previously treated as "nothing to
  do", which silently kept servers the migration was supposed to replace.
- **A settings change notification no longer stops at the first subscriber that throws.** Each
  subscriber receives its own copy, so one that modifies what it was given cannot corrupt the next, and
  a failing subscriber is logged and skipped rather than turning a completed write into a failure.
- **Limit, stated plainly**: this is atomicity against failures handled inside the process. There is no
  journal or recovery protocol, so a system crash between the two file replacements is not covered.

## 2026-08-17: an entry nobody can classify is no longer treated as a file

- **An indeterminate remote type is now explicit** (SFTP-016). When every type test failed, the SFTP
  listing logged the fact and then returned the regular-file kind, so an object nobody could classify
  was transferable like any ordinary file. There is now a distinct kind for it, and the regular-file
  value no longer doubles as "or we could not tell".
- **The unclassifiable kind is the zero value.** An uninitialised value, or a mapper that fell through
  without classifying, now yields the non-transferable kind instead of looking like a plain file. A cast
  from an out-of-range integer is a different case and keeps its own numeric value: it is not converted
  into anything. It is refused because the upload inventory gained a default arm that treats any value it
  does not enumerate as unsupported, and because the SFTP upload guard already refused anything outside
  its known list.
- **FTP was mapping two different situations to "file".** A listing that carries an explicit type
  character this build does not recognise is now unclassifiable, and so is an object type the library
  reports that we cannot interpret. A mode-only permission string such as `rw-r--r--` carries no type
  character at all and is unchanged: it stays a regular file, because reading its first character as a
  type would classify a plain file by whichever permission bit happened to come first.
- **The paths that refuse it, precisely.** The application orchestration excludes it from the
  transferable inventory (upload destinations, download, cross-endpoint paste, duplicate); the transfer
  tree planner refuses it; and the SFTP upload guard refuses it with its typed exception. The upload
  inventory also gained a default arm, so a value it does not enumerate is unsupported rather than
  silently absent from both buckets.
- **What is still not guarded**: `FtpBrowser`'s direct upload API performs no destination-type check at
  all. That has always been true and is equally true for links, pipes, sockets and devices; it is a
  separate defect and this entry does not close it.
- **It is visible.** The listing shows a distinct warning glyph, never the ordinary file icon, and the
  properties dialog names the type.
- **Renaming stays available**, as it does for a pipe: renaming a path moves a name and does not read
  or transfer any content.

## 2026-08-17: a cross-endpoint paste can no longer overwrite what it did not see

- **Pasting between two remote endpoints no longer replaces existing files** (P1). Destination names
  were resolved from the pane's cached listing while the transfer itself used a plain upload, which
  truncates whatever sits at the destination. Anything created since the last refresh was invisible
  to the naming step and was overwritten without a prompt.
- **Every node is now reserved exclusively.** A file is staged and published with a hard link; a
  directory is created with `mkdir` without `-p`. Both fail when the name is taken, and the server
  decides, not a client-side check. The merge tolerance that let a paste continue into an existing
  destination directory is gone: merging is how the collision simply moved one level down.
- **FTP refuses before mutating anything.** It has no commit-time primitive that fails when the
  destination exists, so it does not advertise the capability and the paste stops before any
  directory is created or any byte is sent, rather than pretending to a guarantee it cannot keep.
- **Two different FTP servers are no longer mistaken for one.** The clipboard's endpoint identity was
  derived from a type test on the browser, which stopped matching once the operations-log decorator
  wrapped it: every FTP pane got the same empty key, so a paste between two FTP hosts was routed to
  the same-endpoint path and bypassed the gate entirely. Identity now travels through a seam the
  decorators carry, and an identity that cannot be determined is never treated as a match, so an
  unknown endpoint fails closed on the cross-endpoint path instead of silently overwriting.
- **Listings no longer carry authority.** The destination is read live, but only to pick a free
  name; a listing is stale the moment it returns and no safety decision rests on it.
- **A cut keeps its source unless the move is confirmed.** A collision, a cancellation or an
  unconfirmed outcome all keep the source and the clipboard entry, and Heimdall asks for the
  destination to be reloaded so that what landed becomes visible when the session and the listing are
  still available. A reload that fails never turns an unconfirmed outcome into a success.
- **Limits, stated plainly.** Atomicity is per node, not transactional across a tree, so an
  interrupted paste can leave a partial tree rather than risk a destructive cleanup. Provenance is
  not guaranteed against a malicious actor holding write permission in the same directory. No real
  SFTP server is exercised by the test suite. See `docs/SECURITY.md`.

## 2026-08-17: a server-side copy is the only copy, on every protocol

- **The SFTP copy no longer falls back to a transfer that could overwrite** (SFTP-013). The copy is
  performed by a server-side command over a host-key-pinned exec channel, and that command is what
  makes the no-overwrite contract real: a file is staged then published with a hard link, a directory
  root is reserved with `mkdir` without `-p`, and both fail when the destination already exists.

  When that command could not be used, the copy used to fall back to downloading the file and
  republishing it through a plain rename. A server whose rename silently overwrites the destination
  made that path succeed with no exception and no warning, so the contract the interface documents was
  not honoured there. That fallback is now removed rather than annotated: the copy is refused, with a
  reason of its own, distinct from the FTP refusal because SFTP does have a safe commit and what
  failed was reaching it.

  The attempt now reports a named outcome instead of a boolean, so the caller can tell a decline
  (no pinned context, non-zero exit, internal timeout, host-key rejection) from a transport failure.

- **Cancelling a copy is reported as a cancellation, not as a refusal.** The exec runner tears the
  SSH client down to cancel, which surfaces as an `SshException` rather than a cancellation, so the
  classification now reconciles against the caller's token before deciding. Without that, pressing
  cancel would have been reported as "this server cannot copy safely".

- **The containment guard survived the removal.** Refusing to copy a directory into its own subtree
  was enforced only inside the client-side walk that this change deletes, and a successful server-side
  copy already bypassed it. It is now applied by the caller before any probe and before any command,
  so the check covers every path rather than only the slow one.

  Its oracles moved with it, including the case that a sibling merely sharing a textual prefix
  (`/srv/database` against `/srv/data`) is not inside the source and must still be allowed.

- Documentation reconciled in the same change: `docs/SECURITY.md` described the SFTP copy as a
  best-effort publish that "must not be relied upon", while the FTP refusal added the day before told
  users to switch to SFTP. Both statements are corrected, and `README.md` no longer advertises a
  roundtrip fallback.

## 2026-08-17: FTP no longer offers a copy it cannot make safe

- **Copying on the server is refused over FTP** (SFTP-013). The copy contract is that an existing
  destination is never overwritten. FTP cannot honour it: every publish FluentFTP offers reduces to
  a client-side existence check followed by a plain rename, and RFC 959 says nothing about what a
  rename onto an existing destination does, so a server that silently overwrites is conformant.

  Until now the FTP copy ran through the ordinary upload, whose commit moves an existing
  destination aside, replaces it and reports success. The planner's single pre-check was the only
  thing between a concurrent creation and data loss, so any way that check could miss (a race, a
  stale listing, a server whose existence probe false-negatives) became a silently destroyed file.
  `FtpBrowser.CopyAsync` now always refuses, with a localized reason pointing at SFTP, whose
  `SSH_FXP_RENAME` is specified to fail when the target exists.

  Refusing was chosen over mirroring the SFTP publish on top of a check-then-rename, because that
  would have named a method after a guarantee the protocol does not provide.

  Uploading over FTP is unchanged and still replaces an existing destination when asked: it never
  promised otherwise, and the user chooses replacement explicitly. FTP cut and move also still
  issue a plain rename and may overwrite; that exposure is now documented rather than left implied
  to be safe.

  Corrections to earlier entries that promised more than the code delivered. The 2026-08-03 claim
  that "a copy can no longer overwrite a destination that appeared between check and write" held
  for SFTP only. The 2026-06-29 entry announcing a "no-overwrite `CopyAsync`" with a "recursive
  roundtrip for FTP" was never true of the FTP half. The interface contract, the planner
  documentation, `README.md` and `docs/SECURITY.md` are corrected in this release.

## 2026-08-17: A selected link is no longer followed out of the upload selection

- **An upload root that is a link is refused instead of followed** (SFTP-015). The upload
  planner decided a selected path's type with `Directory.Exists` / `File.Exists`, and both
  follow reparse points. A junction or symbolic link chosen as an upload source was therefore
  walked, and the content of its *target* was uploaded even though that target lies outside the
  selection. A selected file link uploaded the target's bytes the same way.

  Reparse roots are now refused before entering the plan, on the same fail-closed rule already
  applied to reparse points found inside a selected tree: the path joins the skipped-links
  count, the refusal is logged, and nothing is created or transferred remotely. Symbolic links,
  junctions, mount points and other reparse tags are treated alike; no tag-level discrimination
  is introduced. This deliberately reverses the earlier split, under which a link selected as a
  root was accepted and only links found among the children were rejected.

  A source deleted between the existence probe and the classification is unchanged: it stays an
  ignored disappearance and is not counted as a refused link.

  The skipped-links warning described only links "inside the selected upload tree", which is not
  true of a refused root. Both locales now cover links selected as upload sources as well as
  links found inside the selection.

  Two consequences are stated rather than left to be discovered. A reparse point is not only a
  link: Windows sets the same attribute on Files-On-Demand placeholders and deduplicated files,
  so a dehydrated cloud file picked as an upload source is now refused and counted among the
  skipped links. And the local *paste* planner keeps its own root exemption, so a root link is
  still traversed there; that asymmetry is deliberate scope, left to a separate lot rather than
  changed here, and it is now the only place the old root policy survives.

## 2026-08-15: Remote transfer integrity, session closing and window layout (v2026.081501)

The largest release of the cycle: a dependency security update, the remote file
transfer paths reworked so a replacement can no longer damage a destination
silently, a single close-confirmation contract across every way a session can be
closed, and window minimums that stop dialogs from opening larger than the screen.

### Security

- **SSH.NET updated to 2026.0.0** (`0a480cee`). Closes advisory
  GHSA-q939-rpr3-3284, a high-severity issue affecting the 2025.1.0 release that
  shipped in v2026.081000. Consequence worth naming: restoring the v2026.081000
  tag now fails the dependency audit, which is the intended behaviour.
- **`System.Security.Cryptography.ProtectedData` updated to 10.0.11**
  (`b6361cd3`). The package is a direct reference of `Heimdall.Core` and a
  transitive one of eight other projects, so all nine consumer lock files were
  regenerated with it; updating Core's alone fails `restore --locked-mode` with
  `NU1004`.
- **A valid host name can no longer be refused because the machine was busy**
  (`45f89c7f`). Every validation pattern ran on a backtracking engine with a
  250 ms match deadline, and a deadline that elapses was treated as "invalid".
  On a loaded machine that rejects perfectly ordinary input: CI was observed
  refusing `gateway.example.com`. The patterns now run on the non-backtracking
  engine with no deadline at all, which bounds a match by the length of the
  input instead of by how busy the machine is. Injection-shaped values,
  over-long labels and over-long names are refused exactly as before, by not
  matching rather than by running out of time.
- **A Citrix launch token that is not vault-protected is rejected** (`9425fd15`,
  `819982a3`). Plaintext tokens are refused rather than accepted, and tokens
  already persisted are reconciled against the vault state.
- **StoreFront session events are redacted** (`ff4ce12f`).
- **Pageant requests are bounded and their fields validated** (`eeb16399`,
  `1be51780`). A malformed or oversized agent response can no longer drive an
  unbounded read.
- **External RDP artifacts are always cleaned up** (`66089c65`) and the native
  password is reset after disconnect (`0b253ee5`).

### Fixed - remote file transfers (SFTP, FTP)

- **A replacement that cannot be performed safely is refused rather than
  performed badly** (`16978424`). Where the destination could previously be
  swapped for an upload whose permissions did not match, the commit is now
  refused. Related: an upload must land with the exact mode of the file it
  replaces (`710493d0`).
- **A staged upload stays private while it holds content** (`27cc6b72`). The
  temporary file is created empty, tightened to owner-only, and that tightening
  is read back before the first byte is written; a server that ignores the
  request stops the upload instead of receiving the content anyway. The published
  file keeps the destination's own mode, or the server's default when the file is
  new.
- **A privileged replacement preserves the replaced file's extended metadata**
  (`15cf8bf4`). Ownership, mode, timestamps and extended attributes are carried
  across explicitly, and the operation fails closed if they cannot be.
- **FTP now states what it does not preserve** (`dc9b06dd`). Replacing an
  existing file over FTP or FTPS raises a single warning covering both the
  missing atomicity guarantee and the loss of the previous file's owner,
  permissions, timestamps, ACLs, extended attributes and capabilities. The
  warning is raised only after the replacement has actually happened, so a failed
  backup move or a restored commit no longer reports a loss that did not occur.
- **Sudo downloads are streamed** (`8932a282`) instead of being held in memory,
  and inline editor downloads are bounded (`5801cce4`).
- **The inline editor keeps its file's encoding and ownership** (`4d95cf5b`,
  `04c1b1f1`), and a revisioned save is awaited before the editor moves on
  (`6da4a11b`).
- **Remote paste and duplicate go through the transfer coordinator**
  (`ccfd66cb`), the server-side copy fast path can be cancelled (`e77c2381`), and
  a recursive upload plan is built asynchronously and can be cancelled
  (`5e6eea53`).
- **FTP disconnects no longer block the UI** (`9f51c8e6`).

### Fixed - closing sessions

- **Every close path asks the same question** (`3dcf3e40`, `7cfa548a`,
  `e412a5de`, `b18ca9b8`, `4b274ae9`). Closing a tab, closing others, closing to
  the right, closing a split pane and closing the window all run through one
  close-guard contract, so a session with a transfer in flight or an unsaved
  remote editor is no longer torn down without asking. Consent is tied to the
  work that was actually running when the question was asked: work started while
  the prompt was open is re-examined instead of being covered by a stale answer.
- **A grouped close asks once, not once per tab** (`9d0848a3`), the duplicate
  close action is gone (`e77f0b2c`), and exiting with connected sessions is
  confirmed (`5336a617`).
- **Declining a floating window close restores it** (`725f7737`), and duplicate
  tabs for the same session are merged (`674ee695`).

### Fixed - window and toolbar layout

- **Dialogs no longer demand more room than the screen has** (`c8dfd907`,
  `f2850c19`, `9cfe8bd0`, `94f8b014`). Window minimums are held within the
  display's working area. The clamp is reversible, so restoring a window on a
  larger display gives its full declared minimum back, and the declared value is
  captured on first use rather than at opt-in, which previously depended on the
  order of attributes in the layout.
- **The main toolbar wraps as the window narrows** (`b12193cd`).
- **The sidebar keeps its state across fullscreen** (`546006d2`, `a665c502`) and
  honours its action minimum (`80ac28b3`).
- **Restored window bounds are checked against real monitors** (`44f1e63e`).

### Fixed - session tree

- **Keyboard multi-selection** (`33c4b213`, `ee8f8d4e`, `b5dd5de3`) and its
  exposure to assistive technology (`a8810ef7`).
- **Selection survives a refresh of connected servers** (`58c82c1c`), hidden
  selections are purged on collapse (`71a03100`), and native selection is cleared
  after a Ctrl toggle (`e9fe2fc9`).
- **Deleting a filtered group discloses what it will remove** (`e1b3b169`),
  deleted folder metadata is purged (`ba4a5a95`), and expansion state is flushed
  on close (`9c115c2f`).
- **Inline rename commits on focus loss** (`212a3b98`) and a double click on a
  non-server row does nothing (`935d58d3`).

### Fixed - WinRM, SSH and terminals

- **WinRM session materialization is transactional** (`8c23fc0c`) with split
  state isolated and initialized (`d6df9db1`, `d25b1c1e`), exited terminal
  sessions cleaned up (`20ba88e7`) and bootstrap cleaned on a forced close
  (`fbb8df99`).
- **WinRM refuses configurations that cannot work**: an HTTPS gateway assignment
  (`2b3addbd`), an invalid identity mode (`682cde51`), a password bulk without a
  username (`b48b4b22`); implicit TLS ports are resolved (`356d6cea`), the target
  host is canonicalized (`bf137219`) and tunneled endpoints are preflighted
  (`0924dfae`).
- **SSH failure reporting says what actually failed** (`a3cfd921`, `6e50ebab`,
  `95c34b0b`, `59aa34fd`, `8916a2e0`, `80cd619a`, `f503b018`, `4b5b6442`). A
  keyboard-interactive denial is no longer reported as a rejected key, and a port
  substring no longer reads as "port in use".
- **Reusable tunnels are acquired atomically** (`9b2cfe80`), queued reconnect
  ticks are invalidated (`305f0e85`), reconnect attempts are chained
  (`c97ae3bd`), and Plink processes are retained until exit (`ded1246a`).
- **Explicit default ports are preserved** (`260a9263`) and hashed known-hosts
  entries match on custom ports (`7a30b586`).

### Fixed - accessibility, localization and import

- **Duplicate session tabs get distinguishable accessible names** (`3f8e3016`),
  the server accessible name follows the sidebar dot priority (`0e23e440`), and
  global status changes are announced (`882cbba3`).
- **Clipboard transfer statuses and the transfer progress line are localized**
  (`08983ba4`, `abe1dcd8`), the shell status text refreshes on locale change
  (`35dd6d5c`), and 52 orphan locale keys are removed with a prefix guard
  (`f8a571e0`).
- **Legacy profile migration preserves fields, preflights the configuration and
  reports what it skipped** (`d1c4ce08`, `350024ec`, `493e5480`); a declined
  offer is persisted (`0d22ab2e`). Imported connection types are canonicalized
  (`4e6a7862`) and NLA is preserved on RDP import (`bdbb12e4`).

### Fixed - RDP and Citrix display

- **External RDP auto sizing uses physical pixels** (`f60422b9`), so a display
  scaled above 100 % no longer receives a session sized in logical units.
- **Dropped DPI changes are replayed and SmartSizing is stated on every
  resolution choice** (`d7ec559b`).
- **A Citrix session is announced as connected only after the Win32 embedding is
  verified** (`b0448bd3`), with the visible-window baseline captured before the
  launcher starts (`1f56ea7d`).

### Added

- **Cross-cutting close-guard contract** (`src/Heimdall.App/Services/CloseGuard/`)
  with a pane close arbiter, usable by any tool or protocol view without either
  depending on the other.

### Internal

- **Two probe tests no longer measure a port they have released** (`ff8ce89d`,
  `540a817a`). Both obtained a "closed" port by binding a listener and stopping
  it, after which the port belongs to whatever asks for one next; one of them
  was observed reporting a port as reachable for that reason. The port is now
  held, bound and never listened on, for the whole probe, and both tests
  re-assert it is still held after the probe returns.

### Notes

- Test suite: to 9206 blocking tests, 0 skipped. The figure at v2026.081000 was
  not re-measured: restoring that tag now fails the dependency audit because of
  the SSH.NET advisory this release closes, and the audit gate was not bypassed
  to obtain a number.
- Informational lanes are non-blocking. On candidate PR #111, `CIUnstable`
  measured 25 of 26, with `TextDiffSmokeTests.HelpButton_TogglesHelpPanel`
  failing; `RequiresDesktop` measured 104 of 105, with the known session-tree UI
  Automation failure. These are measurements of that run, not guarantees for
  later commits.

## 2026-08-10: Gateway validation failures stop the connection (v2026.081000)

Recorded after the fact from `docs/release-notes/v2026.081000.md`. No test-suite
figure is given: it was not measured at the time and cannot be measured now,
because restoring that tag fails the dependency audit closed by v2026.081501.

### Fixed

- **A profile whose RD Gateway name fails validation no longer falls back to a
  direct connection.** Heimdall stops the connection instead of generating a
  session file with no gateway, and says so in the session status bar. Embedded
  sessions were already protected; the external path was not. Consequence: a
  profile with an invalid gateway that appeared to work will now fail, which
  exposes a connection that was not going through the expected gateway.
- **The gateway verification diagnostic names the settings that diverge**
  instead of reporting a difference without locating it. This separates cases
  that are not handled the same way: a gateway name rewritten by group policy
  and a credential source imposed by the system.

## 2026-08-09: Attested tunnels, gateways and temp-file cleanup (v2026.080901)

Recorded after the fact from `docs/release-notes/v2026.080901.md`. No test-suite
figure is given: it was not measured at the time.

### Fixed

- **An SSH tunnel is only reported as established once its local port is proven
  to belong to the Plink process just launched**, matched by exact process id.
  If the port is held by another process, if nothing is listening, or if the
  system cannot decide, the tunnel is stopped rather than presented as up.
  Consequence: the configured path must point directly at `plink.exe`; a script
  or intermediate launcher cannot be attested and is refused.
- **A declared RD Gateway is verified before use**: presence, write, read-back
  and comparison. Any one of those failing stops the connection. Without it a
  silent failure could let the session go straight to the target with no
  gateway.
- **Cleanup of secret-bearing temporary files is rescheduled while any file
  remains** (Plink password file, WinRM bootstrap script), instead of stopping
  after a single sweep. A file not yet eligible for deletion on the first pass
  is picked up on the next.
- **The last log lines revealing the state of a credential operation on the
  embedded RDP path are removed**, with a guard test against reintroduction.

## 2026-08-09: Windows credential ownership and credential-free logs (v2026.080900)

### Fixed

- **StoreFront URL validation runs before the first log line** (`5d5c6081`). The Citrix handler
  validates the StoreFront URL before any `_logInfo` call and rejects a URL carrying
  `Uri.UserInfo`; log lines are reduced to `scheme://host[:port]/path`. Because the validation
  refuses userinfo outright, no future log line of that handler can leak an identifier - an
  impossibility, not an absence. Closes RDP-002.
- **Heimdall no longer overwrites or deletes a Windows credential it cannot prove is its own**
  (`8a8d4d51`). Ownership is carried by a `Heimdall:RDP:<GUID>` marker in the entry's `Comment`.
  The write path matches the marker non-exactly, so an entry left by an earlier Heimdall launch
  stays replaceable; the delete path requires an exact match, so only the current launch can
  erase its own entry. The `GENERIC` credential type is never deleted. Accepted consequence: an
  entry left by an older version survives - surviving beats destroying an entry that is not ours.
  Closes RDP-001 and RDP-011.
- **A cached Citrix launch token is protected at import** (`e3636214`). Importing a profile that
  carries a cached launch token resolves against the vault state in three explicit branches, with
  no silent drop. Closes the admission path of RDP-003; the lifecycle reconciliation residual
  stays open.
- **Credential presence is no longer logged on the RDP paths** (`96e972ad`). Eight emissions
  asserting the presence, injection, success or cleanup of a credential are removed
  (`RdpActiveXHost` 3, `RdpHandler` 4, `EmbeddedRdpView` 1). Six failure diagnostics are kept at
  `Warn` and are covered by tests that assert they are always emitted: an operator must still be
  able to tell a Credential Manager write failure from a launch failure.
  `CredentialManagerHelper.WriteGenericCredential` is removed - it had no caller in production,
  and since `GENERIC` entries are never deleted a future caller would have created entries
  Heimdall could not remove. Closes RDP-024.

### Added

- **Static guard against StoreFront URL leaks in logs**
  (`tests/Heimdall.App.Tests/SensitiveLoggingGuardTests.cs`). A repo-scanning test fails with
  file and line when a log emission reintroduces a StoreFront URL beyond
  `scheme://host[:port]/path`. Empty allowlist; non-vacuity proven by mutation.

### Notes

- Test suite: 8618 to 8648, 0 skipped.

## 2026-08-03: Remote recursive deletion confinement (v2026.080301)

### Fixed

- **Remote recursive deletion is delegated to the server's own `rm`** (`e124001b`). Deleting a
  remote directory now runs `LC_ALL=C rm -r -- <path>` over a pinned SSH exec channel, which
  guarantees symbolic links are never followed - a guarantee the client-side traversal could not
  give. When no exec channel is available, or the shell or `rm` is missing, the recursive deletion
  is refused with a typed reason (exec unavailable, shell or rm unavailable, permission denied,
  command failed) instead of attempted without the guarantee. Files and links keep the direct
  no-follow SFTP path. The client-side recursive traversal is removed. Closes SFTP-017.
- **Refused entries no longer abort the remaining selection** (`4f3a72c5`). A refusal or failure
  on one entry is reported and the safe siblings proceed; one aggregated summary is shown after
  the operation, after the browser refresh. Sudo escalation is preserved and its failure is
  non-blocking. A cross-session cut whose source could not be deleted keeps the transfer and
  warns. Five new localized messages (EN and FR).

### Notes

- Test suite: 8581 to 8618, 0 skipped.

## 2026-08-03: Local transfer tree confinement (v2026.080300)

### Fixed

- **Local upload walks skip links below the selected root** (`487e59f4`). The upload
  classification rejects any reparse point encountered below a selected root - files and
  directories, junctions and symbolic links alike - before the entry enters the transfer plan.
  The selected root itself may still be a link, matching the local paste policy. Skipped entries
  are reported through one aggregated, non-blocking warning after the browser refresh.
  Closes SFTP-015.

  Correction to the two sentences above. The root exemption was itself the open half of
  SFTP-015, so that entry did not close the finding: a link selected as the root stayed
  traversable, and the upload still left the selection through it. The exemption is removed in
  the 2026-08-17 entry, which also ends the claimed alignment with the local paste policy. The
  paste planner keeps its own root exemption for now.
- **Local paste refuses the source tree as destination** (`db7ccd8a`). Pasting a folder into
  itself or one of its descendants is refused per root with one aggregated dialog; the remaining
  pasted roots proceed. The containment check is lexical and case-insensitive, aligned with the
  same-server remote copy rejection. Previously the pre-planned walk produced a finite nested
  snapshot.

### Notes

- Test suite: 8566 to 8581, 0 skipped.

## 2026-08-03: Symbolic links and special entries on remote servers (v2026.080200)

Backfilled entry: the v2026.080200 release commit (`c80807d1`) updated only the version; this
entry documents what it shipped - twelve commits closing the whole of SFTP-016.

> **Correction (2026-08-17).** "Closing the whole of SFTP-016" was wrong. What shipped here
> handled the special types the servers *name* - links, pipes, sockets, devices. An entry whose
> type could not be determined at all still fell back to the regular-file kind and stayed
> transferable. That remainder is closed by the entry below; this paragraph is left in place
> rather than rewritten, because the overstatement is part of the record.

### Fixed

- **Deleting a symbolic link no longer deletes its target**, and a recursive deletion no longer
  traverses links to destroy what they point to.
- **Renaming a symbolic link over SFTP is refused** - the server rename followed the link and
  renamed the target with no way to prevent it; FTP keeps the rename, where it acts on the link
  itself.
- **Editing or replacing a non-regular entry is refused** - a socket or a pipe can no longer be
  overwritten as if it were a file; transfers skip unsupported entries instead of treating them
  as files.
- **The remote browser shows the real nature of each entry**: dedicated icon, type in the tooltip
  and in the Properties window.
- **Same-server copy**: copying a directory into one of its own subdirectories is refused instead
  of looping; a copy can no longer overwrite a destination that appeared between check and write.
- **Non-atomic replacement is signalled** by a non-blocking warning when a server cannot replace a
  file atomically.

### Notes

- Test suite: to 8566, 0 skipped.
- Commits:
  - `cdb48b6f` fix(sftp): stop remote copy from overwriting a destination created after the check
  - `a8348238` feat(sftp): surface non-atomic remote replacement as a non-blocking warning
  - `23c05a76` fix(sftp): delete the symbolic link itself instead of its target
  - `d1ce928e` fix(sftp): stop recursive delete from destroying symlink targets
  - `b7d83ed9` fix(sftp): reject copy destinations inside the source tree
  - `94259429` feat(sftp): carry an explicit remote entry kind through remote listings
  - `39e5bdd0` feat(sftp): keep unsupported remote entries out of transfer paths
  - `1f861ec1` feat(sftp): refuse uploads whose existing destination is not a regular file
  - `ee67d938` fix(sftp): block editing of remote entries that are not regular files
  - `0b4be893` fix(sftp): skip uploads whose destination is not a regular file
  - `2e2e1667` fix(ui): show remote entry kinds truthfully in the SFTP browser
  - `8d664aef` fix(sftp): refuse renaming symbolic links over native SFTP

## 2026-08-01: Destructive remote replacements, and the last of tier 1 (v2026.080100)

Four fixes. Three of them close the way Heimdall replaces a file on a remote server: the fallback
paths used when a server cannot rename atomically were destructive, and could lose both the old and
the new content on an interruption. The fourth closes `C13`, and with it tier 1 of the audit
campaign. **Measured coverage after this release: 27 of the 151 findings closed, 15 of the 37 in
the source P1 cohort.**

Correction to the entry below. The v2026.073101 entry states 21 findings and 10 P1 closed. A full
recount against the source on 2026-08-01, finding by finding with a fresh read of the current code
required as proof, gives **19 and 9** at that commit. The earlier figure was carried forward rather
than measured. Every number in the present entry is measured.

### Remote file replacement (C4)

- FTP no longer deletes the target before writing. `FtpAtomicUpload` moves an existing remote file
  aside, commits with `FtpRemoteExists.Skip`, restores the backup if the commit fails, and cleans up
  best-effort. FluentFTP 54.2.0 `MoveFile` with `FtpRemoteExists.Overwrite` calls `DeleteFile`
  before `Rename`, which is what made an interruption lose both versions. Closes SFTP-005.
  (`9912e786`)
- The atomic-rename fallback is entered only on a genuine capability failure:
  `NotSupportedException`, or `SftpException` with status `OperationUnsupported`. Permission,
  transport and server errors now propagate without touching the target, and the case is logged.
  The existing target is probed for regular-file type through a listing of its parent directory
  rather than `Get` or `GetAttributes`, which canonicalise through `REALPATH` and therefore follow
  symbolic links. Closes SFTP-009. Restricts SFTP-010, whose crash window between the two renames
  remains open in both SFTP and FTP. (`1ff6b654`)
- The replaced file's permission mode is preserved. The target's full POSIX mode, mask `0x0FFF` so
  that setuid, setgid and sticky are included, is applied to the temporary file before the commit
  rename and never to the final path. If the mode cannot be applied and the temporary carries a bit
  the target did not have, the commit is refused and the original file is left intact: a lost
  permission is tolerated, a silent widening is not. Addresses SFTP-011 for the mode on the normal
  SFTP path only; ownership, POSIX ACLs, xattrs, capabilities, timestamps and the FTP path remain
  open. (`a864b683`)

### Per-path capabilities (C13)

- X11 forwarding and SSH compression are honoured by the external PuTTY and Plink transports and
  ignored by the in-process SSH.NET transport. The transport is not known before the attempt: it is
  chosen in four places, one of them after an SSH.NET authentication failure. Capability is
  therefore resolved when the transport is resolved. The X display server now starts only on
  transports that can forward, instead of on every attempt, and the direct transport reports the
  unavailable capabilities through a single non-blocking notice. Closes SSH-002 and SSH-020.
  (`415f3fed`)

### Measures

- Test suite: 8459 to 8488 passing, 0 skipped.
- Tier 1 (`C1`, `C2`, `C13`) is closed. `SFTP-001` remains open: the FTPS data channel certificate
  is never validated by FluentFTP, at any version and under any configuration. The limitation is
  declared in the FTPS session interface rather than masked.

## 2026-07-31: Tier 1 of the audit campaign - the product stops promising what it does not do (v2026.073101)

The first tier of the audit campaign opened on 2026-07-28. That campaign produced 151
findings across six protocol and UX audits, regrouped by common cause into 19
workstreams. Tier 1 is `C1` (trust), `C2` (dead settings) and `C13` (per-protocol
capabilities): the three whose defects the user meets daily, and the three that make the
interface state things the product does not do. **21 findings closed, 10 of the 37 P1
addressed, 2 removed by product decision.**

### Dead settings (C2)

- **Four settings were loaded, validated, persisted and never read.**
  `MaxEmbeddedSessions`, `TunnelEstablishmentDelayMs`, `RdpAutoReconnectMaxAttempts` and
  the embedded RDP watchdog timeout each had a full round trip through
  `AppSettings`, `SettingsViewModel`, `MigrationService` and in some cases
  `SchemaValidator`, with no consumer at the point of use. They are now applied at
  `SessionCoordinator.cs:595,707`, `TunnelService.cs:341` and
  `EmbeddedRdpView.xaml.cs:1534`. The legacy `EmbeddedRdpTimeoutMs` key survived only as
  a migration mapping onto `RdpConnectWatchdogTimeoutMs` (`9a5145f8`, RDP-008, RDP-009,
  UXG-002, UXG-003).

  Correction to the sentence above. Current .NET settings and legacy PowerShell imports are
  two distinct migration paths, and that mapping existed on one of them only: it lives inside
  the PowerShell importer, which a `settings.json` written by an earlier .NET build never
  enters. So only a configuration imported from the PowerShell version kept its value, while
  a current settings file kept a key nothing reads. `ConfigManager` now migrates the legacy
  key when it loads the current settings file, the canonical key wins whenever both are
  present, and the recognized legacy key is dropped on the next save. The shipped
  `settings.default.json` no longer declares it.
- The RDP resolution preset editor bound to `Settings.RdpResolutionPresetItems`, a
  collection that did not exist - the inner `TextBox` elements bound to items of nothing.
  Replaced by a real multi-line `RdpResolutionPresetsText` property with a reset command
  (`9a5145f8`, RDP-007).
- `ApplyRdpDefaults` omitted `RdpResolutionPresets`, `RdpDialogAdvancedDefault`,
  `RdpResizeEnableDelayMs`, `RdpArtifactCleanupDelayMs` and
  `RdpCredentialAutofillTimeoutMs` while its confirmation promised to restore all
  RDP-related defaults. Completed (`9a5145f8`, RDP-025).
- **`RequireCredentialGuard` stated a compliance guarantee and enforced nothing.** The
  symbol existed at six sites - declaration, property, load, save, migration, binding -
  and had no consumer on any connection path. An administrator could believe connections
  were blocked while they went through. Now checked before an embedded RDP session opens
  (`bf990e5a`, UXG-001).
- The transport option labelled as forcing TCP was reworded. Its documented intent is to
  prevent the UDP probe that times out behind a firewall, which it does achieve by
  disabling bandwidth detection and setting an explicit connection type
  (`RdpActiveXHost.cs:1866-1871`); the label promised more than the workaround delivers.
  Requalified from P1 to P3 on disk evidence, and fixed as a wording defect (RDP-004).

### Trust (C1)

- **Removing a trusted host key was never persisted.** The view dropped its row and the
  service raised `EntryRemoved`, but nothing subscribed: neither `TrustedHostKeys` nor
  `TrustedHostKeysV2` was ever written. The key came back on restart, so a trust decision
  could not be revoked from the interface (`8cfb3771`, SSH-019).
- **A pinned FTPS certificate short-circuited every non-overridable check.** When a
  fingerprint was already stored and matched, `FtpBrowser` refreshed `LastSeen` and
  returned true **without ever consulting `policyErrors`** - an expired, revoked or
  wrongly-purposed certificate was accepted on the strength of having once been approved.
  Pinned paths now run `EnsurePinnedCertificateRemainsValid` with the policy errors in
  hand (`8cfb3771`, SFTP-002).
- `RefreshLastSeen` mutated the store in memory without raising the event the application
  persists on, so the timestamp reverted on restart. Both the SSH and the FTPS side are
  fixed together: they were exact twins, and fixing one alone would have been incoherent
  (`8cfb3771`, SFTP-029, SSH-027 - the latter absorbed from C17, explicitly and not
  silently).
- **Concurrent trust prompts were neither serialized nor deduplicated.** Each host-key
  and each FTPS certificate request scheduled its own `ShowDialog` through the dispatcher
  with the main window as owner; WPF modality opens a nested dispatcher loop and is not
  an application queue. Launching several connections to unknown hosts could stack or
  nest dialogs with no clear indication of which tab was asking, so a user could approve
  the wrong fingerprint during a batch launch. `TrustPromptCoordinator` now holds a
  single prompt slot and coalesces by trust kind, host, port and presented fingerprint
  (`1cd861e9`, UXG-015).
- **The FTPS data-channel limitation is declared rather than hidden.** FluentFTP 54.2.0
  installs an unconditional certificate-acceptance handler on the data channel
  (`FtpDataStream.cs:216-232`, comment *"always accept certificate no matter what"*), and
  the same handler is present on current upstream. No `FtpConfig` option targets it, and
  supplying a second callback through `ConfigureAuthentication` is rejected by .NET
  because `SslStream` is already constructed with FluentFTP's. The limitation cannot be
  fixed inside Heimdall, so it is now written into `docs/SECURITY.md` and surfaced during
  an active FTPS session through `WarnFtpsDataChannelIdentityBadge`. Replacing the
  transport, swapping the library and dropping FTPS were all considered and rejected: the
  campaign exists to remove false promises, and creating one by omission would have been
  incoherent (`8cfb3771`, SFTP-001).

### Per-protocol capabilities (C13)

- **Bulk credential editing wrote into fields the target type does not own.** The
  `default:` branch of `SetEditableUsername` wrote to `dto.RdpUsername` and that of
  `SetEditablePassword` to `RdpPasswordEncrypted` - not inert, but RDP. Since a tool
  context pre-fills from `SshUsername ?? RdpUsername`, dirtying `RdpUsername` on a tool
  node changed that tool's behaviour. A neighbouring asymmetry surfaced at the same time:
  `SetEditablePort` and `SetEditablePassword` had a `VNC` case and `SetEditableUsername`
  did not, so a VNC profile sent its password to `VncPassword` and its username to
  `RdpUsername`. Split into two independent proofs - the menu only offers compatible
  types, and the write boundary refuses the rest, now as `TrySetEditableUsername` and
  `TrySetEditablePassword` returning false (`8e2d5d8a`, TREE-002).
- Telnet username and password fields were offered, persisted and encrypted while no code
  path transmits them. `TelnetSession.StartAsync(string executable, string arguments, …)`
  ignores both parameters by contract - they were never credentials at all. The profile
  logic stops promising them and the inputs are removed with their last consumers
  (`e0e87cc8`, `1fd82774`).
- Citrix claimed ownership of the RDP credential fields, though none of its three launch
  modes - SelfServiceCache, ICA and StoreFront - reads them (`0bcf120c`).
- The authentication badge described the DTO's fields while claiming to describe the
  mechanism the connection would use, which was wrong on five protocols at once. It now
  describes what it actually knows: the credentials on file (`67060c6a`).
- `FtpBrowser.ChmodAsync` returned `Task.CompletedTask` with the comment *"FTP does not
  natively support chmod; this is a no-op"*, and the caller reported success. It now
  throws `NotSupportedException` and the caller surfaces `SftpChmodNotSupported`
  (`17d210c2`, SFTP-019).
- The shared terminal view exposed Elevate and Health to WinRM sessions, where Elevate
  has no subscriber anywhere in the repository and Health can only query an SSH client.
  Both are withdrawn on that protocol (`5f542c30`, WINRM-020).
- Detach was offered on any non-split tab without checking for a host, so detaching an
  external-application tab opened a window with a header and no content (`c5c92a81`,
  UXG-021).
- The inverse case: rename was reachable with F2 but absent from the tool context menu,
  making an existing and valid action undiscoverable precisely on TOOL nodes
  (`de334914`, TREE-020).

### Removed by decision, not by fix

- `SSH-002` (X11) and `SSH-020` (compression) have no consumer on the embedded path. They
  are recorded as product decisions rather than counted as closed defects.

### Test suite

- **A test-isolation hole was closed, and its scope is deliberately narrow.**
  `CredentialProtector` holds process-wide static vault state read by `Protect` and
  `Unprotect`. `CredentialProtectorAppCollection` serializes its own members but runs
  concurrently with other collections, so a test class outside it could observe the vault
  flag set with no data encryption key and fail with `VaultLockedException`. The fourteen
  App.Tests classes that invoke a **direct** reader now carry the collection attribute.
  Transitive readers reaching that state through `ToolGatewayConnector.Connect` or
  `ConnectionHelpers.DecryptPassword` are not covered: the call closure reaches 18 direct
  members and 43 at the second level, and chasing it would have serialized a
  disproportionate share of the suite (`1601e8df`).
- Terminal wait timeouts now report what the session actually observed instead of an
  identical assertion failure (`8efca829`).

## 2026-07-28: Localization integrity, dead commands, and design-system naming (v2026.072801)

A corrective release. Everything here removes something the interface stated but did not
deliver: a label that was never rendered, buttons whose click did nothing, and a style
name that claimed a scope it never had. No feature work.

### Localization

- **A menu key was declared twice in both locale files.** `TreeCtxRename` appeared at
  lines 715 and 729 of `en.json` and `fr.json`. `LocalizationManager.LoadAsync`
  deserializes into a `Dictionary<string, string>`, so System.Text.Json goes through the
  indexer and the **last occurrence wins** - proven empirically, not assumed. One of the
  two labels was dead and invisible. The CI parity check could not see it: it compares
  unique keys, so it honestly reported zero difference. Closed by removing the duplicate
  and by `LocaleDuplicateKeyGuardTests`, which scans with `Utf8JsonReader` instead of a
  dictionary and fails naming the file, the key and every line number (`36803eb9`,
  BL-0058).
- Double-encoded emoji in locale values are repaired, and the mojibake guard no longer
  lets that encoding through (`73dc93c7`).

### Dead commands

- **Two dead clicks, out of a strictly measured population of 400.** The audit counted
  79 commands carrying a `CanExecute` predicate (the 59 quoted in BL-0050 and BL-0054 was
  stale by 20) and 403 `Click`-wired buttons, 3 of which also carry `Command`. The
  backlog premise - a population sweep rather than point fixes - is refuted by that
  measurement: two cases, two distinct idioms, no common sweep. `BtnEnumerate` consulted
  `EnumerateCommand.CanExecute` after the click; `BtnBrowseFile` cleared `TxtInput`
  before consulting it, losing the field and producing no hash (`bcfc490e`, BL-0054).
- **The twin a button census could not see.** `OnDrop` borrows the same `BeginFileHash`,
  so drag-and-drop carried the identical defect without being a button. Found by reading
  the method, closed by the same guard clause (`bcfc490e`, then `bf6910e5` for the drop
  rejection path, BL-0060).

### Design system

- `ToolbarGhostButtonStyle` is renamed `GhostButtonStyle`: 84 occurrences across 17
  files, 84 before and 84 after, no setter, trigger or value changed. The rename was
  risky enough to warrant a guard - a missing `StaticResource` key throws at load, but a
  missing **`DynamicResource` key does not**: the property keeps its default, the control
  renders unstyled, and nothing reports it. On 74 `DynamicResource` references, one miss
  would have been silent. `ButtonStyleResourceResolutionTests` fails when a ghost or
  quiet key referenced by a view does not resolve in the merged dictionaries
  (`5a00b673`, BL-0047).

### Test suite

- Accessibility tests now drain the live-region announcement emitted at window
  initialization before measuring, instead of mixing it with announcements caused by the
  action under test. Discriminating by announcement text is impossible by construction:
  the FlaUI callback reads `element.Name` at delivery, not at emission, so a late initial
  announcement presents under the current name (`bc812733`, BL-0048).
- Password generator tests no longer share the real preset storage (`3e8fcbf4`).
- Source-enumerating guards now exclude build output, which contains copies that skew
  the counts (`d9647b1a`).

## 2026-07-27: Update banner and disabled-button legibility (v2026.072701)

A corrective release on the update banner and on how disabled buttons render. The banner
defect is the most visible of the three releases documented here: the button that
installs an update could stay disabled for a whole session.

### Update banner

- **The install button never became enabled after a successful check.**
  `_availableUpdate` is a plain field, so assigning it in `CheckOnStartupAsync` raised no
  notification, and CommunityToolkit's `RelayCommand` does not requery through WPF's
  `CommandManager` either. The banner's primary button kept the `CanExecute=false` state
  it evaluated at `DataContext` time. Closed by raising `CanExecuteChanged` right after
  the assignment. The existing view-model test polled `ICommand.CanExecute` directly,
  which never observes the event and so could not see this; the new test binds a real
  `Button` the way `MainWindow.xaml` does and asserts `Button.IsEnabled` (`47c53f79`).
- **That guard was decorative until it was moved.** Tagged `RequiresDesktop`, it only ran
  in the informational lane at `ci.yml:105`, which is `continue-on-error`: deleting the
  production `NotifyCanExecuteChanged` call would have left CI green. The trait was
  unwarranted - the test shows no window and injects no input. Blocking lane goes from 5
  to 6 UI tests and stays green (`9034e6f3`).
- Three of the banner's four buttons shared `SecondaryButtonStyle`, including "Skip this
  version", which persists `UpdateSkippedVersion` and which no UI can undo. A third
  weight, `QuietButtonStyle`, now carries "Skip this version" and "View release"
  (`25299448`).
- The row rendered as Primary, Quiet, Secondary, Quiet - the only bordered button sat
  between two quiet ones, so the layout contradicted the weights it expressed. Reordered
  by decreasing weight; since no button carries an explicit `TabIndex`, keyboard tab
  order now follows that same order, which it did not before (`67688a1f`).

### Themes

- **Disabled buttons repainted their fill instead of dimming.** The disabled triggers of
  `PrimaryButtonStyle` and `SecondaryButtonStyle` repainted fill and border to a flat
  surface colour: a disabled primary rendered as a resting secondary stripped of its
  border, and either button vanished outright against a backdrop of its own repaint
  colour - contrast measured exactly 1.00 for primary on a card, and for secondary on the
  window background. Both now dim through the shared `OpacityDisabled` token, as the
  seventeen other disabled triggers in the file already did, holding a floor of 2.41
  across every theme, backdrop and accent combination (`042ad6bd`).

### Test suite

- The SFTP teardown test asserted that the disconnect ran on a different managed thread
  than the caller. Thread identity does not witness that: `DisposeBrowserAsync` queues
  the teardown with `Task.Run`, the caller returns its thread to the pool the moment it
  awaits the disconnect-started signal, and the pool is then free to run the queued work
  on that very thread. The assertion held only while the pool never made that choice, and
  it eventually did, under full solution load. The surviving
  `Assert.False(teardown.IsCompleted)` already carries the real contract, and was checked
  for vacuity. Renamed accordingly (`d8e9e5ca`).

## 2026-07-26: Build, CI, and test-suite hardening (v2026.072601)

A maintenance release. No new features and no user-visible fixes: the work sits in the
build chain, the CI gates, and the reliability of the test suite. Two dependency bumps
and one supply-chain tightening are the only changes that reach a shipped binary.

### Dependencies

- **The native components shipped with the app are now covered by the lock files.**
  Declaring `RuntimeIdentifiers` for `win-x64` makes every restore, RID or not, produce
  the same lock, which pins `SourceGear.sqlite3`, `LibGit2Sharp.NativeBinaries` and
  `System.Management` by content hash. They previously escaped the lock entirely.
  Committing the RID sections alone would not have worked: a plain restore strips them
  again, so the tree would have churned on every build/publish alternation
  (`218349a2`, BL-0042).
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 to 3.0.4 (`c6a45bff`).
- `System.Security.Cryptography.ProtectedData` 10.0.9 to 10.0.10 (`2907c324`, `529e0836`).
- `actions/checkout` 6 to 7 (`7b2d3701`).

### Build

- **`Build.ps1 -DryRun` no longer writes the application project file.** The version is
  injected as an MSBuild property on the dry-run path instead of being written to disk,
  so a simulated publish leaves the tracked tree untouched and no longer advances the
  next build number (`4d3d4cbd`). Proven by a guard that re-demonstrates red
  (`e00460fa`), wired into CI (`a875fbb3`), then renamed and narrowed so its name and
  success message state exactly what it does and does not cover (`565981bc`, `7dd17c25`).
- A `-Publish` run with no conventional `v<version>.md` notes file now warns and names
  the path it looked for, instead of being silently a no-op (`c87e4b24`).

### CI gates

- Committed CRLF blobs now fail the build, anchored on the `i/` column of
  `git ls-files --eol` (`f9b191c9`).
- The release-notes typography guard is keyed on the AZERTY layout as an allow-list
  rather than a block-list, which also catches non-breaking hyphens, arrows and emoji
  (`f12f3551`). A leading byte-order mark is now reported instead of swallowed
  (`c3b0617f`), and the guard's own assertions stay legible when a case yields nothing
  (`5c1df45f`).
- Both gates run on every build (`7f33cbc7`), with the typography step propagating its
  exit code explicitly rather than relying on runner behaviour (`19c4b44f`).

### Test suite

- Bounded waits in the configuration manager tests now name which side stalled, instead
  of reporting an identical assertion failure for both (`d78b4234`). The entry-wait
  message was then narrowed to what it can establish: it cannot tell a callback blocked
  on the lock from a work item that was never scheduled (`04fdccb6`, BL-0033).
- Thread-pool starvation no longer masquerades as a functional failure. The pool floor
  is raised for the test assemblies (`914801c6`), concurrent probes are driven off the
  pool (`d70dcd14`, `a4ed52a3`), and the traceroute stop signal stays diagnosable under
  starvation (`1a46cc35`).
- Polled waits are bounded so they fail instead of hanging (`258743b5`), one test awaits
  completion instead of racing a timer (`18f35ce4`), and tight backstops were widened
  (`b5290ff2`, `ab416af9`).
- Line endings were renormalized after a Dependabot rewrite (`d22b4669`), and the
  local-only artefacts directory is ignored (`6cc6acda`).

## 2026-07-25: Security, lifecycle, and accessibility hardening (v2026.072501)

A cross-reviewed hardening pass over the remote-protocol, updater, and session
lifecycle surfaces, verified finding-by-finding against the real code, plus two
robustness fixes and a tree-accessibility completion. Build clean (0 warnings),
full suite green (8,271 tests).

### Security

- **Embedded WebView documents enforce exact-origin trust.** Messages and navigation
  from the Milkdown notes editor and the embedded VNC view are validated against an
  exact scheme/host/port/path origin instead of a substring check, so a foreign
  document can no longer post messages into or navigate the host (`dc3e3870`, CODEX-007).
- **Privileged SFTP writes no longer stage in attacker-writable `/tmp`.** Sudo edit and
  upload stream their content over the privileged channel into a root-owned temp
  directory created beside the target and commit with an atomic, symlink-refusing
  `mv -fT`; the sudo read path holds a no-follow descriptor. A same-account process can
  no longer swap the staged bytes or redirect the write through a symlink (`fc247df1`,
  CODEX-008).
- **The in-app updater re-validates at the execution boundary.** The verified installer
  is staged under a restrictive-ACL directory in `%LOCALAPPDATA%`, held under a
  deny-write handle from verification through launch, and its SHA-256 (plus Authenticode
  when the installer is signed) is re-checked immediately before the elevated
  `Start-Process`; the relauncher script is itself integrity-checked. A swap between
  verification and the elevated run is refused rather than executed (`3e3b9239`,
  CODEX-009).
- **Citrix launch arguments are decrypted only at the launch boundary and never logged.**
  The pre-authenticated SelfService launch token is decrypted just before use, validated,
  and passed to the launcher without being written to any log or exception; a locked
  vault fails closed (`6ba7392a`, CODEX-010).

### Robustness (lifecycle)

- **Connection lifecycle teardown is bounded and ordered.** Ephemeral per-session state
  entries are removed at terminal teardown (no more unbounded growth across connects),
  and connection-state notifications carry a monotonic per-session revision so a stale
  update can never overwrite a newer one (`c4f683d0`, CODEX-012).
- **Tunnel manager disposal is a real barrier.** A tunnel whose open is still in flight
  when the manager is disposed is now rejected and disposed under the registry lock
  instead of registering into a torn-down manager, closing a leaked-connection window; a
  lifetime token aborts in-flight opens (`ca5e83c0`, CODEX-013).
- **Closing an SFTP tab never blocks the UI.** Tab teardown is bounded and runs off the
  dispatcher; a stuck, non-cancellable directory listing is aborted by disposing the
  underlying client rather than waited on, and the credential context is dropped on both
  the graceful and abort paths (`c7b48342`, CODEX-014).
- **Session health checks no longer overlap.** Reachability cycles run sequentially via a
  `PeriodicTimer`, each stamped with a monotonic generation so an older probe cannot
  overwrite a newer verdict, and verdicts route to their row in O(1) (`6e65d873`,
  CODEX-015).
- **Tree expand-state persistence is thread-safe.** The debounced save snapshots the
  expanded-node set on the UI thread and writes it through an atomic settings merge,
  instead of enumerating a mutable set on a background thread (`c88085a0`, CODEX-028).

### Accessibility

- **Filter result count is a live region.** Screen readers announce the filtered session
  count when it changes, deferred until the count is actually visible and de-duplicated so
  an unchanged value is never re-announced. A keyboard-focused folder is now a reliable
  target for the Shift+F10 / Apps context menu, with a localized automation name and help
  distinct from a server (`eb87590c`, CODEX-031).

### Internal

- **Deterministic search-debounce test.** The search-filter debounce uses an injected
  `TimeProvider` (default `TimeProvider.System`, so runtime behavior is unchanged), letting
  its test advance a virtual clock instead of sleeping on the wall clock (`65227300`,
  CODEX-032).
- **Flaky CI tests fixed.** The SSH shell teardown notification and the tunnel
  registry-lock probes no longer depend on thread-pool scheduling, and the plink
  password-file janitor sweep no longer depends on host elevation. `SshShellSession`
  takes an optional `TimeProvider` (default `TimeProvider.System`, so runtime behavior
  is unchanged) so its teardown wait is driven by a virtual clock (`7c7dd677`,
  `740d0090`, `766fcbd7`, `2b42a4ba`).
- **CIUnstable inventory refreshed.** `docs/CI_FLAKY_TESTS.md` lists all 11 trait sites
  individually with their effective categories (`37072f03`).

## 2026-07-24: Session tree gateway badge fix (v2026.072400)

Targeted hotfix for v2026.072300.

### Fixed

- **Gateway badge no longer overlaps the server name.** In the session tree, a server
  with an SSH gateway had its name column starved (measured as little as ~19px at depth)
  because the row was constrained to the viewport. The horizontal scrollbar is restored
  (`Auto`), name trimming is removed so full names show, recycling virtualization stays
  active, and per-level indentation is tightened.

## 2026-07-23: Cross-review hardening sprint + treeview UX (v2026.072300)

A cross-reviewed hardening and UX campaign, verified
finding-by-finding against the real code. Closes the last release-blocking defects
and adds the requested tree-management features. Build clean (0 warnings), full suite
green (8,184 tests).

### Deployment

- **Writable state under `%LOCALAPPDATA%`.** Config, logs, and other mutable state now
  live under `%LOCALAPPDATA%\Heimdall` instead of next to the executable, so a
  per-machine MSI install starts for a standard user. Bundled `*.default.json` still
  resolve from the install directory, with an idempotent one-time migration of existing
  data (`3239246c`, CODEX-004).

### Session tree (UX)

- **Inline rename.** F2 / Ctrl+E rename of sessions and folders directly in the tree,
  virtualization-safe (`fdc3411b`).
- **Recycling virtualization.** The session tree is virtualized; long names are
  ellipsis-trimmed with a tooltip, and "Expand all" no longer freezes at thousands of
  nodes (~48 realized containers on a 1,200-server inventory) (`4263c5eb`, CODEX-016).
- **Structured filters.** Filter by protocol/type, favorite, and connected state,
  combined with debounced text search through one versioned pass that reuses stable
  nodes; the WinRM username is now included in search (`00711704`, CODEX-017/024).

### Gateways

- **Bulk gateway edit.** Credential-free bulk edit of the SSH gateway across servers with
  four explicit outcomes (preserve / force direct / inherit / specific), skipping
  ineligible protocols (`96c0d1a7`).
- **Centralized eligibility.** `SupportsSshGateway` is defined once (RDP/SSH/SFTP/WinRM)
  and enforced in the dialog, persistence, badge, and bulk path; no more false
  "via gateway" badge on a cleartext protocol such as Telnet (`48dc9989`, CODEX-006).
- **Deletion integrity.** Gateway deletion now analyzes inherited group-default and
  parent-gateway references before acting (`620797fe`, CODEX-018).

### Security hardening

- **Honest connection state.** Connection-state feedback decodes the generated session id
  and aggregates multi-session state per profile (`48dc9989`, CODEX-011/017).
- **RDP autofill fail-closed.** Credential autofill only writes to a confirmed password
  field, never a guessed one, and abandons injection otherwise (`4591956f`, RDP-02).
- **RDP credential deletion, Pageant OOB read, and plink start-failure state** hardened:
  each credential type is deleted independently, the Pageant shared-memory read is bounded
  exactly, and a failed `Process.Start` leaves the runner in a clean retryable state
  (`4591956f`, RDP-05 / SSH-01 / SSH-07).
- **Plink password-file hygiene.** Orphan tunnel password files left by an interrupted
  tunnel are now swept (unified prefix) (`24726bf4`, SSH-09 / CODEX-026).
- **Credential identity across rename.** Renaming a profile no longer changes its external
  credential lookup key; the old display name is frozen centrally in the persistence layer
  when no explicit reference exists (`9bff9888`, CODEX-019).
- **Tool-log hygiene.** External-tool arguments and exception messages are no longer logged
  verbatim, so secrets passed as tool arguments do not reach the log (`9bff9888`, CODEX-027).

### Reliability & data integrity

- **Atomic inventory mutations.** `MutateServersAsync` holds the write lock across
  load-mutate-write; concurrent operations can no longer erase each other
  (`424a560e`, CODEX-002/020/029).
- **Safe config writes + shutdown.** Fail-safe writer plus an awaited save-on-close, with a
  cross-thread WPF close-regression hotfix so closing with valid settings never blocks
  (`31c5da34`, `5e62ff2b`, CODEX-001/003).
- **Atomic folder rename.** Folder rename migrates group defaults and expansion state in a
  single recoverable mutation (`5e62ff2b`, CODEX-005).
- **Schema resilience.** Persisted settings and inventory carry a schema version and
  `[JsonExtensionData]`: unknown fields round-trip through load/save, and a document with a
  newer schema version is never overwritten (`03b36c16`, CODEX-021).
- **Monotonic settings snapshots.** Published settings carry a monotonic revision and are
  deep-cloned, so `CurrentSettings` cannot regress behind a just-committed save
  (`03b36c16`, CODEX-029).
- **Deterministic NuGet restore.** Lock files regenerated and committed, and CI restores
  with `--locked-mode` (`ac3f6e6f`, CODEX-022).

### Accessibility & i18n

- **Localized auth summary + accessible nodes.** The detail-panel authentication summary is
  localized (EN/FR) and refreshes on locale change; tree nodes expose an accessible name
  combining name, protocol, and connection state (`7e0627ee`, CODEX-023/031).

## 2026-06-29: SSH/SFTP companion, follow-directory, and reliability hardening

Merged to `master`; intended for the next release. A focused robustness + interop
campaign on the SSH/SFTP surface (the most-used, production paths), audited and
verified finding-by-finding.

### Integrated SFTP companion

- **Pane-scoped reconnect.** Reconnecting the auto-opened SFTP companion no longer
  tears down the SSH terminal or loses its scrollback: the SFTP overlay reconnect now
  routes through the pane-scoped `ReconnectPaneAsync` and honors the pane's own
  protocol, and the companion gets an isolated session/state key so closing or
  reconnecting it can never reset the sibling SSH session (`5c19050a`).
- **SFTP keepalive.** The SFTP channel now sets `KeepAliveInterval` like the SSH
  session, so idle companion sessions are no longer dropped by the server/gateway/NAT
  (`5c19050a`).
- **Runtime gate.** `SftpBrowserEnabled` is now an honored runtime switch for the
  integrated browser, surfaced in Settings; auto-open is additionally guarded against
  cancellation / closed-tab races (`5c19050a`).

### Follow the SSH working directory (opt-in)

- **OSC 7 directory following.** The companion SFTP pane can follow the SSH terminal's
  current directory via the OSC 7 escape sequence (best-effort; inert on shells that do
  not emit it). Global setting plus a per-pane live toggle; the remote path is treated
  as untrusted input (redacted logs) (`1cd24018`).

### Robustness & resilience

- **Deterministic SSH teardown.** `SshShellSession` disconnect/dispose now run through a
  single synchronized, idempotent teardown that no longer blocks the UI thread (the
  read-loop join moves to the background) and notifies disconnect exactly once
  (`1f103a14` and follow-ups).
- **Lock discipline.** `FtpBrowser.Disconnect()` now takes the operation lock (matching
  `SftpBrowser`), and `DirectoryChanged` is raised outside the client lock to avoid a
  re-entrancy deadlock; transfer and lifecycle `CancellationTokenSource` swaps are
  synchronized.
- **Resilient auto-upload.** A transient SFTP auto-upload failure now re-arms the
  debounce timer and retries, and the last-upload timestamp only advances on success.
- **Resource hygiene.** Tunnel event callbacks are exception-isolated, the plink stderr
  drain uses a per-iteration process reference, and `PrivateKeyFile` is now disposed
  with its owning client (no key-material handle leak). Teardown helpers no longer abort
  on non-`ObjectDisposedException` cleanup errors (`1f103a14`).
- **Bounded inputs.** A bounded, drop-oldest buffer caps pre-ready terminal output;
  inbound WebView2 terminal messages are length-capped before decoding; and sudo file
  edits are size-checked (16 MiB) before download.

### Fail-closed & interop

- **Host-key fail-closed even when wrapped.** A `HostKeyRejectedException` nested inside
  an outer exception is now recognized at every classification site and remains
  non-reconnectable, so a possible MITM is never silently retried (`5dbb8f9d`).
- **Interop edge cases.** Server-health output now reports an explicit unsupported state
  on non-GNU/Linux shells instead of showing 0%; sudo `ls` parsing is culture-invariant;
  SSH pre-login banners decode as UTF-8; the SSH probe tolerates RFC 4253 pre-login
  banner lines; and local upload sources are validated up front (`5dbb8f9d`).

## 2026-06-29: Master password vault, Windows Hello unlock, macros and file-browser workflows

Merged to `master` since v2026.062601; intended for the next release.

### Master password vault (encryption at rest)

- **Versioned crypto envelope.** New vault primitives: Argon2id key derivation and
  AES-256-GCM with a versioned envelope, validated against known-answer tests
  (`d128da0c`).
- **DEK/KEK orchestration.** A data-encryption key is wrapped by a master-password
  key-encryption key; `CredentialProtector` is now version-aware and backward
  compatible with legacy DPAPI blobs (`257f4112`).
- **Lifecycle and migration.** Enable, change, and disable the master password, with
  a migration engine that re-wraps existing credentials (`7618d5ce`).
- **Atomic config writes.** `ConfigManager` writes are now temp-then-rename atomic so
  a crash mid-write cannot corrupt the vault (`17c0096e`).
- **Startup unlock gate.** When a master password is set, the workspace is locked at
  launch behind an unlock gate before any credential is usable (`6921282d`).
- **Vault management UI.** Settings cards to enable / change / disable the master
  password (`738a5f58`); the Command Library Git-token reader is now vault-aware
  (`817ce185`).

### Windows Hello vault unlock

- **Unlock the vault with Windows Hello.** Optional convenience: a TPM-bound Hello
  key (sign -> HKDF -> AES-GCM, AAD-bound, composed with DPAPI) wraps the DEK so the
  vault can be unlocked by biometric/PIN instead of retyping the master password;
  fail-closed with a master-password fallback (`dc4df107`). Enroll / remove cards in
  Settings (gated on TPM presence) and a fingerprint unlock button on the gate and
  lock overlay (`d0b8947e`). Confirmed to survive a machine reboot.

### Workspace lock

- **Lock the workspace.** Manual lock plus idle auto-lock (`AutoLockIdleMinutes`) with
  a lock overlay; locked sessions survive and are masked rather than disconnected, with
  an opt-in disconnect-on-lock (`f12421a8`).

### Expect macros

- **Wait-for-pattern playback.** Macros can wait for an expected output pattern before
  sending the next step (expect-style), with a macro editor to author expect steps
  (`16df054f`, `cb052a31`).

### Per-profile session logging

- **Tri-state per-profile override.** Session logging can be forced on, forced off, or
  inherit the global setting per server profile, exposed as a combo in the server
  dialog (`45a8016c`, `cf1fd484`).

### File browser (SFTP / FTP)

- **Copy primitive.** New no-overwrite `CopyAsync` on the remote browser: server-side
  `cp` (host-key-pinned) with a roundtrip fallback for SFTP, recursive roundtrip for
  FTP, journaled as a single Copy record (`b31f79ea`).
- **Cut / Copy / Paste / Duplicate.** Per-pane clipboard, cross-directory move, and
  non-destructive collision handling in the file-browser context menu (`9e5c827b`,
  robustness fix `43975309`).
- **Recursive folder upload + drop-into-folder.** Dropped folders upload recursively,
  and a drop onto a folder row targets that folder (`ba373ed8`).
- **Cross-pane copy/paste.** Paste between panes on the same server, then across
  different servers (download-temp + upload), with cut semantics preserved
  (`35acd9ca`, `18c537db`).
- **Paste from Explorer.** A dedicated context-menu entry uploads files/folders from
  the Windows clipboard (CF_HDROP) into the current remote directory (`bd149293`).
- **Operations journal.** SFTP/FTP transfers (download, upload, mkdir, delete, rename,
  copy) are journaled at the view/view-model boundary (`3ecac1a7`, `db79ab36`).

### Fixed / internal

- **Editor.** Guard against cyclic AvalonEdit rule-sets in the Dracula syntax palette
  (`10358c12`).
- **UI.** Context menus render uniformly text-only (`b8cbb0b1`).
- **Dependencies.** Bump `System.Security.Cryptography.ProtectedData` 10.0.5 -> 10.0.9
  (`12de9dfc`).
- **Validation.** Blocking CI is green; the RequiresDesktop UIA pass remains
  non-blocking.

## 2026-06-26: v2026.062601 - MultiExec, global session logging, enriched context menus

Released as v2026.062601.

- **MultiExec / broadcast.** Send keystrokes to multiple sessions at once with scope
  modes (current tab / all tabs / selected panes), per-tab and per-pane targeting, a
  scope indicator, and a confirmation before a wide broadcast; fan-out routed through
  the smart-paste guard.
- **Global session logging.** A single global toggle: per-session text transcripts for
  SSH / Telnet / Local Shell, and a connect/disconnect event log (reason + duration)
  for RDP / VNC / Citrix; shared NDJSON event log, size rollover, restrictive ACLs.
- **Enriched context menus.** Server-tree and session-tab context menus gained copy
  address / copy SSH command / test reachability, close-others / close-to-right /
  reveal-in-tree, rename / pin, open-in-split, and connect-as.
- **Fixed.** Replay ConPTY bootstrap output to late subscribers so the Local Shell /
  WinRM initial prompt is no longer dropped; apply inherited ConnectionType /
  Environment and fix group-defaults precedence.

## 2026-06-24: KeePassXC key-file authentication

Added key-file (`.keyx`/`.key`) authentication for the external credential
provider, the main gap for enterprise KeePassXC usage (commit `3472cda7`).

- **Key-file support.** A new `{KeyFile}` command placeholder and the
  `CredentialProviderKeyFile` setting (a plaintext file path, not a secret) feed
  a key file into the command via `-k "{KeyFile}"`, used alone or with a master
  password.
- **Three KeePassXC presets.** `"KeePassXC"` (master password, now with `-q`),
  `"KeePassXC (key file)"` (master password + key file), and
  `"KeePassXC (key file only)"` (`--no-password`, key file alone).
- **Path-aware sanitizer.** For non-shell targets, the path placeholders
  `{Database}` and `{KeyFile}` now strip only the double quote and CR/LF. The
  provider runs with `UseShellExecute=false`, so no shell interprets the
  arguments and the double quote (illegal in Windows filenames) is the only
  argument-boundary metacharacter. This also fixes latent corruption of
  `{Database}` paths containing characters such as `&` or `$`.
- **Settings.** A Key file field with a Browse dialog (`*.keyx;*.key`) and an
  empty-key-file guard on the Test button; EN/FR locale keys at parity.
- **Validation.** Verified against the real `keepassxc-cli` 2.7.11.
- **Tests.** Baseline raised to 7,347 passing tests, 0 failures.

## 2026-06-21: External vault integration overhaul

Hardened and extended the external credential provider after an audit found the
shipped presets unusable out of the box, then added native and biometric options.

- **Interactive vault tools now work.** The command provider can feed an optional
  unlock secret (DPAPI-stored) to the tool via stdin, so KeePassXC and `pass`
  unlock non-interactively. Stderr is drained before the write to avoid pipe
  deadlock, and a hung tool is killed on timeout.
- **Username from the vault.** An optional separate username command resolves the
  login, run only when the profile has no username; failure falls back to the hint.
- **Per-profile vault entry name.** A `VaultEntryName` override decouples the vault
  lookup from the Heimdall display name.
- **All protocols covered.** Telnet and VNC now use the provider alongside
  SSH/SFTP, RDP/Citrix, WinRM and FTP.
- **Provider selection + DI.** `ICredentialProviderFactory` builds either the
  command provider or the new **Windows Credential Manager** provider
  (`CredReadW`, returns username + password). Provider construction is injectable
  and unit-tested end to end.
- **First-line output mode + KeePass2 preset.** A "first line only" option strips
  trailing output such as KPScript's `OK:` line; a KeePass2 (KPScript) preset was
  added, with in-app guidance recommending `keepassxc-cli` against `.kdbx` files
  for stdin-based unlocking.
- **Windows Hello gate.** An optional, fail-closed biometric/PIN verification can
  be required before stored credentials are used, on both single and bulk connect,
  with a configurable in-memory grace window.
- **Fixes.** The Settings "Test" button now uses the configured timeout.
- **Tests.** Baseline raised to 7,337 passing tests, 0 failures.

## 2026-06-20: Magellan theme and release-notes tooling (v2026.062002)

This release adds the Magellan dark theme and streamlines the release-notes
workflow.

- **Magellan theme.** A new brand-derived dark variant (deep navy background,
  indigo accent) is available in the theme picker, sourced from ThemeForge 2.1.0;
  it meets WCAG AA contrast across all functional accents.
- **Release-notes tooling.** `Build.ps1 -Publish` now prepends a hand-written
  notes file (`docs/release-notes/v<version>.md`, or an explicit
  `-ReleaseNotesFile`) above the auto-generated Downloads and Checksums section,
  removing the manual post-publish edit step.
- **Tests.** The automated baseline is now 7,289 passing tests, 0 failures.

## 2026-06-20: Command Library send-state clarity and reliability fixes (v2026.062001)

This release sharpens the Command Library experience and closes three reliability
gaps in command generation, logging, and validation.

- **Clearer send-state.** The Command Library makes the "Send to terminal"
  button state easier to read and adds a copy hint, with the Ctrl+K palette
  wired to the same bridge.
- **Examples apply on row click.** Selecting an example row now applies it
  directly, in both the Command Library and the Ctrl+K palette.
- **Palette surfaces regenerate errors.** The Ctrl+K snippet detail now shows
  `ToolCmdLibGenerateError` when regeneration fails, instead of silently
  clearing the message (parity with the library view).
- **TwinShell logging reaches the FileLogger.** TwinShell `ILogger<T>` output is
  routed through a dedicated MEL provider into the FileLogger; it was previously
  dropped in Release builds.
- **Correct example cap.** The per-action example cap now sums the three buckets
  (common / Windows / Linux), closing a completeness gap in validation.
- **Tests.** The automated baseline is now 7,288 passing tests, 0 failures.

## 2026-06-19: FTPS trust, safer FTP operations and dependency hardening (v2026.061902)

This release closes the FTPS trust gap, makes FTP/local file operations less
destructive, and removes the SQLitePCLRaw audit suppression by moving to patched
packages.

- **FTPS certificate trust.** FTPS now validates server certificates with a
  hybrid system-validation/TOFU model, persists trusted fingerprints, detects
  changed certificates, and shows localized Accept / Trust once / Reject prompts
  with certificate details when user confirmation is required.
- **FTP uploads are atomic.** FTP uploads write to a uniquely named `.part`
  target and move into place only after a successful transfer, with rollback
  cleanup if upload or final rename fails.
- **Safer local renames.** Local file browser renames now ask before overwriting
  an existing target instead of replacing it silently.
- **Localized local-shell failures.** Local shell launch errors now use EN/FR
  localized messages instead of raw fallback strings.
- **Dependency advisory fixed.** TwinShell SQLite persistence moved to EF
  Core/Extensions 10.0.9 and `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3; the
  GHSA-2m69 audit suppression was removed.
- **UI polish and tests.** French button wrapping was tightened in reconnect and
  error surfaces, with coverage added for FTPS trust, FTP atomic uploads,
  local-shell localization, local rename overwrite confirmation, and config
  persistence. The automated test baseline is now 7,236 passing tests, 0
  failures.

## 2026-06-19: Safer update checks (v2026.061901)

This release hardens the in-app update check so Heimdall only offers an update it can
actually install, and tells you when a newer release exists that this build cannot install
on its own.

- **Version compared before assets.** The update check compares the release version first
  and reports "up to date" without requiring any installer asset, so an equal-or-older
  latest release no longer triggers a spurious failure.
- **Install offered only when verifiable.** A newer release is offered as a one-click
  install only when the installer for the running variant is present and a valid SHA-256 is
  published in `SHA256SUMS.txt`, preventing a "Download and install" that would fail only
  after downloading.
- **Newer-but-not-installable is now distinct.** When a newer release exists without a
  matching installer or checksum, the Settings page reports it as available-but-not-
  installable (with the version) instead of a generic check failure.
- **View the release page.** Settings now shows a "View release" action for both installable
  and not-installable updates, opening the release page through a scheme-validated launcher.
- **Internal.** New `UpdateNotInstallable` status and `ReleaseRef` carrier on the update
  result; the automated test baseline is now 7,215 passing tests, 0 warnings.

## 2026-06-18: Command Library quoting fixes (v2026.061803)

This release fixes a class of incorrect shell quoting in the bundled TwinShell command
library, so generated commands stay correct when a parameter is single-quoted in the
template, is a drive letter, or is a Microsoft Defender setting.

- **Correct quoting for embedded parameters.** Parameters sitting inside an existing
  single-quoted span of a template (for example `-Name '{vmName}'` or
  `-like '*{searchTerm}*'`) are no longer double-wrapped, so values with spaces or quotes
  produce valid commands. A new `InlineInQuotes` quoting mode drives this.
- **Drive-letter parameters.** Drive-letter inputs use a dedicated validated type and are
  no longer quoted, so `{driveLetter}:` produces `C:` instead of a broken `'C':`.
- **Microsoft Defender templates.** The three Defender commands now pass numeric and
  boolean values correctly (no stray `$`, no broken quoting).
- **Existing installations migrated.** A SchemaUpgrader step (v3) applies all of these
  fixes to databases created before this release; user-created commands are never touched.
- **Internal.** A shared `SyncServiceBase` removes JSON/YAML sync duplication, the Apache
  2.0 header was added to the remaining TwinShell sources, and the automated test baseline
  is now 7,181 passing tests, 0 warnings.

## 2026-06-17: One-click in-app updater (v2026.061702)

A user-facing release completing the in-app updater: an available update can now be
downloaded, verified and installed in one click, with Heimdall relaunching itself when
the install finishes.

- **One-click "Download & install".** Both the Settings "Updates" card and the startup
  update banner now offer a primary "Download & install" action. It downloads the new
  installer (with a progress bar and a Cancel button), verifies its SHA-256 checksum,
  closes active sessions, installs silently and relaunches Heimdall automatically.
- **Truly unattended install.** The installer no longer shows an "already installed,
  upgrade?" prompt during a silent update; that confirmation now appears only for
  interactive (double-click) installs, since the app already confirms before updating.
- **Internal.** The downloaded installer keeps its real ".exe" extension so it can be
  launched directly; SHA-256 verification is mandatory before any install; the automated
  test baseline is now 7,018 passing tests, 0 warnings.

## 2026-06-17 - In-app updater, French UI polish and Command Library authoring (v2026.061701)

A user-facing release adding a first in-app update experience, fixing French button
clipping in the RDP/SSH overlays, and making Command Library actions fully authorable
(examples, links and per-parameter help) with a guard before sending dangerous commands.

- **In-app updater (v1)** - Heimdall now checks for a newer release on startup (spaced
  out, configurable in Settings) and shows a non-blocking banner with "View release",
  "Later" and "Skip this version". A new "Updates" section in Settings adds a manual
  "Check now" button.
- **Verifiable release checksums** - every GitHub release now publishes a
  `SHA256SUMS.txt` asset, so installer integrity can be verified, and it is the
  foundation for the upcoming one-click update.
- **Command Library authoring** - examples and documentation links are no longer lost
  when editing an action and are now editable in the action dialog; parameters gain a
  description field shown as a tooltip; sending a command flagged dangerous now asks for
  confirmation, both in the dedicated view and the Ctrl+K palette; the search box is
  refocused after a reload; and the "copied" feedback only appears when the clipboard
  write actually succeeded.
- **French UI polish** - button labels are no longer clipped in the RDP and SSH error
  and reconnect overlays, and the bulk-edit dialog Cancel button is styled consistently.
- **Internal** - the automated test baseline is now 6,974 passing tests, 0 warnings, and
  the CI formatting gate was fixed to enforce a CRLF checkout for Windows text files.

## 2026-06-16 - Command Library explanations, accessibility and discoverability (v2026.061602)

A user-facing pass making Command Library snippets self-explanatory and reachable for
every skill level, in both the Ctrl+K palette and the picker, plus internal refactors.

- **Inline explanations** - drilling into a snippet in the Ctrl+K palette now shows its
  description, a risk badge, a platform badge and its notes, instead of only the title.
  The picker rows also show the description and a risk badge.
- **Examples and documentation links** - the snippet detail lists ready-made examples
  (copy or send) and the action's documentation links (click to open).
- **Live command preview in the picker** - the generated command is shown and updated
  as parameters are typed, before you confirm.
- **Accessibility** - risk and platform are announced to screen readers, parameter help
  is no longer hover-only, and keyboard focus follows the selected variant with a visible
  focus ring.
- **Discoverability** - palette search now also matches snippet notes, and the matched
  text is highlighted in the result rows.
- **Less clutter** - the palette command list shows template variants only; examples
  live in their own section and gain a send action.
- **Internal** - a pure `CommandPresentation` resolver is now shared by the palette and
  the picker, and the Git-sync result-to-dialog mapping was extracted to a pure mapper.
  No behavior change.

## 2026-06-16 - Command Library snippets in the palette and sessions tree scrolling (v2026.061601)

User-facing palette feature plus a sessions-tree readability fix and internal hygiene.

- **Command Library in the palette**: Ctrl+K now lists Command Library snippets,
  drills into a selected snippet with inline parameters (Windows/Linux templates
  and examples), and sends the resolved command to the active session terminal
  (Enter / Ctrl+Enter), with a clipboard fallback when no terminal is focused.
- **Sessions tree scrolling**: long session and tool names are now fully
  reachable. The sessions tree uses pixel scrolling with an automatic horizontal
  scrollbar and shows the full display name without ellipsis.
- **Internal hygiene**: import/export logic was moved into a dedicated
  `CommandLibraryTransferService`, and the Command Library action-service
  operations now share a single operation envelope. No behavior change.

## 2026-06-15 - ThemeForge public feed and honest CI formatting gate

Dependency and CI hygiene; no application code change.

- **ThemeForge dependency** - migrated to `ThemeForge.Theme` 2.0.0, restored
  from the public nuget.org feed. The private GitHub Packages source, its NuGet
  credentials and the `THEMEFORGE_PACKAGES_TOKEN` CI secret were removed, so the
  restore is now anonymous. The consumed API surface was unchanged.
- **CI formatting gate** - `dotnet format` previously ran before any build, so
  the WPF project Heimdall.App failed to load (its XAML markup compiler needs
  referenced assemblies on disk) and was silently skipped while the step still
  exited 0. A Debug build now runs before the format check, and the SDK is
  pinned via a new `global.json` (10.0.103, `rollForward` `latestPatch`) so CI
  runs the gate under a verified feature band.

## 2026-06-11 - Quality audit fix pass (v2026.061101)

Audit-fix release following the June 10, 2026 quality pass.

- **Network scanner** - added a reentrancy guard so repeated `Ctrl+Shift+N`
  cannot start two scans and two prompts at the same time.
- **Server list search** - added a roughly 300 ms debounce for large
  inventories while keeping clear/reset immediate.
- **Notes export** - moved file writing off the UI path so exporting notes no
  longer freezes the interface.
- **Accessibility and localization** - command-library example copy buttons now
  expose accessible names/tooltips, and the About window's Author label is
  localized.
- **Internal hygiene** - best-effort cleanup failures are logged, tunnel import
  order was normalized, the test baseline is documented at 6,810 passing tests,
  and internal audit artifacts were removed from the public tree.

## 2026-06-10 - Embedded VNC connection fix (v2026.061006)

Restored embedded VNC sessions and tightened their diagnostics.

- **VNC connection** - the internal WebSocket proxy now advertises the
  subprotocol expected by the WebView2/Chromium viewer, so embedded VNC
  connects normally both direct and through SSH gateways.
- **Reconnect UI** - the VNC display surface is cleared after disconnect so the
  reconnection panel is visible instead of hidden behind the viewer.
- **Diagnostics** - detailed VNC connection failures are now written to the
  application log.
- **Tests** - baseline moved to 6,808 passing tests with coverage for WebSocket
  subprotocol negotiation.

## 2026-06-10 - Cross-application UX pass (v2026.061002)

Grouped six UX-audit PRs covering dialogs, localization, theming and session
states.

- **Dialogs and keyboard** - dialogs now use the themed dialog service, global
  error dialogs hide stack traces, Escape maps to the non-destructive action,
  and repeated F1 no longer stacks duplicate help dialogs.
- **Localization** - corrected double-encoded strings across English/French and
  made the French UI consistently use formal address.
- **Theme and accessibility** - hardcoded colors were migrated to theme
  resources, with localized accessible names for status, favorite and paste
  preview surfaces.
- **Sessions** - WinRM has its own connection state, progress indicators cover
  all protocols, and SFTP/FTP/VNC/Citrix disconnected states expose clear close
  or reconnect actions.
- **Quality gates** - added static guards for locale encoding, dialog keyboard
  contracts and themed colors; baseline 6,802 passing tests.

## 2026-06-08 - SFTP and WinRM fundamentals hardening (v2026.060801)

Security and clarity pass for file transfer and remote shell basics.

- **SFTP safety** - elevated uploads create owner-restricted temporary files and
  always clean them up, while create/rename rejects empty, dot and path-traversal
  names.
- **Connection logs** - SSH/SFTP connection logs no longer include usernames or
  identity details.
- **Error clarity** - SFTP/FTP failures and protected-root deletion now surface
  localized, actionable messages.
- **WinRM diagnostics** - common NTLM loop and WS-Man 12152 failures show
  localized status hints without altering terminal output.
- **Internal hardening** - host-key fingerprints are compared in constant time,
  sudo indicators follow theme resources, and protocol constants/guards were
  tightened.

## 2026-06-07 - Terminal reconnection lifecycle fixes (v2026.060704)

Improved reconnection behavior and process-exit detection for terminal-backed
sessions.

- **Telnet reconnect** - network interruptions now trigger automatic
  reconnection, while voluntary disconnects still close cleanly.
- **Local Shell and WinRM** - process exit detection no longer depends on output
  flow, so fast or silent exits show the manual reconnect overlay reliably.
- **Host-key dialog** - French host-key verification buttons no longer truncate.
- **Tests** - added protocol-category reconnect coverage and ConPTY exit replay
  coverage.

## 2026-06-07 - Tunneled RDP and SSH robustness (v2026.060703)

Security and reliability fixes for tunneled RDP/SSH sessions plus gateway
display cleanup.

- **RDP credentials** - simultaneous external RDP sessions through the same SSH
  gateway now use distinct loopback aliases, isolating Windows Credential
  Manager entries per target.
- **Gateway references** - gateway IDs are compared case-insensitively in the UI
  so valid references are no longer shown as orphaned.
- **Tunnel ports** - local tunnel ports are allocated by the OS at open time,
  removing a rare race where an ephemeral port could be claimed before use.
- **Tests** - expanded coverage for tunnel port allocation, loopback aliases and
  RDP credential isolation.

## 2026-06-07 - SSH gateway import/export and repair (v2026.060702)

Completed first-class handling for SSH gateways across configuration workflows.

- **Import/export** - exports now include gateway definitions and imports
  reconcile them by identity, preserving session-to-gateway links between
  machines.
- **Inventory visibility** - routed sessions show a gateway badge, missing
  gateway references show a warning, and Settings now includes a gateway
  overview panel.
- **Repair workflow** - orphaned gateway references can be cleared or reassigned
  to an existing gateway for a whole group of sessions.
- **Security and internals** - gateway secrets remain excluded from exports, and
  the import reconciler plus grouped gateway mutations are covered by tests.

## 2026-06-06 - SSH/SFTP tunnel security and data safety (v2026.060602)

Security and reliability release for tunneled SSH/SFTP, remote editing and
WinRM/RDP cleanup.

- **Host-key verification** - tunneled SSH/SFTP connections now pin and verify
  the real target identity for SSH.NET, Plink and external PuTTY.
- **Remote file safety** - elevated edit uploads restrict temporary files,
  upload fallback preserves originals, and editor upload tracking is atomic.
- **Downloads and deletion** - remote filenames are constrained to the
  destination folder, FTP downloads are atomic, and root deletion is blocked
  including sudo paths.
- **Protocol fixes** - WinRM SSL/certificate options persist correctly, missing
  PowerShell hosts show a clear error, RDP ACL failures are non-blocking, and SSH
  agent protocol/time-out diagnostics are clearer.
- **Cleanup** - orphaned Plink password files are swept at startup, WinRM
  bootstrap secrets are zeroed, dead code was removed, and tunnel reference
  counting is atomic.

## 2026-06-06 - SSH reconnect and tools polish (v2026.060601)

Small release focused on SSH connection behavior and tools readability.

- **Plink reconnect** - failures during the initial Plink connection phase no
  longer trigger automatic reconnect loops.
- **Localization** - SSH probe messages and embedded-session labels are now
  localized in English and French.
- **Tools tab** - externally detected tools are grouped by provider for easier
  scanning.
- **Configuration** - SSH reconnect, keep-alive, tunnel and Plink timings were
  centralized in settings and schema validation.

## 2026-06-05 - External SSH and RDP trust options (v2026.060502)

Security and usability improvements for external PuTTY, RDP and reconnect
surfaces.

- **PuTTY host keys** - external PuTTY launches now honor Heimdall's host-key
  policy by passing known fingerprints through `-hostkey`.
- **RDP server authentication** - added optional per-profile/global strict server
  authentication when NLA is enabled.
- **Session tabs** - right-clicking a session tab exposes profile actions such
  as edit, copy host and copy username.
- **Reconnect accessibility** - SSH reconnect overlays move keyboard focus to
  their primary action when shown.

## 2026-06-05 - WinRM HTTPS and session shortcuts (v2026.060501)

WinRM HTTPS support plus session keyboard and lifecycle fixes.

- **WinRM certificates** - profiles can opt into ignoring certificate
  validation for trusted internal HTTPS hosts with self-signed certificates.
- **Command palette** - `Ctrl+K` opens quick access even while focus is inside
  SSH, VNC or RDP sessions.
- **RDP credentials** - NetBIOS-domain usernames such as `DOMAIN\user`
  auto-connect without prompting again.
- **Reconnect policy** - Local Shell and WinRM no longer auto-reconnect in loops
  when their process exits; network sessions keep recoverable reconnects.
- **Cleanup** - WinRM temporary bootstrap scripts are cleaned at session end and
  startup, including best-effort fallback paths.

## 2026-06-04 - SSH, SFTP and WinRM quality pass (v2026.060403)

Fundamentals release for safer transfers, clearer remote-shell status and
reconnectable diagnostics.

- **SFTP transfers** - downloads and uploads are atomic, sudo saves can recover
  from permission denial, sudo listings are cancellable, reconnect returns to
  the previous folder, and skipped folder downloads are reported honestly.
- **SSH sessions** - automatic reconnect avoids fatal credential errors, `exit`
  closes cleanly, and validation messages are localized.
- **WinRM sessions** - status no longer claims "connected" before handoff, and
  configuration failures are localized instead of exposing raw exceptions.
- **Diagnostics** - failed SFTP/RDP/WinRM connections open a structured
  reconnectable failure panel, and failed reconnect diagnostics remain visible.

## 2026-06-03 - RDP resilience and diagnostics (v2026.060301)

RDP-focused release after runtime validation of connection, fullscreen and
shutdown edge cases.

- **Connection diagnostics** - RDP authentication and extended disconnect
  reasons are classified more accurately, avoiding pointless reconnect loops for
  server-side refusals.
- **Handshake watchdog** - frozen RDP handshakes are cut off cleanly with an
  explicit reconnect/close overlay.
- **Fullscreen and focus** - F11, toolbar and context-menu fullscreen now share
  one implementation, and focus returns to the RDP surface only when appropriate.
- **Reconnect UI** - disconnect code 1800 and administrator-initiated
  disconnects are surfaced, and cancelled auto-reconnect keeps an accessible
  overlay visible.
- **Stability** - fixed two shutdown crash paths, wired `rdpKeepAliveIntervalMs`
  end to end, restored legacy split-layout loading and documented the 6,441-test
  baseline.

## 2026-06-02 - Release packaging baseline (v2026.060201)

Packaging-only release with no functional notes attached to the GitHub release.

- **Standard portable** - published the standard zip for systems with
  Edge/WebView2 already available.
- **Self-contained portable** - published the bundled WebView2 zip.
- **Installers** - published both Standard and SelfContained setup executables.

## 2026-05-30 - FTP FluentFTP migration

Replaced the FTP browser's deprecated `FtpWebRequest` backend and home-grown
LIST parser with FluentFTP `AsyncFtpClient`.

- **Runtime** - added FluentFTP 54.2.0 to `Heimdall.Sftp`; FTP/FTPS operations
  now use true async client APIs, keep the existing `IRemoteBrowser` contract,
  and serialize FTP client access through the existing operation lock.
- **Security** - explicit FTPS now enables FluentFTP data-connection
  encryption while credentialed cleartext FTP still surfaces the existing
  non-blocking warning.
- **Tests** - removed Unix/DOS LIST parser fixtures and replaced them with
  FluentFTP `FtpListItem` mapping coverage. Build green, **5,985 passing**,
  zero warnings.

## 2026-05-30 - TwinShell dead-code removal

Removed a cluster of TwinShell services that were never wired into Heimdall - 
the DI bridges (`HeimdallSettingsBridge` / `HeimdallLocalizationBridge`) and the
inline bootstrapper seed replace them. Supervisor reconnaissance grepped the
exact symbol across all of `src/` and `tests/` (not just the bootstrapper),
which surfaced two domino pairs (`Backup`→`Config`, `BatchExecution`→`Audit`).

- **Deleted (21 files, −3,320 lines)** - `BackupService` / `IBackupService`,
  `ConfigurationService` / `IConfigurationService`, the native `SettingsService`
  class, `ImportExportService` / `IImportExportService`, `BatchExecutionService`
  / `IBatchExecutionService`, `AuditLogService` / `IAuditLogService` and the full
  Audit cascade (`AuditLogEntity`, repository, EF configuration, `AuditLogs`
  DbSet), `JsonSeedService` / `ISeedService`, `BatchExecutionResult` (`867131b`).
- **Kept (proven live)** - the `ISettingsService` *interface* (GitSync /
  HealthCheck / Theme / Backdrop consume it via `HeimdallSettingsBridge`) and the
  whole `CommandBatch` cluster (one of the four PublicId tables, live through
  JSON/YAML sync + DI + tests).

Pure deletion, zero functional change. Build green, **5,979 passing**, zero
warnings. CI run `26662106337` success.

## 2026-05-30 - TwinShell versioned schema runner (F1 + F3)

Replaced the fragile schema-bootstrap path with a versioned `PRAGMA user_version`
runner, closing audit findings F1 (no migration runner - `EnsureCreatedAsync`
never alters an existing DB) and F3 (non-transactional ALTER→UPDATE→CREATE INDEX
that could leave PublicId columns empty on a mid-upgrade crash).

- **`SchemaUpgrader` / `SchemaStep` (new, `src/TwinShell.Persistence/Schema/`)** - 
  reads `PRAGMA user_version`, applies steps with `Version > current` in
  ascending order, **one transaction per step** with the version bump inside the
  same transaction and best-effort logged rollback (`c698961`).
- **Live wiring + dead-code removal** - `TwinShellSchema.Steps` carries one
  idempotent step (GitOps PublicId columns, byte-identical UUID SQL);
  `TwinShellBootstrapper.InitializeAsync` calls `SchemaUpgrader.UpgradeAsync`
  after `EnsureCreatedAsync`. Dropped `EnsureGitOpsSchemaMigrationAsync`,
  `AddPublicIdColumnIfNotExistsAsync`, the design-time factory, the dead EF
  migration and the `EntityFrameworkCore.Design` package (`3cc69f1`).

Fresh DBs (`EnsureCreated`) and legacy DBs both converge via `user_version 0 → 1`.
Convention for the future: any TwinShell schema change is a new
`SchemaStep(N, …)`, ascending, idempotent, transactional - never an EF migration
or an out-of-transaction ALTER again. **+10 tests** (5,969 → 5,979), build green,
zero warnings. CI run `26656228551` success.

## 2026-05-29 - TwinShell sync hardening

Made the bundled TwinShell sync layer cancellable end-to-end and its export an
authoritative mirror of the DB, closing audit findings J1/J5/J3/J2 and G1/G5/G2.
The layer went from **0 to 24 tests** (`d3d7a1e`).

- **Real cancellation** - `CancellationToken` threaded through `ISyncService`
  import/export, `JsonSyncService` (rollback + rethrow on cancel so GitSync maps
  `Cancelled`), `YamlSyncService`, and every internal GitSync operation
  (clone/fetch/merge/import/export/stage/commit/push) including network abort via
  `OnTransferProgress` / `OnPushTransferProgress`. The visible **Cancel** button
  is no longer cosmetic.
- **CTS race + leak fixed** - `_currentCancellationTokenSource` created /
  assigned / disposed under a dedicated lock (G5); `GitSyncService` is now
  `IDisposable` and disposes its `SemaphoreSlim` + CTS (G2).
- **Export hygiene** - atomic per-file write via `*.tmp` → `File.Move` overwrite
  (J5), collision-safe `Name-{PublicId:N}.json` naming (J3), and orphan cleanup
  so the export folder mirrors the DB across the four managed folders (J2). A
  cancelled export deletes nothing.

**+24 tests** (5,945 → 5,969), build green, zero warnings. First real export
against an existing folder renames every file under the new naming scheme and
removes the old ones - a one-time large git diff, expected.

## 2026-05-29 - CI housekeeping

Migrated the GitHub Actions runner image to `windows-2025` (VS 2026 toolchain)
and bumped the workflow actions to their Node24 majors (`actions/*` v6/v5/v7)
(`b3ab296`). No production change. CI run `26645789164` success (5m38s).

## 2026-05-29 - Terminal transcript: stateful ANSI/VT strip (T-1 D5-bis)

Closing follow-up to the 2026-05-26 UTF-8 transcript decoder (D5). The stateful
UTF-8 decoder fixed multi-byte fragmentation, but the ANSI strip was still a
**stateless** regex applied per chunk, so a VT sequence split across two chunks
(e.g. `\x1b[31` then `m`) leaked into the transcript in cleartext.

- **`StreamingAnsiStripper` (new, `src/Heimdall.Terminal/Logging/`)** - a pure
  char-level state machine (`Normal` / `AfterEsc` / `Csi`) that buffers an
  incomplete escape sequence between `Strip()` calls. API `Strip()` / `Flush()`
  / `Reset()`, with `Flush()` discarding any pending partial. Invalid chars are
  replayed in `Normal` via a `bool consumed` return (index not advanced), which
  reproduces the regex backtrack exactly, ESC-then-ESC included (`0d12a37`).
- **Strict regex equivalence proven by oracle test** - the test reuses the old
  `AnsiEscapeRegex` pattern as the reference on a self-contained token corpus
  plus 500 pseudo-random inputs (seed `20260529`). The regex's OSC-body quirk
  (`]` captured by the Fe class before the OSC alternative) and the `ESC7` /
  `ESC8` passthrough are **preserved deliberately** - D5-bis only addresses
  fragmentation.
- **`EmbeddedSshView` integration** - `_transcriptStripper` wired into
  `WriteToTranscript`, unconditional `Flush` residue on
  `StopTranscriptInternal`, and `StartTranscript` reordered so the old
  transcript flush runs *before* the decoder/stripper `Reset()`. The dead
  `AnsiEscapeRegex` field and its `using` were removed.

Test-only risk profile: 1 production file touched (`EmbeddedSshView`) + 2 new
files. Six new tests (corpus + random equivalence, fragmentation invariance over
every cut point, cross-chunk CSI, Flush discard, Reset, invalid ESC). Build
green, **5,928 passing**, zero warnings. Latent out-of-scope: OSC body leak +
ESC7/8 passthrough in the transcript (minor, never requested).

## 2026-05-28 - CI deflake bundle + Citrix launcher resolution

Two pair-architect chantiers: re-greening CI on master after the Citrix merge,
and a Citrix Workspace App launcher-resolution + inline sign-in spike.

### CI deflake bundle (3 commits)

- **`WpfTestHost` startup timeout 10s → 60s** (`dc0acbe`) to absorb WPF + DI +
  ThemeForge + TwinShell DB-seed init latency on the GitHub Actions Windows 2025
  runner. Resolves all 79 `Heimdall.App.UiTests` failures at once.
- **`CommandCredentialProvider` test timeout bump** - first a surgical single-test
  bump (`59db3f1`, quickly superseded), then a structural refactor (`8b90d5f`)
  introducing a local `CreateProvider` factory with `TestTimeoutMs = 60000`
  routing ~30 test sites; production code untouched (`timeoutMs` default stays
  10s). Final CI run on `8b90d5f` green in 6m24s, 5,897/5,897 (filter
  `Category!=CIUnstable`). The deblock was achieved purely by timeout bumps - no
  new `CIUnstable` tags added.

### Citrix launcher resolution + inline sign-in (`19a8cf6` merge, `92f803a`, `248f20d`)

- **StoreBrowse / SelfService resolution on CWA 2507+** (`92f803a`) - handles the
  new `AuthManager` / `SelfServicePlugin` subfolders via a pure
  `BuildCitrixLauncherCandidates` helper, covered by 4 xUnit tests.
- **Inline embed of the Workspace sign-in window** (`248f20d`, Option 2b) - when
  the sign-in window is foreground, capture is done by diffing window handles
  rather than PID (so apps launched in a shared `wfica32` session are caught),
  cancellation propagated to `WatchForSessionAfterAuthAsync`, fire-and-forget
  observed via `_authWatchTask` / `_authWatchCts`, dedicated i18n key
  `CitrixAuthSignInHint` (EN + FR).

Runtime validation **not performed** - CWA is absent from the current dev box
(`Test-Path` negative on all 8 candidates). Residual risk vs master: nil (every
untested path falls back to external mode). Build green, zero warnings.

## 2026-05-27 - Release.bat encoding fix

- **`Release.bat` ASCII + CRLF + `REM`** (`a48d23a`) - an em dash ` - ` (UTF-8
  `e2 80 94`) on line 2, LF-only EOLs, and `::` comments combined to make
  `cmd.exe` misread the file under its OEM codepage, break the `::` label, and
  eventually evaluate `Heimdall` as a command. Fix: 3 comment lines
  reworded (`::` → `REM`, ` - ` → `-`), EOL converted to CRLF, pure ASCII, no BOM.
  No `.cs` touched, tests unchanged, build green.

## 2026-05-26 - Terminal/ConPTY quality audit (T-1) + release v2026.052601

Pair-architect quality audit of the terminal subsystem (audit report
`docs/audit/audit-terminal-conpty-2026-05-25.md`). Verdict: 0 P1 / 8 P2 / 19 P3.
Closed 8/8 P2 and 14/19 P3 across an 8-chunk audit, then the deferred D-backlog.
Release **v2026.052601** (`860eccf`) was cut between the audit and the D-backlog.

### 8-chunk audit (P2/P3 close, spanned 2026-05-25 → 26)

- **Session lifecycle cleanup hardened** (`e71a476`, A1 - P2-1/5/6).
- **Session event-callback safety** (`f061345`, A2 - P2-3).
- **WebView2 trust boundary + dispatcher hygiene** (`d48e3dc`, B - P2-4 / D3 / D15 / D16).
- **Stateful Telnet parser + `IsRunning`** (`0d3fba7`, C - P2-2 / D17).
- **SmartPasteGuard Windows/PowerShell coverage** (`1b0fda4`, D - P2-7 / D18).
- **`Heimdall.Terminal.Tests` project introduced** (`4346d97`, E1 - D19).
- **Dedicated session tests** (`6299a78`, E2 - P2-8).
- **P3 quick-win sweep** (`9f38c89`, F). Test count 5,847 → 5,879.

### Deferred D-backlog (5 commits)

- **Clamp resize values + dedup failure logs** (`e4bf9e1`, D4) - pure
  `ResizeFailureLogThrottle` component (`{Skip, LogCurrent,
  LogRepeatSummaryThenCurrent}`, thread-safe), dedup signature excludes
  dimensions so a terminal drag cannot bypass it; `Math.Clamp(value, 1, 999)`
  in `ResizeSession`. +8 tests → 5,887.
- **Localize embedded terminal page strings** (`d9c9241`, D13) - pure
  `TerminalHtmlLocalizer` substitutes 3 markers in `terminal.html` with
  context-aware encoding (`WebUtility.HtmlEncode` for HTML, `JsonSerializer`
  for the JS literal) and explicit EN fallback. 3 new locale keys. +9 → 5,896.
- **Stateful UTF-8 transcript decoder** (`73f7e90`, D5) - pure
  `StreamingUtf8Decoder` wrapping `Encoding.UTF8.GetDecoder()` via the
  single-pass `Decoder.Convert(...)`; only the `WriteToTranscript` site had real
  multi-byte fragmentation risk. +10 → 5,906.
- **`CancellationToken` through `ITerminalSession.StartAsync`** (`c8b0d0f`, D1) - 
  optional trailing token (BCL convention). ConPty/PipeMode bail out via
  `ThrowIfCancellationRequested()`; Telnet links the token to its internal CTS,
  making the TCP connect truly cancellable. 4 call sites + 2 test fakes aligned.
  +3 → 5,909.
- **WinRM credential plaintext reduction** (`f81825b`, D8) - `byte[]` +
  `CryptographicOperations.ZeroMemory` end-to-end instead of `SecureString`
  (deliberate deviation: MSFT discourages `SecureString` on modern .NET and
  DPAPI consumes `byte[]` anyway). New `DpapiProvider.UnprotectToBytes` /
  `ProtectBytes`, `HmacIntegrity.UnprotectToBytesWithHmac`,
  `CredentialProtector.UnprotectToBytes`. +9 → 5,918.

### Housekeeping

- **CI flake tags** - `ConPtySession` startup test (`601b0cc`) and
  `TcpPingViewModel` mixed-results test (`d929b81`) tagged
  `[Trait("Category", "CIUnstable")]` (GHA Windows runner timing).
- **HEAD secret scrub** (`629db10`) - redact internal hostnames and an employee
  id from `HEAD`.
- **Docs sync** (`39c98c0`) - test/project counts post-T-1.

Build green, zero warnings. Test count 5,847 → 5,918 over the chantier.

## 2026-05-25 - SFTP/FTP/file-server audit + EmbeddedSftpView MVVM refactor (AD-1)

Two quality audits (SFTP/FTP core, then the App-side SFTP/FTP layer) plus the
AD-1 MVVM refactor of `EmbeddedSftpView`. ~40 commits; grouped below by theme.

### SFTP/FTP core hardening

- **Binary-safe sudo download/upload via base64** for both edit and embedded
  paths (`814bbfb`, `466db09`).
- **Symlink-safe delete + partial-download cleanup** in `SftpBrowser`
  (`a19a76a`), recursive directory delete + symlink/timestamp parsing fixes in
  FTP (`b240412`).
- **Remote-edit auto-upload trailing-edge debounce** so the last save in a burst
  is never lost (`5680d2e`); edit temp-file cleanup on failure + stop on
  duplicate session (`84c4e8d`); confirm remote save before clearing the
  modified flag (`63a11c3`).
- **Temp-dir leak closed + transfers serialized** (`592cbf7`); error-reset timer
  disposed and state reset on failed reconnect (`7a5f2a3`); native transfers
  cancellable without crashing (`c9f628e`).
- **Sudo auth via stdin password feed** with clear failure surfacing
  (`6b2a7e9`, `e5374f8`); `SftpHandler` input validation (`f03e3a0`).
- **Local file browser** - recursion + path-containment hardening (`fef54af`),
  filesystem I/O moved off the UI thread (`213a222`).
- **File server / TFTP** - TFTP handling + HTTP response headers hardened
  (`431f8a7`), magic numbers replaced + start guarded (`e45d4af`), a
  TFTP-unauthenticated warning surfaced on share start (`5fdb736`).

### AD-1 - EmbeddedSftpView MVVM refactor

Drove the view from bindings/commands instead of code-behind (code-behind
1623 → 1145 lines): selection-free and selection-based actions bound to commands
(`253b58d`, `70b75b3`), visual state via triggers (`3631f73`), transfer
orchestration moved to the ViewModel (`f6ca235`), toolbar/connection buttons
binding-driven (`0adb687`), localization migrated to `{loc:Translate}`
(`f2cc67c`), MVVM split documented (`0ff5a9c`). Toolbar + disconnect-overlay UX
polish (`c2a72bc`).

### RDP

- **DPI scale tables consolidated onto `RdpDisplayHelper`** (`119b8b6`).

Tests covered (`a8e2ae5`, `da4590f`). Build green, **5,847 passing**, zero
warnings, EN/FR locale parity preserved.

## 2026-05-24 - Quality audit wave: splits, SSH/tunnel, RDP/ActiveX, i18n gate

A wide audit day across four subsystems plus a new RDP domain field.

- **Split-system audit closed** (`689ea44`, S1–S10 / S12).
- **SSH/tunnel audit** (`8911803` chunks A/B/D/E; `587abe6` H4–H7 final
  hardening) - `FailureClassifier` connection-error classification hardened
  (`4c4ef75`), `KnownHostsParser` multi-colon host tokens validated (`dbdc87f`).
- **RDP/ActiveX audit** - MsTscAx event sink guarded against subscriber
  exceptions (`617c78f`), keyboard-hook callback guarded (`15b54c1`), external
  credential autofill decoupled from the connect-scoped token (`e28d1a9`),
  negative monitor indices rejected in `ValidateMultimon` (`29bb0ad`), the
  non-functional `selectedmonitors` fallback removed (`5c6af8b`), `.rdp`
  generation hardened with explicit CRLF + field validation (`069db12`),
  `EmbeddedRdpView` event-handler/timer lifecycle hardened (`6f13285`),
  connectivity-test invalid-input outcomes localized (`2e5dc8f`), `LastError`
  set on Connect failure + credential-dialog logging trimmed (`099c196`),
  magic numbers named (`75b4820`), dead constants/test relocations (`8ae4f6c`,
  `680b3d3`, `ed03f9e`), stale comments corrected (`55ff6e8`). New RDP coverage:
  disconnect-reason decoder, `RdpSelectedMonitorValidator`, `RdpShortcutParser`,
  external credentialed decrypt-failure (`db27634`, `0acf6f4`, `f29fb68`,
  `86c13e9`).
- **Explicit RDP domain field** added to the ServerDialog with runtime wiring
  (`d0c34d6`, `b357ff1`).
- **i18n gate** - XAML `{loc:Translate}` keys now gated against the locale file
  in CI (`73ba1ae`); missing RDP auto-reconnect / keep-alive labels added
  (`0dabd79`).
- **Polish** - reconnect-overlay message inset (`198edc1`), empty band above the
  first card on Settings sub-tabs trimmed (`198f097`). The SSH-gateway `12152`
  WS-Man limitation documented as environmental (`7c13fe6`).

Build green, zero warnings, EN/FR locale parity preserved. *(Per-chantier test
baseline for this day not recovered from git - backfill if strict convention
parity is wanted.)*

## 2026-05-23 - WinRM-via-gateway + tunnel ref-count fixes + Settings UX

Releases **v2026.052301** (`26f40c8`), **v2026.052302** (`eb20d94`),
**v2026.052303** (`3030ed9`).

- **WinRM-via-gateway** - WinRM profiles can route through an SSH gateway
  (`b406c11`), with gateway selection in the profile UI (`9bf72bb`). HTTP-only;
  `WinRmUseSsl` + gateway is rejected.
- **Tunnel reference-count leak closed** on every protocol exit path: RDP
  (`29b99d2`), SFTP (`cb703c1`), SSH.NET + Plink (`2d3bdff`), external PuTTY
  (`7785e64`).
- **WinRM polish** - ServerDialog UI + connection-path diagram (`3fbe7e2`);
  credential bootstrap no longer aborts on Windows PowerShell 5.1 (`ba1d062`).
- **Stability** - close-time `NullReferenceException` with active sessions
  prevented (`79837dc`); terminal `convertEol` applied once the pipe session is
  attached (`0e0e632`).
- **Settings UX overhaul** - search reworked to locate and highlight a setting
  (`3a66e3f`), per-tab validation error badges (`62129f0`), validation feedback
  fix (`56eea3c`), RDP tab segmented (`9b2a7e8`), Advanced tab restructured with
  server import/export relocated (`da87b53`), RDP resolution-preset labels
  (`66f34f0`), missing accessibility labels (`029a4b8`).
- **Docs** - slow-server RDP cutoff capture procedure
  (`docs/repro/...`, `498c875`); optional SSH-gateway routing for WinRM in the
  README (`ccceeb0`).

Build green, zero warnings, EN/FR locale parity preserved. *(Per-chantier test
baseline for this day not recovered from git.)*

## 2026-05-22 - WinRM 9th protocol completion + NLA parity + RD Gateway UI

Release **v2026.052201** (`7fe7bd1`). WinRM lands as the 9th protocol (ConPTY +
`Enter-PSSession`, credential injected via a self-deleting `.ps1` bootstrap).

- **WinRM runtime** - profile/config/UI support (`6dcc0a1`), launch bootstrap
  (`b55117f`), connection dispatch + embedded runtime wiring (`e3114bc`),
  PowerShell launch correctness (`d0755b2`), transport preflight check
  (`f3ad97c`) with revocation false-negatives avoided (`4dd6c74`).
- **RDP** - RD Gateway exposed in the UI and applied for embedded sessions
  (`fb3aade`); embedded RDP authentication level aligned with the `.rdp`
  generator, i.e. **NLA #16 external parity** (`074c70b`); RDP auto-reconnect
  cancelled when the SSH gateway cannot reach the target (`ec96ecd`); chained
  gateway-unreachable diagnostic emitted (`9460162`).
- **ServerDialog restructure** - four-tab layout (`9afb5a2`) with per-protocol
  visibility + per-tab error badges (`8670873`), duplicate adorner text fixed
  (`c63df2b`), freely resizable with single-level tab scrolling (`a288aeb`).
- **RDP Options sub-tabs** - split into four sub-tabs (`6877e40`) with a
  segmented look, spacing/sectioning/focus refinement (`c8cbf14`, `ecd7e00`),
  roomier focus ring on checkboxes/radios (`617d7f5`), `InputBorderBrush`
  outline (`598f87b`), session-tree left inset (`1369d81`).
- **Repo hygiene** - `.gitattributes` added + EOLs normalized to LF
  (`f3b5248`); `CLAUDE.md` kept local/gitignored (`936ff81`); README/CLAUDE.md
  refreshed for WinRM (`716b34f`, `d7a1ddb`).

Build green, zero warnings, EN/FR locale parity preserved. *(Per-chantier test
baseline for this day not recovered from git.)*

## 2026-05-20 → 21 - ThemeForge theme engine migration + accent tint selector

Heimdall's bespoke theme engine was replaced by the private `ThemeForge.Theme`
NuGet package (16 canonical themes, app default `Drakul`).

- **Package consumed from GitHub Packages** (`a16f34e`); CI offline NuGet source
  + package-token plumbing for the vulnerability scan (`d55d745`, `8bfe27e`).
- **`HeimdallThemeService`** added as the app wrapper around
  `ThemeForge.Theme.ThemeService`, preserving Heimdall's compatibility surface
  (`ApplyTheme`, `CurrentTheme`, `ThemeRevision`, `ThemeChanged`) (`274d73f`).
- **`HeimdallThemeBridge` adaptation layer** (`a336208`) re-expresses Heimdall's
  app brush keys on ThemeForge color slots; the app is switched onto the
  ThemeForge engine (`b97017e`), the theme selector rebuilt on ThemeForge
  palettes (`c9ba6f3`), and the WebView2 background retargeted to the ThemeForge
  slot (`8735e6a`).
- **Accent tint selector** - the ThemeForge accent tint wired through
  `HeimdallThemeService` (`9b10b48`) and exposed as a 9-tint selector in
  Appearance settings (`87072dc`).
- **Post-migration regression sweep** (`9df0226`) - form controls given a
  contrasted resting border (`6b2d387`), dialog cards pointed at the existing
  `CardBrush` (`db77296`).
- **Adjacent fixes** - `OnLoginComplete` COM event DISPID corrected (`9b59cf7`),
  RFC 8332 RSA-SHA2 host-key algorithms recognized (`be4c904`), DNS pre-warm
  task exceptions observed (`6532806`), health probe throttle scoped per cycle +
  CTS disposal deferred (`62a084b`), SSH forwarded-port failures captured for
  diagnostics (`0d5fa38`), gateway-to-target unreachable reported in the RDP
  disconnect overlay (`04b5792`), pane-host detach skipped during shutdown
  (`5b8deab`), generic session-overlay actions routed by tab scope (`7e9a9db`).
- **Docs** - ThemeForge migration documented + agent guidance versioned
  (`cca0c4c`, `f9ab25b`).

Build green, zero warnings, EN/FR locale parity preserved. *(Per-chantier test
baseline for this day not recovered from git.)*

## 2026-05-17 - UX/polish series + Session Health Monitor + sidebar compaction

Ten commits across four chantiers. Tests baseline moved from 5,500 to **5,557 passing + 6 skipped** (+57 covering localizers, the extended `ServerStatusToColorConverter`, ViewModel change-notification, the full health monitor pipeline including port resolver / gateway short-circuit / probe state machine, and the new Settings validation). Locale parity now **5,543 keys EN/FR** (+22 keys this session).

### WCAG visual contrast pass (3 commits)

The 7 light-pastel-accent Dracula variants were painting `#FFFFFF` on light accent backgrounds for the PrimaryButton, CheckBox glyph, and RadioButton dot - roughly 2:1 contrast where WCAG AA requires 4.5:1. A follow-up sweep also found 17 sites painting semantic text in the raw `SuccessBrush`/`WarningBrush`/`ErrorBrush` instead of the WCAG-tuned `*TextBrush` variants, which under-read on the 5 light themes.

- **`TextOnAccentBrush` rebased to the theme background color** for DraculaPro, Drakula, Blade, Buffy, Bathory, Lincoln, VanHelsing, and Morbius, lifting button text contrast from ~2:1 to 5.5–7.6:1 (`fd637cd`). Akasha and Striga already followed this pattern; the fix generalized it.
- **CheckBox check glyph and RadioButton inner dot** switched from `TextPrimaryBrush` to `TextOnAccentBrush` so they remain legible on the same pastel accent fills when checked (`b048d5d`).
- **17 status text usages switched to `*TextBrush`** across `MainWindow.xaml`, `EmbeddedRdpView.xaml`, `ServerDialog.xaml`, `CommandLibraryView.xaml`, and seven other tool/dialog views (`7ab73ed`). On dark themes the `*TextBrush` keys are identical to the plain semantic brushes, so the change is invisible there and corrects only the light themes.

### Command Palette (Ctrl+K) overhaul (3 commits)

- **Phase A - Unified fuzzy ranker** (`dfd349f`): the old `TryParseToolCommand` (87 lines) early-returned on any tool-prefix match, hiding server matches that shared a prefix, and only matched tools by their `CommandPrefixes`. The new pipeline isolates explicit argument-bearing invocations (`ping 8.8.8.8`, `subnet 10.0.0.0/8`) for early return, then scores tools (localized label + aliases + category), external tools, and servers in one pass before sorting and taking the top 20. Queries like `calculator`, `formatter`, or `encoder` now surface the matching tool while server fuzzy matches still appear alongside. Split into 4 focused helpers: `TryParseExplicitToolInvocation`, `ScoreToolDescriptor`, `BuildToolPaletteItem`, `BuildExternalToolItem`.
- **Phase B - Snippets indexed in the palette** (`9ba0bc0`): the 500+ TwinShell action library was previously unreachable from Ctrl+K. The palette refreshes a per-open snapshot via a scoped `IActionService`, scores snippets by Title (full weight), Tags (full weight), Description and Category (halved), and routes selection to a clipboard copy + status message - snippets are clipboard-only, never split, connect, or interrupt the active session. The dispatch path intercepts `snippet-*` Ids before any split/connect routing so a snippet cannot accidentally open a tab or merge a pane. `ResolveSnippetCommand` falls back Windows template → Linux template → first example → action title. Locale keys `PaletteSnippetsHeader` and `PaletteSnippetCopied`.
- **Phase C - Visual section headers** (`9211bb5`): the flat ListBox now consumes a `CollectionViewSource` with a `PropertyGroupDescription` on `Group`. A new `PaletteGroupHeaderConverter` normalizes empty Group values (most ad-hoc and ungrouped servers) to a localized `Servers` / `Quick Connect` fallback so no untitled section ever renders. The textual `· {Group}` suffix on each row was removed - headers carry the categorization now. Locale keys `PaletteQuickConnectHeader` and `PaletteServersHeader`.

### Session Health Monitor (3 commits)

New background reachability monitor that probes the inventory on a Timer and surfaces per-server reachability in the sidebar. Disambiguation note: distinct from `Heimdall.Ssh.ServerHealthMonitor`, which polls CPU/RAM/disk on connected SSH sessions via shell commands - this new service operates on the inventory before/instead of connecting, via raw TCP.

- **Phase 1 - Core service + state model + tests** (`62fd036`): new `Heimdall.Core.SessionHealth` namespace ships `HealthStatus` (Unknown/Probing/Up/Down), `HealthState` (immutable record with `LastCheckUtc`/`LatencyMs`/`Reason`), `IHealthProbe` (test seam), and `TcpHealthProbe` (default implementation with bounded `CancellationTokenSource.CancelAfter` timeout and `SocketError` → reason-tag classification). `Heimdall.App.Services.SessionHealthMonitor` loads the latest inventory from `IConfigManager.LoadServersAsync` on every Timer tick, runs throttled parallel probes through a `SemaphoreSlim` (default 10 concurrent), and re-arms its Timer when `IConfigManager.SettingsChanged` fires so the user can toggle Enabled or change the interval without restart. Gateway-fronted servers (`SshGatewayId != null`) and protocols without a TCP probe port (Citrix, Local Shell) short-circuit to Unknown without consuming a probe slot. State for servers removed between cycles is evicted from the in-memory dictionary. 4 new `AppSettings`: `SessionHealthMonitorEnabled` (default true), `SessionHealthCheckIntervalSeconds` (60), `SessionHealthProbeTimeoutMs` (2000), `SessionHealthMaxConcurrent` (10). 20 unit tests cover the protocol → port resolver, every short-circuit path, Probing/Up/Down event ordering, inventory eviction, and the disabled-state branch.
- **Phase 2 - Sidebar UI integration** (`1a653ca`): a new observable `ServerItemViewModel.HealthState` is fed via `IUiDispatcher.InvokeAsync` on every `StatusChanged` event so the background timer thread never touches WPF bindings directly. `ServerStatusToColorConverter` was extended from 2/3 to 3/4 binding values, accepting an optional `HealthState` as `values[2]`; when the server is in a non-active connection state, the dot color comes from the live health verdict (`Up`→Success, `Down`→Error, `Probing`→Warning, `Unknown`→TextDisabled), and active state colors keep their existing meaning. Old call sites that still pass 2 or 3 values fall back to the legacy connection-type palette - the converter change is fully back-compatible. A new static `HealthReasonLocalizer` centralizes tooltip formatting and reason-tag translation (e.g. `"Reachable (42 ms) · 14:32:55"`). 12 new locale keys (4 statuses + 7 reasons + "never"), 17 new tests.
- **Phase 3 - Settings UI** (`fa375a6`): the 4 settings are mirrored on `SettingsViewModel` as `[ObservableProperty]` fields with `[Range]` validation matching the runtime clamps (interval 15–3600 s, timeout 250–30000 ms, concurrent 1–50). A new Health Monitor group lands in `Settings → Advanced` right after Timeouts (1 CheckBox + 3 int fields in a 2×2 grid, copied from the Timeouts donor pattern). The Settings search bar gains keywords `health`, `monitor`, `probe`, `reachability`, `santé`, `sondage`. Save flow piggybacks on the existing `Save Settings` button; `SettingsChanged` was already wired in Phase 1, so toggling Enabled or changing the interval re-arms the monitor without restart. 5 new locale keys, 3 new tests covering the load/save mirror and out-of-range validation rejecting Save.

### Sidebar toolbar compaction (1 commit)

The Sessions sidebar wasted a full row (~44 px) on a 4-button toolbar (Add, Import, Expand All, Collapse All) under the search box (`83d1630`). The layout collapses to a single row: search takes the remaining width (`MinWidth=120` to stay usable on narrow sidebars), **Add** stays inline as the primary 1-click action, and the three less-used actions move behind a kebab `⋮` (Segoe MDL2 `E712 MoreVertical`) overflow button - Import becomes a submenu, Expand All and Collapse All become direct MenuItems. The filter result count `Mw_FilterResultCount` moves to a hint `TextBlock` that collapses to 0 height when no filter is active (the existing visibility toggle in `MainWindow.xaml.cs` line 838 still drives it). `OnImportButtonClick` renamed to `OnSidebarOverflowClick` (same body, generic name). One new locale key (`TooltipSidebarOverflow`).

Build green, **5,557 passing + 6 skipped**, locale parity **5,543 keys EN/FR** (+22 this session).

## 2026-05-16 - Bulk password update

- **Bulk edit password** - multi-select servers (Ctrl+Click / Shift+Click) → right-click → Edit → Password applies the same DPAPI+HMAC encrypted password to all selected sessions, regardless of protocol. The dialog uses a double PasswordBox (password + confirmation) to prevent typos. The new password is encrypted once via `CredentialProtector.Protect()` and written to the protocol-specific encrypted field (`RdpPasswordEncrypted`, `SshPasswordEncrypted`, `FtpPasswordEncrypted`, `TelnetPasswordEncrypted`, or `VncPassword`) based on each session's `ConnectionType`. Follows the existing `ExecutePersistedBulkMutationAsync` transaction pattern. Affected files/classes: `ServerListViewModel.Bulk.cs`, `ServerBulkEditPasswordViewModel`, `ServerBulkEditPasswordDialog`, `ContextMenuFactory`, `IDialogService`, `WpfDialogService`, locales (8 new keys EN/FR).

Build green, **5,500 passing + 6 skipped**, locale parity **5,505 keys EN/FR**.

## 2026-05-12 - RDP improvement series: per-profile settings, multimon validation, Auto parity, autofill observability

Four focused RDP follow-ups closed latent runtime drift, made invalid monitor topology recover without blocking the user, aligned external `.rdp` Auto mode with embedded behavior, and improved credential-autofill diagnostics without changing fail-closed matching.

- **Per-profile resize enable delay honored at runtime** - embedded RDP now resolves `RdpResizeEnableDelayMs` as profile value when non-null -> global `AppSettings.RdpResizeEnableDelayMs` -> hardcoded 10,000 ms fallback, through the pure static `EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs` helper. Profile `0` is a legitimate user choice that disables the post-connect resize lockout, negative profile values clamp to `0` at runtime while schema/dialog validation rejects them, and a negative global setting falls back to the hardcoded default with a Warning log. The per-profile schema maximum was aligned with the global setting (`30,000` -> `60,000` ms). Affected files/classes: `EmbeddedSessionManager`, `ServerProfileDto`, `SchemaValidator`, `ServerDialogViewModel`, settings UI tests. Commit `038992f`.
- **Multimon topology validation and non-blocking fallback** - connect-time validation now runs through the pure `RdpDisplayResolver.ValidateMultimon` path and `RdpMultimonValidation` records before ActiveX settings are applied. A single-monitor host with Multimon requested, or any `selectedmonitors` index greater than or equal to the host `MonitorCount`, falls back to single-monitor mode; an empty selected-monitor list still means "use all monitors." The fallback surfaces as a localized status message through the existing reconnect status channel (`EmbeddedRdpView.StatusTextBlock`), not a modal, with new keys `RdpMultimonFallbackSingleMonitor` and `RdpMultimonFallbackInvalidSelection`. Affected files/classes: `RdpDisplayResolver`, `RdpMultimonValidation`, `EmbeddedSessionManager`, `EmbeddedRdpView`, locales, RDP display tests. Commit `2e9b938`.
- **External `.rdp` Auto mode aligned with embedded Auto** - embedded Auto remains the reference contract, and external Auto now writes `smart sizing:i:1`, forces `use multimon:i:0` regardless of the profile flag, writes `screen mode id:i:1`, and emits deterministic primary working-area dimensions snapped to a multiple of 4 via `RdpDisplayHelper`. `RdpFileOptions` gained explicit `ScreenMode` and `EmitDisabledMultiMonitor` fields so `RdpProfileResolver` decides Auto semantics while `RdpFileGenerator` stays a dumb writer. Affected files/classes: `RdpProfileResolver`, `RdpDisplayResolver`, `RdpDisplayHelper`, `RdpFileGenerator`, `RdpHandler`, profile resolver/file generator tests. Commit `ae0dd70`.
- **Credential autofill observability without credential leakage** - `CredentialAutofill` now emits one structured Debug entry per autofill attempt with broker window title, handle, PID, process name, rejection reason or accepted marker, plus an Info outcome line and Warning-level logging for enumeration exceptions. Strict host-title fail-closed matching is unchanged. The same pass scrubbed identity fields from RDP connect diagnostics in `RdpActiveXHost.SetCredentials` and `EmbeddedRdpView` so logs no longer include `user=`, `domain=`, or `hasPassword=`. Affected files/classes: `CredentialAutofill`, `RdpActiveXHost`, `EmbeddedRdpView`, credential-autofill tests. Commit `1d7c78c`.

Build green, **5,505 passing + 6 skipped**, locale parity **5,491 keys EN/FR**.

## 2026-05-11 - RDP scrollbar root cause fix and sidebar UX pass

Tonight's pass closed the RDP scrollbar investigation with a resolver-level fix and made the production-sized session sidebar easier to scan.

- **RDP scrollbar fix** - `RdpDisplayResolver.cs` now resolves `RdpResolutionMode.FitWindow` with `smartSizing: true` (`reason: explicit-fit-window-scaled`). The old FitWindow path used `smartSizing: false`, so MsTscAx rendered the remote desktop at native pixel size; on real Windows RDP servers, when that desktop exceeded the AxHost client rect, Windows painted non-client scrollbars. The attempted Win32 workaround (`RdpActiveXHost` stripping `WS_HSCROLL | WS_VSCROLL` via `EnumChildWindows` plus a 12-second `DispatcherTimer`) lost to MsTscAx's own layout loop, which re-applies the bits every layout pass. The resolver flip trades a small amount of bitmap scaling at non-integer ratios for scrollbar-free FitWindow semantics; Fixed mode remains available for pixel-perfect native rendering, explicit Smart Sizing remains available as a named scaled mode, and the strip plumbing remains as defense in depth for the remaining non-smart modes.
- **Sidebar UX pass** - `MainWindow.xaml` now gives the Sessions sidebar a two-row toolbar (full-width search first, icon-only actions second), including an icon-only Import button. `SidebarDisplayNameFormatter` preserves the head of long names and ellipsizes only trailing parenthesized suffixes (`MaxLength = 40`, Unicode `\u2026`), `WindowUIState.DefaultSidebarWidth` moved from 260 to 320 px, the `(No group)` drop zone is visually toned down, and `TreeViewIndentGuideBrush` was added across all seven Dracula variants for hierarchy guides. Folder and leaf row density now differ more clearly, with about 25 tests covering `SidebarDisplayNameFormatterTests`, `RdpDisplayResolverTests`, and the post-connect strip timer.

## 2026-05-09 - SSH/RDP audit follow-up

Fresh audit pass over the SSH and RDP stacks after the 2026-05-05 closure.
Three findings shipped (1 P1, 2 P2); no P0. Build green, +3 tests.

- **`.rdp` file ACL applied atomically (P1).** `RdpFileGenerator.WriteToFileAsync`
  used to write the file with the parent directory's inherited ACL and apply
  the restrictive ACL afterwards - a brief TOCTOU window where another local
  process could read host/user/gateway data. The path now routes through the
  new `SecureFileWriter.WriteAndProtectAsync` helper (async sibling of
  `WriteAndProtect`) so the restrictive ACL is set at file-creation time. The
  previously private `ApplyRestrictedAcl` helper is gone. A regression test
  pins the new behaviour: `WriteToFileAsync_AppliesRestrictedAclAtCreation`
  asserts that immediately after creation, inheritance is disabled and only
  the current user, Administrators, and SYSTEM are present.
- **Host-key fingerprint comparison unified on `ConstantTimeEquals` (P2).**
  `HostKeyTrustService.Verify` / `Trust` / `Import` previously used
  `string.Equals(..., Ordinal)` while `HostKeyStore.ConstantTimeEquals`
  (FixedTimeEquals-backed) sat right next to it. Fingerprints are not secret
  so this is defense-in-depth, not a load-bearing mitigation, but the
  inconsistency invited copy-paste drift. The four sites now share the same
  helper; `SECURITY.md` reflects the broader scope.
- **Plaintext-credential limitation reaffirmed (P2).** Audit confirmed the
  `SECURITY.md` "Credential lifetime in managed memory" section already
  covers the three remaining surfaces (RDP `put_ClearTextPassword`, SSH
  password auth, `CredentialAutofill` `WM_SETTEXT`). No code change - the
  limitation is inherent to .NET's immutable `System.String` and the COM/UIA
  marshalling on credential entry points. Mitigation remains workstation
  lock; `SecureString` does not provide stronger guarantees on modern
  Windows.

## 2026-05-05 - SSH/SFTP/FTP security audit closure

Pair-architect security cycle closing the consolidated SSH/SFTP audit plan
(`archive/2026/ssh-sftp-audit/audit-ssh-sftp-action-plan.md`). 15 items shipped across P0/P1/P2, with
FTP coverage and cleartext-warning work closing the final deferred item.

Security hardening:

- **Gateway-aware tunnel reuse** - reusable tunnels now match on remote target,
  forwarding mode, and a collision-safe gateway chain key built from stable
  gateway IDs and a versioned SHA-256 hash. Overlapping private networks behind
  different bastions no longer share the same local tunnel.
- **Plink host-key fail-closed** - Plink fallback paths use
  `PlinkHostKeyDecider` plus injectable `IPlinkHostKeyProbe`; if Heimdall
  cannot resolve a stored or safely probed fingerprint, the connection fails
  with `SshFailureCode.HostKeyUnavailable` instead of falling back to the
  PuTTY/Plink cache.
- **Compile-time host-key dependencies** - production SSH/SFTP/tunnel/sudo
  entry points now require non-null `HostKeyStore` and `IHostKeyVerifier`
  dependencies. `RejectingHostKeyVerifier` is the safe fail-closed verifier;
  `AutoAcceptHostKeyVerifier` remains isolated to tests that explicitly need
  first-use acceptance.
- **Typed sudo permission handling** - sudo escalation in the SFTP view now
  triggers only on typed permission-denied exceptions, removing the old
  substring heuristic that treated generic `Failure` messages as permission
  denials.
- **Sudo edit verifier caching** - sudo edit sessions cache the pinned
  verifier created when the file is opened. A host-key rotation during
  auto-upload emits `HostKeyRotatedDuringUpload`, closes the edit session, and
  does not silently re-prompt.
- **Mid-session security events** - `SftpBrowser` and `SshShellSession` expose
  typed `SshSessionSecurityEvent` values via `SshSessionFailureDispatcher`;
  SSH auto-reconnect is suppressed on host-key mismatch signals.
- **Sudo upload cleanup** - privileged uploads split the write and cleanup
  commands so `/tmp/.heimdall_*` files are removed from a `finally` path even
  when `sudo tee` fails.
- **External editor launch** - the default editor resolves to the absolute
  Windows Notepad path and launches with `UseShellExecute=false`, avoiding
  file association surprises for privileged temp files.
- **Known hosts importer** - the app-side importer now mirrors the core
  streaming `TextReader` path, refuses files above 50 MB, and reports typed
  `FileTooLarge` / `FileReadError` diagnostics.
- **Remote edit upload lifecycle** - file-watcher uploads are tracked,
  cancellation-aware, and drained on `CloseEdit` / `Dispose` so exceptions are
  observed instead of falling into `UnobservedTaskException`.
- **Legacy host-key verify API** - `HostKeyStore.Verify(byte[])` is marked
  `[Obsolete]` with tests preserving the legacy first-use contract.
- **Shell teardown hygiene** - `SshShellSession` no longer disposes its read
  loop cancellation source while the loop may still be running.

FTP follow-up:

- `FtpBrowser` gained parser/path tests for Unix and DOS listing formats,
  malformed lines, oversized filenames, path normalization, and date rollover.
- `FtpHandler` validates host and port before connect and reuses localized
  validation messages.
- Credentialed FTP sessions without TLS produce a non-blocking
  `ConnectionResult.Warning` routed to the status surface.
- Superseded on 2026-05-30 by the FluentFTP migration entry above, which
  removed the custom LIST parser and the `FtpWebRequest` backend.

Audit documents:

- `archive/2026/ssh-sftp-audit/audit-ssh-sftp-claude.md`
- `archive/2026/ssh-sftp-audit/audit-ssh-sftp-codex.md`
- `archive/2026/ssh-sftp-audit/audit-ssh-sftp-action-plan.md`
- `archive/2026/ssh-sftp-prompts/01-*.md` through `archive/2026/ssh-sftp-prompts/12-*.md`

Documentation reorganization:

- Added `docs/DEVELOPMENT.md` as the versioned development reference for
  build/test commands, versioning, code standards, i18n conventions, namespace
  rules, and CI expectations.
- Inverted the security documentation layout: root `SECURITY.md` is now the
  short GitHub-detected reporting policy, while `docs/SECURITY.md` is the
  canonical threat model, controls, limitations, and security test reference.
- Added `docs/TOOLS.md` as the developer reference for the built-in tool
  catalog, `ToolRegistry`, `IToolView`, SSH gateway routing, external tool
  providers, SecNumCloud audit engine, and Command Library / TwinShell
  integration.

Test baseline after this pass: **5,453 passing + 6 skipped** (was 5,030),
zero warnings, i18n parity preserved (en=fr=5,489 leaf keys).

## 2026-05-04 - RDP UX deferred polish sprint

Pair-architect follow-up sprint closing the 14 deferred findings + 2
follow-ups (`RDP-LIVE-24`, `RDP-LIVE-25`) carried over from the
2026-05-04 audit cycle (`docs/audit/audit-ux-rdp-2026-05-04.md`).

User-visible changes:

- **Resolution menu mode header** (RDP-LIVE-16) - both the toolbar
  Resolution menu and the right-click Resolution submenu now show a
  non-clickable `Active mode: <mode>` header in their first slot,
  followed by `(WIDTH×HEIGHT)` when a fixed resolution is active.
  Reflects the live effective mode (manual session override beats
  profile mode).
- **Resolution button glyph per mode** (RDP-LIVE-21) - five distinct
  Segoe MDL2 glyphs (Auto / FitWindow / SmartSizing / Fixed / Multimon)
  on the toolbar Resolution button. Tooltip is enriched with the mode
  label and dimensions when available.
- **Auto-collapse disabled redirection indicators** (RDP-LIVE-19) - the
  embedded RDP toolbar status zone now hides redirection icons that are
  off, surfacing them through a discreet `+N` expand chip. Opt-in
  setting `RdpRedirectionIndicatorsAlwaysExpanded` in `settings.json`
  preserves the legacy "show all" behaviour for users who prefer it.
- **Edit profile always offered on the reconnect overlay** (RDP-LIVE-22)
 - every disconnect code now exposes the `Edit profile` button, not
  just security/NLA codes. Profile-remediation codes (2055/2308/2311/
  2825/3080/3848/4360) keep `Edit profile` as the *primary* action;
  other codes leave Reconnect primary but still surface Edit profile
  for quick resolution/gateway tweaks without closing the overlay.
- **SendKeys System section** (RDP-LIVE-20) - `Win+L` (lock workstation),
  `Win+D` (show desktop) and `Win+E` (file explorer) added to the
  SendKeys menu in a dedicated System sub-section.
- **Multi-monitor tooltip rewritten** (RDP-LIVE-25) - the
  `Settings → RDP → Display → Multi-monitor` checkbox tooltip now
  describes the per-profile picker introduced by `RDP-PROF-13` instead
  of the obsolete "uses all local monitors" wording.
- **ServerDialog Options mini-toc** (RDP-PROF-07) - RDP profile editor
  Options tab gains four ghost chips (Display / Audio / Devices /
  Performance) at the top that scroll the form to the matching anchor
  on click.
- **Multi-monitor as a separate toggle** (RDP-PROF-08) - Display section
  now exposes an `Enable multi-monitor mode` checkbox bound two-way to
  `RdpResolutionMode == Multimon`, on top of the existing mode
  ComboBox. Disabled when the host has only one screen attached.
- **Common resolution presets** (RDP-PROF-12) - new `Common
  resolutions` ComboBox in Fixed mode pre-fills `RdpFixedWidth` and
  `RdpFixedHeight` from a curated list (1280×720, 1366×768, 1920×1080,
  2560×1440, 3840×2160) without forcing the user to type the values.
- **Sectioned NLA / DynamicResolution / AudioCapture** (RDP-PROF-11) - 
  the three flat checkboxes at the bottom of the Options tab gain
  `Security:` / `Display:` / `Audio:` section labels for visual
  hierarchy.
- **Smart reset of the Advanced expander** (RDP-PROF-09) - when
  `RdpDialogAdvancedDefault` is on but no advanced field is customized
  (UseGlobalDefaults, AntiIdle, BitmapCaching, Compression,
  AutoReconnect, AdminMode, FullScreen all at their defaults), the
  Advanced expander auto-collapses on a profile re-open. Users keep the
  Advanced view only when they actually need it.
- **Clickable protocol chip in Step 2** (RDP-PROF-10) - replaces the
  static badge + separate `Back` button with a single chip carrying the
  protocol icon (`Geo.Protocol.*`) and label. Click returns to the Step
  1 protocol selector in add mode; the chip is disabled in edit mode.
- **Resolution presets editable from Settings** (RDP-SET-01a) - new
  `Server dialog` card at the bottom of `Settings → RDP` exposes the
  previously hidden `RdpResolutionPresets` array as a multi-line
  TextBox (one preset per line, format `WIDTHxHEIGHT`) with a
  `Reset to defaults` link, and the `RdpDialogAdvancedDefault` flag
  as an explicit checkbox.
- **Per-host palette protocol bias** (RDP-DISC-04) - when typing a bare
  IP/hostname in the Ctrl+K palette, the SSH and RDP ad-hoc suggestions
  reorder to match the protocol last used for that host.
- **Recent connections in the empty palette** (RDP-DISC-05) - opening
  Ctrl+K with no query bubbles the servers whose host appears in the
  recent-connections log to the top of the suggestion list, ordered
  most-recent-first.
- **Letterbox bands now match the SurfaceBrush** (RDP-LIVE-24) - the
  `WindowsFormsHost` is now pinned to the exact RDP region size in
  letterbox mode, so the Win32 HWND no longer bleeds the system gray
  background through the bands. The bands now render in
  `SurfaceBrush` (Dracula `#1B1C25`) like the rest of the surface.

New abstractions worth knowing:

- `RdpResolutionModeIndicator` (`Heimdall.App/Views/EmbeddedRdp/`) - 
  pure, stateless static helpers behind the toolbar Resolution button:
  `Resolve(profileMode, manualW, manualH, profileW, profileH)` returns
  a `RdpEffectiveResolutionState` record; `GetGlyph(mode)` and
  `GetModeLocalizationKey(mode)` produce the icon and label per mode;
  `FormatHeader` / `FormatTooltip` build the display strings. Same
  helper drives the toolbar menu *and* the right-click Resolution
  submenu (via `EmbeddedRdpView.GetEffectiveResolutionState()` exposed
  to `SessionTabContextMenuFactory`).
- `RdpRedirectionVisibilityPolicy` (`Heimdall.App/Views/EmbeddedRdp/`)
 - pure helpers for the `+N` expand badge and per-icon visibility:
  `IsIndicatorVisible(isActive, alwaysExpanded, sessionOverride)`,
  `ShouldShowExpandBadge(disabledCount, alwaysExpanded,
  sessionOverride)`, `CountDisabled(states)`.
- `IRecentConnectionTracker` / `RecentConnectionTracker`
  (`Heimdall.App/Services/`) - in-memory log of successful host /
  protocol pairs (max 50 entries, deduped by `(host, protocol)`). Fed
  from `ServerListViewModel.OnConnectionStateChanged` whenever a
  session reaches `Connected` or `LaunchedExternalClient`. Consumed by
  `CommandPaletteViewModel` for `RDP-DISC-04` and `RDP-DISC-05`.
- `RdpDisconnectActionPolicy.IsProfileRemediationCode` (private) and
  the new `ResolveAdvancedDefault(persistedDefault, isEditMode,
  AdvancedRdpSnapshot)` policy used for `RDP-PROF-09`.
- `AppSettings.RdpRedirectionIndicatorsAlwaysExpanded` (`bool`,
  default `false`) - opt-in to keep all redirection indicators
  visible regardless of state. Not exposed in the Settings UI in
  this iteration; users who want it edit `settings.json` directly.

Test baseline: **5,311 passing + 6 skipped** (was 5,281), zero
warnings, i18n parity preserved (en=fr=5,485 leaf keys, +27).

## 2026-05-04 - RDP UX audit cycle implementation

Pair-architect cycle implementing the RDP UX audit
(`docs/audit/audit-ux-rdp-2026-05-04.md`). 8 prompts + 2 mini-correctifs,
12 of 26 findings closed (2 critical / 7 important / 3 minor). Complete
implementation log in the audit report.

User-visible changes:

- **External RDP applies the profile** (RDP-DISC-06) - the generated `.rdp`
  now respects per-server `RdpResolutionMode`, `RdpFixedWidth/Height`,
  multi-monitor and smart sizing settings instead of falling back to the
  global defaults. `RdpProfileResolver.ResolveResolution` mirrors the
  existing color-depth resolution pattern.
- **Honest "external client launched" status** (RDP-LIVE-23) - the
  `LaunchedExternalClient` state is now painted in `WarningBrush` (orange)
  instead of `SuccessBrush` (green). A dedicated status text and tooltip
  make clear that Heimdall cannot directly observe the remote session
  state until the external client exits.
- **One-shot Embedded/External override** (RDP-DISC-03) - right-click any
  RDP profile to open *Connect with...* and pick `Connect (embedded)` or
  `Connect (external mstsc)` for a single launch without editing the
  profile. Forced sessions show a discreet `(forced embedded/external)`
  suffix in the tab title.
- **Per-monitor selection in Multimon mode** (RDP-PROF-13) - when the
  resolution mode is set to `Multi-monitor`, a `Selected monitors`
  sub-section lists detected screens with their resolution and a
  `(primary)` / `(vertical)` suffix where relevant. Empty selection keeps
  the existing behaviour ("use all monitors") for backward compatibility.
- **Settings → RDP reorganized** (RDP-SET-02) - the previously flat list
  of 18 controls is now grouped into 6 cards: Defaults / Display / Audio
  / Performance / Devices / Advanced timeouts. The 3 RDP timeouts
  (`RdpResizeEnableDelayMs`, `RdpArtifactCleanupDelayMs`,
  `RdpCredentialAutofillTimeoutMs`) move from the Advanced tab into the
  RDP tab. Added a `Reset RDP defaults` link with confirmation, plus
  tooltips on every checkbox using the localized `Rdp*Hint` keys.
- **`Apply to all` confirmation** (RDP-SET-05) - the destructive bulk
  mutation that overwrites RDP mode on every existing profile now
  triggers a confirmation dialog stating the affected profile count.
- **Embedded RDP toolbar grouping** (RDP-LIVE-18) - two thin vertical
  separators split the toolbar into 3 logical groups
  (Session control / Session interaction / Display configuration). Same
  separator style applied to SFTP for consistency.
- **Letterbox region delimited** (RDP-LIVE-17, structural) - a 1px Border
  now materializes the active RDP region in fixed-resolution sessions, so
  the letterbox bands no longer read as a display bug. A first-letterbox
  hint badge fades in/out to explain the mode. Visual polish on the band
  colour (currently system gray instead of `SurfaceBrush`) tracked as
  follow-up `RDP-LIVE-24`.
- **Unified `.rdp` import** (RDP-DISC-07) - the `Settings → Import`
  button and the drag-and-drop drop handler now share a single
  `IProfileImportService`, so both entry points get the rich
  preview/conflict resolution flow. Historic formats
  (MobaXterm/RDCMan/mRemoteNG) keep their dedicated parsers.

New abstractions worth knowing:

- `RdpProfileResolver.ResolveResolution(server, settings)` - returns
  `(Width, Height, MultiMonitor, SmartSizing, SelectedMonitorIndices)`,
  centralising the per-server resolution decision for both Embedded and
  External paths.
- `RdpModeOverride` enum (`UseProfile` / `ForceEmbedded` /
  `ForceExternal`), threaded through `IConnectionService` /
  `IProtocolHandler` / `RdpHandler` as an optional parameter that never
  mutates `server.RdpMode`.
- `IMonitorEnumerator` test seam wrapping `Screen.AllScreens` so the
  ServerDialog ViewModel can be unit-tested without an interactive
  display.
- `IRdpExternalClientLauncher` for testable mstsc spawning.
- `IProfileImportService` (cross-format) above `IRdpImportService`
  (`.rdp`-specific), shared by drag/drop and Settings import.

Test baseline: **5,281 passing + 6 skipped**, zero warnings, i18n parity
preserved (en=fr=5,458 leaf keys).

Two follow-ups remain open: `RDP-LIVE-24` (letterbox band SurfaceBrush +
hint-badge first-display verification) and `RDP-LIVE-25` (Multi-monitor
default tooltip wording in Settings → RDP, made stale by the new
per-profile picker). 14 lower-priority findings deferred to a future
polish sprint, listed in the audit report.

## 2026-05-02 - Post-Phase 3 documentation refresh

Phase 3.8 doc-only pass refreshing tracked living documentation after the
Phase 3 cluster.

- Updates `docs/ARCHITECTURE.md` for the Phase 3.1 tunnel panel state model,
  the Phase 3.6 `INetworkKnowledgeBaseStore` seam and initialization
  serialization pattern, and the Phase 3.7 Settings layout / TFTP relocation.
- Updates `README.md` so Quick File Server describes HTTP-by-default sharing
  with opt-in TFTP from Settings > Advanced > File sharing.
- Updates `docs/TROUBLESHOOTING.md` so TFTP port troubleshooting starts with
  the opt-in Settings prerequisite.

No code changes. Test baseline unchanged: **5,103 passing + 6 skipped**.

## 2026-05-02 - Settings and header hygiene

Phase 3.7 pass cleaning up the main header's top-right controls and moving
file-sharing/tool preferences into more coherent Settings locations.

- Converts the quick file server and quick connect controls into compact
  icon-only header buttons while preserving tooltip and accessibility labels.
- Removes the permanent TFTP disclaimer cluster from the header and relocates
  TFTP enablement to Advanced > File sharing with the warning shown inline.
- Moves the external editor path out of General > Appearance into the Advanced
  tools area under a dedicated External editor card.
- Keeps `SettingsViewModel` independent of `FileShareService`; `MainWindow`
  bridges the new `FileShareEnableTftp` setting to the existing immediate
  persist-and-restart runtime behavior.
- Adds an initialization guard so loading persisted settings at startup does
  not trigger a spurious file-share restart through the property-change bridge.

UI structure is smoke-validated manually; automated coverage is limited to the
new Settings property load/save path.

Test baseline after this pass: **5,103 passing + 6 skipped**, zero warnings.

## 2026-05-02 - Network cartography KB flake hardening

Phase 3.6 pass fixing the transient
`NetworkCartographyViewModelTests.ClearKb_ResetsStats` failure. Recon traced the
flake to the ViewModel's fire-and-forget initial KB stats load racing with
`ClearKbAsync`, plus the test fixture touching the shared static
`config/network-kb.json` path.

- Adds an `INetworkKnowledgeBaseStore` persistence seam with the production
  `FileNetworkKnowledgeBaseStore` adapter and an in-memory test store.
- Constructor-injects the store into `NetworkCartographyViewModel` while keeping
  the synchronous `Initialize` contract unchanged.
- Captures the initial load task, exposes `WaitForInitialLoadAsync`, and
  serializes `ClearKbAsync` behind any pending initial load so stale stats cannot
  overwrite a cleared KB.
- Refactors network cartography ViewModel tests off the shared file path and adds
  a deterministic `TaskCompletionSource`-gated regression test for the original
  race.

Test baseline after this pass: **5,100 passing + 6 skipped**, zero warnings.

## 2026-05-02 - Timezone type-to-select city bias

Phase 3.5 pass improving DateTime Converter timezone type-to-select after
Phase 3.4 smoke exposed that typing a city prefix such as `par` did not jump
to the Paris timezone because `TimeZoneInfo.DisplayName` starts with the
`(UTC...)` offset.

- Adds a `SearchableName` value to timezone picker items while keeping the
  visual `DisplayName` unchanged in the ComboBox.
- Biases WPF `TextSearch` toward the last listed city in standard display
  names, e.g. `Paris - (UTC+01:00) Bruxelles, Copenhague, Madrid, Paris`.
- Documents the intentional limitation: WPF type-to-select remains
  prefix-based, so this quick fix makes one city per timezone searchable
  rather than implementing full substring search across every listed city.

Test baseline after this pass: **5,098 passing + 6 skipped**, zero warnings.

## 2026-05-02 - Tool ComboBox text-search hardening

Phase 3.4 pass hardening tool-view ComboBoxes after two runtime
`NullReferenceException` observations in WPF `BindingExpression.Activate` paths:
one during Hacker Simulator timer-driven scenario re-selection, and one during
`SessionPaneControl` unload while clearing a hosted split-pane tool view.

- Adds explicit `TextSearch.TextPath` values to the seven tool-view ComboBoxes
  that used `DisplayMemberPath` without an explicit text-search path:
  Hacker Simulator scenario/category/realism/playlist, DateTime timezone, HMAC
  algorithm, and Privilege Launcher level.
- Preserves type-to-select behavior while avoiding WPF's implicit display-path
  inference during timer and teardown binding lifecycles.
- Does not add an automated repro test: no failing stack was captured in the
  available logs, the suspected WPF binding lifecycle race is not deterministic
  enough for a stable xUnit harness, and the change is defensive XAML cleanup
  over an identified anti-pattern.

Test baseline after this pass: **5,092 passing + 6 skipped**, zero warnings.

## 2026-05-02 - RDP shortcut settings cleanup

Phase 3.3 pass retiring the unused `AppSettings` surface for remapping embedded
RDP release-focus and fullscreen-toggle shortcuts.

- Removes `RdpReleaseFocusShortcut` and `RdpFullscreenToggleShortcut` from
  `AppSettings` and from `settings.default.json`; legacy settings files with
  these keys are accepted through the default unknown-field behavior.
- Keeps runtime behavior fixed on the existing built-in shortcuts:
  `Ctrl+Alt+Home` for release focus and `F11` for fullscreen toggle/help text.
- Removes the stale fullscreen-router TODO that pointed at the retired settings
  fields.

Test baseline after this pass: **5,092 passing + 6 skipped**, zero warnings.

## 2026-05-02 - RDP legacy resolution DTO cleanup

Phase 3.2 pass retiring runtime usage of the legacy per-server
`RdpDefaultResolutionWidth` / `RdpDefaultResolutionHeight` fields.

- Replaces the DTO fields with obsolete setter-only JSON migration shims that
  forward legacy values into `RdpFixedWidth` / `RdpFixedHeight` without
  reserializing the old property names.
- Preserves hybrid JSON semantics where `rdpFixedResolutionWidth` /
  `rdpFixedResolutionHeight` win over legacy defaults regardless of property
  order.
- Removes the remaining runtime write path from "Save as default for this
  server" and the embedded RDP legacy read fallback.

Test baseline after this pass: **5,090 passing + 6 skipped**, zero warnings.

## 2026-05-02 - Tunnels panel collapse-by-default

Phase 3.1 pass changing the Tunnels panel from a single global expanded flag
into a per-active-tab resolved state, with a per-server-profile override, an
ad-hoc tab-local fallback, a discrete tab-header badge, and a Settings toggle
controlling the application default. Five-commit incremental ship across DTO,
Settings UI, panel state resolution, badge state aggregation, and badge visual.

- Adds nullable `ServerProfileDto.TunnelsPanelExpanded` and bool
  `AppSettings.CollapseTunnelsPanelByDefault` (default `true`); legacy JSON
  without the new fields naturally deserialises to `null` / default. No
  migration class required.
- Adds the Appearance Settings checkbox bound to
  `CollapseTunnelsPanelByDefault`, with localized label, tooltip, and
  `AutomationProperties.Name`. EN/FR locale parity preserved.
- Refactors `TunnelsViewModel.IsPanelOpen` from a global flag into a resolved
  state with strict precedence: per-tab manual override → per-profile
  `TunnelsPanelExpanded` (loaded fresh from disk via
  `ConfigManager.LoadServersAsync`) → application default
  `!CollapseTunnelsPanelByDefault`. Re-resolves on active-session change,
  `ConfigManager.SettingsChanged`, and tab `RootContent` changes.
  `Interlocked`-versioned async resolution prevents stale writes when a toggle
  and a tab switch race.
- Removes the previous `OnTunnelOpened` force-`IsPanelOpen = true` path; the
  new tab-header badge dot replaces this affordance.
- Persists manual toggles to disk for saved profiles via
  `ConfigManager.SaveServersAsync`; ad-hoc sessions
  (`SessionTabViewModel.IsAdHoc`) keep the override tab-local only.
  Profile-deleted-mid-session falls back to tab-local with a `FileLogger.Warn`.
- Introduces an `internal ITunnelsHost` adapter (3-member surface) so
  `TunnelsViewModel` can be tested without constructing the full
  `MainViewModel`.
- Adds `SessionTabViewModel.TunnelBadgeState`
  (`Hidden` / `Healthy` / `Unhealthy`) and the stateless
  `TunnelBadgeStateResolver`, which walks every leaf via
  `SplitTreeHelper.EnumerateLeaves` and aggregates per-pane tunnel health via
  `ConnectionStateMachine.GetStateData(serverId)?.TunnelLocalPort` +
  `TunnelManager.GetTunnel(port)?.IsAlive`. The snapshot-based limitation
  (no event for silent `IsAlive` transitions) is documented and accepted.
- Orchestrates per-tab badge updates in `TunnelsViewModel` via subscriptions
  to `TunnelManager.TunnelOpened` / `TunnelClosed` and to the existing
  `ConnectionViewModel.ActiveSessions.CollectionChanged`; tracks subscribed
  tabs in a `_trackedTabs` HashSet for idempotent subscribe/unsubscribe, and
  unsubscribes every per-tab handler in `Dispose`. No new public event added.
- Renders the badge as a corner-overlay `Ellipse` next to the protocol icon
  in the session tab header. Layout is stable (overlay on the existing 14×14
  icon, zero impact on title or sibling elements). Visibility is computed by
  `TunnelBadgeVisibilityConverter` (`IMultiValueConverter` bound to both
  `TunnelBadgeState` and `Tunnels.IsPanelOpen`); fill via
  `TunnelBadgeStateToBrushConverter` (`SuccessBrush` / `WarningBrush`);
  tooltip via XAML `DataTrigger` on `TunnelBadgeState`. Five new i18n keys
  total across the Settings checkbox and the badge.

Test baseline after this pass: **5,087 passing + 6 skipped**, zero warnings
under `TreatWarningsAsErrors`. EN/FR locale parity at 5,402 leaf keys.

## 2026-05-01 - RDP resolution, DPI, fullscreen, and lifecycle hardening

Two-phase RDP pass covering DPI correctness, per-server resolution profiles,
ActiveX lifecycle cleanup, and fullscreen usability.

### Phase 1 - RDP DPI plumbing

- Injects `DesktopScaleFactor` and `DeviceScaleFactor` before `Connect()` via
  direct QI on `IMsRdpExtendedSettings` (`ocx as IMsRdpExtendedSettings`) with
  an explicit `Marshal.QueryInterface` fallback. The dynamic
  `ax.ExtendedSettings` IDispatch path and `IServiceProvider.QueryService`
  path were both proven unreliable on real `MsTscAx.MsTscAx.10` installs.
- Tracks monitor DPI changes via `Window.DpiChanged` and reuses the guarded
  `UpdateSessionDisplaySettings` path for live display updates.
- Snaps RDP widths to a multiple of 4 before display updates.
- Adds the session-tab context-menu Resolution submenu with standard presets,
  Match Window, Custom, and Save as default for this server.
- Removes the previous global forced `SmartSizing = true`; current default
  behavior is preserved through explicit initialization instead.

### Phase 2 - Resolution profiles, fullscreen UX, lifecycle hardening

- Adds per-server `RdpResolutionMode` schema (`FitWindow`, `Fixed`,
  `SmartSizing`, `Multimon`) with migration from legacy
  `RdpFixedResolutionWidth` / `RdpFixedResolutionHeight` and
  `RdpMultiMonitor` fields. Legacy JSON property names remain readable.
- Adds the ServerDialog "Resolution profile" section with mode-specific field
  visibility, validation ranges, snap-to-4 acceptance for fixed widths, and
  EN/FR localization parity.
- Adds centered letterbox sizing for `Fixed + SmartSizing=off`, positioning the
  `WindowsFormsHost` with explicit `Margin` / `Width` / `Height` inside a
  themed host surface instead of relying on WPF transforms.
- Migrates `UseMultimon` from the fragile `AdvancedSettings9` path to the
  documented `IMsRdpClientNonScriptable5` QI path.
- Harmonizes RDP disconnect teardown across tab close, toolbar disconnect,
  context-menu disconnect, and reconnect/failed-session cleanup through
  `RdpDisconnectTeardownSequence`.
- Improves fullscreen UX with a themed auto-hiding exit chip, top-edge reveal,
  universal F11 toggle, Esc exit, Ctrl+Shift+F11 toggle, and layered keyboard
  routing (`PreviewKeyDown`, `ThreadPreprocessMessage`, low-level
  `WH_KEYBOARD_LL` hook, foreground-process filter).

Test baseline after this pass: **5,030 passing + 6 skipped**, zero warnings.

## 2026-04-25 - SSH audit follow-up (Pageant DACL, known_hosts DoS caps, lifecycle)

Four-commit hardening pass on the SSH/SFTP/Tunnel surface following a
multi-pass audit. Previous-pass findings vetted, two false positives dropped,
one self-introduced regression caught and fixed in the same pass.

- **Pageant IPC** - `PageantClient.SendMessage` now creates the shared file
  mapping with a self-only DACL (`D:P(A;;FA;;;<currentUserSid>)`) and a
  cryptographically random suffix in the mapping name (64 bits of entropy via
  `RandomNumberGenerator.GetHexString(16)`). The new
  `SecurityAttributesScope` allocates `SECURITY_ATTRIBUTES` and the security
  descriptor under a try/catch that releases both pointers on any failure
  between alloc and ownership transfer.
- **known_hosts parsing** - `KnownHostsParser` enforces a per-line cap of
  64 KB and exposes a streaming `TextReader` overload; `KnownHostsImporter`
  refuses files larger than 50 MB, streams via `StreamReader`, and degrades
  to an empty report (with `FileLogger.Warn`) on I/O / decoding failures
  instead of bubbling exceptions to the UI.
- **Constant-time fingerprint compare** - `HostKeyStore.Verify` now uses
  `CryptographicOperations.FixedTimeEquals` after a length-equality guard
  (safe because OpenSSH host-key fingerprints are fixed-length).
- **Plink stderr redaction and lifecycle** - `PlinkTunnelRunner.SanitizeForLog`
  redacts password / passphrase / token / bearer assignments and `-pw` /
  `-pwfile` flags via compiled regexes; `Stop()` cancels and joins the stderr
  drain task (with a 500 ms timeout) before killing the process, so the
  background reader cannot outlive the pipe.
- **SSH agent and shell** - `SshShellSession` links the read-loop CTS to the
  caller's cancellation token (and now throws ahead of the link if the
  caller-supplied token is already cancelled). `OpenSshPipeAgent.SendRequest`
  is rebuilt on `PipeOptions.Asynchronous` + `ReadExactlyAsync` with a
  linked timeout token, replacing the best-effort `ReadTimeout` that
  `NamedPipeClientStream` silently ignores in some modes.
- **Tunnel allocation** - `TunnelManager.AllocatePort` distinguishes
  `AddressAlreadyInUse` from other socket failures and logs the fallback
  to ephemeral.
- **SFTP** - `SftpBrowser.DeleteDirectoryRecursive` is now an iterative
  post-order traversal capped at 256 levels, eliminating the stack-overflow
  risk on hostile remote filesystems. `RemoteFileEditor.AutoUploadAsync`
  re-throws `HostKeyRejectedException` instead of folding it into a generic
  upload failure, surfacing host-key changes to the UI as a security event.
  `RemoteFileEditor.LaunchEditor` now uses `ProcessStartInfo.ArgumentList`
  for the local path.
- **ServerHealthMonitor** - Start/Stop/Stop sequencing is serialized via an
  internal `Lock` and the cts/poll-task pair is snapshotted under the lock
  before any blocking await, so an immediately-following `StartAsync` sees a
  clean state.
- **OpenSshConfigImporter** - `MakeUniqueName` caps its suffix loop at 1000
  and falls back to a guid-tagged name beyond that.
- **AuthPreflightChecker** - emits an aggregated warning when every
  configured agent failed to enumerate identities, distinguishing
  "no agent has keys" from "every agent crashed".
- **FtpBrowser** - warns at connect time when TLS is disabled (credentials
  in clear text) and rejects LIST entries whose filename exceeds 4 KB.
- **i18n / configuration** - new `PlinkTunnelRunnerOptions` record (timings as
  named values rather than positional ints) and `SshLocalizationKeys` const
  class consumed by `SshHandler` + `TunnelService`, so a typo in an i18n key
  fails to compile rather than silently surfacing the literal key in the UI.

Test count after this pass: **421 SSH + 2 625 Core (~Ssh subset 212) +
1 348 App + 96 UI = 4 490 passing + 6 skipped**, zero warnings under
`TreatWarningsAsErrors`.

Deferred (tracked, not implemented in this pass): mktemp-based portable
remote temp dir for `RemoteFileEditor` (requires per-host probe cache),
typed `FailureClassifier` properties (depends on SSH.NET surface),
`SshHandler.cs` 834-LOC refactor (separate task).

## 2026-04-25 - SSH runtime validation fixes

- Hardened embedded SSH terminal shutdown: late disconnect/output callbacks now stop posting to WebView2 once the terminal surface or dispatcher is disposed, preventing `TerminalWebView` object-disposed popups during app exit.
- App shutdown now closes active sessions through the silent cleanup path instead of invoking the user-facing "close all sessions" confirmation while WPF is already shutting down.
- Documented the `Heimdall-TestEnv` gateway setup split: imported server profiles reference gateway ids, but gateway definitions must exist in the runtime build's `config\settings.json` (`AppSettings.SshGateways`). Added smoke-test and troubleshooting notes for running `Inject-Gateway.ps1` against the exact Debug/Release build being launched.
- Baseline after this pass: **4,454 passing + 6 skipped** tests.

## 2026-04-24 - SSH hardening roadmap (lots 1-9)

Full roadmap addressing the SSH review recommendations. Nine lots plus one follow-through patch, each landed as independently-green commits on `master`.

- **Lot 1 - Host-key deadlock removed, trust path fail-closed.** Interactive host-key decisions now happen before the real `Connect()` via a dedicated pre-authentication probe (`SshConnectionFactory.ProbeHostKeyAsync` with `NoneAuthenticationMethod`). Real connections use a strict, synchronous `PinnedFingerprintVerifier` that accepts only the pre-resolved fingerprint. SSH.NET's `HostKeyReceived` callback is guaranteed to be pure-synchronous - no async, no UI dispatch, no sync-over-async from inside it. Production runtime paths no longer fall back to `AutoAcceptHostKeyVerifier.Instance` when `HostKeyStore` is provided without an `IHostKeyVerifier`: they fail closed with a clear exception. `ToolGatewayConnector` refuses to route tool traffic through a gateway that has no pinned fingerprint yet; the user must complete a normal interactive SSH session first.
- **Lot 2 - Key passphrase separated from login password.** New `SshKeyPassphrase` field on `SshConnectionParams`, persisted encrypted via `SshKeyPassphraseEncrypted` alongside `SshPasswordEncrypted` in `servers.json`. The Server dialog now exposes two distinct `PasswordBox` fields; the passphrase field is visible only when a key path is configured. Password can now serve as a true fallback auth method when a key is also present, not as a silently-repurposed passphrase. Legacy profiles (key path set + password set, no `sshKeyPassphraseEncrypted` field) are kept read-only on disk; legacy mapping is applied at runtime (password tried both as passphrase and as password fallback, strictly more permissive than before), and an info log is emitted. Auto-migration happens only when the user saves from the UI. Plink fallback fails fast with a descriptive error when a passphrase is set, since plink cannot prompt for it.
- **Lot 3 - Public SSH.NET resize API.** Removed the private-field reflection previously used in `SshShellSession.Resize()` and switched to SSH.NET 2025.1.0's public `ShellStream.ChangeWindowSize(uint columns, uint rows, uint width, uint height)`. A strict signature regression test guards future SSH.NET upgrades.
- **Lot 4 - Windows OpenSSH Agent support.** New `ISshAgent` / `ISshAgentKey` abstraction with two implementations: `PageantAgent` (existing Pageant IPC refactored behind the interface) and `OpenSshPipeAgent` (new, named pipe `\\.\pipe\openssh-ssh-agent` per draft-ietf-sshm-ssh-agent). `SshAgentRegistry` enumerates agents in a user-configurable priority order via new `AppSettings.SshAgentPreference` (default: Windows OpenSSH first, Pageant second). RSA keys are advertised with their SHA2 variants (`rsa-sha2-256` flag 0x02, `rsa-sha2-512` flag 0x04, plus legacy `ssh-rsa`) so modern servers with `ssh-rsa` disabled still accept cached keys. Agent IPC handles are never kept alive across requests (no handle leaks on Windows).
- **Lot 5 - `HostKeyTrustService` and known_hosts synchronization.** New centralized orchestration layer above `HostKeyStore`, with enriched `HostKeyEntry` metadata (`FirstSeen`, `LastSeen`, `Algorithm`, `Source`, `PublicKeyBase64`). `LastSeen` is updated on every successful verification, outside the SSH.NET callback. Added explicit import/export against OpenSSH `~/.ssh/known_hosts`, including parse support for hashed entries (HMAC-SHA1 per OpenSSH `HashKnownHosts`). Persistence schema bumped to `trustedHostKeysV2`; legacy `trustedHostKeys` is read without modification or deletion so downgrades remain safe. New `AppSettings.SyncKnownHostsAtStartup` (opt-in, off by default) runs the importer non-blocking at startup. CA-signed host keys (`@cert-authority`) and revoked lines (`@revoked`) are parsed and skipped with diagnostics; full CA support is a future lot.
- **Lot 5B - UI import metadata propagation.** The pre-existing `ImportKnownHostsDialogViewModel` path now routes through `HostKeyTrustService.Import()` so imported entries carry `Source=ImportedKnownHosts` and a `PublicKeyBase64` blob, enabling round-trip export from the UI.
- **Lot 6 - ProxyJump import.** `OpenSshConfigParser` now maps OpenSSH `ProxyJump` directives (single-hop and multi-hop chains) to `SshGatewayDto` gateways linked via `ParentGatewayId`, consumable by the existing `GatewayChainResolver` unchanged. `ProxyJump none` is accepted as "no proxy". Unsupported forms are explicitly rejected with localized diagnostics: `ProxyCommand` (any form), mixed `ProxyJump+ProxyCommand`, `%h`/`%p`/`%r` token substitution, quoted/malformed syntax, and cycles. Reuse rules: inside the same import batch `(host, port, user, keyPath)` identity; against existing Heimdall gateways, `(host, port, user)` - in both cases, no mutation of the existing gateway, just reference sharing. Cycle detection rejects the entire chain, never a partial import.
- **Lot 7 - TunnelManager refactor.** Extracted shared helpers (`ResolvePinnedVerifierAsync`, `ConnectSshClientWithCancellationAsync`, `WireFinalForwardedPorts`, `BuildTunnelInfo`, `RegisterTunnelSession`, `ClassifyAndBuildFailureResult`) into a partial class `TunnelManager.Build.cs` with a `TunnelBuildContext` holder. `OpenTunnelAsync` and `OpenChainedTunnelAsync` became thin orchestrators sharing 100 % of post-connect logic. `TunnelManager.cs` trimmed from ~825 to 462 lines (-44 %). Nine characterization tests added in a dedicated prior commit; the `tests/` diff between the characterization commit and the refactor commit is empty, proving no behavior change.
- **Lot 8 - Trusted host keys UI.** New sub-panel under `Settings > SSH & SFTP` exposing the `HostKeyTrustService` data. Dense grid with columns Host:Port, Algorithm, Source (localized label - no raw enum leaks to XAML), First seen, Last seen, truncated fingerprint, row actions. Sortable columns, substring filter on Host:Port. Row actions: copy full fingerprint, details modal (full fingerprint and public key base64), remove with a confirmation dialog that discourages habitual removal. Global actions: import from `~/.ssh/known_hosts`, export to it, refresh. Conflict resolution on import goes through a dedicated modal with per-row "Keep existing" default and explicit opt-in "Replace with imported"; the grid itself has no replace action by design, so a host-key mismatch always stays a notable decision.
- **Lot 9 - Local bind retry.** `ForwardedPortLocal.Start()` and `ForwardedPortDynamic.Start()` calls now retry up to 3 times with 50 ms spacing on `SocketException(AddressAlreadyInUse)` only, closing the TOCTOU window between `AllocatePort` and the actual bind. `RemotePortForward.Start()` is not retried (server-side bind, different race surface). Chained-tunnel intermediate local ports also covered. The retry helper accepts an injectable sleep delegate so unit tests stay deterministic.

Invariants preserved across all nine lots and verified at every merge:

- Zero `AutoAcceptHostKeyVerifier.Instance` occurrences in production code paths at any point.
- No sync-over-async introduced in any host-key or auth code path (grep guard: `.Result`, `.Wait(`, `GetAwaiter().GetResult`).
- SSH.NET `HostKeyReceived` handler is pure-synchronous from lot 1 onwards.
- Host-key persistence schema migrations are strictly additive on disk (lot 5 keeps `trustedHostKeys` intact alongside `trustedHostKeysV2`, mirroring the non-destructive `servers.json` migration pattern of lot 2).

Baseline after the roadmap: **4,448 passing + 6 skipped** (`4,454` discovered), **59 built-in tools**, **5,185 locale keys** per locale (EN and FR at strict parity enforced by CI). Zero build warnings, zero skipped changes in the 6-skip count across all 10 commits.

## 2026-04-24 - SSH/RDP security audit remediation
- Hardened SSH host-key trust across SSH.NET and Plink: Plink now consumes pinned fingerprints from `HostKeyStore`, first-use and mismatch decisions route through `IHostKeyVerifier`, and the themed `HostKeyPromptDialog` handles deliberate acceptance or rejection.
- Added explicit host-key mismatch diagnostics, localized user messages, and persistence semantics where `HostKeyEvent` fires only from `Trust()`.
- Refactored `TunnelManager` cleanup through single cleanup helpers for partial simple and chained tunnel setup failures.
- Switched `ServerHealthMonitor` command execution to SSH.NET APM async execution with concurrent CPU/RAM/disk probes and cancellation coverage.
- Tightened RDP credential broker autofill so broker windows require a host-title match before password injection.
- Added root `SECURITY.md` with the current threat model, known limitations, and security test entry points.
- Current baseline after this audit line: **4,318 passing + 6 skipped** (`4,324` discovered), **59 built-in tools**, and **~5,118 locale keys** per locale.

## 2026-04-23 - release 2026.042302 - audit remediation patch release
- Version bump from `2026.042301` to `2026.042302` (`InformationalVersion`).
- Packages the full 2026-04-22 audit-remediation line already merged on `master`: session/WebView handler leak cleanup, File Share bearer-token hardening with TFTP opt-in, startup async de-blocking, terminal asset caching, subprocess argument hardening, MVVM cleanup, UI polish, accessibility fixes, and repository housekeeping.
- Follow-up release patch after the first publish:
  - aligns the formatting gate with the repository's expected CRLF / `using` order
  - relaxes one `TcpPingViewModelTests` timeout under code coverage so GitHub Actions stays stable without changing runtime behavior
- Current baseline for this release line: **4,233 passing + 6 skipped** in CI, **59 built-in tools**, and **5,105 locale keys** per locale.

## 2026-04-22 - sessions diagnostics, NotesTool cleanup, and docs sync
- Introduced a shared `SessionDiagnostic` / `SessionFailureStage` contract and surfaced pane-scoped SSH failure diagnostics end-to-end, including a `Details` disclosure in `SessionPaneControl`.
- Wired RDP diagnostics on both pre-tab failure branches (`RdpHandler`) and mid-session host events (`RdpActiveXHost.Disconnected` / `FatalError`) while retiring the legacy local detail text block in `EmbeddedRdpView`.
- Kept failed-session panes interactive by suppressing the tab-loading overlay when diagnostics already exist, and compacted the failure-overlay Reconnect / Close buttons for narrow panes.
- Modernized `NotesTool` with `{loc:Translate}` migration, ViewModel-owned Confluence/HTML export payload generation, declarative tag-chip binding via `ItemsControl`, and a denser Obsidian-like explorer presentation.
- Refreshed README / architecture / smoke documentation to match the current gate (**4195 passing + 6 skipped**, `4201` discovered) and locale catalog size (**5,102 keys per locale**).

## 2026-04-21 - release 2026.042102 - ARP Monitor refactor + locale/parser fixes
- Version bump from 2026.042101 to 2026.042102 (`InformationalVersion`).
- Fixes the JWT Parser locale-switch crash by marshaling `OnLocaleChanged` back to the WPF dispatcher when locale changes originate from a non-UI thread.
- Aligns the UI test harness locale-switch path with the production `LocalizationSource` bridge, restoring clone-clean UI smoke stability (`93/93` on repeated runs).
- Fixes the French `netsh wlan` parser ambiguity between `Type de réseau` and `Type de radio`, so `RadioType` is now populated correctly on FR output.
- Refactors ARP Monitor in three phases without behavior drift:
  - extracts `ArpTableParser` into `Heimdall.Core.Network` with dedicated Windows/Linux/macOS parser tests,
  - introduces `IArpTableReader` + `ArpMonitorViewModel` + extracted `ArpEntry`,
  - migrates ARP alerting, vendor lookup, and TSV copy payload generation into the ViewModel while leaving only non-bindable UI effects in the view.
- Closes the `#52` audit follow-up by centralizing the duplicated `IsCollapsed(AutomationElement?)` helper into `UiTestBase`.
- Housekeeping: tests now stand at **4156 passing + 6 skipped** (`4162` discovered), with clean local build/test gates before release.

## 2026-04-21 - release 2026.042101 - Remote access audit package
- Version bump from 2026.042003 to 2026.042101 (`InformationalVersion`).
- Consolidates batches 55.1, 58, and 59 (see entries below dated 2026-04-19).
- Adds the remote-access audit package:
  - `archive/2026/audits/audit-connection-sequences-2026-04-19.md`
  - `archive/2026/audits/audit-gap-rdp-2026-04-19.md`
  - `archive/2026/audits/audit-gap-ssh-terminal-sftp-2026-04-19.md`
  - `archive/2026/audits/audit-roadmap-remote-access-2026-04-19.md`
- No new runtime feature in this release.

## 2026-04-19 - batch 55.1 - Remove legacy workspace path

- Deleted `WorkspaceService` + `WorkspaceDto`/`WorkspaceSessionDto`.
- Deleted the orphan `SessionCoordinator.RestoreWorkspaceAsync` method.
- Retired the `EnableSessionPersistence` UI toggle; session snapshot save/restore
  (b55) is now unconditional. The property stays on `AppSettings` for backward
  deserialization compatibility but has no runtime effect.
- Removed four locale keys (`WorkspaceRestoring`, `LogWorkspaceRestored`,
  `SettingsWorkspaceRestore`, `A11ySettingsWorkspaceRestore`) from `en.json`/`fr.json`.
- Removed the legacy `EnableSessionPersistence = $false` line from the
  sidebar-favorites smoke script.

## 2026-04-19 - batch 58 - Post-connect Command Library linkage

- Extended `PostConnectStep` with optional `CommandLibraryId` and
  `CommandLibraryParams` so SSH embedded sessions can resolve Command Library
  actions at run time while preserving the dormant literal `Input` for unlink.
- Added `CommandLibraryStepResolver` and the `Broken` post-connect status so
  missing actions, Windows-only templates, and invalid parameters surface as
  configuration errors instead of silent fallbacks or runtime failures.
- Added a modal `CommandLibraryPickerDialog` to `ServerDialog` so operators can
  link or unlink post-connect steps without editing the Command Library itself.
- Kept the scope SSH-embedded only; Plink, Telnet, and Local Shell post-connect
  flows remain unchanged in this batch.

## 2026-04-19 - batch 59 - Post-connect parameter auto-prefill

- Added one-shot auto-prefill for linked Command Library parameters in the
  picker, using server-profile host/port/user aliases captured at open time.
- Added a `Change...` path for already linked steps so operators can re-open the
  picker with their existing values preserved.
- Kept prefill snapshot-only, with no live binding back to the server fields.
- Structurally blacklisted secrets (`password`, `token`, `secret`, etc.) from
  any auto-prefill path.

## [Unreleased] - 2026-04-14

### UX - session-tree move-to-group parity + sidebar favorites

- **Session tree move-to-group unified**: context-menu and drag-drop now converge on a single `ServerListViewModel` core move path, preserving in-memory expansion state by avoiding the previous `LoadServers` rebuild after drag-drop
- **Drag/drop destination policy aligned**: drag-over and drop validate against the same project-scoped target set as the context menu, and the session tree now exposes an explicit no-group drop zone for drag-to-root parity
- **Sidebar Favorites section added**: the sidebar Tools tree now shows an always-present localized Favorites category at index 0, populated from `AppSettings.FavoriteToolIds` and sorted alphabetically by localized display name
- **Cross-surface favorite sync**: `MainViewModel.ToggleFavoriteToolAsync` now raises `FavoritesChanged`, and `SidebarViewModel` applies targeted add/remove mutation so the sidebar stays in sync with both the sidebar ContextMenu and the full-page Tools tab pin button
- **Right-click no longer launches sidebar tools**: a `_suppressSidebarLaunch` guard blocks the `SelectedItemChanged` launch path during right-click targeting, and the redundant sidebar double-click launcher was removed to avoid duplicate tabs on context/network tools
- **Durable UIA smoke added**: `scripts/smoke/move-to-group-smoke.ps1` and `scripts/smoke/sidebar-favorites-smoke.ps1` were added to the repo harness, with WPF ContextMenu-specific gaps explicitly marked as skipped and delegated to human smoke

### Refactor - MainWindow + MainViewModel decomposition (Phases 1–4)

**`MainWindow.xaml.cs`: 3,490 → 2,123 LOC (−39%)**

- **Phase 1** - Extract 3 isolated low-risk domains:
  - `OnboardingFlowViewModel` (first-launch 3-step overlay, resolved by `MainWindow` via DI)
  - `FileShareService` (ephemeral HTTP/TFTP folder sharing, event-based API, `IAsyncDisposable`)
  - `WindowUIState` POCO + `MainWindow.WindowUI.cs` partial (fullscreen, sidebar toggle, tree scroll persistence, folder expand/collapse memory, window-bounds save/restore)

- **Phase 2** - Extract keyboard + sidebar + tools tab:
  - `KeyboardShortcutService` (18 shortcuts, fluent registration, `canExecute` gating) replaces the monolithic `OnPreviewKeyDown` switch
  - `SidebarViewModel` with XAML bindings (Sessions/Tools toggle, tool filter, lazy population, Ctrl+Shift+T toggle)
  - `ToolsTabViewModel` (full-page Tools browser VM state - favorites, recents, filter; section rendering still in `ToolsTabPopulationService` via Panel injection)
  - **Fix**: remove dead `OnWindowDeactivated` Command Palette auto-close handler that had been closing the palette on every open (pre-existing bug)

- **Phase 3** - Extract session/tree/tab interactions:
  - `TreeInteractionState` POCO + `MainWindow.TreeInteractions.cs` partial (session TreeView drag-drop, filter box, inline rename)
  - `TabInteractionState` POCO + `MainWindow.TabInteractions.cs` partial (tab drag-to-reorder, drag-to-detach, drop target resolution, hover tracking)
  - `SessionTabContextMenuFactory` + `ISessionTabContextCallbacks` (335-LOC menu builder, 19 conditional items)
  - `SessionSplitService` (detach/split/merge/unsplit orchestration, `SplitPaletteRequested` event)
  - Initial `ServerListViewModel.MoveServerToGroupAsync` extraction for the tree drag-drop write path (later unified with the context-menu path in the move-to-group parity pass)

**`MainViewModel.cs`: 1,917 → 628 LOC (−67%)**

- **Phase 4** - `MainViewModel` decomposition into 4 sub-VMs (constructor-composed, not DI-registered; `IDisposable` for event-subscription cleanup):
  - `CommandPaletteViewModel` (14 methods: fuzzy search ranking, tool-command parsing, ad-hoc `user@host:port` parsing with protocol inference, connect/split flows, `SplitLayoutMemory` pairing)
  - `TunnelsViewModel` (tunnel panel + tab, `ResolveRoute(sessionId)` for session header display)
  - `ScheduledTasksViewModel` (`TaskSchedulerService` ownership, idempotent `_started` flag)
  - `SessionCoordinator` (8 external wire-ups - 5 `Split.*` providers/setters + 3 `EmbeddedSessionManager` callbacks; broadcast cluster; `OnSessionReady` / `OnReconnectRequestedAsync` / `AutoOpenSftpAsync`)

### Refactor - Declarative i18n migration (Phase 5, in progress)

- **Phase 5A** - Navigation + toolbar imperative labels → `{loc:Translate}` (58 sites). `ApplyNavigationLocalization` / `ApplyToolbarLocalization` now empty stubs pending Phase 5D cleanup
- **Phase 5B** - Accessibility pass → `AutomationProperties.Name="{loc:Translate}"` (39 sites). `ApplyAccessibilityLocalization` deleted entirely
- Phase 5C (Tunnel/Scheduled/Settings/About apply helpers) and Phase 5D (format-args + computed properties + composite strings) pending

### Refactor - Command Library ViewModel extraction

- `CommandLibraryViewModel` extracted from `CommandLibraryView` code-behind with XAML bindings migration (fuzzy filter, platform/category/risk filters, parameter editor, favorites, history, Git Sync). View code-behind now limited to WebView2 and dispatcher-bound glue

### Fixed

- Ctrl+K Command Palette no longer closes immediately on open (dead `OnWindowDeactivated` handler removed; pre-existing bug)
- Filter box `TextChanged` handler no longer duplicates on locale switch (`Mw_FilterBox.TextChanged` subscription moved from `ApplyLocalization` to the `MainWindow` constructor)
- `App.OnExit` service provider disposal now routes through `IAsyncDisposable.DisposeAsync` to properly dispose async-only services (`FileShareService`)
- `MainViewModel` no longer leaks `CollectionChanged` + `PropertyChanged` handlers on session-tab teardown

### Housekeeping

- Tests: **1,775 passing** (unchanged) + 6 skipped (WPF `Application` context gating)
- Build: clean, 0 warnings, 0 errors
- i18n: 4,855 keys (EN/FR parity maintained, no changes this round)

---

### Post-v2026.041301 audit follow-up (2026-04-13) - code-behind split, observability, assets diet

#### Code organization - MainWindow code-behind split (Chantier 1)
- **`MainWindow.xaml.cs` shrunk from 4,895 → 3,490 lines** (−1,405 lines, −29%) via three structural extractions. Zero behavior change - pure file splits verified by build + full test suite
- **`Services/ContextMenuFactory.cs`** (647 lines, new) - builds the four session `TreeView` context menus (server, folder, tool, empty area) and the "Detected Tools" submenu from `ExternalToolProviderService`. Constructor-injected via DI; reached from MainWindow through a small `IContextMenuCallbacks` interface so the menu builder never touches window-scoped state directly
- **`Services/ToolsTabPopulationService.cs`** (605 lines, new) - owns the full-page Tools tab rebuild (Favorites / Recents / categories / 280px cards with search filter), the sidebar Tools `TreeView` data + filter logic, and the pure helpers `GetCategoryBrushKey` / `GetInheritedToolTargetHost` / `CreateInheritedToolContext` / `ResolveToolTabTitle`. Tool card click/pin callbacks are plain `Action<T>` delegates (no interface ceremony for two callbacks). Uses `Application.Current.FindResource` for theme tokens so the service stays decoupled from any specific `FrameworkElement`. `PopulateToolsTab` itself stayed in `MainWindow.xaml.cs` as a thin wrapper because it writes to named header elements (`Mw_ToolsTabTitle`, `Mw_ToolsTabCount`) that are tightly coupled to the XAML tree
- **`MainWindow.Localization.cs`** (519 lines, new partial class) - holds the 8 `Apply*Localization` methods (`ApplyLocalization` orchestrator + Navigation / Toolbar / Tunnel / Scheduled / Settings / About / Accessibility) and the three helpers that are only ever called from `ApplySettingsLocalization` (`PopulateCredProvPresets`, `PopulateExtToolPlaceholderList`, `UpdateExtToolPreview`). `UpdateExternalToolProviderStatus` and `UpdateTokenStatus` stayed in the main code-behind because they have additional callers (external-tool rescan, Git sync token save/clear handlers)
- All three extractions were carried out as pure structural moves with no logic change, no rename, and no signature change. `ContextMenuFactory` and `ToolsTabPopulationService` are registered as singletons in `App.xaml.cs` DI

#### Observability - empty catch blocks (CQ-01)
- **`FileLogger.Debug(string)` + `FileLogger.Debug(string, Exception)`** added to `Heimdall.Core.Logging.FileLogger` - mirrors the existing `Error` overloads, emits at level `DEBUG` through the same queue-and-flush pipeline
- **`TunnelManager.cs`** - 21 empty `catch {}` blocks now log at Debug level: 18 `dispose?.Dispose()` pairs in the tunnel-establishment error handlers (`Dynamic port dispose suppressed` / `Remote port forward dispose suppressed`), plus 3 lambda `Disconnect()` calls wired to `CancellationToken.Register` (`Client disconnect on cancel suppressed` / `Root client` / `Hop client`). Inner catches use a local `cleanupEx` variable to avoid shadowing the outer exception dispatch
- **`SshShellSession.cs`** - single `_client.Disconnect()` cancellation lambda now logs `SSH disconnect cleanup suppressed` at Debug level
- Rationale: these sites are defensible (cleanup paths shouldn't throw) but silent failures hid any surprising exception at runtime. Logging at Debug is cheap, observable through the dev-console trace, and doesn't change behaviour

#### Performance - NotesStorageService dispatcher starvation (PERF-03)
- **`NotesStorageService.SaveNote()`** - the synchronous save path called from `IToolView.CanClose()` / `IDisposable.Dispose()` now waits on its `SemaphoreSlim` with a 2-second timeout instead of blocking indefinitely. On timeout it logs `SaveNote timed out waiting for write lock` via `FileLogger.Warn` and returns without writing - far better than stalling the WPF dispatcher if an async `SaveNoteAsync` is in flight

#### Testing - SplitService unit tests (TEST-01)
- **`tests/Heimdall.App.Tests/SplitServiceTests.cs`** (+14 tests) covering `SplitService`'s synchronous, self-contained methods - the service had zero direct coverage despite being the central owner of pane lifecycle
- **Category A - CancellationTokenSource lifecycle (5 tests)**: `RegisterSession` token creation, `CancelSession` cancels a previously captured token, unknown-session `GetSessionToken` returns `CancellationToken.None`, unknown-session `CancelSession` no-op safety, idempotent re-register (second `TryAdd` keeps original)
- **Category B - `CloseAllPanes` tool-pane blocking (4 tests)**: empty tree, single closable tool pane (disposed + host control cleared), single blocking tool pane (host control preserved, no dispose), mixed tree with one blocker (pre-check means neither pane is disposed)
- **Category D - `ToggleSplitOrientation` (3 tests)**: Horizontal↔Vertical both directions plus unsplit no-op
- **Category E - `SplitSessionWithTool` guards (2 tests)**: unknown tool id short-circuit + max-panes (8) cap with `SetStatusText` callback capture
- **All 7 `SplitService` dependencies are `sealed`** - Moq cannot mock them. The fixture uses real instances for `ConfigManager` (temp dir), `LocalizationManager` (unlocalized, keys return verbatim), and `ToolRegistry` (built-in registry), and passes `null!` for `ConnectionStateMachine` / `TunnelManager` / `EmbeddedSessionManager` / `ConnectionService` because every tested code path was verified to never dereference them. A code comment in the fixture documents this rationale. **Moq was NOT added to the project** despite the initial plan suggesting it
- **`SwapSplitPanesAsync` intentionally untested** - it early-returns when `System.Windows.Application.Current?.Dispatcher` is null, which is always the case in xUnit. Standing up a WPF `Application` + STA dispatcher pump is the same blocker that keeps `ThemeServiceTests` at `[Skip]` and is out of scope here

#### Assets diet (Chantier 3 - PERF-04 + PERF-05)
- **Orphaned PNGs removed (−9.6 MB)**: `Assets/Icons/app/icon-flat.png` (4.19 MB), `icon-rays.png` (3.18 MB), `logo.png` (1.85 MB). Reference audit (`git grep` across source + XAML + csproj + installer scripts) turned up only historical `docs/CHANGELOG.md` mentions and unrelated `/logo.png` references inside `drawio/js/*.min.js` (which point at `Assets/drawio/images/logo.png`, a different file). The real app icon `src/Heimdall.App/app.ico`, wired via `<ApplicationIcon>` in the csproj, is untouched
- **Draw.io locales pruned (−2.95 MB)**: `Assets/drawio/resources/` went from 59 files / 3.1 MB down to 4 files / 149 KB. Kept `dia.txt` (base / English fallback - draw.io's loader uses this name for English), `dia_fr.txt`, `dia_i18n.txt` (auto-generated key manifest), and `README.md`. Removed 55 other `dia_*.txt` locale files - Heimdall is English/French only and draw.io falls back to `dia.txt` for any missing locale
- **`Assets/drawio/VENDORED.md`** updated with three new sections documenting what was pruned, what is a candidate for further pruning *with a runtime test plan* (viewer bundles ~5.6 MB, `shapes-14-6-5.min.js` vs `shapes.min.js` duplication ~1.4 MB, clipart `img/` categories up to ~8 MB), and what is intentionally kept
- **Total on-disk savings: ~12.55 MB** from source control. The `Assets\**\*` glob in `Heimdall.App.csproj` means removed files simply drop out of the deploy - no csproj edit required

#### Housekeeping
- Tests: **1,775 passing** (was 1,761) + 6 skipped (WPF Application context gating - intentional)
- Build: clean, 0 warnings, 0 errors
- i18n: 4,855 keys (EN/FR parity maintained, no changes this round)
- `MainWindow.xaml.cs`: 4,895 → 3,490 lines across Chantier 1 (Step 1 extracted `ContextMenuFactory`, Step 2 extracted `ToolsTabPopulationService`, Step 3 extracted `MainWindow.Localization.cs`)

---

## [v2026.041301] - 2026-04-13

### Sessions rename + full project audit pass

#### UX - Servers → Sessions rename
- **Wholesale rename** of all user-facing "Servers" labels to "Sessions" across navigation tabs, sidebar tabs, dialog titles, status bar, tooltips, error messages, accessibility names, onboarding steps, and tree/empty-state hints - better reflects that Heimdall manages local shells (PowerShell, CMD, WSL) alongside remote SSH/RDP/VNC/SFTP/FTP/Citrix sessions
- **XAML element renames**: `TabServers → TabSessions`, `SidebarTabServers → SidebarTabSessions`, `SidebarServersContent → SidebarSessionsContent`, `ServerTreeView → SessionTreeView`, `ServerTreeColumn → SessionTreeColumn`, `ServerDetailPanel → SessionDetailPanel`, `Mw_AddMenuServer → Mw_AddMenuSession`, `Mw_EmptyBtnAddServer → Mw_EmptyBtnAddSession`, `Mw_EmptySelectServer → Mw_EmptySelectSession`
- **MainViewModel**: `IsServersTabSelected → IsSessionsTabSelected`, `_selectedTab` / `_previousTab` defaults `"Servers" → "Sessions"`, all tab-routing string literals updated
- **Event handlers**: `OnServersTabChecked → OnSessionsTabChecked`, `OnSidebarTabServersChecked → OnSidebarTabSessionsChecked`
- **Preserved as-is** (intentional): `ServerListViewModel`, `ServerItemViewModel`, `ServerDialog`, `ServerProfileDto`, `ServerId` / `OriginalServerId` model properties, `EphemeralFileServer`, `X11ServerManager`, `servers.default.json` filename, and every `server` reference in tool help text that means an actual remote machine (HTTP / DNS / SMB / FTP / VNC / TLS / SSH server, host key verification, etc.)

#### UX - Sidebar tab persistence (PERF-99 / DOC-03)
- **Bidirectional persistence**: new `PersistSidebarTabChoice(bool isTools)` writes the choice via `ConfigManager.MergeSettingAsync(s => s.ShowToolsPanel = isTools)` whenever either RadioButton is checked. Previously only the onboarding flow set `ShowToolsPanel = true`; manually switching back to Sessions never wrote `false`, so every subsequent launch defaulted to Tools
- **`_sidebarTabRestored` startup guard**: prevents `InitializeComponent()`'s default `IsChecked="True"` from clobbering the persisted preference before the `Loaded` handler can restore it. The `OnSidebarTabSessionsChecked` / `OnSidebarTabToolsChecked` handlers no-op until the flag is set in the Loaded handler, immediately after the restore block
- **Onboarding cleanup**: removed the dead in-memory `vm.CurrentSettings.ShowToolsPanel = true` assignment that never actually persisted (the subsequent `MergeSettingAsync(s => s.OnboardingCompleted = true)` reloads from disk and only mutates that one field). Now the RadioButton check naturally routes through the new persist helper

#### Performance
- **PERF-05 (critical) - Async/await replaces blocking `.GetAwaiter().GetResult()`** in 4 sites:
  - `RestoreWindowBounds`: signature changed to `(AppSettings settings)`, settings now passed from the Loaded handler (already loaded by `LoadCommand.ExecuteAsync`)
  - `OnClosing`: converted to `protected override async void` with a deferred-close pattern (`_closeConfirmed` guard). Cancels the close, awaits `ShowSaveDiscardCancelAsync`, then re-invokes `Close()` - previously deadlocked on the dispatcher when the dialog tried to post back
  - `EphemeralFileServer.StartHttpServer` / `StartTftpServer` → renamed to `StartHttpServerAsync` / `StartTftpServerAsync` with `await StopHttpServerAsync()` / `await StopTftpServerAsync()` for the double-start path. Caller (`OnShareFolderClick`) converted to `async void`
- **PERF-01 - Event cleanup in `MainWindow.OnClosed`**: stored 4 long-lived event handler delegates in fields (`_connectionPropertyChangedHandler`, `_serverListPropertyChangedHandler`, `_externalToolsChangedHandler`, `_localeChangedHandler`) so they can be unsubscribed via `-=` on close. Without this, the captured-`this` lambdas kept the window rooted past `Close()`
- **PERF-07 - Draw.io excluded from Debug builds**: `Heimdall.App.csproj` `<Content Include="Assets\drawio\**">` now wrapped in `Condition="'$(Configuration)' != 'Debug'"`. Saves ~48 MB / 2258 files copied to `bin/Debug/` on every iterative dev build. `DiagramEditorView.InitializeWebViewAsync` shows a localized "Release-only" fallback panel (new key `DiagramEditorDebugOnly`) when the directory is missing instead of crashing
- **PERF-09 - Lossless PNG re-compression**: `icon-flat.png` 4.41 → 4.19 MB (-224 KB), `icon-rays.png` 4.55 → 3.18 MB (-1.37 MB) via Pillow `optimize=True compress_level=9` with byte-perfect pixel verification. `logo.png` and `splash-screen.png` left untouched (already encoded by a stronger optimizer; Pillow output was *larger*). ~1.6 MB saved on disk

#### Code Quality
- **CQ-08 - `sealed` modifier** added to **225 non-inherited declarations** (170 classes + 55 records) across 9 projects: TwinShell.Core (60), TwinShell.Infrastructure (48), Heimdall.Core (45), TwinShell.Persistence (33), Heimdall.App (20), Heimdall.Ssh (9), Heimdall.Sftp (5), Heimdall.Rdp (3), Heimdall.Terminal (2). Audit script applied skip rules for inherited types (built a cross-codebase derived-name index), WPF view bases (`Window`/`UserControl`/`Page`/`Control`/`MarkupExtension`), classes containing the `virtual` keyword in their body, and an explicit blocklist for COM event sinks (`MsTscAxEventSink`). Zero build errors, zero rollbacks
- **CQ-06 - Empty `catch {}` blocks fixed in `BackupService.cs`** (3 sites): temp directory cleanup, backup metadata read, backup metadata write - all replaced with `catch (Exception ex) { _logger.LogWarning(ex, ...) }` using the existing injected `ILogger<BackupService>`. Other bare catches across the codebase already had inline rationale comments (`/* best effort */`, `/* already exited */`, etc.) and were left untouched

#### Accessibility
- **A11Y-04** - `LogViewerView` `BtnTail` `ToggleButton` now sets a descriptive `AutomationProperties.Name` (new key `A11yLogViewerTailToggle` - EN: "Toggle live tail mode" / FR: "Activer/désactiver le mode tail temps réel") via the existing code-behind localization pattern. The previous "Tail" label was non-descriptive for screen reader users. Every other tool-view `ToggleButton` was audited - this was the only gap

#### UI
- **Settings toolbar button truncation** - replaced fixed `Width="130"` / `Width="160"` with `MinWidth` on 4 buttons (`Mw_SettingsResetBtn`, `Mw_SettingsExportBtn`, `Mw_SettingsImportBtn`, `Mw_SettingsCitrixBtn`). French translations now auto-size instead of clipping mid-word. `SecondaryButtonStyle` already defines `Padding="16,8"`, so no inline padding override needed

#### i18n
- **fr.json mojibake repair (1170 substitutions across 631 lines)**: fixed double-UTF-8 encoding affecting all French accented characters (ô è é à ç î ê ù â É À) via a two-pass codec round-trip - pass 1 (latin-1) for accented lowercase forms, pass 2 (CP1252) for the uppercase `É` / `À` whose smart-punctuation second char (`‰` U+2030, `€` U+20AC) sits outside the latin-1 0x80-0xBF continuation range. `WindowTitle` now correctly displays "Centre de Contrôle d'Accès Distant"
- **Stale Heimdall-profile values cleaned** (13 keys × 2 locales): `ErrorEmergencyResolveServers`, `ErrorEmergencySaveServers`, `ErrorRestoreServersFailed`, `SettingsApplyModeToAll`, `ConfirmDeleteGatewayDetailMessage`, `ToolSshConfigGenerateAllHint`, `AccessSearchFilter`, `SearchResultCount`, `AccessDetailConnect`, `AccessEmptyImport`, `A11ySearchAndFilter`, `OnboardingStep1Title`, `OnboardingStep1Desc` - all updated from "server(s)" to "session(s)" where the term refers to a saved profile, not an actual remote machine
- **Sessions rename** (locale value updates for the wholesale UX rename): `TabSessions`, `SidebarTabSessions`, `A11ySidebarTabSessions`, `A11ySessionsTab`, `StatusBarSessions`, `EmptyStateBtnAddSession`, `EmptyStateSelectSession`, `AddMenuSession`, plus 119 value-only updates across status messages, dialog titles, confirmations, tooltips, tree/empty-state hints, error messages, scheduled task labels, and accessibility names. Dropped the duplicate `NavTabServers` (`NavTabSessions` already existed)
- **+2 keys** (EN/FR): `DiagramEditorDebugOnly`, `A11yLogViewerTailToggle`. Final state: 4855 keys per locale, parity verified, JSON valid

#### DevOps
- **DEVOPS-02** - `dotnet list package --vulnerable --include-transitive` step added to `.github/workflows/ci.yml` after the test step. Emits a `::warning::` instead of failing the build (vulnerability databases occasionally have false positives or no upgrade path; informational only). Implemented in `pwsh` to match the runner's default shell

#### Documentation
- **DOC-03** - `CLAUDE.md` updated for the Sessions rename: 3 stale "Servers" references in the sidebar/tools description and Session-Grid airspace section. The 6 remaining "server" hits are all preserved C# class/file/property identifiers (`EphemeralFileServer`, `X11ServerManager`, `ServerListViewModel`, `servers.default.json`, `ServerId`, `### ServerDialog` section header)

#### Testing
- **+37 new tests** in `tests/Heimdall.App.Tests/` covering services with previously zero coverage:
  - `ThemeServiceTests` (10 - 4 active + 6 `[Skip]` for WPF Application context): `AvailableThemes` enumeration, constructor defaults, `ThemeRevision` initial value, no-throw under no-Application. Migration / idempotence / event / canonical-casing scaffolds wait for a future WPF fixture
  - `MigrationServiceTests` (13): `DetectLegacyInstallation` positive/negative/null path, `ImportFromLegacyAsync` round-trip with valid settings + server inventory, empty arrays, malformed JSON, missing-file failure mode
  - `EphemeralFileServerTests` (14): HTTP/TFTP lifecycle, argument validation, idempotent stop-when-not-running, **PERF-05 double-start regression guard**, `Dispose`/`DisposeAsync` cleanup, `GetLocalIpAddress` static helper. Each test uses a distinct port in the IANA dynamic range (49510-49514) and silently skips when port acquisition fails for restricted CI environments
- All new tests follow the existing project pattern: xUnit only, `IDisposable` cleanup with temp directories, no mocking library

#### Housekeeping
- Tests: **1,761 passing** (was 1,724) + 6 skipped (intentional WPF scaffolds)
- i18n: 4,855 keys (EN/FR parity maintained, +2 net)
- Build: clean, 0 warnings, 0 errors

---

## [v2026.041202] - 2026-04-12

### Theme system overhaul - centralized ThemeService, 7 Dracula variants only

#### ThemeService (single owner of the theme swap)
- **`Services/ThemeService.cs`**: singleton DI service with `ApplyTheme(string?)` as the only code path that replaces the theme `ResourceDictionary` in `Application.Resources.MergedDictionaries`
- **Idempotent swap**: no-op when the requested theme is already active; searches the existing dictionary via `Source.OriginalString.Contains("Theme.xaml")`
- **Legacy migration**: settings containing `"Dark"` or `"Light"` are silently migrated to `DraculaPro` and persisted via `ConfigManager.MergeSettingAsync`
- **`ThemeRevision`**: monotonic counter bumped *before* the `ThemeChanged` event fires, used by XAML `MultiBinding` triggers
- **DWM integration**: every open `Window` gets its dark-mode title bar flag refreshed via `WindowThemeHelper.ApplyCurrentTheme` after each successful swap
- **Duplication removed**: `App.xaml.cs` and `MainViewModel.cs` no longer contain their own theme switch statements (the previous duplication was the root cause of commit `0d3d9c0`, where `ApplyThemeFromSettings` only knew Dark/Light)

#### Themes removed
- **Deleted**: `src/Heimdall.App/Themes/DarkTheme.xaml`, `src/Heimdall.App/Themes/LightTheme.xaml`
- **Kept**: 7 Dracula variants - `DraculaProTheme` (default), `AlucardTheme`, `BladeTheme`, `BuffyTheme`, `LincolnTheme`, `MorbiusTheme`, `VanHelsingTheme`
- `App.xaml` default merged dictionary → `Themes/DraculaProTheme.xaml`
- `config/settings.default.json`, `AppSettings.DefaultTheme`, `SettingsViewModel._defaultTheme`, `SchemaValidator.ValidThemes` all updated to `DraculaPro` / the 7-variant set
- Settings theme `ComboBox` in `MainWindow.xaml` cleaned up (removed `Mw_ThemeDark` and `Mw_ThemeLight` items + their localization hooks)

#### Theme reactivity - converters, code-behind, editor
- **Brush-resolving converters** (`ConnectionTypeToColorConverter`, `ConnectionTypeToBrushConverter`, `ConnectionStateToBrushConverter`, `ServerStatusToColorConverter`) implement both `IValueConverter` *and* `IMultiValueConverter` with a shared `ResolveBrush` helper. XAML sites route them through `MultiBinding [value, DataContext.ThemeRevision]` so WPF re-runs the converter on each swap. `ElementName=MainWindowRoot` required (not `RelativeSource AncestorType=Window`) so the binding resolves from inside Command Palette `Popup` content
- **Generic resource-key converters**: `ResourceKeyToBrushConverter` (dual `IValue`/`IMulti`, used by the sidebar Tools `TreeView`) and `ResourceKeyToGeometryConverter` (simple `IValue`, resolves `Geo.Tool.*` keys)
- **Code-built UI in `MainWindow.xaml.cs`** (`PopulateToolsTab`, `RefreshToolsTabSections`, `CreateToolsTabCard`, `UpdateToolLaunchContextLabels`): `element.SetResourceReference(<DP>, "BrushKey")` instead of caching `Brush` instances from `FindResource`. Hover-state toggles call `SetResourceReference` with a conditional key rather than flipping pre-cached brushes
- **`EmbeddedEditorView`**: reads AvalonEdit chrome colors (`Background`, `Foreground`, `LineNumbersForeground`, `SelectionBrush`, `CurrentLineBackground/Border`) via `ResolveColor("BrushKey", fallback)` - no more Dark/Light branches. Subscribes to `ThemeService.ThemeChanged` in `Loaded`, unsubscribes in `Unloaded`. Syntax token palette stays fixed Dracula (shared across all variants)
- **Hardcoded hex cleanup in `MainWindow.xaml`**: `ContentDropZone` background → `{DynamicResource DragDropOverlayBackground}`, broadcast-mode `DataTrigger` → `{DynamicResource BroadcastActiveBrush}`

### Sidebar UX redesign - tabbed Servers / Tools panel

- **Tabbed sidebar**: two `RadioButton`s (`SidebarTabServers` / `SidebarTabTools`, `GroupName=SidebarTabs`) replace the collapsible `ToolsQuickPanel` (`MaxHeight=350`, bottom-docked). Both tabs now share the full sidebar height; `Visibility` of `SidebarServersContent` / `SidebarToolsContent` is bound to each RadioButton's `IsChecked`
- **`SidebarTabStyle`** (`CommonControls.xaml`): flat `RadioButton` template with accent underline on `IsChecked`, `HighlightBrush` on hover, `FocusIndicatorBrush` on keyboard focus - all colors via `DynamicResource`
- **Servers tab**: unchanged - toolbar (search, add, expand/collapse) + `ServerTreeView`
- **Tools tab**: filter `TextBox` + context label + full-height `TreeView` with collapsible categories. Data model:
  - `SidebarToolCategoryViewModel` (ObservableObject): `CategoryName`, `BrushKey`, `Tools`, `VisibleCount`, `IsExpanded`, `IsVisible`
  - `SidebarToolItemViewModel`: `Id`, `Name`, `BrushKey`, `IconGeometryKey`, pre-lowercased `Searchable` blob (`name + aliases`)
- **Lazy populate**: `BuildSidebarToolsData()` reads `ToolRegistry.All`, groups by `Category`, sorts alphabetically per group - invoked on first `SidebarTabTools.Checked` and rebuilt when `ToolRegistry.ExternalToolsChanged` fires
- **Filter**: `Searchable.Contains(filterLower)` per item, auto-expand matching categories, empty-state label when no results
- **Launch flow**: `LaunchSidebarTool(item)` reuses the same primitives as the full-page Tools tab (`CreateInheritedToolContext` / `ResolveToolTabTitle` / `vm.OpenToolTabAsync` / `vm.TrackRecentTool`)
- **Ctrl+Shift+T**: toggles the active sidebar tab. Gotcha: setting `RadioButton.IsChecked = false` on a grouped button does NOT auto-check the sibling; `ToggleSidebarTab()` explicitly assigns `IsChecked = true` on the target
- **Persistence**: reuses the existing `ShowToolsPanel` bool setting (`true` = Tools tab active at startup)

### Locales
- +4 keys (EN/FR): `SidebarTabServers`, `SidebarTabTools`, `A11ySidebarTabServers`, `A11ySidebarTabTools`

### Removed
- `Themes/DarkTheme.xaml`, `Themes/LightTheme.xaml`
- `ToolsQuickPanel`, `BtnToggleToolsPanel`, `ToolsToggleChevron`, `Mw_ToolsToggleLabel`, `Mw_ToolsPanelHeaderLabel`, `Mw_ToolsPanelNoResults`, `Mw_ToolsPanelContextText`, `Mw_ToolsScanIndicator`, `ToolsCategoryStack`, `ToolsPanelScroll`, `ToolsPanelScrollHint`
- `MainWindow.ToggleToolsPanel()`, `PopulateToolsPanel()`, `CreateToolCard()`, `PersistToolsPanelState()`, `OnToolsFilterChanged`, `OnToolsPanelScrollChanged`, `_toolsPanelPopulated`
- `App.xaml.cs::ApplyThemeFromSettings()` and `MainViewModel::OnThemeChanged()` - both switch statements moved into the centralized `ThemeService`

### CI fix - SDK 10.0.201 overload resolution
- `dotnet format` on SDK 10.0.201 mis-inferred `var queryLower = query.ToLowerInvariant()` as `int` in 3 specific sites with lambda / nested `var` contexts, routing `string.Contains(string, StringComparison)` to the `char` overload (CS1503). Replaced `var` with explicit `string` types in `MainWindow.OnSettingsSearchTextChanged` and `OnSidebarToolsFilterChanged` - 67 other call sites in the codebase were unaffected
- `dotnet format` pass applied in a separate commit to fix ENDOFLINE / CHARSET / IMPORTS drift that had accumulated across recent PRs

### Housekeeping
- Tests: 1,730 passing
- i18n: parity maintained EN/FR (+4 keys)
- CI build: .NET 10.0.x runner

---

## [Unreleased] - 2026-04-02

### Terminal keyboard fix - Delete key no longer triggers server deletion

- **Root cause**: WebView2 SDK routes keys via `AcceleratorKeyPressed` → synthetic WPF `KeyDown`, but `Keyboard.FocusedElement` stays stale on the TreeView. The previous fallback (`FindAncestor<TreeView>` exclusion) was self-defeating in the most common scenario (user clicks TreeView then terminal).
- **Fix**: Check `e.OriginalSource is WebView2` in the `OnKeyDown` handler - the SDK always sets `OriginalSource` to the WebView2 control for terminal-originated keys. Removed the unreliable `ActiveSession.ConnectionType` + `TreeView` exclusion fallback.

## [Unreleased] - 2026-04-01

### Command Library UX audit - layout, responsiveness, feedback, performance

#### Layout
- **Generator panel sticky buttons**: Copy/Send/Edit/Delete action buttons moved outside the ScrollViewer into a fixed Grid row - always visible regardless of parameter count, notes, or examples in the scrollable area
- **Generator ↔ History mutual exclusion**: selecting an action auto-closes the History panel; toggling History auto-closes the Generator - prevents both panels from crushing the action list on 1080p split panes
- **Responsive filter bar**: replaced DockPanel with Grid+WrapPanel - search TextBox always gets full width (own row), filter ComboBoxes wrap gracefully on narrow panes instead of crushing the search input
- **HistoryList themed hover/select**: added ControlTemplate with SurfaceBrush (hover) and CardBrush (select) matching the ActionList visual treatment

#### Feedback
- **Loading indicator**: ToolLoadingBarStyle ProgressBar shown during initial data load with `finally` block for guaranteed cleanup on error
- **Example click clears stale validation**: clicking a pre-built example now clears any previous parameter validation error

#### Performance
- **O(1) search filtering**: replaced `_searchResults.Any(r => r.Id == ...)` (O(n) per item) with a `HashSet<string>` lookup in `FilterPredicate`

#### Dialog
- **DefaultValue watermark**: both Windows and Linux parameter DefaultValue TextBoxes now use `WatermarkTextBoxStyle` - placeholder text ("Default value") visible when empty and unfocused

---

## [v2026.033108] - 2026-03-31

### Fix tunnel scan - host discovery, per-probe timeout, zombie prevention

#### Network Cartography (critical fix)
- **Root cause**: scanning via SSH tunnel found only 1 host (the gateway itself) instead of the full subnet. Two bugs: (1) no host discovery phase (ping sweep, ARP) - only hosts with open ports on the scanned list were returned, (2) sequential `/dev/tcp` probes with no per-probe timeout - a single filtered port blocked the entire scan per IP for 20-127 seconds (kernel TCP retransmit timeout), causing `CommandTimeout` to kill the command before most ports were tested
- **Phase 1 - Host discovery**: batch ping sweep via SSH (all IPs as parallel background jobs in a single `CreateCommand`), ARP table read (`/proc/net/arp`) for ICMP-blocked hosts, automatic fallback to full-subnet scan when ping is restricted on the gateway
- **Phase 2 - Batch reverse DNS**: single SSH command for all alive hosts (was one command per IP)
- **Phase 3 - Parallel port probes**: all ports for a host run as background bash jobs simultaneously (`(echo >/dev/tcp/IP/$p && echo $p) &`), bounded by `sleep 5; kill $(jobs -p); wait` fence - no single filtered port can block the scan
- **Explicit `bash -c`**: ensures `/dev/tcp` support regardless of the gateway's login shell (`dash`/`sh` lack it)
- All alive hosts now included in results (even those with no open ports), matching direct scan behavior

#### Port Scanner, Banner Grabber, Firewall Tester, Default Credential Scanner
- **Same `/dev/tcp` fix**: all four tools' tunnel probe functions wrapped with `timeout 2 bash -c` to prevent filtered ports from leaving zombie bash processes on the gateway
- `CommandTimeout` raised from 2-3s to 5s as a safety net (per-probe timeout is now the primary mechanism)

#### i18n
- +3 keys (EN/FR): `ToolNetMapTunnelPingSweep`, `ToolNetMapTunnelDiscovered`, `ToolNetMapTunnelScanningHost`

#### Housekeeping
- Tests: 1,714 passing (unchanged)
- i18n: 4,688 keys (EN/FR parity maintained)

---

## [v2026.033006] - 2026-03-30

### UX audit remediation - Dispose memory leaks, i18n format strings

#### Memory Leak Fixes
- **18 tool views**: added event handler unsubscription (`-=`) in `Dispose()` for all subscriptions (`+=`) made in constructors - prevents views from being retained in memory after tab closure
- Affected views: ArpMonitor, Base64, CertInspector, CrontabBuilder, DateTimeConverter, HackerSimulator, HttpStatusCodes, IpConverter, NetworkCalculator, NetworkCartography, Notes, Ping, PortScanner, ServiceStatus, SubnetCalculator, TextCaseConverter, TextDiff, MilkdownEditor
- Timer cleanup: `Tick -= handler` added before `Stop()` on all `DispatcherTimer` fields (Arp, Ping, ServiceStatus, HackerSimulator, TextDiff, DateTimeConverter)
- WebView2 cleanup: `NavigationStarting` and `WebMessageReceived` unsubscribed with null guard in MilkdownEditorControl

#### i18n
- **DefaultCredentialView**: replaced string concatenation (`service + " " + L(key)`) with proper `string.Format(L(key), service)` for RTL-safe formatting
- Updated locale keys `ToolDefCredDetailAccepted` and `ToolDefCredDetailRejected` to use `{0}` placeholder (EN + FR)

#### Housekeeping
- Tests: 1,714 passing (unchanged)
- i18n: 4,685 keys (EN/FR parity maintained)

---

## [v2026.033005] - 2026-03-30

### Security audit remediation - context-aware sanitization, external tools, a11y

#### Security
- **Context-aware placeholder sanitization**: `InputValidator.IsShellTarget()` detects shell interpreters (cmd.exe, PowerShell, bash, sh, zsh, wsl, cscript, wscript, mshta) and script extensions (.bat, .cmd, .ps1, .vbs, .js, .wsf, .hta). Shell targets get strict metacharacter stripping; regular .exe targets get relaxed stripping that preserves `()`, `'`, `%` in legitimate values (double quotes always stripped for MSVC CRT safety)
- Applied to both `ExternalToolDefinition.ResolveArguments()` (user-defined tools) and `CommandCredentialProvider.ExpandTemplate()` (credential provider CLI)
- **VNC WebSocket Origin validation**: replaced `StartsWith` with exact `Uri` host matching to prevent CSWSH subdomain bypass
- **Command palette tool shadowing**: external tools now always searched alongside native tool prefix matches (previously hidden when a native tool prefix matched first)
- **External tools config validation**: save blocked on empty name/path or duplicate names with inline error via `ValidationSummary`; `ExternalToolItemViewModel` uses `[Required]` + `[NotifyDataErrorInfo]`
- **Credential provider soft failures surfaced**: `ShowWarning()` dialog when `GetCredentialAsync()` returns null (empty output or non-zero exit) instead of silent fallthrough
- **ServerDialog async**: removed `.GetAwaiter().GetResult()` blocking calls, replaced with async `Loaded` handler
- **RunHidden alignment**: `CreateNoWindow = true` added to context menu launch path (was only on palette path)

#### UX
- **External tools editor**: Browse button for working directory; structured placeholder help panel; live command preview with resolved placeholders from selected server; Test button to launch from Settings; binary existence validation on save
- **Credential provider setup**: preset dropdown (KeePassXC, Bitwarden CLI, 1Password CLI, pass); database path browse button; Test button with inline feedback (success/no result/timeout/error); placeholder hint below command field
- **Onboarding interactive**: each step now navigates to the relevant UI area (Step 1 → Servers tab, Step 2 → Settings, Step 3 → enables Tools panel); keyboard a11y (Escape, Tab cycle, focus, synced AutomationProperties.Name)
- **Configurable external tool timeout**: `ExternalToolTimeoutMs` in Settings > Advanced (default 60s, range 5s–600s), replaces hardcoded 60s in ExternalToolWrapperView
- **Tool scan indicator**: "Scanning..." label on Tools panel header during background third-party tool detection

#### Previous (v2026.033005-pre)
- **External tool placeholder resolution**: `{Port}` now resolves to the protocol-specific port (SSH→22, FTP→21, VNC→5900, Telnet→23) instead of the generic RDP port; `{KeyFile}` placeholder now populated from server SSH key path
- **Process timeout cleanup**: external tool wrapper kills the process tree on timeout/cancel in both standard and elevated (UAC) code paths
- **Credential provider stderr deadlock**: stderr is now drained concurrently to prevent 4KB pipe buffer deadlock on Windows
- **Settings dirty flag**: inline edits to external tool properties now correctly mark Settings as dirty
- **ServerDialog i18n**: 44 new keys (EN/FR) covering port labels, help text, session kinds, mode summaries, tunnel descriptions, and gateway captions for all 8 protocols
- **Ctrl+K palette**: external tool placeholders resolved against selected server when available

#### Housekeeping
- `InternalsVisibleTo` added to `Heimdall.Core.csproj` for `Heimdall.Core.Tests` (ExpandTemplate testing)
- `VENDORED.md` manifests added for Assets/Tools (plink 0.83, gsudo 2.5.1), Assets/vnc (noVNC 1.5.0, pako 1.0.3), Assets/drawio (26.0.9) - upstream versions, licenses, and review dates
- i18n: +189 keys (4,685 total, EN/FR parity)
- Tests: 1,714 passing (+81 new: IsShellTarget, context-aware sanitization, ExpandTemplate relaxed/strict paths)

---

## [v2026.033004] - 2026-03-30

### Network Cartography - multi-probe discovery, new columns, SNMP OID classifier

#### Discovery Pipeline
- ARP table seeded before ping sweep: hosts known to the OS bypass ICMP
- Multi-probe fallback for undiscovered IPs: reverse DNS + NetBIOS Name Service + TCP connect on 5 key ports (22, 80, 443, 445, 3389)
- Filter empty hosts: `HostScanResult.HasMeaningfulData` removes IPs with no ports, hostname, role, or metadata from both display and CSV export

#### DataGrid & Export
- New **MAC Address** column (after IP)
- New **Latency** column (after OS, shows ping round-trip in ms)
- CSV export filters out empty hosts (no more 238 phantom rows on a /24)

#### SNMP Enterprise OID Classifier
- Cisco: routers (1.3.6.1.4.1.9.1), Catalyst switches (9.5), switches (9.6) - confidence 80-85%
- Juniper, MikroTik, Fortinet, Palo Alto, VMware, Microsoft OID branches
- Boosts role classification confidence on OID match

#### Housekeeping
- i18n: +2 keys (4,452 total, EN/FR parity)
- Tests: 1,610 passing

---

## [v2026.033003] - 2026-03-30

### UX audit fixes, CIDR auto-detection, and scan timeout resilience

#### Accessibility & Keyboard
- AutomationProperties.Name on MainWindow navigation tabs (Servers, Tunnels, Scheduled, Settings, About) via `{loc:Translate}`
- TabIndex keyboard navigation added to 5 tool views: HackerSimulator, CronJobManager, PasswordAudit, SshKeyAudit, DiagramEditor
- GridSplitter accessibility label in SplitContainerControl
- WCAG contrast fix: replace Opacity="0.6" on settings unit suffixes with TextDisabledBrush

#### Visual
- Fix tool card hover: remove default WPF button chrome (bare ContentPresenter template), use HighlightBrush for hover background
- Fix SecNumCloudAuditView CornerRadius cast error (`CornerRadius` resource was cast as `Double`)

#### Network Tools
- Ping Monitor: add gateway routing via SSH (`CmbRouteVia` selector, tunneled ping via SSH exec)
- SecNumCloud Audit: auto-detect local CIDR on init, detect remote CIDR on gateway selection
- Extract shared `SubnetDetector` helper from NetworkCartography (reusable across tools)

#### Critical Bug Fix - Scan Timeout Resilience
- Fix scans silently aborting when per-operation timeouts fire: `CancellationTokenSource(timeout)` + linked token `OperationCanceledException` was indistinguishable from user cancellation
- 13 catch sites fixed across 7 files: CartographyEngine (ProbePortAsync, InspectTlsWithHttpAsync), SecNumCloudAuditEngine (6 check methods), NetworkScanner, HttpFingerprinter, FaviconHasher, BannerGrabberView, CertInspectorView
- Fix pattern: `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` absorbs per-operation timeouts without aborting the entire scan

#### Housekeeping
- i18n: +11 keys (4,450 total, EN/FR parity)
- Tests: 1,610 passing (283 SSH + 131 App + 1,196 Core)

---

## [v2026.032903] - 2026-03-29

### Comprehensive UX audit - accessibility, async guards, empty states, keyboard across 49 tools

#### Accessibility
- Explicit `TabIndex` on 45 tool views (top-to-bottom, left-to-right visual order)
- 15 new empty state panels with localized icon + hint text
- 24 empty states migrated to shared `ToolEmptyStateIconStyle`/`ToolEmptyStateTextStyle`
- Watermarks added: PasswordAudit, SshKeyAudit, ServiceStatus
- DiagramEditor: tooltips on 13 toolbar buttons

#### Async & Keyboard
- SshKeyGenerator/CertificateGenerator: key generation moved to `Task.Run` (unblocks UI thread)
- TextDiffView: double-click guard + input disable during comparison
- Enter key wired: ArpMonitor, TextCaseConverter (`Ctrl+Enter`), CrontabBuilder, DateTimeConverter
- Focus on load: CrontabBuilder, ServiceStatus, DiagramEditor, HackerSimulator
- DiagramEditor: toolbar disabled until WebView2 initialization completes

#### Code Quality
- `DefaultPorts`: extended with 22 named constants, replacing magic numbers across presets
- `ToolAsyncStateController`: fix primary constructor redundant field re-declaration
- `ToolPickerDialog`: input validation via `InputValidator.Validate()`, trigger ordering fix
- `NetworkToolPresets`: DNS server labels localized, `DnsServerPreset` nested in class
- Remove dead `showUnpin` parameter from `CreateToolsTabCard`
- Fix regex false positives in `ToolXamlInputHardcodingTests`
- Fix fragile attribute-order assumption in `DenseToolTabOrderTests`

#### Housekeeping
- Remove 6 obsolete docs (UX_GITHUB_ISSUES.md, network-discovery research, 4 audit screenshots)
- i18n: +24 keys (4,453 total, EN/FR parity)
- Tests: 1,610 passing (283 SSH + 131 App + 1,196 Core)

---

## [Unreleased] - 2026-03-28

### Comprehensive audit - security, i18n, accessibility, and robustness across 49 files

#### Security
- Centralize shell escaping in `InputValidator`: `EscapeShellArg()`, `EscapeForDoubleQuotedString()`, `ValidateDomain()`, `SanitizeCsvCell()`
- Add input validation + shell escaping on all `CreateCommand()` calls across 16 tool views (CWE-78 prevention)
- CSV formula injection prevention via `SanitizeCsvCell()` in 10 exporters + generic `ToolContextMenuHelper`
- CRLF sanitization on raw HTTP Host header construction
- IIS CVE predicates: proper version checks replacing always-true predicates

#### Fixed
- SslStream disposal in 7 files (try/finally + DisposeAsync + leaveInnerStreamOpen)
- SemaphoreSlim disposal in 6 files
- RSA/ECDSA crypto key disposal in 3 files (using var)
- X509Certificate disposal after clone, CTS disposal in finally
- Process kill-on-cancellation for DNS processes
- OperationCanceledException propagation at 40+ catch sites
- Blocking async converted to proper await (TlsAuditView certificate retrieval)
- Dead code removal (TlsAuditView cipher enumeration)
- Race condition on CTS lifecycle (Interlocked.Exchange)
- Password cleared on Dispose (PasswordAuditView)
- DKIM success message showing DMARC wording
- Punycode/IDN hostname validation (allow -- mid-label)

#### Internationalization
- Extract ~170 i18n keys from SecNumCloudAuditEngine, HtmlReportGenerator, and tool views
- SecNumCloudAuditEngine: `Func<string, string> localize` constructor parameter
- HtmlReportGenerator: `localize` parameter on `Generate()`
- Locale count: ~4,290 keys (EN/FR parity)

#### Accessibility
- AutomationProperties.Name on all interactive controls across 17+ XAML files
- Hardcoded English accessibility labels replaced with runtime-localized SetName() pattern

#### Data Model
- AuditScope.Targets: `List<string>` -> `IReadOnlyList<string>`

### UX audit - a11y, design tokens, i18n, and interaction across 49 tools

Three-pass cross-audit covering all 49 built-in tools (64 files, +809/-417 lines).

#### Accessibility
- 565 AutomationProperties.Name annotations in XAML (49/49 tool files covered)
- 592 AutomationProperties.SetName() calls in code-behind (49/49 files)
- 11 unnamed buttons given x:Name for a11y (ChmodCalculator presets, PasswordGenerator quick-lengths)

#### Design Tokens
- New `ToolContentMaxWidth` (700) token - 20 files migrated from hardcoded MaxWidth values
- New `PaddingButtonToolbar` (8,4) token - 17 buttons migrated (DiagramEditor, NotesToolView)
- ~90 buttons migrated to padding tokens (PaddingButtonCopy, PaddingButtonPreset, PaddingButtonPrimary, PaddingButtonToolbar, PaddingButtonHelp)
- Hardcoded `CornerRadius="3"` replaced with CornerRadiusXs token (SnmpWalker, CveLookup)
- Hardcoded `Foreground="White"` replaced with TextOnAccentBrush (SshKeyAudit, CveLookup)
- Hardcoded `FontSize="12"` / `FontSize="16"` replaced with FontSizeCaption / IconSizeMedium tokens

#### Interaction
- 8 tools now handle Enter key on input fields (UUID, SshKeyGen, CertGen, FirewallTester, NetworkCalc, SshConfigGen)
- 2 ProgressBars added (CronJobManager, ServiceStatus) for async loading feedback
- UUID BtnGenerate promoted from SecondaryButtonStyle to PrimaryButtonStyle

#### Internationalization
- FirewallTester placeholder moved from hardcoded XAML Tag to locale keys
- 6 new locale keys added (en.json + fr.json): ToolFwTestHostsPlaceholder, ToolCronJobA11yLoading, ToolServicesA11yLoading

---

## [v2026.032701] - 2026-03-27

### Comprehensive tool audit - robustness, accessibility, and UX (15 tools, 26 files)

#### Password Generator overhaul
- **3 generation modes**: Random, Syllable (CV/CVC), and Passphrase with per-mode presets
- **Optional clipboard auto-clear** (30s): checkbox in Advanced section, visual hint after copy
- **Custom presets filtered by mode**: only presets matching the current mode are shown
- **Title vs WordCase differentiated**: Title capitalizes first group only, WordCase capitalizes every group
- **Strength hidden when empty**: no more "Critical (0 bits)" on blank output
- **Quick-length highlight** now updates correctly after preset application
- **TextBox guards**: MaxLength on separator (4) and custom specials (64) inputs
- **Preset cache**: avoids disk I/O on every mode change
- **try/finally** on ApplyCustomPreset to prevent flag freeze on exception

#### Cross-tool robustness (12 files)
- **Clipboard.SetText protection**: 21 unprotected calls across 12 tools wrapped in `try/catch(ExternalException)` to handle locked clipboard gracefully (Base64, CertGenerator, Chmod, Crontab, Json, JWT, SshConfig, TextDiff, HostsFile, Notes, PasswordGenerator)
- **try/finally on boolean flags**: HackerSimulator (`_isRunning`, `_typingInProgress`, `_cursorVisible`), PingTool (`_isRunning`), PortScanner (`_isScanning`) - prevents UI freeze if setup code throws
- **CanClose()** added to ServiceStatus and CronJobManager to prevent close during async operations

#### Accessibility
- **LiveSetting="Polite"** added to 9 dynamic output elements across 7 tools (PasswordGenerator, ServiceStatus, CronJobManager, SshConfigGenerator, UUID, NetworkCalculator, LogViewer, NetworkCartography)
- **Focusable="True"** on PasswordGenerator output TextBox for keyboard navigation

#### i18n
- 2 new locale keys: `ToolPwdGenClipboardAutoClear`, `ToolPwdGenClipboardClearHint` (EN + FR)
- Total: 3,654 keys per locale

---

## [v2026.032606] - 2026-03-26

### Security Audit tool overhaul

#### Extensible scenario system
- **25 scenarios** across 6 categories (Visual, Attack, Deployment, Hardening, Incident, Identity) and 3 realism levels (Demo, Ops, Enterprise)
- **External JSON scenario packs**: template engine with `{{pick:...}}`, `{{number:min-max}}`, `{{hex:N}}`, `{{ip}}`, `{{mac}}` variables - add custom scenarios without recompiling
- **Playlist system**: ordered scenario sequences with 5 built-in playlists (Client Demo, SOC, DevOps, Compliance, Red Team)
- **Favorites**: star/unstar scenarios, filter by favorites
- **Toolbar redesign**: scenario picker, category/realism filters, text search, speed slider, playlist selector

#### New infrastructure scenarios (JSON-driven)
- Ansible Rolling Deployment, Multi-Hop Server Chain, Role Rollout / Hardening
- Vault Secret Rotation, HAProxy Blue/Green Promotion, Linux Patch Window
- AWX Job Template Rollout, Helm / Kubernetes Upgrade, PKI / Certificate Renewal

#### Playback features
- **Seed-based deterministic replay**: same seed reproduces identical scenario output
- **Transcript export**: text and Markdown format with per-scenario sections
- **Vintage CRT mode**: scanline overlay with flicker animation

#### Settings persistence
- Favorites, last scenario, playlist, random mode, vintage monitor state saved to `settings.json`
- 5 new `HackerSimulator*` properties in `AppSettings`

#### Code quality (post-review cleanup)
- 35 UI chrome strings extracted from inline `Tx()` to locale files (CI key-parity enforced)
- 9 redundant C# scenario builders removed (JSON-only, no dead code)
- Blocking `GetAwaiter().GetResult()` replaced with proper async
- 4 bare `catch {}` blocks narrowed to `catch (Exception)`
- 10 magic numbers extracted to named constants
- Duplicated `JsonSerializerOptions` consolidated to `static readonly` field

---

## [v2026.032605] - 2026-03-26

### Diagram Editor audit and embed protocol fixes

#### Diagram Editor (P1)
- **Empty diagram loading**: Canvas now initializes automatically on open (previously blocked on "Loading" until user clicked New)
- **Native autosave**: Replaced custom polling autosave (manual graph serialization via mxCodec) with draw.io's native `autosave`/`save` embed events - preserves full .drawio context
- **External link relay**: Help menu and external links now open in the default browser via `openLink` embed event
- **Menu bar hidden**: draw.io's built-in menu bar (File/Edit/View/Arrange/Extras/Help) disabled - `mxPopupMenu` dropdowns cannot open inside a WebView2 iframe due to pointer event routing limitations; Heimdall's own toolbar provides New/Open/Save/Export PNG

#### Architecture constraint documented
- draw.io embed mode requires `(window.opener || window.parent) != window` - iframe is mandatory (direct WebView2 load bypasses `initializeEmbedMode`)

#### CLAUDE.md
- Condensed from 495 to 170 lines (~65% reduction) - removed content derivable from code, kept all bug-prevention gotchas

---

## [v2026.032601] - 2026-03-26

### Comprehensive UX audit and Codex audit implementation

#### WCAG Contrast Fixes (P0)
- **Dark ErrorColor**: #FF5555 → #FF6B6B (5.13:1 on primary background)
- **Dark BorderColor**: #6272A4 → #7B8EC4 (4.41:1)
- **Dark TextDisabledColor**: #9298B0 → #A8AECA (4.17:1 on surface)
- **Light BorderColor**: #94A3B8 → #708090 (3.72:1)
- **Dark SurfaceColor**: #44475A → #4A4D64 (improved card/background separation)

#### Accessibility (P0-P1)
- **14 empty AutomationProperties.Name** replaced with declarative `{loc:Translate}` in MainWindow
- **Keyboard context menu**: Shift+F10 / Apps key opens context menu on TreeView
- **LiveSetting="Polite"**: SSH/RDP/VNC status text announced by screen readers
- **Icon button a11y**: Overlay reconnect/close buttons labeled in all embedded views
- **59 decorative MDL2 icons**: Hidden from screen readers via `AutomationProperties.Name=""`
- **Tab focus ring**: Navigation tabs show FocusIndicatorBrush on keyboard focus

#### ServerDialog Redesign (Codex Critique)
- **Auth fields in basic mode**: Username, password, SSH key now visible without Advanced toggle
- **Protocol-specific sections**: RDP/SSH/SFTP/VNC/FTP/Telnet/Local/Citrix each show relevant auth fields
- **Advanced mode reduced**: Only Connection diagram, Tunneling, Options, Info, Gateway Auth remain behind toggle

#### Scheduled Task Dialog (Codex Elevated)
- **New ScheduledTaskDialog**: Replaces two sequential InputDialogs with structured form
- **Server ComboBox**: Searchable dropdown from server inventory
- **Schedule type**: Daily (time picker) or Interval (minutes) with live validation
- **Next run preview**: "Next execution: tomorrow at 09:30" shown in real-time
- **Edit support**: Edit button + double-click on DataGrid row
- **Dirty state guard**: Warns on close with unsaved changes

#### Command Palette Safety (Codex P1)
- **Click = select only**: Single click highlights without executing
- **Enter / double-click = execute**: Prevents accidental connection launches
- **Ctrl+Enter = split**: Unchanged

#### Server Detail Panel Enrichment (Codex P2)
- **6 new metadata rows**: Project (with color dot), Username, Gateway, Auth summary, Tags, Favorite star
- **Auth summary**: Per-protocol (e.g., "SSH Key + Password", "Agent", "Prompt")
- **Gateway name resolution**: Resolved from inventory map

#### Settings Improvements (Codex Elevated)
- **Layout widened**: MaxWidth 600px → 900px for better desktop utilization
- **Sticky action bar**: Save/Reset/Import/Export pinned at top with border separator
- **Explicit Browse buttons**: "..." replaced with folder icon + "Browse" label on all 5 buttons
- **Search filter**: TextBox filters sub-tabs by keyword (bilingual EN/FR matching)

#### Filter Enrichment (Codex Medium)
- **8-field search**: Sidebar filter + Command Palette now search DisplayName, RemoteServer, Group, Username, ConnectionType, Environment, Tags, ProjectName

#### Validation Consistency (Codex Medium)
- **GatewayDialog**: Per-field inline errors (NameError, HostError, PortError, UserError)
- **ProjectDialog**: Per-field inline errors (NameError, DescriptionError)
- **Live re-validation**: Both dialogs re-validate on keystroke after first save attempt
- **Focus on dialog open**: First field auto-focused in GatewayDialog and ProjectDialog

#### Dirty State Guards (P1)
- **ServerDialog**: IsDirty tracking with _isInitializing guard, confirm on Cancel
- **GatewayDialog**: Same pattern with per-property tracking
- **ProjectDialog**: Same pattern

#### Typography & Visual Hierarchy (Codex Medium)
- **Scale widened**: Caption 11→12, Body 12→13, Subtitle 14→15, Title 18→20
- **SpacingLg**: 16→20 for more section breathing room
- **Section title margin**: Added top/bottom spacing in DialogCommonStyles
- **OpacityDisabled**: 0.55 → 0.60 for better dark theme distinction

#### Keyboard & Navigation
- **InputGestureText**: Ctrl+E, Ctrl+Del, Ctrl+N shown on context menu items
- **Tooltip shortcut hints**: Ctrl+Del, Ctrl+K added to toolbar buttons
- **Scroll position restore**: TreeView scroll offset saved/restored on tab switch
- **Discoverability hints**: Visible "Ctrl+N · Ctrl+K · F1" in empty state, detail panel, status bar

#### Additional Improvements
- **Last-used gateway**: Pre-selects in Add Server dialog (persisted in AppSettings)
- **SFTP cancel**: Icon button with mid-transfer cancellation via progress callback
- **LocalFileBrowserView**: Dynamic Name column sizing
- **MessageDialog**: Button order normalized (Cancel → Primary), resizable
- **InputDialog**: SizeToContent instead of fixed height
- **Button MinWidth**: Standardized to 80px across all dialogs

#### i18n
- 3,566 keys (EN/FR parity) - +87 keys
- `StringToBrushConverter` for project color dots in detail panel

#### Tests
- **1,586 tests** (1,196 Core + 283 SSH + 107 App), all passing

---

## [v2026.032508] - 2026-03-25

### Full UX audit implementation (P0-P2)

#### WCAG Contrast Fixes (P0)
- **FileIconColorConverter theme adaptation**: Replaced 6 hardcoded Dracula RGB brushes with theme-aware resources (FileScriptBrush, FileConfigBrush, etc.) - Light theme file icons now legible (was 1.5:1, now 4.5:1+)
- **Dark theme ErrorColor**: #FF6E6E → #FF5555 (4.2:1 → 4.6:1, meets WCAG AA)
- **Dark theme TelnetBadgeBrush**: #A0A0B0 → #B0B4C8 (4.5:1 → 5.2:1)
- **Light theme BorderColor**: #CBD5E1 → #94A3B8 (1.5:1 → 3.2:1, meets WCAG 2.1 § 1.4.11 non-text)

#### Data Loss Prevention (P1)
- **Unsaved settings warning on tab switch**: Save/Discard/Cancel dialog when leaving Settings tab with pending changes
- **Unsaved settings warning on app exit**: Same dialog in Window.Closing handler
- **3-button MessageDialog**: New `ShowThreeWay()` method + `BtnTertiary` for Save/Discard/Cancel pattern
- **Window size/position persistence**: Saves Width, Height, Left, Top, WindowState to AppSettings on close; restores on load with virtual screen bounds validation

#### Accessibility (P1)
- **Reduced-motion support**: Respects Windows "Show animations" setting (`SystemParameters.MenuAnimation`) - animation durations overridden to 0ms when disabled (WCAG 2.1 § 2.3.3)

#### Keyboard Shortcuts (P2)
- **Ctrl+W**: Close current session tab (with confirmation if connected)
- **Ctrl+Tab / Ctrl+Shift+Tab**: Cycle between session tabs (next/previous)
- **F1 help updated**: New shortcuts documented in EN/FR
- **Tooltip shortcut hints**: Toggle sidebar tooltip now includes "(Ctrl+B)"

#### UX Guards (P2)
- **Double-click connect guard**: `_connectingServerIds` HashSet prevents duplicate concurrent connections to the same server from rapid clicks

#### i18n
- 3,501 keys (EN/FR parity confirmed) - +2 keys (BtnDiscard)

#### Tests
- **1,586 tests** (1,196 Core + 283 SSH + 107 App), all passing

---

## [v2026.032506] - 2026-03-25

### UX audit phase 2: validation, palette redesign, protocol-driven add server

#### Server Dialog - Protocol-Driven Flow
- **Protocol selector**: New Step 1 with 8 large card buttons (vector icons + protocol colors) replaces the connection type dropdown in add mode
- **Contextual fields**: Form fields adapt to selected protocol - Local Shell shows only name, SSH shows host+port, etc.
- **Edit mode**: Read-only protocol badge, form pre-populated, protocol selector bypassed
- **Back button**: Returns to protocol selector in add mode without losing form data

#### Server Dialog - Inline Validation
- **Per-field errors**: Inline error messages below DisplayName, Server, Port, LocalPort, AudioMode, ColorDepth
- **Live re-validation**: Errors clear in real-time as user corrects fields (ValidateProperty per keystroke)
- **Tab error badges**: Red count badges on Tunneling and Options tabs when they contain errors
- **Auto-focus**: First invalid field receives focus on save, with automatic tab/advanced mode expansion
- **Protocol-aware validation**: Only relevant fields validated per protocol; HasErrors stays consistent via ClearErrors per-protocol cleanup
- **VNC port validation**: Added [Range] validation with i18n support
- **Reusable style**: FieldValidationErrorStyle in DialogCommonStyles.xaml

#### Command Palette (Ctrl+K) - Redesign
- **Two-line layout**: Line 1: protocol icon + name + badge; Line 2: host:port + username + project + group
- **Responsive width**: 550-700px (MinWidth/MaxWidth) instead of fixed 550px, MaxHeight 450px
- **Active session indicator**: Protocol-colored left rail on connected sessions
- **Protocol badge**: Short labels (RDP, SSH, TEL, CTX, SH, TOOL) with per-protocol colors
- **Correct endpoint per protocol**: SSH/SFTP use SshPort, FTP uses FtpPort, VNC uses VncPort, Telnet uses TelnetPort
- **FTP/Telnet usernames**: Palette now shows credentials for all protocols, not just SSH/RDP

#### Settings
- **Unsaved changes indicator**: Orange dot on Settings tab when IsDirty, with localized tooltip
- **Theme revert on discard**: Live theme preview reverts to saved theme when user discards changes
- **Locale key fix**: Unsaved settings prompt now uses correct i18n keys

#### Bug Fixes
- **ServerDialog crash**: Fixed LayoutTransform storyboard using `FrameworkElement` instead of `UIElement` (runtime BAML error)
- **Scrollbar inversion**: Added `IsDirectionReversed="True"` to vertical Track in custom ScrollBar template
- **Telnet port loss on edit**: Telnet connections now load TelnetPort (not RemotePort) and skip default port reset in edit mode
- **Focus persistence**: FocusFirstInvalidField no longer permanently changes user's advanced-mode preference
- **Application.Current null check**: PaletteActiveIndicatorConverter safe during shutdown

#### Detail Panel
- **Edit/Delete buttons**: Added to server detail panel alongside Connect for better discoverability
- **Accessibility**: AutomationProperties.Name on all new interactive controls

#### Empty State
- **"No selection" enriched**: Segoe MDL2 icon + hint text + Ctrl+K quick connect tip when servers exist but none is selected

#### i18n
- 51+ new keys with full EN/FR parity (validation, protocol cards, hints, palette)
- 3,478+ keys per locale

---

## [v2026.032507] - 2026-03-25

### Complete UX audit implementation (19/20 items from triple-audit: Claude, Codex, Gemini)

#### Accessibility & Tooltips
- **Tooltip campaign**: Added localized tooltips to all icon-only buttons across MainWindow, EmbeddedRdp/Ssh/Sftp/Vnc/Citrix views, LocalFileBrowser, NotesToolView, PasswordGenerator, SessionPaneControl, SplitContainerControl (~47 buttons)
- **AutomationProperties localized**: Moved 45 hardcoded English `AutomationProperties.Name` from XAML to code-behind `ApplyLocalization()` using i18n keys (`A11y*` pattern)
- **Minimum font size**: Raised `FontSizeSmallCaption` from 9px to 11px for better readability on dense exploitation screens

#### Zero Hardcoding compliance
- **ComboBoxItems extracted to i18n**: Terminal color schemes (5), PowerShell execution policies (5), shell executables (5), SSH key algorithms (3), certificate algorithms (2), file encodings (4), HMAC formats (2), ping intervals (5) - all use `Tag` for stored value, `Content` set via `ApplyLocalization()`
- **Hardcoded ToolTip="Copy" removed** from PasswordGenerator history button (now localized via `Loaded` event handler)

#### Theme & Contrast
- **Scrollbar thumb contrast fixed**: Dark theme #7B8298 → #A8B0CC (2.8:1 → 4.2:1), Light theme #C0C0C0 → #999999 (1.8:1 → 4.8:1) - meets WCAG 2.1 non-text contrast minimum
- **Badge/protocol brush consolidation**: 5 new badge brushes (VNC, FTP, Citrix, Telnet, Local) + RDP/SSH/SFTP badge colors aligned with protocol accent brushes for visual consistency
- **Toolbar ghost button pressed state**: Changed from TextSecondaryBrush (poor contrast) to HighlightBrush

#### Discoverability & Navigation
- **Tools panel visible by default**: 33 built-in tools now shown on first launch instead of hidden behind collapsed toggle
- **Ctrl+Shift+T documented in F1 help** (EN/FR)
- **Wording "server-first" updated**: StatusReady, EmptyStateSelectServer, SearchPlaceholder now reference tools and Ctrl+K - not just servers
- **Command Palette mode indicator**: Shows "Split Mode" / "Merge Mode" label when palette opens in split/merge context
- **Command Palette auto-close**: Closes on sidebar tab change and window deactivation (preserves StaysOpen=True for ActiveX airspace compatibility)

#### Design System
- **EmptyStateStyle**: New reusable style in CommonControls.xaml for empty/onboarding states
- **DialogCommonStyles.xaml**: Extracted 8 shared styles (label, section title, hint text, section card, form inputs) from ServerDialog/GatewayDialog/ProjectDialog into shared resource dictionary
- **FadeIn animation**: Applied to ToolsQuickPanel for smooth expand transition
- **Notes dirty indicator**: Header shows "Unsaved changes" warning via `ToolNotesUnsaved` key when editor has pending changes

#### Network Cartography
- **Scan progress indicator**: Real-time "Scanning: X/Y hosts..." TextBlock in scan toolbar, updated from `HostDiscoveryProgress` event

#### Progressive Disclosure (ServerDialog)
- **Simple/Advanced mode**: Essential fields (Name, Host, Port, Type, Project, Gateway) always visible; 5 advanced tabs hidden behind animated toggle with ScaleY + Opacity transition (300ms ease-out open, 250ms ease-in close)
- **Mode persistence**: Advanced mode preference saved to AppSettings via `ConfigManager.MergeSettingAsync()`

#### Declarative i18n (loc:Translate markup extension)
- **TranslateExtension**: WPF `MarkupExtension` enabling `{loc:Translate Key}` syntax in XAML - auto-updates on runtime language switch via `INotifyPropertyChanged` on indexer
- **LocalizationSource**: Singleton bridge between WPF binding system and `LocalizationManager` DI service
- **PinDialog migrated**: Full POC - all 7 manual localization calls replaced with declarative XAML bindings, code-behind reduced to focus logic only

#### Icon System Unification
- **BitmapImage system removed**: Deleted `ConnectionTypeToIconConverter`, `ConnectionStateToIconConverter`, `IconResources.xaml`, and 37 PNG files
- **Two-tier icon architecture**: Vector geometries (`Geo.*` in IconGeometries.xaml) for domain icons + Segoe MDL2 Assets for standard UI chrome
- **TreeView rewrite**: Replaced ~180 lines of MDL2 DataTriggers with 2 converter bindings (`TypeToGeoConverter` + `TypeToColorConverter`)
- **ToolRegistry updated**: All 33 tools reference `Geo.Tool.*` geometry keys with `FrozenDictionary` lookups
- **Documented conventions**: Comprehensive header in IconGeometries.xaml describing naming pattern and extension procedure

#### i18n
- 3,457 keys (EN/FR parity confirmed) - +111 keys (tooltips, A11y, ComboBox content, empty states, palette modes, scan progress, disclosure, etc.)

#### Tests
- **1,586 tests** (1,196 Core + 283 SSH + 107 App), all passing

---

## [v2026.032506] - 2026-03-25

### Notes audit fixes, template i18n, Tools panel UX

#### Notes tool - bug fixes from multi-model audit (Codex + Gemini)
- **P1 - Milkdown fallback**: `TryInitializeMilkdownAsync` now checks `MilkdownEditorControl.IsHostInitialized` after `InitializeAsync()` - machines without WebView2 runtime correctly fall back to AvalonEdit instead of showing a non-functional Milkdown host
- **P1 - camelCase settings mismatch**: `CreateStorageService()` and `LoadSidebarWidth()` now read camelCase property names (`notesDirectory`, `notesSidebarWidth`) matching `ConfigManager`'s `JsonNamingPolicy.CamelCase` serialization - configurable `NotesDirectory` path was silently ignored after any settings round-trip
- **P2 - Sidebar width persistence race**: replaced ad-hoc `settings.json` direct write with `ConfigManager.MergeSettingAsync()` - prevents concurrent TOFU host key writes or other settings updates from being silently overwritten
- **P2 - Wiki-link accent regression**: `Slugify()` now strips diacritics via Unicode normalization (`FormD` decomposition + `NonSpacingMark` removal) - `Procédure` slugifies to `procedure`, and `FindNotePathAsync()` uses accent-insensitive title fallback so `[[Procedure]]` resolves `# Procédure`
- **Sync save in CanClose/Dispose**: new `NotesStorageService.SaveNote()` synchronous method avoids `.GetAwaiter().GetResult()` sync-over-async pattern
- **`_pendingReadOnly` nullable**: `MilkdownEditorControl` uses `bool?` to correctly handle `SetReadOnly(false)` before editor ready

#### Notes tool - Zero Hardcoding compliance
- **Template factory i18n**: all 26 hardcoded template strings extracted to locale files (`ToolNotesTpl*` keys) - `NotesTemplateFactory.Create()` accepts optional `LocalizationManager` parameter, propagated from view → storage → factory
- **French translations**: templates fully localized (Objectifs, Chronologie, Résumé, Étapes, Retour arrière, etc.)

#### Tools panel UX refonte
- **Removed redundant header**: deleted the "Tools ▾" panel header and its close button - the toggle button at the bottom is the sole open/close control
- **Chevron state indicator**: toggle button shows `▲` when panel is closed, `▼` when open
- **Category headers with colored accent**: each category section now displays a 3px colored bar (Network=blue, Security=amber, Encoding=purple, System=teal) with uppercase label in matching color
- **Alphabetical sort**: tools within each category sorted alphabetically by localized name

#### Infrastructure
- `ConfigManager.MergeSettingAsync(Action<AppSettings>)`: atomic load-mutate-save under write lock for targeted property updates
- `App.Services` public accessor for DI service resolution from tool views
- `NotesTemplateFactory.RemoveDiacritics()`: reusable Unicode diacritics stripping

#### i18n
- 3,346 keys (EN/FR parity confirmed) - +48 keys (26 template sections + 22 existing updates)

#### Tests
- **1,586 tests** (1,196 Core + 283 SSH + 107 App), all passing - +10 new (3 sync save, 3 diacritics, 2 accent-insensitive wiki-link, 2 template i18n)

---

## [v2026.032505] - 2026-03-25

### Notes tool enhancements and swap panes fix

#### Notes: sidebar toggle, context menu, Dracula theme
- **Sidebar toggle**: collapsible TreeView panel via hamburger button in header bar - saves/restores width across toggles
- **Editor right-click context menu**: 17 Markdown formatting actions (Bold, Italic, Strikethrough, Inline Code, Code Block, Link, Image, Note Link, Headings 1–3, Bullet/Numbered/Task List, Blockquote, Table, Horizontal Rule) - works in both Milkdown (JS) and AvalonEdit (WPF) editors with localized labels (EN/FR)
- **Dracula theme**: full Dracula palette for Milkdown dark mode (#282a36 bg, #f8f8f2 fg, #bd93f9 purple accents, #8be9fd cyan links, #ff79c6 pink inline code) via native Crepe `--crepe-*` CSS tokens (removed legacy `@milkdown/theme-nord` import). AvalonEdit syntax highlighting colors updated to match

#### Fix: swap panes freeze
- **Async two-phase handoff**: `SwapSplitPanesAsync` detaches host controls, awaits visual tree stabilization (`AwaitVisualTreeAsync` at Loaded + ContextIdle priority), swaps model references, awaits again, then restores controls - prevents UIElement single-parent race between old and new `SessionPaneControl` instances
- **`SessionPaneControl` lifecycle guards**: `SyncContent()` and `UpdateOverlays()` gated by `IsLoaded`; `HostPresenter.Content` cleared in both `OnUnloaded` and `OnDataContextChanged`; PropertyChanged subscription only while loaded - prevents disconnected controls from stealing WebView2/ActiveX children

---

## [v2026.032404] - 2026-03-24

### Notes tool - Obsidian-style Markdown editor with Milkdown

#### New tool: Notes (#34 NOTES)
- **Milkdown WYSIWYG editor** via WebView2 (ProseMirror-based, MIT licensed) with AvalonEdit + syntax highlighting fallback
- **TreeView file explorer** mirroring filesystem hierarchy with folder icons, drag-and-drop between folders, and folder creation
- **4 templates**: Blank, Daily, Incident, Procedure - with contextual server metadata pre-fill
- **`[[wiki-link]]` support**: click navigation, back/forward history, auto-completion popup on `[[` keystroke
- **Tag filtering**: `> tags: infra, prod` metadata line, dynamic filter buttons
- **Export**: Confluence Storage Format XML (copy/export), HTML standalone
- **Drag-and-drop import** of external `.md` files
- **Context menu**: New/Daily/Incident/Procedure, New Folder, Rename, Duplicate, Delete, Open in Explorer
- **Autosave** with 850ms debounce, path traversal protection, atomic writes
- **Configurable storage path** via `NotesDirectory` in settings.json

#### Integration
- Server right-click → "Notes" submenu with all templates (pre-filled ToolContext)
- Command Palette: `Ctrl+K → notes`
- Dedicated `Geo.Tool.Notes` icon

#### Infrastructure
- New `Heimdall.App.Tests` project: **97 tests** (SimpleMarkdownConverter, ConfluenceStorageConverter, NotesTemplateFactory, NotesStorageService)
- Session tab context menu exclusion for tool TreeViews (prevents Split/Merge menu from intercepting tool-owned context menus)
- `WebView2Loader.dll` copied to bin root for `dotnet run` compatibility
- `PlaceholderRegex` fix: removed `^$` anchors that prevented inline placeholder restoration

#### i18n
- 3,298 keys (EN/FR parity confirmed)

#### Tests
- **1,576 tests** (1,196 Core + 283 SSH + 97 App), all passing

## [v2026.032403] - 2026-03-24

### Split/Merge audit - 7 fixes (bugs, robustness, cleanup dedup)

#### Bug fixes
- **CancellationTokenSource leak**: `CancelSession` now disposes the CTS after a 5-second delay (deferred dispose) - previously cancelled but never disposed, leaking one CTS per tab close
- **GridSplitter cursor**: cursor now updates dynamically (`SizeNS` for Horizontal, `SizeWE` for Vertical) in `ApplyLayout()` - previously hardcoded `SizeWE` regardless of orientation
- **Reconnect self-referential LayoutMemory**: `ReconnectPaneAsync` no longer calls `LayoutMemory.Record` (was recording the same server as both primary and secondary, polluting palette suggestions)
- **MergeExistingSession HostControl check**: now checks all source tree leaves via `EnumerateLeaves().Any(p => p.HostControl is not null)` instead of the primary shim - split tabs with a disconnected primary were incorrectly blocked from merging

#### Robustness
- **CancellationToken propagation**: `ConnectByProtocolAsync` now passes `ct` to all `ConnectionService.Connect*Async` protocol handlers - closing a tab during a slow tunnel or SSH handshake now actually cancels the connection attempt
- **Merge blocked feedback**: `MergeExistingSession` now shows a status bar message (`SplitMergeBlockedByTool`) when a busy tool pane prevents the merge - previously returned silently with no user feedback

#### Cleanup deduplication
- **`CloseAllPanes` extracted to SplitService**: centralized tab teardown (CanClose gate, cancellation, disconnect history, tunnel release, state reset, disposal) - `ConnectionViewModel.CloseSessionInternal` now delegates entirely to `SplitService.CloseAllPanes`, eliminating 30 lines of duplicated cleanup logic
- **ConnectionViewModel slimmed**: removed 3 unused DI dependencies (`ConnectionStateMachine`, `TunnelManager`, `ConfigManager`) and their imports after cleanup extraction

#### i18n
- Added `SplitMergeBlockedByTool` key (EN/FR)

#### Tests
- **1,479 tests** (1,196 Core + 283 SSH), all passing

## [v2026.032402] - 2026-03-24

### SplitService extraction + race condition fixes

#### Architecture
- **SplitService extracted**: All split/merge orchestration (`SplitSessionWithServerAsync`, `SplitSessionWithTool`, `MergeExistingSession`, `ClosePane`, `ReconnectPaneAsync`, `SwapSplitPanes`, `ToggleSplitOrientation`) moved from `MainViewModel` to dedicated `SplitService` singleton (~500 lines extracted, ~350 lines removed from MainViewModel)
- **Unified protocol dispatch**: `ConnectByProtocolAsync` helper deduplicates the 8-protocol switch statement that was duplicated between split and reconnect flows
- **Callback wiring pattern**: `SplitService` uses the same callback property injection as `EmbeddedSessionManager` for access to `ActiveSessions`, `ActiveSession`, `HasActiveSessions`, and `StatusText`
- **DI registration**: `SplitService` registered as singleton in `App.xaml.cs`, injected into both `MainViewModel` and `ConnectionViewModel`

#### Race condition fixes
- **Per-session CancellationToken**: `RegisterSession`/`CancelSession` lifecycle on `SplitService` creates per-session `CancellationTokenSource`. Async split/reconnect methods check cancellation between config load and connection, and in post-await guards. `CloseSessionInternal` calls `CancelSession` before pane cleanup to abort in-flight operations
- **Deferred state machine cleanup in ReconnectPaneAsync**: Old tunnel reference and state machine entry are now released AFTER the new connection succeeds or definitively fails (via `ReleaseOldConnectionState` helper). Previously, old state was reset before reconnection, causing state loss on reconnect failure
- **Fixed disposal order**: `ClosePane` and `CloseSessionInternal` now detach HostControl from visual tree (set null) BEFORE removing from tree and disposing. Prevents RDP/ActiveX airspace issues during disposal
- **OriginalServerId set at pane creation**: `SplitSessionWithServerAsync` now sets `OriginalServerId` on the new pane immediately (was empty until post-connection finalization). Enables proper disconnect history and tunnel cleanup if pane is closed during async connection
- **MergeExistingSession CanClose check**: Now verifies `IToolView.CanClose()` on all source tree tool panes before merging. A busy tool (e.g., scan in progress) blocks the merge
- **SafeDispose enhanced**: Now logs unexpected exceptions (non-`ObjectDisposedException`) via `FileLogger.Warn` instead of silently swallowing them

#### UX improvements
- **Minimum pane size**: `SplitContainerControl` content presenters now enforce `MinWidth="120" MinHeight="80"` to prevent splitter from collapsing panes to unusable size
- **Double-click splitter reset**: Double-clicking the `GridSplitter` resets split ratio to 50/50 (`SplitContainerModel.DefaultRatio`)
- **NaN/Infinity guard**: `OnSplitterDragCompleted` now guards against `NaN`/`Infinity` ratios from collapsed panes (falls back to `DefaultRatio`)
- **Hover border on panes**: `SessionPaneControl` now shows a subtle 1px border on `IsMouseOver` (in addition to the existing 2px accent border on `IsKeyboardFocusWithin`) for better active pane feedback in split views
- **Splitter cursor**: `Cursor="SizeWE"` set on `GridSplitter` for visual feedback

#### Code quality
- **NotifyTreeDependentProperties**: Shared method replaces duplicated 12-line `OnPropertyChanged` blocks in both `OnRootContentChanged` and `NotifyShimPropertiesChanged` (DRY)
- **_emptyPane per-instance**: Changed from `static readonly` to instance field - prevents cross-session state leakage if fallback pane properties are modified
- **CTS lifecycle**: `CancelSession` no longer immediately disposes the CTS (just cancels). In-flight operations holding token references remain valid for guard checks
- **Diagnostic logging**: Added `FileLogger` calls at all guard points: pane not found, max panes reached, session cancelled, orphaned pane cleanup, double-close detection, tool CanClose blocked, reconnect skip (already in progress)

#### Schema versioning
- **SplitLayoutMemory**: `config/split-layouts.json` now uses versioned format `{ "version": 1, "entries": [...] }`. Load is backward-compatible with legacy bare-array format (auto-migrates on next save)

#### Tests
- **1,479 tests** (1,196 Core + 283 SSH), all passing - zero regressions from refactoring

## [v2026.032403] - 2026-03-24

### Symmetric split/merge between sessions and tools

#### New features
- **Mixed session + tool splits**: sessions and built-in tools can now be freely split and merged in any combination (e.g., SSH terminal left + Network Cartography right)
- **`SplitSessionWithTool`**: new method docks a built-in tool directly into a split pane without requiring a network connection - tool creation is synchronous, no loading overlay needed
- **Command Palette split mode**: tool tabs now appear as merge candidates alongside sessions; selecting a tool from search results in split mode docks it as a pane
- **Context menu merge**: "Merge with..." submenu now lists both sessions and tool tabs

#### Cleanup hardening
- **Per-pane cleanup in `CloseSessionInternal`**: refactored from early-exit tool check to per-pane handling in the recursive leaf loop - mixed splits (session + tool in same tab) now clean up correctly: tool panes respect `CanClose()` and skip state machine/tunnel teardown, while connection panes get full disconnect/tunnel/state-machine cleanup
- **`ClosePane` tool awareness**: closing a tool pane in a split tree now checks `IToolView.CanClose()` (e.g., blocks close during active scan) and skips state machine/tunnel operations
- **Busy tool blocks tab close**: if any tool pane in a split tree has `CanClose() == false`, the entire tab close is blocked (consistent with standalone tool tab behavior)

#### Routing
- `ExecutePaletteSelection`: added `tool-*` branch before generic server split path
- `ConnectFromPaletteAsync`: added `tool-*` branch in split mode routing
- `ConnectSplitFromPaletteAsync`: tools now split into active session pane instead of opening a new tab

## [v2026.032402] - 2026-03-24

### Split/Merge system hardening

#### Bug fixes
- **`ReplacePane` short-circuit**: extracted `ReplacePaneRecursive` with `bool` return - stops traversing after first match instead of processing both children
- **`RemovePane` null subtree**: when recursive removal empties a subtree, promotes the sibling instead of assigning `null` to `First`/`Second` (prevented potential `NullReferenceException`)
- **`ReplaceContainer` short-circuit**: converted from `void` to `bool` return for early exit after match
- **`MergeExistingSession` lookup**: added `OriginalServerId` fallback - context menu and palette merge no longer silently fail if `ServerId` is empty during connection
- **`OnSplitterDragCompleted` orientation guard**: explicit `SplitOrientation.Vertical` check prevents fallthrough to column calculation when horizontal grid is misconfigured

#### Memory leak fixes
- **`SessionPaneControl`**: added `Unloaded` handler - detaches `PropertyChanged`, `Button.Click`, `DataContextChanged`, `Loaded` subscriptions
- **`SplitContainerControl`**: added `Unloaded` handler - detaches `PropertyChanged`, `DragCompleted`, `DataContextChanged`, `Loaded` subscriptions

#### Thread-safety & I/O hardening
- **`SplitLayoutMemory`**: all public methods (`Record`, `FindPartner`, `FindAllPartners`) synchronized via `lock`; constructor `Load()` also under lock
- **Atomic save**: unique temp file per write (`Guid`-suffixed) with `finally` cleanup on failure - prevents corruption on concurrent writes or crash

#### Zero-hardcoding cleanup
- `SessionPaneControl.xaml`: replaced `Background="#B0000000"` → `{DynamicResource OverlayBackground}`, `FontSize="28"` → `{StaticResource FontSizeHeadline}`, `Foreground="#AAAAAA"/"White"` → theme brushes, removed English `FallbackValue`
- `SessionPaneControl.xaml.cs`: `"Disconnected"`/`"Error"` magic strings → `nameof(ConnectionState.Disconnected)`/`.Error`
- `SessionPaneModel.cs`: default `_status` changed from `"Connecting"` to `""` (set by caller via i18n)
- `SplitContainerModel.cs`: named constants `MinRatio` (0.1), `MaxRatio` (0.9), `DefaultRatio` (0.5), `SplitterThickness` (4)
- `SplitContainerControl.xaml.cs`: all magic numbers replaced with model constants; removed redundant `SetRowSpan/SetColumnSpan(1)` calls
- `SplitLayoutMemory.cs`: extracted `FileName` constant

#### Model improvements
- **`SplitRatio` auto-clamping**: `OnSplitRatioChanged` partial method clamps to `[MinRatio, MaxRatio]` - view no longer double-clamps
- **Merge ratio restoration**: `MergeExistingSession` consults `SplitLayoutMemory` for prior ratio when merging a previously-paired server pair
- **`SyncContent` optimization**: `ReferenceEquals` check prevents unnecessary `ContentPresenter.Content` reassignment

#### Menu restructure
- **"Split..." submenu**: replaced two top-level items with nested submenu (Split... → Horizontal | Vertical), matching "Merge with..." pattern
- **Palette split mode**: shows ALL servers from inventory (previously limited to 10 recent)
- New i18n keys: `SplitMenu`, `OrientationHorizontal`, `OrientationVertical` (EN + FR)

#### Accessibility
- `GridSplitter`: added `AutomationProperties.Name="Split pane resizer"`
- Disconnect icon: added `AutomationProperties.Name="Disconnected"`
- Overlay buttons: added `AutomationProperties.Name` for Reconnect/Close

#### Tests
- 5 new unit tests: deep `ReplacePane` (3+ levels), non-existent pane, short-circuit verification, deep `RemovePane` subtree promotion, `SplitRatio` clamping
- Total: **1,469 tests** (1,186 Core + 283 SSH), all passing

## [v2026.032401] - 2026-03-24

### Recursive N-Pane Split System

#### Architecture overhaul
- **Recursive split tree**: replaced flat `Secondary*` properties with binary tree model (`ISplitContent` → `SessionPaneModel` | `SplitContainerModel`)
- Up to **8 panes per tab** in any layout: 2x2, L-shape, 3 side-by-side, deeply nested splits
- All operations addressed by `PaneId` (GUID) - split, merge, swap, close, reconnect, detach
- WPF rendering via implicit `DataTemplate` resolution: `SessionPaneControl` (leaf) + `SplitContainerControl` (recursive container with `GridSplitter`)
- `SplitTreeHelper` static utilities: `EnumerateLeaves`, `FindPane`, `FindParent`, `FindSibling`, `RemovePane`, `ReplacePane`, `CountLeaves`, `FirstLeaf`
- 37 new unit tests for tree operations

#### New split features
- **Swap panes**: right-click → "Swap Panes" exchanges primary and secondary content
- **Toggle orientation**: Ctrl+Shift+O switches split between horizontal and vertical
- **Detach any pane**: extract any individual pane from a split tree into a floating window
- **Drag-to-split**: drag a tab onto the content area of another tab to merge (works on already-split targets for 3+ panes, orientation auto-detected from drop position)
- **Per-pane loading overlay**: spinner shown during connection with server title and status
- **Per-pane disconnect overlay**: Reconnect and Close buttons when a pane disconnects
- **Splitter ratio memory**: each pane's splitter position preserved across tab switches
- **Split layout persistence**: `SplitLayoutMemory` records server pairs in `config/split-layouts.json`, boosts previously paired servers in Command Palette

#### Context menu improvements
- "Merge with..." uses nested submenu per session (Session Name → Horizontal | Vertical)
- Split actions (Swap, Toggle Orientation, Close Secondary, Detach Secondary) shown when split is active
- "Detach Secondary" disabled while pane is still connecting

#### Safety and cleanup
- Post-await guard: `!Connection.ActiveSessions.Contains(session)` prevents orphaned connections when tab is closed during async split
- `CleanupOrphanedSecondary()` exposed for code-behind to clean up state machine/tunnel entries
- Close confirmation checks all panes in the tree (not just primary)
- State machine reset and tunnel reference release in `ClosePane` for each individual pane
- MergeExistingSession preserves state machine entries (connections are alive, just reparented)
- Anti-double-reconnect guard via `HostControl is null` check
- Layout coalescing: `_layoutDirty` flag prevents redundant grid rebuilds

#### Backward compatibility
- `SessionTabViewModel` exposes shim properties (`ServerId`, `Title`, `Status`, `HostControl`, `IsSplit`, `SplitOrientation`, `SplitRatio`, `Secondary*`) delegating to tree leaves
- `NotifyShimPropertiesChanged()` for in-place tree mutations (swap)
- Legacy `CloseSecondaryPane` and `ReconnectSecondaryAsync` relay commands preserved

#### Files added
- `Heimdall.Core/Models/ISplitContent.cs`, `SessionPaneModel.cs`, `SplitContainerModel.cs`, `SplitTreeHelper.cs`
- `Heimdall.App/Views/SessionPaneControl.xaml/.cs`, `SplitContainerControl.xaml/.cs`
- `Heimdall.Core/Configuration/SplitLayoutMemory.cs`
- `Heimdall.Core.Tests/SplitTreeHelperTests.cs`

#### Files removed
- `Heimdall.App/Views/SplitPaneHost.xaml/.cs` (replaced by `SessionPaneControl` + `SplitContainerControl`)

## [v2026.032312] - 2026-03-23

### Network Cartography - Deep Fingerprinting Engine

#### OS fingerprinting overhaul
- **Port-based OS inference**: RDP/WinRM → Windows, SSH-only → Linux, Kerberos+LDAP → Windows Server
- **SNMP sysDescr OS detection**: 19 patterns (VMware ESXi, Cisco IOS, Ubuntu, Debian, Red Hat, Windows, FreeBSD, etc.)
- **NTLM OS build mapping**: Extracts exact Windows version from SMB2 NTLM challenge (e.g., "Windows Server 2022 Build 20348")
- **MergeAll()**: Combines 5 sources (TTL, banner, ports, SNMP, NTLM) with multi-source confidence boosting

#### New probe modules
- **NtlmProbe**: SMB2 Negotiate + NTLMSSP Type 1/2 exchange - extracts hostname, domain, DNS forest, OS build, SMB dialect, signing policy, server GUID, uptime without credentials
- **SshFingerprinter**: HASSH fingerprint (MD5 of KEX_INIT algorithm lists) - identifies SSH implementation precisely
- **FaviconHasher**: Shodan-compatible MurmurHash3 favicon fingerprinting with 30+ known device hashes (FortiGate, VMware ESXi, Synology, Grafana, Jenkins, Freebox, TP-Link, Hikvision...)
- **HttpFingerprinter**: Cookie detection (12 frameworks), error page regex (7 patterns), product URL probing (13 vendor-specific paths: Hikvision, Synology, QNAP, MikroTik, FortiGate, ESXi...)
- **IanaPenDatabase**: SNMP sysObjectID → vendor decode via 50+ IANA Private Enterprise Numbers

#### Role classification improvements
- 4 new role definitions: LDAP Directory, Syslog Server (TLS/6514), HTTP Proxy (3128), Windows Server
- 6 conflict resolution rules: LDAP suppresses SSH, Windows Server suppresses generic RDP, AD suppresses partial roles
- Removed 3 dead UDP-only role definitions (Syslog/514, DHCP/67, UPnP/1900) unreachable via TCP scan
- Manufacturer-based role inference: Arlo → IP Camera, Verisure → Alarm System, Hikvision/Dahua → IP Camera
- Randomized MAC detection → "Smartphone/Tablet" role for devices with privacy MAC
- Certificate enrichment: issuer O=/OU= parsing, self-signed + 10yr validity → appliance default cert detection
- Chromecast confidence raised (70 base) to outrank generic "Web Server (HTTPS-Alt)"

#### SNMP enhancements
- 3 additional OIDs: sysObjectID (vendor/model), sysUpTime (uptime), sysServices (OSI layer bitmask)
- ASN.1 OID and TimeTicks decoders for response parsing
- NetBIOS parser bounds hardening: qdCount cap, strict offset validation

#### UPnP / SSDP deep discovery
- Fetch rootDesc.xml from SSDP LOCATION URL
- Parse friendlyName, manufacturer, modelName, modelNumber, serialNumber, presentationURL
- SsdpInfo extended with 3 new optional fields

#### OUI database expansion
- Added: Hikvision (BCBAC2, 4CF5DC, 54C4A5, C4A36E), Free/Freebox (DC00B0), Arlo Technologies (B8060D, 9C7B6B), Securitas Direct/Verisure (0023C1), Samsung (58B568)
- Locally administered MAC detection → "Private (Randomized MAC)" for smartphone/tablet identification

#### Knowledge base & scan engine
- KB persistence fixed: removed SecureFileWriter double-write that could corrupt the file
- AreUdpProbesFresh: null observations use LastSeen as proxy instead of being treated as "fresh"
- ARP table refresh post-scan (ping+TCP populates ARP cache during scan)
- Manufacturer re-resolution post-scan when MAC exists but OUI was previously unresolved
- KB backfill: null OS/hostname fields populated from prior scan observations
- IP probe order randomization (Fisher-Yates shuffle) to reduce IDS triggering

#### UX improvements
- Progress bar shows IsIndeterminate animation immediately on scan start
- ProgressPanel stays visible after scan when status message is displayed (0-hosts warning no longer vanishes)
- "No hosts responded" message with Skip Ping / gateway suggestion
- Gateway tunnel scan: batched port probes (single SSH command per host instead of per-port, ~24x faster)
- Cross-thread fix: UI checkbox state captured before ConfigureAwait(false)

#### VlanDetector
- Dynamic subnet grouping from scan profile CIDR instead of hardcoded /24
- Proper uint mask computation for edge cases (prefix ≥ 32)

#### CSV export
- 6 new columns: SNMP_ObjectID, NTLM_DNS, NTLM_Domain, NTLM_Build, SSH_HASSH, Favicon_Hash (27 total)
- SSDP column enriched with FriendlyName/Manufacturer/Model/Server

#### Tooltip enrichment
- SMB: dialect version, signing policy, server GUID, calculated uptime
- NTLM: DNS computer/domain/forest, OS build
- SSH: HASSH fingerprint
- Favicon: hash value + known device name lookup
- HTTP: detected framework + product identification
- UPnP: friendlyName, manufacturer, model, model number, serial number

## [v2026.032309] - 2026-03-23

### Split & Merge Sessions + Airspace Fix + RDP Improvements

#### Session merge (new feature)
- Right-click tab → **"Merge with..."** submenu lists all active sessions with horizontal/vertical orientation
- Merges the selected session into the current tab's split pane without reconnecting - the live connection is reparented instantly
- Unsplit restores the merged session as an independent tab
- Split palette also shows active sessions at the top for merge via keyboard (Enter)

#### Airspace fix (Command Palette over RDP/VNC)
- Command Palette converted from WPF Grid overlay to `Popup` (own HWND) - renders above WindowsFormsHost/ActiveX surfaces
- Win32 focus forced via `SetForegroundWindow`/`SetActiveWindow`/`SetFocus` P/Invoke on Popup open
- Keyboard navigation via `PreviewKeyDown` on Border parent (intercepted before TextBox consumes arrows)
- Click item resolved from `ListBoxItem.DataContext` via `PreviewMouseLeftButtonDown`

## [v2026.032304] - 2026-03-23

### Split Session Fix + RDP Improvements

#### Airspace fix (Command Palette over RDP/VNC)
- **Fix**: Command Palette (Ctrl+K) was invisible over RDP sessions due to WPF airspace issue - `WindowsFormsHost` HWND always rendered above WPF overlay content
- Replaced the `Grid` overlay with a WPF `Popup` that creates its own HWND, rendering above all Win32 surfaces
- Drop shadow and proper `PlacementTarget` for consistent positioning
- Deferred focus via `Dispatcher.BeginInvoke` (Popup content enters visual tree asynchronously)
- Dismiss on outside click via `PreviewMouseDown` on the main Window

#### Split session
- **Fix**: split session was silently failing because default RDP/SSH mode was "External" - embedded mode is now the default
- Force embedded mode for split pane connections (external mstsc.exe cannot be docked)
- Add missing VNC, FTP, Citrix protocol cases in split session switch

#### RDP ActiveX enhancements
- Auto-reconnect events (`LoginComplete`, `AutoReconnecting`, `AutoReconnected`) with bounded retry count and cancel support
- Disconnect reason decoder with localized messages (24 reason codes)
- UPN credential format support (`user@domain.com`)
- USB device redirection, bandwidth auto-detect, network connection type
- Performance flags and DisableUdp options in `.rdp` file generation
- Fix `AudioCaptureRedirectionMode` COM property type (int, not bool)
- Fix COM dispose - let AxHost handle RCW cleanup (prevents "COM object separated" errors)

#### Settings
- Default connection mode changed from "External" to "Embedded" for both RDP and SSH
- "Apply to all servers" button for bulk SSH/RDP mode switching

## [v2026.032303] - 2026-03-23

### Network Cartography - Knowledge Base + Security Hardening

#### Knowledge Base (persistent host data across scans)
- New `KnowledgeBaseManager` with per-field `Observation<T>` timestamps and source tracking
- Merge-on-scan: every scan enriches the persistent KB (`config/network-kb.json`)
- TTL-based cache acceleration: ping (4h), ports (24h), banners (7d), UDP probes (7d), certs (30d)
- `CacheHitProgress` event for real-time UI feedback during cache-accelerated scans
- KB stats in footer (host count + time-ago), Clear KB button with confirmation dialog
- Checkbox to enable/disable cache usage per scan; KB always enriched regardless
- `PurgeStaleHosts()` for automatic cleanup of old entries
- `ToScanResult()` round-trip conversion for cached data
- 28 unit tests covering merge, confidence, serialization round-trip, purge, TTL

#### Security hardening (audit-driven)
- Shell injection prevention: `IPAddress.TryParse()` + port range validation before SSH `/dev/tcp` and `host` commands (CWE-78)
- Process timeout: `WaitForExit(5000)` + `Kill()` on ARP table process (Windows + macOS)
- TLS callback documented as intentional (scanner inspecting certs, not trusting connections)
- Atomic writes: temp-file-then-rename for scan snapshots and KB persistence
- ACL enforcement: `SecureFileWriter.WriteAndProtect()` on scan history and KB files (Windows)
- Path traversal prevention: `Path.GetFileName()` + `..` rejection + `scan_` prefix whitelist in `LoadSnapshot()`
- Scan snapshot retention policy: max 20 files, oldest auto-deleted

#### Performance optimizations
- Compiled regex cache: `ServerHeaderRegex`, `TitleTagRegex`, 7 HTTP header regexes (static readonly + `RegexOptions.Compiled`)
- `RoleClassifier.CnRegex`: compiled static regex for X.500 CN extraction
- Concurrent collections: `ConcurrentBag<HostScanResult>`, `ConcurrentDictionary` for ping results (eliminates lock contention)
- Ping sweep respects `MaxConcurrency` (`Math.Min(64, profile.MaxConcurrency)`)
- `GetProbeStrategy()` called once per port (was called twice)
- Layout flush reduced from 3 to 2 in `EmbeddedRdpView.BeginConnect()`

#### RDP connection performance
- COM pre-warm: background STA thread creates/disposes throwaway `RdpActiveXHost` at app startup (~400ms saved on first connection)
- DNS pre-resolution: `Dns.GetHostEntryAsync()` fire-and-forget on server selection in tree view
- TCP keep-alive: `KeepAliveIntervalMs = 60_000` named constant via `AdvancedSettings9.KeepAliveInterval`
- Performance flags: per-server bitmask (wallpaper, themes, animations, drag, cursor shadow, composition) via `AdvancedSettings9.PerformanceFlags`
- Disable UDP: per-server TCP-only option via `TransportSettings3` (avoids UDP probe timeout behind firewalls)
- ServerDialog UI: new "Experience" expander with 7 checkboxes + bitmask recomposition on save

#### UI and i18n
- Scan error feedback: `ToolNetMapErrorScanFailed` key with error message in status bar
- 21 new i18n keys (KB UI, cache hit, RDP experience, scan errors) in EN + FR
- 7 `AutomationProperties.SetName()` on RDP experience checkboxes (accessibility)
- 13 `AutomationProperties.SetName()` on Network Cartography controls

#### Tests
- 93 new tests: KnowledgeBaseManager (28), VlanDetector (16), ScanHistoryManager (16), DrawIoExporter (10), RdpRedirectionOptions (20), CartographyEngine round-trip (3)
- Total: 1,417 xUnit tests (was 1,324)

---

## [v2026.032302] - 2026-03-23

### Local Shell Elevation - ElevationMode + AdminByRequest Compatibility

#### Elevation Mode (replaces checkbox)
- New `ElevationMode` enum: `None`, `Auto`, `Gsudo`, `Runas`
- `Auto` mode: tries gsudo with `--direct` flag first (bypasses ServiceHelper), falls back to external elevated window on failure
- `Gsudo` mode: gsudo only (embedded terminal, fails if gsudo is blocked)
- `Runas` mode: ShellExecute `runas` verb in external window (compatible with AdminByRequest, CyberArk, BeyondTrust)
- Server Dialog: checkbox replaced with "Elevation" dropdown ComboBox
- Backward compatible: existing `LocalShellElevated=true` maps to `Auto` via `EffectiveElevationMode`

#### gsudo + Endpoint Privilege Manager Fix
- Added `--direct` flag to all gsudo invocations (bypasses `ServiceHelper.StartService` crash caused by AdminByRequest invalidating process handles)
- Graceful fallback chain in `Auto` mode: gsudo `--direct` → external elevated window → clear error message
- UAC cancellation (Win32 error 1223) handled with user-friendly message
- External elevated sessions show info panel in tab ("Elevated shell launched in external window")

## [v2026.032301] - 2026-03-23

### Tools UX Harmonization & Network Cartography Remote Subnet Detection

#### Design System
- Add `PaddingButtonHelp`, `PaddingButtonCopy`, `PaddingButtonPrimary`, `PaddingButtonPreset`, `PaddingInput` tokens in CommonControls.xaml
- 181 hardcoded padding values replaced with design tokens across all 33 tool views
- All tools now use consistent tokenized spacing (global change via a single file)

#### Tool Views (33 tools) - Structural Harmonization
- Unified header Border: `Padding="12,8"`, no extra margin, across all 33 tools
- Unified title TextBlock `x:Name="HeaderTitle"` (was split between `HeaderTitle` and `TitleText`)
- Added `VerticalAlignment="Center"` on all title TextBlocks
- Apache 2.0 licence headers added to 17 XAML files that were missing them
- Copy button padding standardized to `PaddingButtonCopy` token

#### Watermark Localization (i18n)
- 24 watermark placeholder strings extracted from XAML `Tag` attributes into i18n locale files
- 17 code-behind files updated to set `Tag` via `L()` helper in `ApplyLocalization()`
- Full EN/FR translations for all watermark placeholders

#### Empty State Panels
- Added `ToolEmptyStateStyle` panels with Segoe MDL2 icons to 8 tool views: Whois, Cert Inspector, Subnet Calculator, SSH Config Generator, Service Status, Cron Job Manager, Log Viewer, Regex Tester
- Panels shown before first action, hidden when results appear

#### Accessibility (a11y)
- `AutomationProperties.LiveSetting="Polite"` added to 15 tool result areas (was 5)
- Screen readers now notified of dynamic result updates across all major tools

#### Tools Panel (Sidebar)
- Category-based fallback icons (Segoe MDL2 glyphs) when tool vector/bitmap icon is missing
- Scroll-more indicator (chevron) at bottom of panel when content overflows

#### Tab Busy Indicator
- New `IsBusy` property on `SessionTabViewModel` with pulsing accent dot in tab header
- `SetBusyAction` callback in `ToolContext` for tools to signal long-running operations
- Wired on Ping, Port Scanner, Network Cartography (pulse visible during active scans)

#### Network Cartography - Remote Subnet Auto-Detection
- Selecting an SSH gateway in "Route via" now auto-detects remote subnets
- SSH connection to gateway, runs `ip -4 addr show` (Linux), `ifconfig` (Unix/macOS), `ipconfig` (Windows)
- Parses non-loopback IPv4 CIDRs, normalizes to network addresses, pre-fills TxtSubnet
- Multiple detected subnets accessible via tooltip on the subnet field
- Localized status messages (EN/FR) during detection

## [v2026.032210] - 2026-03-22

### Comprehensive UX Audit - WCAG AA, Design Tokens, Accessibility

#### Design System (40 tokens, WCAG AA compliant)
- Add `ContentAreaMargin`, `SessionHeaderPadding`, `ToolHeaderPadding`, `ToolFooterPadding` spacing tokens
- Add `FontFamilyMonospace` token for path boxes and code editors
- Add `FocusIndicatorBrush` (cyan on dark, blue on light) for keyboard focus on all button styles
- PrimaryButton foreground changed to `TextOnAccentBrush` (white on accent surfaces)
- 19 themed control styles with complete hover/pressed/focused/disabled states
- DataGrid column header, cell, and row styles now applied globally (fixes unthemed DataGrid in tools)

#### WCAG AA Contrast Fixes
- Dark theme: AccentColor adjusted for 4.53:1 contrast with white text (was 2.41:1)
- Dark theme: TextSecondary and TextDisabled colors lightened for better readability on card surfaces
- Light theme: AccentColor darkened for stronger contrast
- Light theme: TextDisabled darkened to 4.51:1 (was 2.88:1)
- Light theme: ProtocolSsh and ProtocolSftp brushes darkened to meet AA on white backgrounds

#### Tool Views (33 tools)
- Help button ("?") added to all 21 tools that were missing it (33/33 complete)
- Help keys follow UPPERCASE convention (e.g., `ToolHelpBASE64`)
- Hardcoded `Margin="16,0,16,16"` replaced with `ContentAreaMargin` token in 6 tools
- CrontabBuilder `Foreground="Red"` replaced with `ErrorTextBrush`
- DiagramEditor header padding unified to `12,8` (was `8,6`)

#### Views and Dialogs
- Unique protocol glyphs in TreeView: Local (`E770`), Telnet (`E968`), FTP (`E896`)
- `Background="Black"` replaced with theme-aware `BackgroundBrush` in RDP and Citrix views
- Session header strips use `SessionHeaderPadding` token (RDP, SSH, VNC, Citrix, SFTP)
- `FontFamilyMonospace` token applied to SFTP, LocalFileBrowser, and Editor path boxes
- Focus vs Selected states distinguished in ListView items (`FocusIndicatorBrush`)
- Status bar height increased from 28px to 36px
- Dialog buttons: `Width` changed to `MinWidth` across all dialogs (Gateway, Project, Pin, Server, Message)
- PinDialog buttons right-aligned (was centered)
- Hardcoded placeholder text removed (code-behind i18n binding)

#### App Icon
- Rebuilt from clean ARGB source (`icon-flat.png`) with proper transparency
- No more white haze/shadow on dark taskbar backgrounds

#### Documentation
- ARCHITECTURE.md: rewritten design system section with 40 tokens, WCAG AA, help system
- README.md: updated test count, tool count, design system description, i18n key count

## [v2026.032204] - 2026-03-22

### Network Cartography - Enhanced Device Detection
- OS fingerprinting via ICMP TTL analysis (Windows/Linux/Network Equipment) and banner pattern matching (33 patterns)
- NetBIOS NBSTAT probe (UDP 137): computer name, domain/workgroup, MAC address extraction
- SNMPv2c GET probe (UDP 161): sysDescr, sysName, sysLocation with raw ASN.1/BER encoding
- mDNS/Bonjour service discovery (multicast UDP 5353): 26 service types (AirPlay, HomeKit, Chromecast, printers, etc.)
- HTTP header deep analysis: Server, X-Powered-By, WWW-Authenticate, X-Frame-Options, HSTS extraction
- HTTPS header extraction: TLS handshake + HTTP GET over SSL for HTTPS-only endpoints (443/8443/9443)
- Expanded OUI database from 101 to 300+ manufacturer prefixes (IoT, enterprise, ISP routers, industrial/SCADA, mobile, media)
- Enhanced role classification (`ClassifyEnriched`): multi-source evidence from ports + banners + OS + NetBIOS + SNMP + mDNS + HTTP headers
- 20 new banner fingerprints (Shelly, Tasmota, Jenkins, GitLab, Portainer, etc.) and 4 new role definitions (UPS, CI/CD, GitLab, Container Registry)
- Ping latency capture (was hardcoded to 0)
- New DataGrid columns: OS, Details (compact NB/SNMP/mDNS summary)
- Row tooltip with full enrichment data on hover (localized labels)
- CSV export expanded to 20 columns with localized headers
- Draw.io export enriched with OS, NetBIOS name, SNMP sysName in node labels
- History diff detects OS, NetBIOS, and manufacturer changes (typed `HostChange` model)
- Enrichment progress display in status bar during NetBIOS/SNMP phase
- Cross-platform ARP table: Windows (`arp -a`), Linux (`/proc/net/arp`), macOS (`arp -a` with regex)
- Debug logging on UDP probe failures (NetBIOS, SNMP, mDNS)
- 92 new xUnit tests covering OsFingerprinter, UdpProbeEngine (including realistic NBSTAT payloads), RoleClassifierEnriched, OuiDatabase, CartographyEngine (TLS port classification, CIDR parsing, typed diff model)

## [v2026.032203] - 2026-03-22

### UX Audit (6 passes)
- Gateway diagram: Viewbox auto-scaling prevents truncation
- ServerDialog: tabs stay visible but disabled (not hidden), with tooltip explanation
- 33 tool icons: 4 category colors + per-tool glyphs replace uniform wrench
- Ctrl+K palette: protocol icons, status dots, endpoint hints
- VNC session parity: Split, Reconnect, overlay - fully wired in EmbeddedSessionManager
- Settings bar: WrapPanel, Save button separated from secondary actions
- SFTP: bookmark overflow menu, optimized column widths
- Broadcast button: icon + localized label replaces cryptic "B"
- Session loading overlay: semi-transparent with progress bar + status
- Empty states: DNS, PortScanner, NetworkCartography show guidance before first query
- Error text wrapping on all 10 tool error TextBlocks
- Merged duplicate search fields into single sidebar filter
- Project dialog: multi-line description, inline color name label
- MessageDialog DWM dark mode, removed 6 empty ToolTip flashes
- FloatingSessionWindow: connection status displayed

### Design System
- Typography tokens: FontSizeCaption/Body/Subtitle/Title/Headline
- Spacing tokens: SpacingXs/Sm/Md/Lg/Xl
- 506 hardcoded FontSize values migrated across 45 files
- Micro-animations: FadeInPanelStyle (150ms) on 4 overlays
- DataGrid: global Ctrl+C copy via ClipboardCopyMode
- TextBox IsReadOnly: triple visual signal (background + border + opacity)

### Accessibility
- 385+ AutomationProperties.SetName via code-behind
- Keyboard focus indicators on TreeView/ListBox items
- Disabled tab tooltips, BtnGoPath/PaletteInput labels
- Toolbar tooltips with keyboard shortcuts

### Developer
- IToolView.CanClose() default interface method
- ToolContextMenuHelper: CopyAll + ExportCSV for DataGrid tools
- Build.ps1: regex fix for suffixed folders, GitHub release collision check
- CI: nuget.org source for offline-first NuGet.Config

## [v2026.032012] - 2026-03-20

### Features
- 21 built-in sysops tools as session tabs (Ping, DNS, Cert Inspector, Port Scanner, Subnet Calculator, IP Converter, Password Generator, SSH Key Generator, Hash, HMAC, Base64, URL Encoder, JWT Parser, Chmod Calculator, Crontab Builder, JSON Formatter, Regex Tester, Text Diff, DateTime Converter, UUID Generator, HTTP Status Codes)
- Tools accessible via Ctrl+K palette, "+" menu, right-click context menu, and TreeView double-click
- Enhanced Password Generator: 3 modes (Random/Syllable/Passphrase), 7 case options, 6 presets, CLI-safe mode, custom specials, exclude ambiguous, NATO phonetic, AZERTY/QWERTY layout, 5-level strength with mode-aware issues
- Wordlists expanded to 525 EN / 513 FR words with validation

### Security
- Unbiased random generation (modulo bias eliminated)
- CLI-safe fallback bypass fixed
- XXE protection on all XML importers
- Citrix command injection validation
- Password file TOCTOU eliminated

### UX
- Tool tabs integrate with TreeView (icons, double-click, edit, context menu)
- Detail panel shows "Open" for tools, hides connection info
- Copy feedback "✓" on all tool copy buttons
- Input validation with error messages on network tools
- Large payload protection (JSON/Base64 5MB, Regex 500 cap)
- AutomationProperties localized on all controls

### Architecture
- ToolContext record, CreateToolControl factory, TOOL:* ConnectionType prefix
- Tool type list shared constant, no duplication
- Preset suspension flag prevents multi-regeneration

## [v2026.032002] - 2026-03-20

### Security
- Remove password file TOCTOU fallback (fail hard if SecureFileWriter fails)
- Add Unix file mode 0600 on Plink password files
- Add XXE protection (DtdProcessing.Prohibit) on all XML importers
- Validate CitrixLaunchCommandLine against shell metacharacters
- Wrap async void event handlers with try-catch

### Performance
- Reduce Task.Wait() timeouts from 2-3s to 500ms (4-5x faster session close)
- Parallelize health monitor SSH commands via Task.WhenAll (3x faster)
- Increase health poll interval from 5s to 15s (66% less SSH traffic)
- Cache FolderViewModel.ServerCount with auto-invalidation

### Architecture
- Split ApplyLocalization() into 7 sub-methods
- Extract ImportConfigAsync() into 6 format-specific helpers
- Eliminate CloseAllSessions() code duplication
- Extract CredentialTarget record for credential resolution
- Replace all Debug.WriteLine with FileLogger (77 occurrences)
- Consolidate duplicate DefaultPorts constants
- Extract WebView2 message protocol constants
- Convert async void OpenFile() to async Task

### Tests
- Add 508 tests across 20 new test files (505 to 1013 total)
- Cover: CredentialProtector, DpapiProvider, SecureFileWriter, AclEnforcer
- Cover: RdcManImporter, MRemoteNgImporter, RdpFileImporter, SchemaValidator
- Cover: TunnelManager, RdpFileGenerator, AspectRatioManager
- Cover: LocalizationManager, FileLogger, ConnectionHistory, CommandCredentialProvider

## [v2026.032001] - 2026-03-20

### UX
- 117 fixes across 5 audit passes
- Add 47 i18n keys (2086 EN/FR in perfect parity)
- Add AutomationProperties.Name on all interactive controls (20+)
- Add keyboard focus indicators on PrimaryButtonStyle and SecondaryButtonStyle
- Add TextTrimming on all dynamic TextBlocks
- Add HorizontalScrollBarVisibility="Disabled" on form dialogs
- Localize MessageDialog, SSH status strings, filter placeholders
- Replace all Debug.WriteLine with FileLogger in App layer (31 occurrences)
- Add IsBusy on ImportConfigAsync
- Add CanExecute guards on SettingsViewModel commands
- WebView2 DefaultBackgroundColor now theme-aware

## [v2026.031917] - 2026-03-19

### Initial Release
- 8 protocol support: RDP, SSH, SFTP, VNC, Telnet, FTP, Citrix, Local Shell
- Embedded sessions via ActiveX (RDP), WebView2+xterm.js (SSH/Telnet), noVNC (VNC)
- DPAPI+HMAC credential encryption with external vault integration
- Pageant SSH agent via native Win32 IPC
- Multi-gateway SSH tunnel chaining with ref-counting
- SFTP browser with sudo elevation fallback
- Quick Connect (Ctrl+K), Network Scanner, Macro Recorder
- Dark/Light themes, bilingual EN/FR interface
- Import from MobaXterm, mRemoteNG, RDCMan, .rdp files
- Tab detach to floating windows, split pane sessions
- 505 xUnit tests
