<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Settings FAQ

*Also available in French: [fr/SETTINGS-FAQ.md](fr/SETTINGS-FAQ.md).*

Settings has 65 options. This page covers the ones people actually ask about: the ambiguous
ones, the ones with a real trade-off, and the one whose label is misleading. Options that do
what their name says, such as Theme or Font size, are not repeated here.

Where an answer says a setting does not do something, that is a measured or code-verified
statement, not a guess.

## Security

**Network Level Authentication (NLA)** - the remote machine authenticates you *before* opening
a desktop session. Leave it on. Turn it off only for targets that cannot do it, such as most
Linux `xrdp` servers, which do not implement CredSSP at all. With NLA off you will land on the
remote login screen instead of being logged in directly.

**Strict server authentication** - refuses to connect if the server's identity cannot be
verified. It is off by default because many internal RDP servers use self-signed certificates,
which cannot be verified by design. Turning it on is safer and will break connections to those
servers.

**Require Credential Guard** - refuses to open an *embedded* RDP session unless Windows
Credential Guard is running on **your** machine. It protects the credentials your machine
delegates to the remote server. It checks your local machine, not the target. External RDP
sessions are exempt.

**Require Windows Hello before connecting** - asks for your Windows Hello factor before a
connection starts. **Re-verify after** sets how long a successful check stays valid, so you are
not prompted on every tab.

## Credentials from an external vault

**Use external credential provider** - lets Heimdall fetch a password by running a command,
typically a password manager CLI such as KeePassXC, Bitwarden or 1Password.

**Username command (optional)** - this one is easy to miss and it answers a common complaint.
Without it, only the *password* comes from the vault and the username stays whatever the
profile says. Fill it in and the username is fetched too, by a second command.

**Unlock secret** - passed on the command's standard input, for vaults that need to be unlocked
first. Bitwarden and 1Password additionally need a session established outside Heimdall
(`BW_SESSION`, `op signin`); Heimdall does not establish those for you.

**Use only the first line of output** - some CLIs print the secret followed by other fields.
Leave this on unless your password legitimately contains a newline.

The vault entry is looked up by the profile's **Vault entry name** if you set one, and by the
profile's display name otherwise. Set it when the entry in your vault is not named exactly like
the entry in Heimdall.

## RDP and memory

**Bitmap caching - read this one.** The label is misleading and we are aware of it. The
checkbox maps to the RDP control's `BitmapPersistence` property, which decides whether the
bitmap cache is written **to disk** between sessions. **It does not control the in-memory
cache**, and no setting in Heimdall does. Turning it off frees no RAM and costs you the disk
cache that would otherwise spare redraws on reconnect. Leave it on.

**Color depth** - 32-bit by default. Lowering it to 16-bit saved no measurable memory in
testing. Lower it if you are short of bandwidth, not if you are short of memory.

**Resolution mode, Width, Height** - the only settings measured to change memory use. A smaller
session costs about 86 MB less than 1920x1080. `Auto` follows the Heimdall window, `Fixed` pins
the size you choose.

Full measurements are in [RDP memory and session tuning](RDP-PERFORMANCE.md).

**Max embedded sessions** - a ceiling on simultaneously embedded sessions. Raise it if you work
with many at once and have the memory; each one costs roughly 194 MB.

**Dynamic resolution** - lets the session resize with the window instead of reconnecting.
Leave it on unless a server misbehaves when the resolution changes mid-session.

**Multi-monitor** - spans the session across your monitors. It multiplies the session geometry,
so it multiplies the memory cost.

## Timeouts, and why there are so many

The advanced timeouts exist because different failures need different patience. You rarely need
to touch them.

**RDP connection watchdog timeout** - how long to wait for a connection before declaring it
failed. Raise it for slow or distant servers.

**Resolution stabilization delay after connect** - a pause before resizing is allowed, so a
session that is still negotiating its geometry is not immediately resized again.

**Credential autofill watcher timeout** - how long Heimdall watches for the credential prompt
of an *external* mstsc session in order to fill it. It does not affect embedded sessions.

**.rdp file and credential cleanup delay** - external mode writes a temporary `.rdp` file and a
credential; this is how long Heimdall waits before deleting them, so `mstsc.exe` has time to
read them.

**Session keep-alive interval** and **Anti-idle interval** are different things. Keep-alive is
protocol traffic that stops the *server* dropping an idle session. Anti-idle simulates activity
so the remote *desktop* does not lock.

## Background probes

**Enable background reachability probes** - Heimdall periodically opens a TCP connection to
each configured server to colour the dot in the session tree. This is why a server's logs show
one short connection per interval from your machine even when you are not connected. It is not
a connection attempt and it does not authenticate.

**Check interval**, **Probe timeout** and **Max concurrent probes** control how often, how long
and how many at a time. Turn the whole thing off if you manage many servers and the noise in
their logs matters more to you than the status dots.

## Modes

**Default RDP mode** - `Embedded` renders the session inside a Heimdall tab. `External` launches
`mstsc.exe` in its own window, with credentials filled in for you. External uses more memory per
session but isolates each session in its own process.

**Default SSH mode** - `Embedded` uses the built-in terminal. `External` uses PuTTY, which needs
**PuTTY path** set.

## Logging

**Enable logging** writes the application log. **Enable session logging** additionally records
the content of terminal sessions to **Session log directory**. The second one records what you
typed and what came back, so consider where that directory lives.

## Related

- [RDP memory and session tuning](RDP-PERFORMANCE.md)
- [User Guide](USER-GUIDE.md)
- [Troubleshooting](TROUBLESHOOTING.md)
