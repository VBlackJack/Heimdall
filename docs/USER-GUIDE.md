<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - User Guide

*Also available in French: [fr/USER-GUIDE.md](fr/USER-GUIDE.md).*

This guide is for people using Heimdall, not building it. It answers the questions that come
up in the first hour and the ones that come up when something goes wrong. For the full list of
features see the [README](../README.md); for developer topics see [DEVELOPMENT.md](DEVELOPMENT.md).

Heimdall connects you to remote machines: Windows desktops over RDP, Linux and network gear
over SSH, file transfers over SFTP and FTP, plus VNC, Telnet, Citrix, WinRM and a local shell.
Everything opens in a tab inside one window.

---

## Contents

1. [Installing](#installing)
2. [Your first connection](#your-first-connection)
3. [Where your passwords are kept](#where-your-passwords-are-kept)
4. [Transferring files](#transferring-files)
5. [When a connection fails](#when-a-connection-fails)
6. [Sending a log when you need help](#sending-a-log-when-you-need-help)
7. [Updating](#updating)
8. [Shortcuts worth knowing](#shortcuts-worth-knowing)

---

## Installing

Download the latest release from the [Releases](https://github.com/VBlackJack/Heimdall/releases) page. There are two editions,
and both already contain everything .NET-related they need. You do not install anything else
first.

| Edition | Pick it when |
|---|---|
| **Standard** | Normal Windows 10 or 11 machine. Smaller download. |
| **Self-Contained** | The machine has no Microsoft Edge, or no internet access at all. Larger download, needs nothing. |

**If you are not sure, pick Standard.** It relies on Microsoft Edge, which is present on
essentially every Windows 10 and 11 machine.

Each edition comes as an **installer** (creates shortcuts, handles upgrades, can be uninstalled)
or a **zip** (unzip it anywhere and run `Heimdall.exe`, nothing is installed).

> If terminals, the VNC screen or the notes editor come up blank with a message about WebView2,
> the machine has no Edge. Install Microsoft Edge and restart Heimdall, or reinstall using the
> Self-Contained edition.

---

## Your first connection

The left panel is your list of sessions. It starts empty.

1. Press **Ctrl+N**, or use the button above the list, to add a session.
2. **Choose the protocol first.** RDP for a Windows desktop, SSH for a Linux or network shell,
   SFTP to browse files, and so on. The fields on the next step change to match.
3. Fill in the name you want to see in the list, the machine's address, and your login details.
4. Save. The session appears in the left panel.
5. Double-click it to connect.

A few things worth knowing at this point:

- **The SSH username is not optional.** Heimdall cannot sign in without it, and will tell you so
  rather than attempting the connection.
- **The port is usually already right.** Leave it alone unless you were told otherwise.
- **Advanced settings are hidden by default** behind a toggle in the dialog. You do not need
  them for an ordinary connection.

### The first time you connect over SSH

You will be asked to confirm the server's *host key* - a fingerprint that identifies the machine.
This is normal on a first connection. Accept it if you are connecting to a machine you expect to
reach; Heimdall remembers it afterwards.

If that same prompt appears again later for a machine you have already accepted, **stop and ask
someone.** It can mean the machine was rebuilt, or that something is impersonating it. On that
prompt the highlighted button is **Reject**: pressing Enter refuses the connection. Accepting the
new key, or trusting it for this session only, takes a deliberate click.

### Quick connect

**Ctrl+K** opens a search box where you can type the name of an existing session, or an address
directly such as `admin@192.168.1.10`. It is the fastest way to reach something you use often.

---

## Where your passwords are kept

Passwords you save in a session are encrypted on your own machine, tied to your Windows account.
Another Windows user on the same computer cannot read them. They live under:

```
%LOCALAPPDATA%\Heimdall
```

You can paste that path into the File Explorer address bar.

### The master password, and one warning

Settings offers a **master password** that encrypts your stored credentials behind a password you
type at startup. It adds real protection, and it comes with one consequence you should read
before turning it on:

> **The master password cannot be recovered or reset.** If you forget it, Heimdall will not open
> and the stored credentials are lost. There is no reset link and no backdoor, by design.

If you turn it on, treat it like the key to a safe: write it down somewhere you trust, or store
it in a password manager.

Heimdall can also use **Windows Hello** (fingerprint, face or PIN) as a gate before stored
credentials are used, and can read passwords from an external password manager instead of storing
them itself. Both are in Settings, under Security.

---

## Transferring files

Open an **SFTP** session (or FTP) to get a two-panel file browser: your machine on one side, the
remote machine on the other.

- **Drag and drop** between the panels to copy, in either direction, including whole folders.
- **Double-click a remote text file** to edit it. Heimdall downloads it, opens it, and uploads it
  again each time you save. Close the editor when you are done.
- **F2** renames, **F5** refreshes the listing.

> Deleting in the local file browser is **permanent**. It does not use the Recycle Bin, and a
> folder goes with everything inside it. The confirmation says so; read it before clicking yes.

---

## When a connection fails

Heimdall shows the reason in plain language wherever it can. The common ones:

| What you see | What it usually means |
|---|---|
| The password is refused | Wrong password, or the account is locked on the remote machine. |
| The connection times out | The machine is off, or a firewall is in the way. Check the address. |
| The host key changed | See the warning above. Do not accept it without asking. |
| The server asks a question this client cannot answer | The server wants a verification code or another second factor. Heimdall only answers password prompts; use another client for that server. |
| A message about WebView2 | The machine has no Microsoft Edge. See [Installing](#installing). |
| "SSH gateway not found" | The session points at a gateway that no longer exists. Edit the session and choose one, or recreate it in Settings. |

An RDP session that disconnects on its own will try to reconnect by itself, and shows you what it
is doing. You can cancel that from the toolbar.

If the reason on screen is not enough, the log will have more.

---

## Sending a log when you need help

Heimdall keeps a diagnostic log. When you report a problem, that log is the single most useful
thing you can attach.

**To find it:**

1. Go to **Settings** (the gear), then the **Advanced** tab, then **Tools & integrations**.
2. Scroll to the **About** section. It shows the log folder's full path on screen, next to
   **Logs**, and a **Open logs folder** button beside it.
3. The file is named for today's date, like `heimdall_20260821.log`.

If the button does nothing, copy the path shown next to it and paste it into the File Explorer
address bar instead.

**Before sending it**, open it and skim it. It records the machines you connected to and the
errors you hit. It does not contain your passwords - Heimdall never writes those to the log -
but hostnames and usernames are the kind of thing you may not want to post publicly.

---

## Updating

Heimdall checks for updates on its own and tells you when one is available.

To check yourself: **Settings** -> **General** -> **Check now**.

If an update is found, Heimdall can usually download and install it for you. Some builds cannot
install themselves, in which case it says so and points you at the release page to install it by
hand.

If you installed from the zip rather than the installer, replace the folder contents with the new
version. Your sessions and settings live outside the program folder and are not affected.

---

## Shortcuts worth knowing

Press **F1** at any time for the full list. The ones that pay for themselves immediately:

| Shortcut | What it does |
|---|---|
| `Ctrl+K` | Quick connect: search sessions, or type an address |
| `Ctrl+N` | Add a session |
| `Ctrl+E` | Edit the selected session |
| `Ctrl+B` | Show or hide the left panel |
| `Ctrl+F` | Jump to the search box |
| `F11` | Fullscreen, `Escape` to leave it |
| `Ctrl+Shift+T` | Switch the left panel between Sessions and Tools |
| `Ctrl+A` | In the sessions tree, select every session in the open folders |
| `Alt+Up` / `Alt+Down` | Move the focused session within its folder |
| `F2` | Rename the selected session or folder |

---

## Still stuck?

- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) covers specific failures in detail. It is written for a
  technical reader, so search it for the exact message you saw.
- The project page is reachable from the same About section as the log folder.
