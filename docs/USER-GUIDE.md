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
over SSH, file transfers over SFTP and FTP, plus VNC, Telnet, Citrix, WinRM, web consoles and a
local shell. Everything opens in a tab inside one window.

---

## Contents

1. [Installing](#installing)
2. [Your first connection](#your-first-connection)
3. [Where your passwords are kept](#where-your-passwords-are-kept)
4. [Transferring files](#transferring-files)
5. [When a connection fails](#when-a-connection-fails)
6. [Sharing what a session printed](#sharing-what-a-session-printed)
7. [Sending a log when you need help](#sending-a-log-when-you-need-help)
8. [Updating](#updating)
9. [Shortcuts worth knowing](#shortcuts-worth-knowing)

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

### Web consoles

Choose **Web** when you add a session, for an iLO, an iDRAC, a switch or a NAS admin page. Type
only the name or address in the host field: tick **Use plain HTTP** if the console has no HTTPS,
and put a page such as `/login.html` in **Path**. The line under the path shows the address that
will open.

- The first time a console presents a certificate Windows does not trust, which is usual for a
  self-signed one, Heimdall shows its fingerprint. **Trust certificate** remembers it,
  **Trust this session** only until Heimdall closes. If the certificate changes later, you are
  asked again with a warning. To take back a certificate trusted for good, open
  **Settings > Security > Web certificates**, find it under **Trusted web console certificates** and forget it.
- A username and password in the profile answer the browser's own sign-in window (Basic or
  NTLM). A sign-in form inside the page is not filled in. With the credential provider turned
  on, give the profile a username only: the password comes from your vault.
- With the reachability check on, a web console's dot asks for its page rather than only opening
  its port: a console answering with a server error (5xx) shows as unreachable, with the code in
  the tooltip. A console whose secure connection the system refuses, often an old one, still shows
  as reachable, with the reason in the tooltip; one that cuts the connection off shows as unreachable.
- A console opens at the zoom you last left it at (Ctrl and the mouse wheel, or Ctrl and + or -),
  kept per profile. Back at 100%, nothing is kept.
- If a console looks wrong in its tab, the button at the top right of the tab opens the same page
  externally; the panel shown when a console cannot be loaded offers it too.
- For a console too old for today's browsers, open the **Options** tab and tick **Open with an
  external program**. Name the program, for example an old portable browser, or leave it empty
  for your default browser. `{Url}`, `{Host}` and `{Port}` in the arguments are replaced.
- Web tabs share one browser profile: a console you signed in to stays signed in in the next tab
  and after a restart.
- A console only reachable through a bastion: pick the SSH gateway in the **Network** tab, as for
  RDP. Type the console's own name or address; it is resolved on the gateway's side. Consoles
  behind a gateway have a browser profile of their own, so you sign in to them separately.

### Quick connect

**Ctrl+K** opens a search box where you can type the name of an existing session, or an address
directly such as `admin@192.168.1.10`. It is the fastest way to reach something you use often.

---

### Find and organize sessions in the tree

- Type in the filter box above the tree to search. Every word you type has to appear somewhere in a session's name, address, folder, username, protocol, environment, tags or project, in any order, and accents are ignored: `web prod` finds web01 in the Prod folder.
- In the filter box, **Escape** clears the search, **Down** moves to the first session in the list, and **Enter** opens the session when exactly one matches. With two matches or more, Enter does nothing.
- In the tree itself, type the first letters of a name to move to the next session or folder that starts with them. Typing the same letter again steps through the rows that start with it, and a pause of one second starts a new search. The folder you moved to stays marked when the focus goes to a menu or the filter box.
- Active filters appear below the search controls. Remove one with its cross, or use **Reset all filters** to clear the search and all filters. Display preferences are preserved. While a filter is on, a folder's count reads visible/total, such as `2/40`.
- Search results include the folder path and host address. Hover over that line to read the full values. If nothing matches, use **Clear search**.
- A selection that a search or a closed folder hides is given back once its rows are visible again, unless you selected something else in the meantime.
- Right-click a session, or several, and choose **Add to favorites** to mark them. A favorite shows a small star, and the filter menu can keep favorites only.
- The dot before a session shows its state. A filled dot is the connection: open, opening or failed. A ring of the same colours is the background check of whether the host answers. Hover a row for its full name, protocol and state. A session reached through an SSH gateway or an RD Gateway is not checked, and its hover text says so.
- Select several sessions to show the selection count with **Connect selected**, **Move** and **More actions** below the tree. These use the same checks as the context menu.
- **Move to folder** follows the folder tree: a folder with sub-folders opens a submenu, and its first entry, **Into** followed by the folder's name, moves into that folder itself.
- While dragging, a hint names the destination and the number of sessions, or the folder being moved. Hold over a closed folder to expand it; approach the top or bottom of the tree to scroll. To take a session or a folder out of its folder, drop it on **Drop here to take it out of its folder**, which appears below the tree while you drag.
- After a move, inline rename, or session reorder, the bar below the tree names the change and offers **Undo** for 30 seconds. One step is kept during the current application session. Undo does not cover deletion, and refuses an incompatible later edit. Deleting a folder withdraws the offer. For folders, a later organization or folder-defaults change prevents undo.
- The **Delete** key acts on sessions, never on a folder: to delete a folder, use **Delete folder** in its menu.
- **Show properties panel**, in the filter menu, in `Settings > General > Appearance > Session tree`, or with **Ctrl+Shift+B**, docks the properties of the selected session under the tree: protocol, address with its port, authentication, user, SSH key, gateway, folder, environment, tags, the latency of the last successful reachability check, and when the session was last opened. It follows the selection even while a session tab covers the detail pane, and it is read-only. On a folder, or with several sessions selected, it shows a summary instead: how many sessions, how many are connected, and how many use each protocol. A folder's subfolders are counted, and a search counts only what it shows. Choosing a session brings the panel back to it. Drag the bar above it to resize it; the height and the choice are remembered. Click a value, or reach it with Tab and press Enter, to copy it: every value but the protocol, the state and the favorite mark. The SSH key line appears for SSH and SFTP sessions only. The last opened time comes from the connection history, which keeps about the last 2 MB of opens and closes. The authentication line says how the session will sign in: SSH key, stored password, SSH agent when the profile stores neither, or the credential provider when it is turned on and the profile stores no password.

### When the tabs no longer fit

- The strip scrolls by itself to the tab you switch to, and the mouse wheel scrolls it sideways.
- An **All open tabs** button appears at the right end of the strip. It lists every open tab,
  those scrolled out of sight and those of a collapsed folder included, with the open one in
  bold; pick one to switch to it.
- To move a tab to the end, drag it onto the empty part of the strip after the last tab.
  Dropping it on the session area below splits that session instead.

### Closing and reopening tabs

- Middle-click a tab to close it; a connected session asks first, as with its close button.
- **Reopen closed tab**, in any tab's menu, reconnects the last session you closed. Up to ten are
  remembered while Heimdall runs; tool tabs and quick connections are not.
- With the tabs arranged by folder, a tab's menu also offers **Close this folder's tabs**.

### Arrange your tabs by folder

Right-click any session tab and tick **Arrange tabs by folder**. The tabs of each folder then sit
side by side in the tab strip, behind a chip with the folder's name and colour, and a coloured
line under each of them. The choice is remembered.

- Click a chip to fold that folder's tabs into the chip alone, and click it again to unfold them.
  The tab menu also offers **Collapse this folder's tabs** and **Expand this folder's tabs**.
- **Collapse the other folders**, in a tab's menu, folds every other folder at once and keeps this
  one open: one gesture to switch from one set of machines to another. Tabs outside any folder
  stay visible.
- Collapsing the folder that holds the open tab moves to the nearest tab outside it. When every
  tab belongs to that folder, its tabs stay open.
- Opening a session, or switching to a tab, of a collapsed folder unfolds its tabs.
- The tabs follow the folder: move the session to another folder, or rename or recolour the
  folder, and the tab follows. A workspace restored at startup comes back arranged the same way.
- Pinned tabs stay at the front of the strip, outside any folder's tabs. Tool tabs, quick
  connections and sessions outside any folder are never arranged.

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
- **Download** (toolbar button, right-click menu, or **Ctrl+Shift+D**) saves the selected files
  and folders into a folder you choose. A folder comes with everything it contains; links, pipes
  and devices are left out, and the pane says how many were skipped.
- **Drag remote entries onto a folder row** to move them into that folder on the same server. A
  name already in use there is given a new name; nothing is replaced.
- **One transfer runs at a time.** A batch started while another one runs waits in the
  **Transfer queue** below the list. Each batch there can be stopped with **Cancel this
  transfer**; one that failed or was cancelled stays listed with its reason and runs again with
  **Retry**, and **Clear finished** removes those. The bar measures the whole batch, and shows the
  rate and the time left once there is enough to measure.
- When a file is already there, the conflict dialog shows the size and date of both copies and
  which one is newer, and can replace only the newer ones. Cancel stops the whole batch.
- **Double-click a remote text file** to edit it. Heimdall downloads it, opens it, and uploads it
  again each time you save. Close the editor when you are done.
- **F2** renames, **F5** refreshes the listing. Neither fires while you are typing in the filter
  box.
- **Right-click** a row to act on it: the row under the pointer is selected before the menu opens.
- **Edit in external editor** uses the editor chosen in Settings, for remote files too. Heimdall
  keeps a local copy while that editor is open and uploads each save; closing the pane while a
  file is still open there asks you first. If the server refuses an upload (permission denied,
  for example), the pane says why once and tries again the next time you save.
- In the built-in editor, **Ctrl+S** saves and **Ctrl+W** closes. A file that is not UTF-8 opens
  as Latin-1 and says so in the editor's status bar.

> Deleting in the local file browser is **permanent**. It does not use the Recycle Bin, and a
> folder goes with everything inside it. The confirmation says so; read it before clicking yes.

---

## When a connection fails

Heimdall shows the reason in plain language wherever it can. The common ones:

| What you see | What it usually means |
|---|---|
| The password is refused | Wrong password, or the account is locked on the remote machine. |
| The connection times out | The machine is off, or a firewall is in the way. Check the address. An SSH host that does not answer is reported after 15 seconds. |
| The host key changed | See the warning above. Do not accept it without asking. |
| The server asks a question this client cannot answer | The server wants a verification code or another second factor, and this connection cannot ask you: the file browser, gateways and the route test only answer password prompts, and never with your password when the question names a one-time code. The SSH terminal asks you instead. |
| A WinRM session ends as soon as it opens | The sign-in failed. When Heimdall recognizes the cause (credentials rejected, access denied, Kerberos, TrustedHosts, a host unreachable behind a gateway), the status line at the top of the tab says so in red, and the PowerShell error above the end marker gives the detail. Heimdall ends PowerShell rather than leave you at a prompt on your own machine in that tab. If the message says the execution policy refused the sign-in script, use the current Windows identity for that host, or ask your administrator. |
| A message about WebView2 | The machine has no Microsoft Edge. See [Installing](#installing). |
| "SSH gateway not found" | The session points at a gateway that no longer exists. Edit the session and choose one, or recreate it in Settings. |
| A VNC server asks for a username, or for a password | The server wants a username (macOS Screen Sharing, for example), which Heimdall does not store, or a password the session does not have. For a password, add it to the session and reconnect. |

An RDP session that disconnects on its own will try to reconnect by itself, and shows you what it
is doing. You can cancel that from the toolbar. **Escape** closes a disconnect message without
closing the tab, and **Reconnect** in the session header then tries again.

If the reason on screen is not enough, the log will have more.

For a disconnected SSH or SFTP session, choose **Edit profile** to correct its saved settings,
then reconnect. The session stays open while you edit. In a split view, this edits the profile
of the pane where you clicked, rather than the first pane. A session opened with **Ctrl+K** has
no saved profile: **Edit profile** opens the session dialog on what you typed, so you can correct
it and save it as a profile.

In the SSH terminal, **Cancel** stops a connection that is still opening, and **View output**
hides the end message so you can read what the server printed. **Ctrl+Shift+F** searches the
output (**Enter** for the next match, **Shift+Enter** for the previous one), and **Ctrl+click**
opens a link.

For RDP, **Copy anonymized report** copies the UTC time, Heimdall version and available RDP
codes without server addresses, accounts or error message text. The existing **Copy error**
action keeps the detailed report, including the server identity. Review that report before sharing.

For an SSH gateway, open its add/edit dialog and use **Test route**. The preview shows the
complete parent chain and includes the values currently in the form. Testing does not save them.
Optionally enter a destination host and TCP port to check access from the last gateway.
Each step shows its duration and an action to take if it fails; later steps stop at the first failure.
Use **Stop test** or close the dialog to cancel. Changing the form clears the previous report.

The diagnostic uses existing SSH trusted host keys and never accepts a new or changed key for you.
Each step gets 15 seconds, the time a real tunnel gives each hop. **Copy diagnostic report**
omits hostnames, accounts, key paths and raw errors. A successful destination step proves TCP
access only, not that an RDP, database or other application login will work.

---

## Sharing what a session printed

When you need to paste what a terminal printed into a ticket, a chat or an email, use **Copy output, secrets masked**. It works on SSH, Telnet, WinRM and the embedded local shell. It does not exist for RDP, VNC, SFTP, FTP or web tabs, which have no terminal text to read.

Open it in any of three ways:

- press **Ctrl+Shift+M** in the terminal tab;
- right-click the session tab and choose **Copy output, secrets masked...**;
- press **Ctrl+K** and run the **Copy output, secrets masked** command.

Heimdall reads the text of the session, hides the secrets it recognizes, and shows you the result in a preview. Each hidden value becomes `[masked:<rule>]`, and the preview lists how many values each rule hid. Nothing is copied until you press **Copy**. **Cancel** copies nothing.

What the rules hide:

- a private key, from its BEGIN line to its END line;
- an `Authorization: Bearer` or `Authorization: Basic` header value;
- the password in a URL (`https://user:password@host/`);
- a password given on a command line: `--password`, `sshpass -p`, `export ...PASSWORD=`, `PGPASSWORD=`, `MYSQL_PWD=`, and `-p` on the mysql family of commands;
- what was typed after a password prompt and echoed back, including a password typed into a shell that answered "command not found";
- the value of a key named like a secret (`password`, `secret`, `token`, `api_key`, `access_key`, `private_key` and a few more);
- AWS access keys, GitHub tokens, Slack tokens and JSON Web Tokens.

One option is off by default: **Also mask host names and addresses**. It replaces IPv4 addresses with `ip-1`, `ip-2`... and host names (the host in `user@host` prompts, and names with at least two dots) with `host-1`, `host-2`.... The same address or name always gets the same pseudonym inside one copy, so the text stays readable.

What it does not hide. The rules are patterns, not understanding, so read the preview before you paste it:

- user names, file paths, and anything else that is not on the list above;
- a password typed where no rule can see it, for example in the middle of a sentence or into an editor;
- a private key whose BEGIN and END lines have both scrolled out of what was read;
- host names and addresses, unless you tick the option above;
- anything a rule does not recognize, such as a secret in an unusual format.

Good to know:

- Nothing leaves your machine on its own. The masking runs locally, uses no network and no outside service, and writes nothing to disk. You choose where to paste the result.
- Only the last 4 MiB of the scrollback are read. When the output was longer, the preview says that it was cut.
- If a rule takes too long on the text, Heimdall refuses to copy anything rather than copy text that might still hold a secret.
- Session logs are not masked. This feature only changes what you copy.

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
version. Your sessions and settings live outside the program folder and are not affected. Heimdall
recognises such a copy and does not offer to install over it; it shows the release page instead.

If you chose **Skip this version** on the banner and change your mind, the skipped version is listed
under **Settings** -> **Updates** with a button that offers it again.

Installing an update saves your unsaved settings, the tree's expand state and the window position
before Heimdall exits, exactly as closing the window would. If an update did not apply, the banner
says so at the next start and the relauncher's log is in the log folder shown in the About panel.

---

## Shortcuts worth knowing

Press **F1** at any time for the full list. The ones that pay for themselves immediately:

| Shortcut | What it does |
|---|---|
| `Ctrl+K` | Quick connect: search sessions, or type an address |
| `Ctrl+N` | Add a session |
| `Ctrl+E` | Edit the selected session |
| `Ctrl+B` | Show or hide the left panel |
| `Ctrl+Shift+B` | Show or hide the properties panel under the tree |
| `Ctrl+F` | Jump to the sessions filter box; on the Settings tab, to the settings search |
| `Ctrl+,` | Open the Settings tab |
| `Ctrl+S` | On the Settings tab, save the settings when there are changes to save |
| `F11` | Fullscreen, `Escape` to leave it |
| `Ctrl+Shift+T` | Switch the left panel between Sessions and Tools |
| `Ctrl+Shift+M` | In a terminal tab, copy its output with secrets masked, after a preview |
| `Ctrl+Tab` | Next tab, `Ctrl+Shift+Tab` for the previous one; a collapsed folder is skipped |
| `Ctrl+PgDn` / `Ctrl+PgUp` | Next or previous tab, from a terminal too; inside an RDP session they go to the remote computer |
| `Ctrl+Shift+PgDn` / `Ctrl+Shift+PgUp` | Move the open tab right or left, within its pinned block and its folder |
| `Ctrl+1` to `Ctrl+8` | Go to that tab; `Ctrl+9` goes to the last one |
| `Ctrl+Shift+R` | Reopen the tabs of the last close: one tab, or every tab a "Close others" or "Close all tabs" closed |
| `Ctrl+F4` | Close the open tab, from a terminal too; inside an RDP session it goes to the remote computer |
| Middle click on a tab | Close that tab |
| `Shift+F10` or the Menu key | On the tab strip, open the tab's menu; in the tree, the session or folder menu |
| `Ctrl+A` | In the sessions tree, select every session in the open folders |
| `Alt+Up` / `Alt+Down` | Move the focused session within its folder |
| `F2` | Rename the selected session or folder |

---

## Still stuck?

- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) covers specific failures in detail. It is written for a
  technical reader, so search it for the exact message you saw.
- The project page is reachable from the same About section as the log folder.
