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

1. Open **Settings > General > Shared session sources** and click **Add**.
2. **Name**: the name the tree shows for this source, for example `Team`.
3. **File**: the path of the `.mxtsessions` file. **Browse...** picks it, or type a path with environment variables so it works for everyone, for example `%OneDriveCommercial%\Team\sessions.mxtsessions`.
4. Keep **Enabled** ticked, then **Save settings**.

The **Status** column says what the last read found: the number of sessions, or why the file could not be read. **Reload** reads the saved sources again. The source appears in the session tree as a root of its own, after your folders, with its MobaXterm folders underneath.

## 2. Check the login variable

MobaXterm files often write the login as a variable, such as `[DAS]`. Heimdall replaces it with the value set in **Settings > General > MobaXterm import variables**. `DAS` defaults to `%USERNAME%`, your Windows login. If your login on the servers is different, change the value there; the **Resolves to** column shows what it becomes. A variable with no value is listed in the import report (section 7).

## 3. What is read-only, and why

Shared sessions belong to the file, which other people maintain. In Heimdall they cannot be edited, renamed, duplicated, moved, reordered, deleted or marked favorite, and nothing can be dragged into or out of a shared folder. A lock on the row names the source.

To change a shared session, change it where the team keeps it: in MobaXterm, or in the file itself. Heimdall picks up the change on its own (section 6).

Connecting, opening in a split, copying the host and the health indicator work as for your own sessions.

## 4. First connection: the login command

A shared session can carry a login command, such as `sudo su - <account>`, that MobaXterm types after you connect. The first time, Heimdall shows the command and asks before typing it. Your answer is kept on your machine, for that session and those exact commands.

You are asked again when the command changes in the file. Approvals are personal: they are never exported with the settings, and **Reset defaults** clears them.

## 5. Passwords and SSH keys stay personal

A shared file never provides a password or a key: Heimdall does not read them from it, even when MobaXterm stored some. For the bastion and for the server, the password is typed when the connection asks for it, or comes from the external credential provider if you use one. Keys come from your SSH agent (Pageant or the Windows OpenSSH agent). Nothing you type is written to a shared session, which stays read-only.

## 6. Automatic reload and offline copy

Heimdall watches each source file. When it changes, that source alone is read again a moment later, the tree follows, and your expanded folders and selection stay. The status line says how many sessions the source now holds. Open sessions are never closed by a reload.

If the new file cannot be read, or suddenly holds no session, the previous sessions are kept and the source says why.

After each successful read, Heimdall keeps a copy of the file in its data folder. If the file is missing at startup (for example while the shared folder is not synchronized yet), the sessions are read from that copy and the source shows **offline copy from** a date. Removing the source deletes its copy.

## 7. Read the import report

Each read also checks the file for common mistakes. When it finds some, the source root shows a count, for example `3 issues`. Right-click the root and choose **Show import report**, or use **Report** in the settings card. The report lists, by folder and session:

- **Skipped sessions**: sessions not loaded, and why.
- **Unresolved variables**: a `[NAME]` with no value (section 2).
- **Name and host mismatch**: a session named after one machine that reaches another.
- **Gateway outliers**: a session that does not take the bastion most of its folder takes.
- **Duplicate hosts**: two sessions of one folder reaching the same host.

The report only informs: every session it lists is still loaded as the file describes it. **Copy** and **Export CSV** let you send it to whoever maintains the file.

## 8. Known limits

- Only SSH and RDP sessions are imported. FTP, SFTP, VNC and Telnet sessions of a MobaXterm file are not imported yet; they are listed as skipped in the import report.
- Shared sessions are read-only in Heimdall (section 3).
