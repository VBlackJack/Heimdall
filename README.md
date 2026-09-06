<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

![Heimdall](docs/readme-banner.png)

# Heimdall

*Also available in French: [README.fr.md](README.fr.md).*

[![CI](https://github.com/VBlackJack/Heimdall/actions/workflows/ci.yml/badge.svg)](https://github.com/VBlackJack/Heimdall/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-10%2C000%2B%20passing-brightgreen.svg)]()
[![Tools](https://img.shields.io/badge/tools-58%20sysops-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)]()

**One window for every machine you look after.**

Heimdall keeps your remote connections in one place: Windows desktops, Linux shells, file
transfers, and the rest. You save a machine once, then reach it with a double-click. Passwords
stay encrypted on your own computer, and every session opens as a tab in the same window instead
of scattering across half a dozen programs.

It is free, open source, and runs on Windows 10 and 11.

---

## What you can connect to

| | For |
|---|---|
| **RDP** | Windows desktops, embedded in a tab or opened full screen |
| **SSH** | Linux servers, switches, firewalls, anything with a shell |
| **SFTP** and **FTP** | Moving files, with a two-panel browser and drag and drop |
| **VNC** | Screens on Linux, macOS, appliances |
| **Telnet** | Older network gear |
| **Citrix** | Published applications and desktops |
| **WinRM** | PowerShell on remote Windows machines |
| **Local shell** | A terminal on your own machine, in the same window |

---

## What it gives you

- **Everything in one window.** Tabs, and split views when you want two machines side by side.
- **Passwords you do not have to remember.** Stored encrypted for your Windows account only. You
  can add a master password on top, or let Windows Hello unlock them with your fingerprint.
- **Your existing password manager, if you prefer.** KeePassXC, Bitwarden, 1Password and others
  can supply the passwords instead, so Heimdall never stores them at all.
- **Tools you would otherwise go hunting for.** Ping, port scanner, certificate inspector, hash
  and password generators, and dozens more, built in.
- **Sessions organised the way you work.** Folders with colours, drag and drop to move a folder
  or to arrange sessions by hand, filters by protocol, favourite, connected state or gateway, and
  a tree that opens where you left it.
- **Trust you can audit.** SSH host keys are pinned the first time you connect and shown for you
  to compare; a key that changes is refused by default, and your `~/.ssh/known_hosts` can be
  imported and exported so OpenSSH and Heimdall agree on who is who.
- **Nothing to install alongside it.** Both downloads are self-contained.

The full catalogue is in the [feature reference](docs/FEATURES.md).

---

## Download

Get the latest release from the [Releases](../../releases) page. Two editions, both complete:

| Edition | Size | Pick it when |
|---|---|---|
| **Standard** | ~106 MB installer / ~159 MB zip | An ordinary Windows 10 or 11 machine. |
| **Self-Contained** | ~267 MB installer / ~380 MB zip | The machine has no Microsoft Edge, or no internet access. |

> **Not sure? Choose Standard.** It works on any Windows 10 or 11 machine that has Edge, which is
> nearly all of them.

The releases page also carries a `.msi`, and it sorts first in the list. It is there for managed
deployment through GPO or SCCM: it installs for every user on the machine, needs an administrator,
and is not updated by Heimdall's own updater. Unless you are deploying it across an organisation,
take a `_Setup.exe` or a `.zip` instead.

Each edition comes as an **installer** (shortcuts, upgrades, uninstaller) or a **zip** (unzip and
run `Heimdall.exe`, nothing installed).

---

## Getting started

**[Read the User Guide](docs/USER-GUIDE.md).** It walks through your first connection, where your
passwords live, moving files, what the common errors mean, and how to send a log if you need help.

The short version: press **Ctrl+N** to add a machine, pick the protocol, fill in the address and
your login, then double-click it in the list. Press **F1** at any time for the keyboard shortcuts,
and **Ctrl+K** to jump straight to a machine by name or address.

---

## Documentation

| | |
|---|---|
| [User Guide](docs/USER-GUIDE.md) | Start here if you are using Heimdall |
| [Feature reference](docs/FEATURES.md) | Everything it does, protocol by protocol |
| [Tools](docs/TOOLS.md) | The built-in sysops toolbox |
| [Settings FAQ](docs/SETTINGS-FAQ.md) | The options that are not self-explanatory, and the one that misleads |
| [RDP memory tuning](docs/RDP-PERFORMANCE.md) | What a session costs, and the one setting that changes it |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Specific failures, written for a technical reader |
| [Security](SECURITY.md) | How credentials are protected, and how to report a problem |
| [Code signing policy](docs/CODE-SIGNING-POLICY.md) | Who approves a signature, and what is signed |
| [Development](docs/DEVELOPMENT.md) | Building, testing, contributing |
| [Architecture](docs/ARCHITECTURE.md) | How it is put together |
| [Changelog](docs/CHANGELOG.md) | What changed, and when |

Every public document exists in English and in French.

---

## Requirements

Windows 10 or 11. Nothing else is required: both editions include the .NET runtime.

Optional, and only if you use the matching feature: PuTTY (for Pageant SSH keys), an X11 server
such as VcXsrv (for X11 forwarding), and the Citrix Workspace App (for Citrix sessions).

---

## Building it yourself

Double-click `Run.bat` to build and launch, or `Test.bat` to run the test suite. Everything else,
including release builds and installers, is in [DEVELOPMENT.md](docs/DEVELOPMENT.md).

---

## License

Copyright 2026 Julien Bombled

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.

Heimdall redistributes third-party components under their own licences. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
