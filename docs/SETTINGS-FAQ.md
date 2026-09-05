<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Settings FAQ

*Also available in French: [fr/SETTINGS-FAQ.md](fr/SETTINGS-FAQ.md).*

Settings is a large screen, and some of its options are opaque unless you already know what they
refer to. This page covers those: the ambiguous ones, the ones carrying a real trade-off, the one
whose label is misleading, and the ones whose wording assumes knowledge the interface never gives
you. Options that do what their name says, such as Theme or Font size, are not repeated here.

Where an answer says a setting does not do something, that is a measured or code-verified
statement, not a guess.

## Locking Heimdall itself

Three controls, in the same screen, protecting three different things.

**Application PIN** is a screen lock. Heimdall stores a hash of your PIN and compares what you
type at startup. It stops someone who sits down at your unlocked machine from browsing your
server list. **It encrypts nothing.** Your saved passwords are protected by Windows DPAPI
whether or not you set a PIN, and someone holding your configuration file can strip the PIN out
of it.

**Master password** is encryption. What you type goes through Argon2id to derive a key, and that
key encrypts the credential store. Without it the stored secrets cannot be read, including by
something running under your own Windows account. Set this one if you want your credentials
protected at rest.

**Windows Hello unlock** replaces neither. It sits on top of the master password so you can
unlock with a fingerprint instead of typing it.

**Which do you actually want?** If the worry is a colleague using your unattended machine, the
PIN is enough. If the worry is the credential file itself, only the master password answers it.
The two stack, and setting a PIN when you meant the second gives you much less than it appears.

**Generate Recovery File** writes a `.heimdall-recovery` file that can reset a forgotten PIN.
One caveat the dialog does not spell out: the file is encrypted for your Windows account on this
machine, so it cannot be used from another machine or another account. It rescues a forgotten
PIN, not a lost computer.

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

**Hardware-accelerated rendering** - **off by default since v2026.082401, and the setting that
matters most.** With it on, the RDP control builds a Direct3D device and a decoding context for
every session you open. Three concurrent sessions at 1920x1080 measured 1146 MB with it on and
763 MB with it off, a third of the footprint and 840 fewer Windows handles. The trade is that
decoding moves to the processor: no difference was measurable on idle desktops or scrolling text,
and a session showing video was not measured. Switch it back on, globally or for one server, if a
session feels less smooth.

**Keep bitmap cache on disk** - this checkbox used to be called "Bitmap caching", which was
misleading, so it was renamed. It decides whether the bitmap cache is written **to disk** between
sessions, so a reconnection can reuse it instead of redrawing. **It does not control the in-memory
cache.** Turning it off frees no memory and costs you the disk cache. Leave it on.

**Color depth** - 32-bit by default. Lowering it to 16-bit saved no measurable memory in
testing. Lower it if you are short of bandwidth, not if you are short of memory. The accepted
values are 16, 24 and 32: those are the depths the Remote Desktop control and the `.rdp` format
know, and a lower depth in an imported file (a 256-colour or 15-bit mRemoteNG profile, a
`session bpp` below 16) is brought to 16, which is what the session was given before the bound
said so.

**Resolution mode, Width, Height** - the other settings measured to change memory use. A smaller
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
read them. If Heimdall exits before the delay elapses, both are released at exit; after a
crash, they are reclaimed at the next startup or the next external launch.

**Idle Remote Desktop controls kept for reuse** and **Idle control expiry** - when an embedded
RDP tab closes, its control is kept alive so the next connection reuses it instead of paying
about 66 kernel handles for a new one; each idle control holds about 300 MB. The first setting
says how many are kept (0 to 8, 0 creates a control per session), the second how many minutes
an idle one is kept before its memory is released (0 keeps it until Heimdall exits). Both apply
without a restart. See [RDP-PERFORMANCE.md](RDP-PERFORMANCE.md).

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

## Legacy migration

**What "legacy" means here** - Heimdall replaced an earlier PowerShell tool called
**RDPManager**. This section concerns only people who used it. If that name means nothing to
you, nothing here applies.

At first start, Heimdall looks in the folders around itself for an `RDPManager` folder holding a
`config/servers.json`, and offers to import it. Decline, and it fingerprints that data so it
stops asking.

**Offer legacy migration at next startup** clears that refusal. Two conditions must still hold
at the next start, and this is the part that surprises people: the old folder must still be
there, **and your current server list must be empty**. Heimdall never offers to merge an import
into an inventory you have already built. Click it with servers already configured and nothing
will happen at the next start, with no message to explain why.

## File sharing

**Enable TFTP sharing** starts a small TFTP server, for pushing firmware and configuration to
network gear that speaks nothing else. TFTP has no authentication and no encryption of any kind:
anyone who can reach the port can read and write the shared folder. Turn it on for a trusted LAN
for the length of the transfer, and turn it off afterwards.

## SSH gateways, PuTTY and Plink

**SSH Gateways** are jump hosts. You declare a machine that is reachable, and Heimdall routes
sessions through it to machines that are not reachable directly. This is the setting to look for
when a server is only accessible from inside a network you reach over SSH.

Renaming a gateway is reflected at once on every session that routes through it: the badge
and tooltip in the tree, the detail pane, and the `{Gateway}` token of external tools. Editing a
gateway also reaches the "Route via" list of every open network tool, which keeps its selection
and dials the edited host on its next run; a run already in progress keeps the tunnel it opened.
Deleting the selected gateway puts the tool back on a direct connection and says so on its error
line. What an open connection shows on its tab, in the Tunnels panel and on a certificate
question is the name the connection was made through, and it stays so.

**Path to plink.exe** is needed only for the PuTTY-based paths: Pageant keys,
keyboard-interactive servers, and the Plink fallback. Key files alone need nothing here.
**PuTTY path** is needed only when SSH mode is set to External; left blank it is looked for next
to plink.exe.

## Third-party tool detection

**Sysinternals, NirSoft and NanaRun directories** - Heimdall does not ship these suites. Point it
at a folder where you have already installed one, and the tools it finds there appear in the
toolbox. Leave them empty and Heimdall simply offers its own built-in tools.

## Projects

A label for grouping sessions by client, site or environment, and filtering the tree by it.
Purely organisational: it changes nothing about how a connection is made.

## External editor

The editor opened when you edit a remote file over SFTP. Left empty, Windows opens the file with
whatever it associates with that extension.

## Where a setting's range lives, and what happens outside it

Every numeric setting that has a recommended range declares it once, on the setting itself, in
`AppSettings`. The loader, the settings screen, the message the screen shows and both
translations read that one declaration; none of them holds a number of its own. Before this,
the same bound was written in four places by hand and they had drifted apart.

The two readers do different things with a value outside the range. The settings screen refuses
to save it and names the bound. The loader, reading `settings.json`, keeps the value exactly as
written and logs a warning saying so: a file written by a newer Heimdall must survive an older
one, so the loader never rewrites what it does not understand. Whether an out-of-range value
then has an effect is decided where the setting is used. A setting whose "off" value is zero
declares that too, and zero is accepted without a warning.

## Related

- [RDP memory and session tuning](RDP-PERFORMANCE.md)
- [User Guide](USER-GUIDE.md)
- [Troubleshooting](TROUBLESHOOTING.md)
