<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - MobaXterm shared session sources

*Also available in French: [fr/MOBAXTERM-SHARED-SESSIONS.md](fr/MOBAXTERM-SHARED-SESSIONS.md).*

For teams that keep their sessions in a MobaXterm `.mxtsessions` file on a shared folder. Heimdall reads that file and lists its sessions, read-only, next to your own. The full reference is in [FEATURES.md](FEATURES.md#import-and-migration).

---

## 1. Add a shared source

1. Open **Settings > General > Shared sources** and click **Add**.
2. **Name**: the name the tree shows for this source, for example `Team`.
3. **File**: the path of the `.mxtsessions` file. **Browse...** picks it, or type a path with environment variables so it works for everyone, for example `%OneDriveCommercial%\Team\sessions.mxtsessions`.
4. Keep **Enabled** ticked, then **Save settings**.

The **Status** column says what the last read found: the number of sessions, or why the file could not be read. **Reload** reads the saved sources again. The source appears in the session tree as a root of its own, after your folders, with its MobaXterm folders underneath.

## 2. Check the login variable

MobaXterm files often write the login as a variable, such as `[DAS]`. Heimdall replaces it with the value set in **Settings > General > Settings file & import > MobaXterm import variables**. `DAS` defaults to `%USERNAME%`, your Windows login. If your login on the servers is different, change the value there; the **Resolves to** column shows what it becomes. A variable with no value is listed in the import report (section 7).

## 3. What is read-only, and why

Shared sessions belong to the file, which other people maintain. In Heimdall they cannot be edited, renamed, duplicated, moved, reordered, deleted or marked favorite, and nothing can be dragged into or out of a shared folder. A lock on the row names the source.

To change a shared session, change it where the team keeps it: in MobaXterm, or in the file itself. Heimdall picks up the change on its own (section 6).

Connecting, opening in a split, copying the host and the health indicator work as for your own sessions.

## 4. First connection: the login command

A shared session can carry a login command, such as `sudo su - <account>`, that MobaXterm types after you connect. The first time, Heimdall shows the command and asks before typing it. Your answer is kept on your machine, for that session and those exact commands.

You are asked again when the command changes in the file. Approvals are personal: they are never exported with the settings, and **Reset defaults** clears them.

## 5. Passwords and SSH keys stay personal

A shared file never provides a password or a key: Heimdall does not read them from it, even when MobaXterm stored some. Keys come from your SSH agent (Pageant or the Windows OpenSSH agent).

When your agent offers no key, Heimdall asks for the password of the bastion, then of the server, before it connects. The question has a **Remember this password on this computer** box, and each person chooses, as in MobaXterm:

- **Ticked**: the password is kept once the server has accepted it, and the next connections use it without asking.
- **Not ticked**: the password is used for this connection only.

A remembered password is yours alone. It stays on this computer, encrypted for your Windows account, in `shared-session-secrets.json` in the Heimdall data folder. It is never written to the shared file, to the offline copy, to `servers.json` or to the settings, an export of the settings or of the profiles never carries it, and **Reset defaults** does not delete it.

A remembered password belongs to one bastion (its host, port and login) or to one session (the session, its host, port and login). If the shared file sends a session to another host or another login, Heimdall asks again instead of sending the old password. When a server refuses a remembered password, Heimdall forgets it and asks again next time.

- If you use an external credential provider, its password comes first: a remembered password only fills what it leaves empty. For a shared session the provider is asked for the entry named after the session's **host**, not after the session's name: file your vault entries under the host name (for example `srv-app-01.corp.example`).
- When **Windows Hello** is required to connect, it is asked before any remembered password is used.
- Reopening sessions at startup and **Connect all** use remembered passwords but never ask: a session without one asks when you open it alone.
- A shared SFTP session remembers its password per user, like an SSH session, with the same box and the same rules.
- A Telnet session asks for its login and password in the terminal, which Heimdall does not remember.
- A VNC session is not asked for a password before it connects: nothing in a shared file says whether its server wants one. When the server asks, the session waits with "This VNC server asks for a password" and offers **Enter password**. The question has the same **Remember this password on this computer** box. The connection stays open while you type, and what you typed answers the server on it: closing it would count as a failed sign-in on servers such as TigerVNC, which block your address after a few. If the server stopped waiting in the meantime, the session connects again with what you typed.
  - A ticked password is kept once the VNC server has accepted it, for that session, its host and its port. The next connections, **Reconnect** included, use it without asking.
  - When the server refuses the password, Heimdall forgets it and offers **Enter password** again. A refusal that comes before any password is sent, for example a server that blocks your address after too many failures, keeps it.
  - An entry of the external credential provider under the session's host name still comes first, as above.
  - A VNC server that also asks for a username stays out of reach: Heimdall has no VNC username.
- An RDP session asks for its password in the RDP window (the Windows Security prompt, which also offers smart card and Windows Hello for Business where the server allows them), and Heimdall does not remember it. To avoid retyping it, file the password in the external credential provider under the session's host name, as above: Heimdall then fills it in. The password of its bastion is remembered like any other.

In the settings, each source with remembered passwords shows **Forget saved passwords**. Removing a source also forgets its passwords. Nothing you type is ever written to a shared session, which stays read-only.

## 6. Automatic reload and offline copy

Heimdall watches each source file. When it changes, that source alone is read again a moment later, the tree follows, and your expanded folders and selection stay. The status line says how many sessions the source now holds. Open sessions are never closed by a reload.

If the new file cannot be read, or suddenly holds no session, the previous sessions are kept and the source says why.

After each successful read, Heimdall keeps a copy of the file in its data folder. If the file is missing at startup (for example while the shared folder is not synchronized yet), the sessions are read from that copy and the source shows **offline copy from** a date. Removing the source deletes its copy.

## 7. Read the import report

Each read also checks the file for common mistakes. When it finds some, the source root shows a count, for example `3 issues`. Right-click the root and choose **Show import report**, or use **Report** in the settings card. The report lists, by folder and session:

- **Skipped sessions**: sessions not loaded, and why.
- **Unresolved variables**: a `[NAME]` with no value (section 2).
- **Name and host mismatch**: a session named after one machine that reaches another.
- **Gateway outliers**: a session that does not take the bastion most of its folder takes. Only sessions whose type can name a jump host in MobaXterm count, which means SSH and RDP: a Telnet, VNC or SFTP session is direct whatever its folder does.
- **Duplicate hosts**: two sessions of one folder reaching the same host with the same protocol. An SSH session and an RDP or SFTP session to one host are two ways to use it, not duplicates; two SSH sessions to it are.

The report only informs: every session it lists is still loaded as the file describes it. **Copy** and **Export CSV** let you send it to whoever maintains the file.

## 8. Known limits

- Five session types are imported: SSH, RDP, SFTP, Telnet and VNC. Every other session of a MobaXterm file is listed as skipped in the import report, with its reason (section 7):
  - **FTP**: not imported yet. FTP sends the password in clear, and a shared session must not send a vault or remembered password that way.
  - **WSL**: a Linux system on the computer that runs MobaXterm, so there is nothing to connect to.
  - **Serial, Mosh, RSH, Xdmcp, Browser, Shell, File and S3**: not imported.
  - **A Telnet or VNC session through an SSH jump host**: skipped, because Heimdall does not route these protocols through a jump host and a direct connection would go around the bastion.
  - **An SSH, Telnet, VNC or SFTP session through a proxy**: skipped, because Heimdall does not use a proxy for these protocols and a direct connection would go around the proxy.
  - **An RDP session through an RD Gateway**: skipped, because the RD Gateway of a MobaXterm session is not imported yet and a direct connection would go around the gateway.
- MobaXterm has no proxy setting for an RDP session: its only other route is the RD Gateway above.
- A Telnet session asks for its login in the terminal, so no user is imported for it.
- Network tools (ping, port scan and the other tools that can go through a gateway) route only through your own SSH gateways from the settings, not through the bastions a shared source defines. To reach hosts behind a shared bastion from a tool, define that bastion as one of your own gateways.
- The remote start folder of an SFTP session is not carried over: the session opens in the default folder.
- Shared sessions are read-only in Heimdall (section 3).
