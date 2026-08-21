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
[![Tests](https://img.shields.io/badge/tests-8%2C000%2B%20passing-brightgreen.svg)]()
[![Tools](https://img.shields.io/badge/tools-59%20sysops-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)]()

**The secure, all-in-one Windows connection manager for RDP, SSH, SFTP, VNC, Telnet, FTP, Citrix, WinRM, and local terminals.**

Built with .NET 10 and WPF. Secure, feature-rich Windows connection manager with enterprise-grade encryption and modern UX.

---

## Why Heimdall?

- **9 protocols, one interface** --- RDP, SSH, SFTP, VNC, Telnet, FTP, Citrix, WinRM, and local shell sessions in a single tabbed window
- **Zero-trust credential storage** --- DPAPI + HMAC-SHA256 integrity, an optional **master-password vault** (Argon2id key derivation, AES-256-GCM) with a startup unlock gate and idle workspace lock, PBKDF2 PIN protection, Windows ACL enforcement, and optional **Windows Hello** (biometric/PIN) to unlock the vault and as a gate before connect
- **External vault integration** --- KeePassXC, KeePass2 (KPScript), Bitwarden CLI, 1Password CLI, `pass`, the native Windows Credential Manager, or any command-line password manager
- **Pageant-native** --- Direct IPC with PuTTY Pageant via shared memory (no agent forwarding hacks)
- **Portable distribution** --- Self-contained build with no installer required; writable data is stored per user under `%LOCALAPPDATA%\Heimdall`

---

## Features

### Remote Desktop (RDP)
- Embedded sessions via ActiveX MsTscAx in a tabbed interface
- External sessions via mstsc.exe with credential autofill - the generated `.rdp` honors the per-server resolution profile, and Auto mode now matches embedded Auto with Smart Sizing, windowed launch, single-monitor mode, and primary working-area dimensions (`ae0dd70`)
- **One-shot mode override**: right-click any RDP profile -> *Connect with...* to launch in embedded or external mode for a single session, leaving the saved profile untouched. Forced sessions show a discreet `(forced embedded/external)` tab-title suffix
- Dynamic resolution resize with stabilization guard
- Per-server resolution profiles: Fit Window, Fixed, Smart Sizing, and Multimon, with a per-profile **Selected monitors** picker in Multimon mode (empty selection = use all monitors, backward-compatible with existing profiles) and connect-time topology validation that falls back to single-monitor mode when the host cannot honor the saved selection (`2e9b938`)
- Fit Window mode scales the remote desktop to the host area with Smart Sizing enabled by default, eliminating native Win32 scrollbars on real Windows RDP targets; use Fixed mode for pixel-perfect native rendering
- Automatic DPI scale tracking via `IMsRdpExtendedSettings` with `Window.DpiChanged` updates
- **Mode-aware Resolution menu and toolbar button**: the menu starts with an `Active mode: <mode>` header (showing `Fixed (1920x1080)` when applicable) and the toolbar button glyph changes per mode (Auto / Fit / Smart / Fixed / Multimon)
- Tab context-menu resolution submenu with presets, Match Window, Custom, and Save as default - same `Active mode` header as the toolbar menu
- Letterboxed fixed-resolution rendering when Smart Sizing is disabled - the active RDP region is materialized by a 1px border, with surrounding bands rendered in the theme `SurfaceBrush` (the `WindowsFormsHost` is pinned to the exact region so the Win32 HWND no longer bleeds the system-gray default through the letterbox), and a first-letterbox hint badge fades out after a few seconds
- Fullscreen UX with a high-contrast exit chip plus F11, Esc, and Ctrl+Shift+F11 escape paths
- Aspect ratio management (Stretch, 16:9, 4:3, 21:9) and anti-idle prevention
- Full redirection surface: clipboard, drives, printers, COM ports, smart cards, webcam, USB, audio
- **Auto-collapsed redirection indicators**: by default the toolbar status zone hides redirections that are off and surfaces them through a discreet `+N` expand chip; opt-in `RdpRedirectionIndicatorsAlwaysExpanded` setting keeps the legacy "show all" behaviour
- **SendKeys System shortcuts**: in addition to Ctrl+Alt+Del / Win / Alt+Tab / Ctrl+Esc / PrtSc / Esc, the SendKeys menu now includes `Win+L` (lock workstation), `Win+D` (show desktop) and `Win+E` (file explorer) for quick admin tasks
- **Edit profile always reachable from the reconnect overlay**: every disconnect code (network, transient, security) keeps the `Edit profile` button visible so users can tweak resolution, gateway or multi-monitor without closing the overlay first
- Credential autofill for CredUI dialogs (EnumThreadWindows + UI Automation), with Debug broker-window diagnostics limited to metadata such as title, handle, PID, and process name; credential fields are never logged (`1d7c78c`)
- **Honest external-launch state**: when an external mstsc client is spawned, the session shows up in warning color with a dedicated *External client launched* status, signalling that Heimdall cannot directly observe the remote session beyond the launch itself
- **Unified RDP import**: `.rdp` files dragged onto the main window or imported from `Settings -> Import` go through the same preview/conflict resolution flow
- **Performance**: COM pre-warm at startup, DNS pre-resolution on server selection, per-server experience flags (wallpaper/themes/animations), TCP-only mode for firewall-heavy environments

### SSH Terminal
- Embedded terminal via WebView2 + xterm.js (full VT100/xterm rendering)
- Pipe mode transport for correct arrow keys, colors, and escape sequences
- **Multi-agent support**: Pageant (PuTTY) and Windows OpenSSH Agent (named pipe `\\.\pipe\openssh-ssh-agent`) behind a common `ISshAgent` abstraction. User-configurable priority in `Settings > SSH & SFTP > SSH agent preference` (default: OpenSSH first, Pageant second). RSA keys negotiate SHA-2 automatically so modern servers with `ssh-rsa` disabled still accept cached agent keys.
- **Separate key passphrase field**: distinct from the login password, both persisted encrypted. Enables key-with-fallback-password workflows without the field ambiguity of legacy setups.
- **OpenSSH config import with ProxyJump**: single-hop and multi-hop chains auto-mapped to Heimdall's gateway model with `ParentGatewayId` links. Unsupported forms (ProxyCommand, `%h`/`%p` tokens, cycles) rejected with explicit diagnostics rather than silently mis-imported.
- SSH keepalive heartbeat (prevents TMOUT disconnects)
- User-confirmed TOFU host key verification with persistent fingerprint pinning; trust decisions resolved *before* `Connect()` via a dedicated pre-authentication probe - SSH.NET's `HostKeyReceived` callback never performs async work or UI dispatch
- Fail-closed host-key enforcement for SSH.NET and Plink fallback paths, including `HostKeyUnavailable` when a pinned gateway key cannot be resolved without falling back to PuTTY/Plink's cache
- Gateway-aware tunnel reuse identity (stable gateway IDs + normalized chain hash) prevents accidental sharing across overlapping private networks
- Multi-gateway tunnel chaining with circular dependency detection
- Dynamic tunnel port allocation with bounded retry on bind-race (`AddressAlreadyInUse`)
- Tunnel ref-counting (shared tunnels survive individual session close)
- Terminal resize via SSH window-change request (public `ShellStream.ChangeWindowSize` API, no reflection)
- X11 forwarding with automatic X server detection and auto-start
- 29 structured failure codes with localized error messages
- Typed mid-session security events distinguish host-key attacks from ordinary disconnects and suppress SSH auto-reconnect on MITM signals
- Auto-reconnect overlay on unexpected disconnect (SSH and RDP)

### VNC
- Embedded VNC viewer via noVNC + WebView2
- WebSocket-to-TCP proxy for seamless integration
- Clipboard sync, scaling modes, view-only mode
- WebView2 portable deployment (bundled Fixed Version Runtime for isolated servers)

### Telnet
- Raw TCP Telnet with IAC negotiation
- NAWS (window size) subnegotiation support
- Rendered in the same xterm.js terminal as SSH
- Username/password authentication, plaintext security warning

### SFTP Browser
- Embedded file browser panel with directory tree and file list
- **Auto-open companion**: opens automatically alongside an SSH session as a vertical split (gated by a setting). Reconnecting the SFTP pane is pane-scoped, so the sibling SSH terminal and its scrollback are preserved; an SSH keepalive keeps idle SFTP sessions alive
- **Follow the SSH directory** (opt-in): the companion can track the SSH terminal's current working directory via the OSC 7 escape sequence, with a per-pane toggle (best-effort; inert on shells that do not emit OSC 7)
- Dual edit modes: integrated AvalonEdit editor OR external editor with auto-upload on save (transient upload failures are retried)
- **"Browse as root" sudo mode**: toggle in toolbar enables `sudo ls -la` directory listing via SSH exec channel - browse any directory regardless of SFTP user permissions
- **Full sudo fallback** on all operations: upload (`sudo tee`), download (`sudo cat`), edit, chmod, rename, delete, mkdir - triggered only on typed permission-denied exceptions
- Sudo edit sessions cache the pinned host-key verifier, detect mid-edit host-key rotation, track upload tasks, and clean temporary files even when the privileged write fails
- Drag-and-drop upload and download
- **Cut / Copy / Paste / Duplicate** with non-destructive collision handling on SFTP; the server-side copy (host-key-pinned) reserves the destination exclusively and is journaled as a single operation. Copying on the server is refused whenever no such reservation is possible: always over FTP, and over SFTP when the server-side command cannot be used
- **Recursive folder upload** and drop-into-folder targeting
- **Cross-pane paste** between two file browsers, same server or across servers
- **Paste from Explorer**: upload files/folders from the Windows clipboard (CF_HDROP) into the current directory
- SFTP/FTP file operations journaled (download / upload / mkdir / delete / rename / copy)
- Chmod dialog, path bookmarks, filename filter

### FTP Browser
- FTP/FTPS client backed by FluentFTP async APIs
- Reuses the full SFTP browser UI via `IRemoteBrowser` interface
- Configurable passive mode and SSL/TLS (FTPS) support
- Cleartext FTP connections with credentials surface a non-blocking warning in the session status area
- Host and port validation matches SSH/SFTP handlers
- Directory listing parsing is delegated to FluentFTP across server variants

### Citrix
- StoreBrowse integration for published applications and desktops
- SSO (Kerberos) authentication support
- Embedded session tabs with the same UX as RDP

### WinRM (PowerShell Remoting)
- Remote PowerShell sessions over native WinRM / PS-Remoting - direct connections need no SSH
- Embedded interactive terminal: a local `pwsh.exe` (PowerShell 7+, auto-detected) or `powershell.exe` (5.1 fallback) is hosted in a ConPTY and runs `Enter-PSSession`, reusing the Local Shell terminal view
- HTTP (5985) and HTTPS (5986) transports with a `Use SSL` toggle and a dynamic default port; full TLS certificate validation by default
- Two identity modes: an explicit stored credential (DPAPI-encrypted) or the current Windows identity (Kerberos SSO, no stored secret)
- `Negotiate` authentication (Kerberos with NTLM fallback)
- Credential mode injects the password via a self-deleting, ACL-restricted bootstrap script - no plaintext on disk or in PowerShell history
- Transport pre-flight check (TCP reachability + TLS handshake) surfaces clear, localized errors before the session launches
- Optional SSH gateway routing: a WinRM session can be tunneled through an SSH bastion, like RDP and SSH. Over the tunnel the WinRM transport is HTTP only (NTLM authentication); direct WinRM connections are unaffected.
- Known gateway limitation: some environments accept the tunnel at TCP level but the target (or an intermediate device) closes the WinRM HTTP exchange - diagnosed as environmental, not a Heimdall fault. See [docs/winrm-gateway-12152-diagnostic.md](docs/winrm-gateway-12152-diagnostic.md).

### Local Shell
- Embedded PowerShell, cmd, bash, or custom shell via ConPTY
- Configurable elevation mode: **Auto** (gsudo `--direct` with fallback), **gsudo**, **Runas** (external window), or **None**
- Compatible with endpoint privilege managers (AdminByRequest, CyberArk, BeyondTrust) via `--direct` flag and runas fallback
- Side-by-side local file browser with cd synchronization and embedded AvalonEdit editor
- HEIMDALL_* environment variables injected for contextual scripting

### Multi-Exec Broadcast
- Send keystrokes simultaneously to multiple active SSH sessions
- Visual indicators: colored border and BROADCAST badge on receiving terminals

### Quick Connect (Ctrl+K)
- Command palette for ad-hoc connections without saving a server profile
- Supports `user@host:port` format with optional protocol prefix
- Bare IP or hostname input auto-proposes SSH and RDP connections, with the order biased by per-host history (last-used protocol on top)
- Also used as split session server and tool picker (fuzzy search scales to any inventory size)
- Renders as a WPF `Popup` (own HWND) so it displays above RDP/VNC ActiveX surfaces
- Empty-query view bubbles servers whose host appears in the recent-connections log to the top of the suggestion list, so reconnecting to a recently used machine is one Ctrl+K + Enter away
- Runs Command Library snippets directly from the palette: drill into a snippet to see its description, risk and platform badges, notes, examples (copy or send) and documentation links; fill template parameters inline with a live command preview; matched search terms are highlighted in the results, and the detail is screen-reader and keyboard accessible (v2026.061602)

### Tunnel Panel
- Retractable side panel showing all active SSH tunnels
- Real-time status, local port, remote target, and gateway chain display
- Tunnel chain visualization in session tab headers (via GatewayA -> GatewayB)
- Dynamic port allocation with ref-counting for shared tunnels

### Server Health Monitoring
- Collapsible sidebar panel showing CPU, RAM, and Disk usage
- Multiplexed SSH channel (doesn't interfere with the terminal session)
- Polls `top`, `free`, `df` every 15 seconds with async SSH commands and progress bars

### Macro Recorder
- Record terminal input with timing between keystrokes
- Save macros to JSON files, replay with original delays
- **Expect steps**: a macro can wait for an expected output pattern before sending the next step (wait-for-pattern), authored in a dedicated macro editor
- Accessible from session context menu

### Network Scanner
- ICMP ping sweep on CIDR subnets (Ctrl+Shift+N)
- TCP port probe on responsive hosts (SSH, RDP, VNC, HTTP, HTTPS)
- One-click "Add to Sessions" for discovered hosts with auto-detected connection type

### Scheduled Tasks
- Daily or interval-based automatic connection scheduler
- Background timer with proper async dispatch and semaphore-guarded ticks

### External Tools
- Configurable tools in server context menu with inline edit panel
- 8 variable placeholders: `{Host}`, `{Port}`, `{User}`, `{ServerName}`, `{Protocol}`, `{KeyFile}`, `{Project}`, `{Gateway}`
- Run as Administrator, Run Hidden, Working Directory options with browse buttons
- Live command preview with resolved placeholders from selected server
- Test button to launch directly from Settings
- Binary existence validation on save (PATH + absolute path lookup)
- Configurable execution timeout (default 60s)
- Integrated into Ctrl+K command palette

### Quick File Server
- One-click HTTP file server with optional TFTP support, enabled from Settings > Advanced > File sharing, for transferring files to servers without SFTP (hardened servers, containers, network equipment)
- Displays ready-to-use `wget`/`curl` commands, adds the `tftp` command snippet only when TFTP is enabled, and auto-copies the share URL to clipboard
- HTTP: directory listing, MIME types, path traversal protection
- TFTP: RFC 1350 read-only implementation

### Built-in Sysops Toolbox (59 tools)

All tools open as session tabs (split with any session or tool, detach, reorder). Accessible via **dedicated Tools tab**, **Ctrl+K** palette, the **sidebar Tools tab** (Sessions/Tools toggle at the top of the left panel, **Ctrl+Shift+T**), or **"+" -> Add Tool** menu. The tabbed sidebar hosts the session `TreeView` and a full-height tool browser side by side - the Tools tab displays a collapsible `TreeView` of categories (Network, Security, Encoding, System, External) populated from `ToolRegistry`, with a filter box matching on name + aliases and an always-present **Favorites** section at the top. Right-click any sidebar tool leaf to pin or unpin it without launching the tool; the same persisted `FavoriteToolIds` feed both the sidebar Favorites section and the dedicated Tools tab, while favorites stay sorted alphabetically by localized display name and filtered like every other category. Tools can be saved in the session `TreeView` alongside real sessions. Centralized `ToolRegistry` with vector icons, categories, and command aliases. **Favorites** (pin/unpin with persistence) and **recently used** tools remain available on the dedicated Tools tab as well. Singleton behavior for context-free tools. Built-in help system with usage examples (? button). Dedicated detail panel for tools with descriptions. Password Generator supports saveable custom presets (JSON persistence), optional clipboard auto-clear, and 3 generation modes (Random, Syllable, Passphrase). Cross-tool navigation via right-click context menus (IP -> Port Scanner -> Cert Inspector). Network tools support scanning via SSH tunnel ("Route via" gateway selector). **First-launch onboarding** overlay with guided introduction.

| Category | Tools |
|----------|-------|
| **Network** | **Network Cartography** (ARP seeding + multi-probe discovery [reverse DNS, NetBIOS, TCP], ping sweep + OS fingerprinting, port scan, banner grab, HTTP/HTTPS header analysis, TLS cert inspection, NetBIOS NBSTAT probe, SNMPv2c query + enterprise OID classifier [Cisco/Juniper/Fortinet/Palo Alto/MikroTik/VMware], mDNS/Bonjour discovery, 300+ OUI MAC lookup, multi-source role classification, VLAN detection, MAC address + latency columns, Draw.io topology export, scan history/diff, remote subnet auto-detection via SSH gateway, **persistent Knowledge Base with TTL-based cache acceleration**, **tunnel scan with remote ping sweep + ARP discovery + parallel port probes**), **Ping Monitor** (continuous latency graph + gateway routing via SSH), DNS Lookup (custom server + via tunnel), SSL Cert Inspector (chain + TLS version + via tunnel), Port Scanner (progress + banner grab + via tunnel), Subnet Calculator (IPv4 + IPv6), IP Converter, HTTP Status Codes, Whois Lookup, Network Calculator (supernet + VLAN planner) |
| **Security** | Password Generator (crack time + history + saveable presets), SSH Key Generator (RSA + Ed25519), Hash Generator (SHA3 + progress), HMAC Generator, JWT Parser (HMAC signature verify), Certificate Generator (self-signed + CA/leaf), TOTP Generator (RFC 6238), **SecNumCloud Audit** (ANSSI v3.2 compliance across Network/Crypto/Access/Ops with CIDR auto-detection + gateway routing + HTML/CSV/Draw.io export), **Hacker Simulator** (Hollywood-style terminal with 25 scenarios, playlists, vintage CRT mode) |
| **Encoding** | Base64 Encoder (URL-safe RFC 4648), URL Encoder, JSON Formatter (error position), Regex Tester (match highlighting), Text Diff (word-level), Text Case Converter (8 formats) |
| **System** | Chmod Calculator, Crontab Builder, DateTime Converter (timezone + relative), UUID Generator (v4 + v7), Hosts File Editor, SSH Config Generator, Log Viewer / Tail (regex filter), Cron Job Manager (crontab + Windows tasks), Service Status Dashboard, **Notes** (Obsidian-style Markdown editor with Milkdown WYSIWYG + Dracula theme, collapsible sidebar with persisted width, right-click formatting menu, localized templates EN/FR, TreeView file explorer, accent-insensitive `[[wiki-links]]`, tags, drag-and-drop, Confluence/HTML export), **Diagram Editor** (draw.io embedded offline, New/Open/Save/Export PNG) |

### Session Management
- Tabbed sessions with drag-to-reorder
- Tab detach to floating window (Chrome-style drag-out or context menu)
- **Recursive N-pane split**: up to 8 panes per tab in any layout (2x2, L-shape, 3 side-by-side, etc.)
- Split any pane further: right-click -> "Split..." -> Horizontal | Vertical, or Command Palette
- **Merge existing tab**: right-click -> "Merge with..." -> session or tool -> Horizontal | Vertical (reparents live connection without reconnecting)
- **Mixed session + tool splits**: freely combine connections and built-in tools in the same tab (e.g., SSH terminal left + Network Cartography right)
- **Drag-to-split**: drag a tab onto the content area of another tab to merge (orientation auto-detected from drop position)
- Swap panes, toggle orientation (Ctrl+Shift+O), detach any pane to floating window
- Unsplit restores panes as independent tabs with all metadata preserved
- **Dedicated SplitService**: split/merge orchestration in a dedicated service with per-session cancellation tokens (deferred CTS dispose), CancellationToken propagated to all protocol handlers, centralized `CloseAllPanes` tab teardown
- Per-pane disconnect overlay with Reconnect and Close buttons (accessible labels for screen readers)
- Pane-scoped failure disclosure for SSH and RDP with structured stage/code/detail diagnostics
- Loading overlay with spinner during pane connection
- **Minimum pane size enforcement**: 120x80px prevents splitter from collapsing panes to unusable size
- **Double-click splitter** resets ratio to 50/50; hover border on panes for better active pane feedback
- **Dynamic splitter cursor**: SizeNS for horizontal splits, SizeWE for vertical (updates on orientation toggle)
- Splitter ratio remembered per pane across tab switches; restored on merge from split layout history
- Split layout persistence (versioned JSON schema): previously paired servers suggested in Command Palette (all servers visible in split mode)
- **Per-session cancellation**: closing a tab cancels any in-progress split or reconnect operation gracefully (token propagated to SSH/RDP/VNC connection handlers)
- **Deferred state machine cleanup**: reconnect releases old tunnel/state only after new connection succeeds or definitively fails
- **Merge feedback**: status bar message when a busy tool blocks a merge operation
- Command Palette renders as a WPF `Popup` (own HWND) above RDP/VNC ActiveX surfaces
- **Bulk operations**: multi-select (Ctrl+Click, Shift+Click) -> right-click -> bulk connect, duplicate, delete, move to project/group, edit port, edit username, edit password (DPAPI-encrypted, with confirmation dialog), and **bulk edit SSH gateway** (credential-free, four explicit outcomes - preserve / force direct / inherit / specific - skipping protocols that do not support gateways)
- **Inline rename**: F2 or Ctrl+E renames sessions and folders directly in the tree, without opening a dialog, and stays correct under virtualization
- **Structured tree filters**: filter the session tree by protocol/type, favorite, and connected state, combined with the debounced text search through one versioned pass over stable nodes; a colored dot on the filter button signals an active filter
- **Virtualized session tree**: recycling virtualization keeps the tree smooth at thousands of sessions and "Expand all" no longer freezes; long names show in full with an automatic horizontal scrollbar when they exceed the pane, and search now expands folders that contain a match and highlights the matched text
- Global session logging (opt-in): per-session text transcripts for SSH / Telnet / Local Shell and a connect/disconnect event log (reason + duration) for RDP / VNC / Citrix, restrictive ACLs and size rollover; **per-profile tri-state override** (force on / off / inherit) in the server dialog
- Connection history log (JSONL with auto-rotation)
- Screenshot capture to clipboard (Ctrl+Shift+S)

### User Interface
- Runtime theme switching across **17 ThemeForge themes** (Drakul default, plus Dracula, Striga, Cinder, Bracken, Tarn, Mortis, Slate, Magellan, Voivode, Carmilla, Whitby, Vesper, Wormwood, Sconce, Parchment, Folio - grouped Root / Dark / Light / Alt), plus a **9-tone accent selector** (Default, Blue, Cyan, Green, Orange, Pink, Purple, Red, Yellow). Powered by the public `ThemeForge.Theme` NuGet package (nuget.org) through the `HeimdallThemeService` wrapper; a `HeimdallThemeBridge` ResourceDictionary re-expresses Heimdall's app brushes on ThemeForge color slots and is re-merged on every theme swap so converters and code-built panels recolor live
- 1,870+ lines of WPF control styles shared across all themes, reactive to runtime theme swaps (`DynamicResource` everywhere; `MultiBinding` + `ThemeRevision` trigger for brush-resolving converters; `SetResourceReference` in code-behind panels)
- Design System with 45 tokens: typography (10 sizes, min 11px), spacing (8 tokens incl. asymmetric), button padding (4 roles), input padding, corner radius, opacity, icon sizes, monospace font family, micro-animations (150ms/250ms)
- WCAG AA compliant: all foreground/background pairs verified at 4.5:1+ contrast ratio, scrollbar thumb 4.2:1+ across every ThemeForge theme
- FocusIndicatorBrush for keyboard navigation accessibility on all button styles
- Unified two-tier icon system: vector geometries (`Geo.*`) for domain icons + Segoe MDL2 for UI chrome
- Localized tooltips on all icon-only buttons; AutomationProperties.Name on all interactive controls via i18n
- 19 themed control styles with complete hover/pressed/focused/disabled states
- 5 terminal color schemes: Dracula, Solarized Dark, Monokai, Nord, Default - Dracula also applied to Notes Milkdown editor
- Configurable terminal font family and size
- Settings panel with 6 left-navigation sub-tabs (General, Terminal, SSH & SFTP, RDP, Security, Advanced); the RDP sub-tab now exposes the previously hidden `RdpResolutionPresets` array as an editable multi-line list and the `RdpDialogAdvancedDefault` flag as a checkbox
- Server Dialog: a two-step flow - protocol card selection, then a **four-tab editor** (General / Options / Network / Info) with a persistent protocol badge in the header, per-tab error badges, per-protocol tab visibility, and validation focus that jumps to the first invalid field across tabs. The window is freely resizable with native mouse-wheel scrolling. The RDP Options tab keeps a four-chip mini-toc (Display / Audio / Devices / Performance); its Display section offers a `Common resolutions` ComboBox to pre-fill Width/Height in Fixed mode and a dedicated `Enable multi-monitor` toggle
- TreeView hierarchy: Project > Group > Server with category-colored tool icons and status dots
- Command Palette (Ctrl+K): protocol icons, status dots, endpoint hints, Ctrl+Enter for split
- Connection inheritance: group-level defaults for gateway, SSH username, key path
- Empty states: tool views show guidance before first query, welcome panel with import CTA
- Built-in help button ("?") on all 59 tools with localized usage instructions
- Tab busy indicator: pulsing accent dot on tabs during long-running tool operations
- **Tabbed sidebar** (Sessions / Tools): full-height tool browser with collapsible categories, an always-present Favorites section, single-click launch, and right-click favorite management without accidental tool launch. Ctrl+Shift+T toggles the active sidebar tab
- **Sidebar sessions UX**: two-row toolbar with full-width search above icon-only actions, 320px default width, and smart long-name truncation that preserves the session identifier while ellipsizing trailing parenthesized suffixes
- Fullscreen mode (F11), toggle sidebar (Ctrl+B), filter (Ctrl+F)
- **First-launch onboarding**: 3-step guided introduction overlay with skip/next/get started
- Bilingual interface: English and French (6,054 i18n keys per language, exact EN/FR parity)
- Declarative i18n: `{loc:Translate Key}` WPF markup extension with runtime language switching
- WCAG 2.1 AA accessibility: AutomationProperties.Name on all interactive controls via `{loc:Translate}`, LiveSetting="Polite" on dynamic outputs, keyboard focus indicators, disabled state tooltips, a live-region filter result count announced on change, and keyboard-focused folder rows as reliable Shift+F10 / Apps context-menu targets with a localized automation name

### Security
- DPAPI encryption + HMAC-SHA256 integrity via unified `CredentialProtector`
- **Master-password vault (optional, encryption at rest)**: an Argon2id key-encryption key wraps a data-encryption key (DEK/KEK); `CredentialProtector` is version-aware and backward compatible with legacy DPAPI blobs. Enable / change / disable from Settings, with a migration engine that re-wraps existing credentials and atomic (temp-then-rename) config writes
- **Startup unlock gate and workspace lock**: when a master password is set, the workspace is locked at launch until unlocked; manual lock plus idle auto-lock (`AutoLockIdleMinutes`) with an overlay, locked sessions masked rather than disconnected (opt-in disconnect-on-lock)
- **Windows Hello vault unlock**: a TPM-bound Hello key (sign -> HKDF -> AES-GCM, composed with DPAPI) optionally unlocks the vault by biometric/PIN instead of the master password; fail-closed with a master-password fallback, gated on TPM presence
- External credential provider: choose a command-line provider (preset templates for KeePassXC, KeePass2/KPScript, Bitwarden CLI, 1Password CLI, pass - with an optional unlock secret fed via stdin, a separate username command, a per-profile vault entry name, and a "first line only" output mode) or the native Windows Credential Manager; the provider is built through `ICredentialProviderFactory` and covers SSH (embedded sessions and the Plink fallback), SFTP, RDP, WinRM in Credential mode, FTP and VNC. Telnet, Citrix and the external PuTTY mode do not consume credentials from this provider - database path browser, placeholder hints, test button with inline feedback; KeePassXC also supports key-file authentication (.keyx/.key), alone or combined with a master password
- Optional Windows Hello gate: requires a biometric/PIN verification before stored credentials are used on connect (single and bulk), fail-closed with a configurable grace window
- PBKDF2-SHA256 PIN hashing (100,000 iterations) with lockout mechanics
- Windows ACL enforcement on config directories, log files, and temp files
- Centralized `InputValidator` security utilities: `EscapeShellArg()`, `EscapeForDoubleQuotedString()`, `ValidateDomain()`, `SanitizeCsvCell()`, `IsShellTarget()` - shell injection prevention (CWE-78) on all SSH tunnel and tool `CreateCommand()` calls, context-aware placeholder sanitization (strict for shell targets, relaxed for regular executables), CSV formula injection prevention on all exporters, CRLF sanitization on HTTP headers
- HTTP/TFTP directory traversal prevention with sibling-prefix check
- WebSocket Origin validation on VNC proxy (CSWSH prevention)
- Atomic file creation with restrictive ACL for sensitive temp files (TOCTOU-safe)
- Privileged (sudo) SFTP edit/upload stream their content over the privileged channel into a root-owned temp directory created beside the target and commit with an atomic, symlink-refusing rename - no attacker-writable `/tmp` staging, and the privileged read path holds a no-follow descriptor
- Citrix launch tokens are decrypted only at the launch boundary and are never written to any log or exception; a locked vault fails closed rather than launching
- Path traversal prevention on local file browser rename/new folder operations
- ConfigManager concurrency-safe writes via SemaphoreSlim
- WebView2 Content Security Policy (CSP) and navigation blocking
- Embedded WebView documents (Milkdown notes editor, VNC view) enforce an exact scheme/host/port/path origin for accepted `postMessage` traffic and navigation - a substring-style trust match is not used, so a foreign document cannot post into or navigate the host
- Pageant IPC hardened with self-only DACL on the shared file mapping, cryptographic random suffix in the mapping name (64 bits of entropy), trusted Pageant process whitelist before any agent traffic, and empty-agent preflight check
- Constant-time host-key fingerprint comparison via `CryptographicOperations.FixedTimeEquals`
- `known_hosts` import bounded by per-line (64 KB) and per-file (50 MB) caps with streaming `StreamReader`; malformed input degrades to diagnostics rather than UI exceptions
- Plink stderr drain redacts password / passphrase / token / bearer assignments and `-pw` / `-pwfile` flags; the drain task is joined before `Process.Kill()` so background readers cannot outlive their pipe
- XXE protection: DtdProcessing.Prohibit on all XML importers (mRemoteNG, RDCMan, Citrix cache)
- Plink password file: atomic ACL creation on Windows, mode 0600 on Unix (no fallback)
- Wake-on-LAN via UDP magic packet (right-click context menu)
- User-confirmed SSH host key first-use and mismatch handling across SSH.NET and Plink fallback paths, with interactive decisions resolved in a pre-authentication probe rather than inside SSH.NET's `HostKeyReceived` callback
- Centralized `HostKeyTrustService` with per-entry metadata (first seen, last seen, algorithm, source) - production paths require host-key verifier dependencies at compile time; no silent auto-accept fallbacks in release code
- Compile-time non-null host-key dependencies on SSH/SFTP/tunnel/sudo entry points; `RejectingHostKeyVerifier` is the safe fail-closed verifier and `AutoAcceptHostKeyVerifier` is test-only
- Mid-session host-key mismatch events propagate through `SshSessionSecurityEvent` / `HostKeyRotatedDuringUpload` instead of being collapsed into generic disconnect text
- `Settings > SSH & SFTP > Trusted host keys` sub-panel: dense auditable grid of every trusted host key with source provenance, import from `~/.ssh/known_hosts`, export to it, explicit per-row conflict resolution ("Keep existing" default), and copy/remove row actions
- Opt-in `known_hosts` synchronization at startup so Heimdall, OpenSSH CLI, and Plink share one view of trust
- Plink fallback sessions enforce pinned `-hostkey` fingerprints from the shared trust store and refuse to launch when Heimdall cannot resolve a pinned/probed fingerprint safely
- Credential broker autofill requires an RDP host-title match before injecting passwords
- Known security limitations and threat model notes are tracked in [docs/SECURITY.md](docs/SECURITY.md)
- Session-scoped CredMan entries with deterministic cleanup

### Import and Migration
- Migration from Heimdall v1 (DPAPI-encrypted credentials preserved)
- Import from JSON, MobaXterm (.mxtsessions / .ini), mRemoteNG (.xml), RDCMan (.rdg), and .rdp files
- JSON session imports contain server profiles only. SSH gateway definitions live in `settings.json`
  (`AppSettings.SshGateways`), so test fixtures such as `Heimdall-TestEnv` must inject the
  matching gateway into the exact runtime build configuration before tunneled sessions can resolve it.

### In-App Updates
- Startup update check against the latest GitHub release (spaced out, configurable in Settings) with a non-blocking banner offering **View release**, **Later**, and **Skip this version**
- **Updates** section in Settings with a manual **Check now** button and the current installed version
- Every release publishes a `SHA256SUMS.txt` asset so installer integrity can be verified before running
- The downloaded installer is staged under a restrictive-ACL directory and held under a deny-write handle from verification through launch; its SHA-256 (plus Authenticode when the installer is signed) is re-checked immediately before the elevated launch, so a swap between verification and execution is refused

---

## Download

Two editions are available. Both include the full .NET runtime and require **no prior .NET installation**.

| Edition | Size | WebView2 | Best for |
|---------|------|----------|----------|
| **Standard** | ~106 MB (installer) / ~159 MB (zip) | Requires Edge or WebView2 Evergreen Runtime (pre-installed on Windows 10/11) | Most users - workstations, laptops, any PC with Edge |
| **Self-Contained** | ~267 MB (installer) / ~380 MB (zip) | Bundled (WebView2 Fixed Version Runtime included) | Air-gapped servers, restricted environments without Edge, isolated VMs |

Both editions are available as **installer** (.exe with shortcuts, upgrade detection, uninstaller) or **zip** (extract and run, no installation required).

> **Which should I choose?** If you're unsure, pick **Standard**. It works on any Windows 10/11 PC with Edge installed (which is virtually all of them). Choose **Self-Contained** only if your target machine has no internet access and no Edge browser.

---

## Requirements

| Dependency | Minimum Version | Notes |
|---|---|---|
| Windows | 10 / 11 | Both editions |
| Edge or WebView2 Runtime | Evergreen | Standard edition only (pre-installed on Windows 10/11) |
| PuTTY (Plink + Pageant) | 0.81+ | Optional, for Pageant-only SSH key auth |
| X11 Server | VcXsrv / Xming / X410 | Optional, for X11 forwarding |
| Citrix Workspace App | Latest | Optional, for Citrix connections |

---

## Quick Start

Download the latest release from the [Releases](../../releases) page. Run the installer or extract the zip and launch `Heimdall.exe`.

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| F1 | Keyboard shortcut help |
| Ctrl+K | Quick Connect palette (servers, tools, external tools) |
| Ctrl+Shift+T | Toggle sidebar tab (Sessions / Tools) |
| Ctrl+N | Add new server |
| Ctrl+E | Edit selected server |
| Ctrl+Del | Delete selected server |
| Ctrl+Shift+N | Network Scanner |
| Ctrl+Shift+S | Screenshot to clipboard |
| Ctrl+Shift+O | Toggle split orientation (H/V) |
| Ctrl+B | Toggle sidebar |
| Ctrl+F | Focus search/filter |
| Ctrl+Enter | Execute action (JSON Formatter, etc.) |
| F11 | Toggle fullscreen |
| Escape | Exit fullscreen / close palette |
| F2 | Rename (SFTP/local file browser) |
| F5 | Refresh directory |

---

## Build from Source

**Batch shortcuts** (double-click):

| File | Action |
|------|--------|
| `Run.bat` | Build + launch in Debug mode |
| `Test.bat` | Run all tests |
| `Build.bat` | Debug build with publish |
| `Release.bat` | Full release: build, test, package, commit, push, GitHub release |

**PowerShell** (advanced):

```bash
# Quick dev cycle
dotnet run --project src/Heimdall.App

# Debug build (auto-increments version)
powershell -File Build.ps1

# Release build - both editions + installers
powershell -File Build.ps1 -Mode Release

# Release + publish to GitHub
powershell -File Build.ps1 -Mode Release -Publish

# Dry-run: full build + simulated publish (no git/gh changes)
powershell -File Build.ps1 -Mode Release -DryRun

# Force a specific version
powershell -File Build.ps1 -Mode Release -Version 2026.033101

# Standard edition only / skip tests
powershell -File Build.ps1 -Mode Release -Variant Standard
powershell -File Build.ps1 -SkipTests
```

Build output goes to `Dist/debug/` or `Dist/release/` with versioned folder names.

| Edition | Size (zip / installer) | WebView2 |
|---------|----------------------|----------|
| **Standard** | ~159 MB / ~106 MB | Requires system Edge/WebView2 |
| **Self-Contained** | ~380 MB / ~267 MB | Bundled Fixed Version Runtime |

Release mode also produces Inno Setup `.exe` installers in `Dist/installers/` with desktop/start menu shortcuts and upgrade detection.

---

## Technology Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 (C# 14) |
| UI Framework | WPF (MVVM via CommunityToolkit.Mvvm) |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| SSH/SFTP | SSH.NET 2025.1.0 |
| Terminal Rendering | WebView2 + xterm.js |
| VNC | noVNC (HTML5 VNC client in WebView2) |
| Code Editor | AvalonEdit |
| RDP | ActiveX MsTscAx (WindowsFormsHost) |
| Citrix | StoreBrowse CLI integration |
| Crypto | System.Security.Cryptography.ProtectedData (DPAPI) |
| Testing | xUnit (7,900+ passing tests across 8 projects) |
| Built-in Tools | 59 sysops tools (Ctrl+K -> `tools` or Ctrl+Shift+T) |
| Serialization | System.Text.Json |

---

## Architecture

The solution is split into 9 source projects with clear dependency boundaries:

```
Heimdall.App          WPF application (MVVM, views, themes, services)
  +-- Heimdall.Core     Models, security (DPAPI, HMAC, PIN), config, state machine, i18n
  +-- Heimdall.Ssh      SSH engine (SSH.NET), tunnels, Pageant IPC, TOFU, failure classifier
  +-- Heimdall.Rdp      RDP + Citrix engine (ActiveX MsTscAx), credential autofill, StoreBrowse
  +-- Heimdall.Sftp     SFTP/FTP browser (SSH.NET + FluentFTP), remote file editing
  +-- Heimdall.Terminal  Terminal sessions (pipe mode, ConPTY, Telnet), smart paste guard
  +-- TwinShell.*        Terminal emulator core, persistence, and infrastructure components
```

Test projects: `Heimdall.Core.Tests`, `Heimdall.Ssh.Tests`, `Heimdall.Rdp.Tests`, `Heimdall.Sftp.Tests`, `Heimdall.Terminal.Tests`, `Heimdall.App.Tests`, `Heimdall.App.UiTests`, `TwinShell.Infrastructure.Tests`.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed design decisions and data flow diagrams.

---

## License

Copyright 2026 Julien Bombled

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for details.
