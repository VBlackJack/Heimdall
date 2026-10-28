<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Tools Reference

*Also available in French: [fr/TOOLS.md](fr/TOOLS.md).*

Developer reference for Heimdall's built-in tools, external tool
providers, and tool-hosting infrastructure.

## Overview

Heimdall ships 58 built-in tools registered by `ToolRegistry`. The raw
`Entry(` token appears more often in `ToolRegistry.cs` because it also matches
the internal `ToolEntry` record, the dynamic external-tool registration path,
and the private helper method. The effective built-in count is the set of
registry entries in the constructor:

- Network: 17
- Security: 15
- Encoding: 6
- System: 14
- External native tools: 6

External provider tools are detected at runtime and appended to the registry
after startup scans. They are not part of the 58 built-in count.

## Built-In Tool Catalog

Names below are sourced from `locales/en.json`; IDs are the stable registry
keys used by command palette, tabs, split panes, and tool lookup.

### Network

| ID | Name |
|---|---|
| `PING` | Ping Monitor |
| `DNS` | DNS Lookup |
| `CERT` | Certificate Inspector |
| `PORTSCAN` | Port Scanner |
| `SUBNET` | Subnet Calculator |
| `IPCONV` | IP Address Converter |
| `HTTP` | HTTP Status Codes |
| `WHOIS` | WHOIS Lookup |
| `HTTPHEADERS` | HTTP Header Analyzer |
| `BANNER` | Banner Grabber |
| `TCPTRACE` | Traceroute |
| `SNMPWALK` | SNMP Walker |
| `ARPMON` | ARP Monitor |
| `FWTEST` | Firewall Tester |
| `NETMAP` | Network Cartography |
| `NETCALC` | Network Calculator |
| `TCPPING` | TCP Ping |

### Security

| ID | Name |
|---|---|
| `HASH` | Hash Generator |
| `HMAC` | HMAC Generator |
| `PASSWORD` | Password Generator |
| `SSHKEY` | SSH Key Generator |
| `CERTGEN` | Certificate Generator |
| `JWT` | JWT Parser |
| `TOTP` | TOTP Generator |
| `PWDAUDIT` | Password Auditor |
| `SSHAUDIT` | SSH Key Auditor |
| `TLSAUDIT` | TLS Auditor |
| `DNSSEC` | DNS Security Checker |
| `SMBENUM` | SMB Enumerator |
| `DEFAULTCREDS` | Default Credential Scanner |
| `CVELOOKUP` | CVE Lookup |
| `SECNUMCLOUD` | SecNumCloud Audit |

### Encoding

| ID | Name |
|---|---|
| `BASE64` | Base64 Encoder / Decoder |
| `URLENC` | URL Encoder / Decoder |
| `JSON` | JSON Formatter |
| `REGEX` | Regex Tester |
| `DIFF` | Text Diff |
| `TEXTCASE` | Text Case Converter |

### System

| ID | Name |
|---|---|
| `CHMOD` | Chmod Calculator |
| `DATETIME` | DateTime Converter |
| `UUID` | UUID Generator |
| `ULID` | ULID Generator |
| `CRONTAB` | Crontab Builder |
| `LOGVIEW` | Log Viewer |
| `HOSTS` | Hosts File Editor |
| `SSHCONFIG` | SSH Config Generator |
| `CRONJOB` | Cron Job Manager |
| `SERVICES` | Service Status Dashboard |
| `NOTES` | Notes |
| `DIAGRAM` | Diagram Editor |
| `CMDLIB` | Command Library |
| `PRIVLAUNCH` | Privilege Launcher |

### External Native

| ID | Name |
|---|---|
| `WOL` | Wake-on-LAN |
| `OPENPORTS` | Open Ports |
| `NETIF` | Network Interfaces |
| `ROUTES` | Route Table |
| `DNSBATCH` | DNS Batch Resolver |
| `WIFI` | WiFi Networks |

## Tool Infrastructure

`ToolRegistry` is the single source of truth for built-in tool metadata and
factory creation. Adding a built-in tool means adding one registry entry plus
the corresponding `IToolView` implementation.

`ToolDescriptor` carries the stable metadata:

- `Id`: short lookup key such as `PING`
- `Category`: `Network`, `Security`, `Encoding`, `System`, or `External`
- `CategoryLabelKey`: i18n key for the category header
- `LabelKey`: i18n key for the display name
- `LabelWithArgKey`: optional i18n key for command-palette suggestions with a
  target argument
- `CommandPrefixes`: command palette aliases
- `IsNetworkTool`: whether standalone launch should prompt for a target
- `IconResourceKey`: XAML geometry resource key
- `DescriptionKey`: optional explicit description key; when null, the UI uses
  the `ToolDesc{Id}` convention

`IToolView` is the runtime contract for tool views. It exposes:

- `Initialize(ToolContext?, LocalizationManager?)`
- `CanClose()`
- `Dispose()`

`ToolsTabPopulationService` builds both the full Tools tab and the sidebar
Tools tree from the registry. It owns favorites, recents, search filtering,
category grouping, tool cards, and context inheritance.

`SidebarToolCategoryViewModel` and `SidebarToolItemViewModel` back the sidebar
Tools tree. Search uses precomputed lower-case searchable text (`name +
aliases`) so filtering does not allocate repeatedly.

Icons use geometry resources such as `Geo.Tool.PortScanner` and category brush
resources such as `ToolNetworkBrush`. `ToolRegistry.GetGeometryKey()` and
`ToolRegistry.GetCategoryBrushKey()` exist for XAML converters that do not
have DI access.

Gateway routing is view-level, not purely registry-level. `ToolGatewayConnector`
creates an SSH client for remote command execution through an SSH gateway and
requires a pinned gateway host key before connecting. Current views with a
`CmbRouteVia` route selector are:

- `BannerGrabberView`
- `CertInspectorView`
- `DefaultCredentialView`
- `DnsLookupView`
- `DnsSecurityView`
- `FirewallTesterView`
- `HttpHeaderAnalyzerView`
- `NetworkCartographyView`
- `PingToolView`
- `PortScannerView`
- `SecNumCloudAuditView`
- `SmbEnumeratorView`
- `SnmpWalkerView`
- `TcpTracerouteView`
- `TlsAuditView`
- `WhoisLookupView`

Tools that shell out through a gateway must keep per-probe timeouts and
explicit shell selection. For `/dev/tcp` checks, use `timeout ... bash -c ...`
so filtered ports do not leave remote shell processes running after the SSH
command channel is killed.

## External Tool Providers

Runtime-detected third-party tools use `IExternalToolProvider`,
`ExternalToolProviderService`, `ExternalToolInfo`, and
`ExternalToolWrapperView`.

Current provider implementations:

- `SysinternalsToolProvider`: scans Sysinternals tools such as PsExec, PsInfo,
  PsList, PsService, PsPing, Tcpvcon, Autorunsc, Sigcheck, AccessChk, Handle,
  ListDLLs, Disk Usage, and Whois.
- `NirSoftToolProvider`: scans NirSoft tools such as PingInfoView, CurrPorts,
  NetworkLatencyView, WakeMeOnLan, FastResolver, CountryTraceRoute,
  DNSDataView, NetResView, NetworkInterfacesView, WifiInfoView,
  Wireless Network Watcher, FullEventLogView, TaskSchedulerView, USBDeview,
  BlueScreenView, and ProduKey.
- `NanaRunToolProvider`: scans CLI-capable NanaRun project tools currently
  represented by MinSudo and SynthRdp.

`ExternalToolProviderService.ScanAll()` aggregates providers and applies
user-configured search paths from `AppSettings` (`SysinternalsPath`,
`NirSoftPath`, `NanaRunPath`). Detected tools are registered into
`ToolRegistry` as dynamic `ToolDescriptor` entries using IDs in the format:

```text
EXT:PROVIDER:TOOLID
```

`ExternalToolWrapperView` launches the detected executable, applies `{Host}`
and `{Port}` placeholders from `ToolContext`, captures stdout/stderr, and
displays output as text or structured data depending on `OutputFormat`.
Tools with `RequiresElevation` show an upfront elevation warning.

Licensing rule: third-party binaries are detected and wrapped only. Do not
redistribute NirSoft, Sysinternals, NanaRun, or other third-party tools inside
Heimdall packages unless their license explicitly allows it.

## SecNumCloud Audit Engine

`SecNumCloudAuditEngine` orchestrates the `SECNUMCLOUD` tool. It is aligned to
four SecNumCloud-oriented chapters:

- Network
- Cryptography
- Access Control
- Operations

The engine currently runs 15 checks across discovery, network exposure, TLS,
SSH, SMB, SNMP, HTTP headers, DNS records, default credentials, CVE banner
matching, and operational posture.

Progress is exposed through events:

- `PhaseProgress`
- `StatusChanged`
- `CheckCompleted`

Exports:

- `HtmlReportGenerator` creates a standalone HTML report.
- `CsvEvidenceExporter` exports audit evidence rows.
- `DrawIoExporter` in `Heimdall.Core.Discovery` creates Draw.io topology
  diagrams for discovery/cartography data.

Localization is injected into `SecNumCloudAuditEngine` through a
`Func<string, string>` delegate. Keep audit output strings localizable and do
not hardcode user-facing text in the engine.

## Diagram Editor

The `DIAGRAM` tool embeds the draw.io editor in a WebView2 surface. The editor
is vendored under `src/Heimdall.App/Assets/drawio/` and never reaches the
network: it is loaded with `offline=1&stealth=1`, and the vendored `index.html`
carries a Content-Security-Policy that keeps every request on the local virtual
host `heimdall-drawio.local`.

### Three ways in

- **From the Tools list**, with an empty canvas, or from File > New from
  template. Templates ship under `Assets/diagram-templates/`.
- **From Network Cartography**, with the Edit Diagram button. The scan is turned
  into a draw.io document and handed to the editor as unsaved content. It has no
  file behind it, so the first save asks where to put it.
- **From a recent diagram**, under File > Recent.

### A scan is drawn as a topology

`DrawIoExporter` turns a `NetworkScanSnapshot` into a drawing that says what
talks to what, not only what exists:

- One swimlane per classified role, plus one for hosts with no open port.
- One segment node per detected VLAN, or one for the scanned subnet when no VLAN
  was detected. A segment shows its gateway. VLAN membership and gateways come
  from `VlanInfo`, which already carries both.
- An edge from every host to its segment. A host that is itself the gateway is
  joined the other way round, from the segment outwards, so it reads as the
  centre.
- Shapes that follow the role: a cylinder for a database, a hexagon for routing
  equipment, a cube for a hypervisor. These are shapes built into mxGraph, never
  stencils, because the vendored bundle is pruned and a missing stencil renders
  as an empty box. `DrawIoExporterTopologyTests` fails if a style names anything
  else.

### Host nodes open sessions

A host whose open ports offer a session Heimdall can open is written as a
`UserObject` carrying a link such as `heimdall://session/RDP/10.0.0.5:3389`.
Which session a host gets is decided by `SessionProtocolChoice`, the same
decision the cartography context menu uses.

Selecting such a node and choosing Open session from the canvas context menu
connects. draw.io's editing mode follows a cell's link on no mouse gesture, so
Heimdall gives the action its own entry rather than relying on a click.

The link format is `SessionLaunchRequest`, which both writes and reads it.
Everything in a link is untrusted, because a .drawio file can come from
anywhere and the link is an instruction to reach a machine: only a protocol from
a fixed list is accepted, the host must be an address or a plain host name, and
the port must be in range. A valid request is then named to the user, protocol
and destination, before anything connects.

The session itself is opened through `ToolContext.OpenSessionAction`, wired by
`MainViewModel` to `ConnectAdHocSessionAsync`. No profile is saved.

### The cartography menu keeps a host, or reaches it

Right-clicking a row in Network Cartography offers the same two things about the
host it found. Open session connects without saving anything, through
`ToolContext.OpenSessionAction`. Add to servers keeps it, through
`ToolContext.AddServerAction`, which opens the ordinary Add Server dialog filled in
with the address, the name the scan resolved and the session the open ports call
for. Nothing is written until the dialog is accepted.

Both entries take the protocol from `SessionProtocolChoice`, so they cannot
disagree with each other or with the diagram's links. A host offering no session at
all is still worth recording, so Add to servers falls back to SSH on its default
port and lets the dialog say otherwise, while Open session is simply not offered.

Add to servers used to ask the shell for a tool named `__ADD_SERVER__`, which no
registry knew; opening an unknown tool id does not throw, it puts a tab reading
`Tool: <id>` on screen. `ToolRouteReachabilityGuardTests` now reads every tool id
written as a literal at a call site and refuses one the registry cannot build.

### Merging a later scan

File > Merge a scan reads a freshly exported scan into the diagram on screen
instead of replacing it. `DiagramScanMerge` applies three rules:

1. A cell both documents have keeps its geometry and takes the scan's label,
   style and link. The layout is the user's decision, the data is the scan's.
2. A cell the user drew is never touched. Heimdall's own cells are recognised by
   their identifier prefix; anything else is the user's.
3. A Heimdall cell the scan no longer reports is kept and greyed out, because a
   host that has gone is a finding, not a mistake to erase.

This works because cell identifiers are derived from the host address rather
than from a counter, so the same host keeps the same identifier across exports.

### Saving, drafts and recovery

The header shows the current file name, followed by `(modified)` while the
editor holds changes that are not on disk. Save writes to the current file;
Save As always asks for a new one. Ctrl+S inside the editor does the same thing
as the Save button. Closing the tab, or replacing the document, asks what to do
with unsaved work.

Every change the editor reports is also written as a draft under the
application's data directory, keyed by a hash of the document path. A draft is
removed as soon as the document reaches its real file, so a draft that is still
there on open means the previous run did not finish, and the editor offers it
back. The state itself lives in `DiagramDocumentState`, which answers what a
save must do next and whether there is unsaved work.

### Language, theme and WebView2

The host page is opened as `heimdall-host.html?lang=<en|fr>&theme=<dark|light>`
and forwards both to draw.io, so the editor follows Heimdall rather than the
operating system locale. Browser accelerator keys are off: F5 would reload the
host page and drop the diagram. Without a WebView2 runtime, the tool shows a
fallback message instead of an editor.

The assets are about 44 MB across 2300 files and are excluded from Debug builds
(`Heimdall.App.csproj`), so the tool reports "Release builds only" when run from
a Debug output.

### Upgrading the vendored editor

Run `scripts/Vendor-DrawIo.ps1` against an upstream `src/main/webapp` checkout.
It copies the shipped subset, re-applies Heimdall's two edits (the
`heimdallDrawioApp` hook in `js/bootstrap.js`, the Content-Security-Policy in
`index.html`) and rewrites the version rows of `VENDORED.md` and both
`THIRD-PARTY-NOTICES` files. `DrawioAssetGuardTests` then checks that those
manifests match the version the bundle reports. Smoke-test the tool end to end
before committing: the guards read source, not behaviour.

## Command Library And TwinShell

The `CMDLIB` tool embeds the TwinShell command library inside Heimdall.

Primary components:

- `TwinShellBootstrapper`: registers TwinShell persistence, repositories,
  services, localization bridge, settings bridge, and Git sync services in the
  Heimdall DI container.
- `CommandLibraryView`: WPF tool view.
- `CommandLibraryViewModel` and partial classes: filtering, command actions,
  history, favorites, generation, and UI state.
- TwinShell projects: `TwinShell.Core`, `TwinShell.Persistence`, and
  `TwinShell.Infrastructure`.

Persistence uses SQLite through `TwinShellDbContext`. The shared database path
is under the user's local application data in a `TwinShell` directory.

Seed data lives in `data/seed/actions/`. Files whose names start with `_` are
ignored; the current seed set contains 527 action JSON files. Their text is an
English pivot with other languages under `translations`: French carries every field,
Spanish carries the title and the description only (the notes and the example
descriptions fall back to English, an example being keyed by its exact command),
and `CommandLibraryContentLanguageParityTests` keeps every block in step with the
pivot. Their categories are ASCII keys that the view resolves to localized labels. At startup the seed
directory is hashed; when the hash differs from the one recorded in the
`SeedState` table, the system actions are refreshed in one transaction.

The optional workstation debloat pack lives in `data/packs/`, in the import
format, and is not seeded. Its text is English with `fr` and `es` blocks, read by
the import door like the seeds.

Key user flows:

- fuzzy search with platform, category, and risk filters
- parameterized command generation
- favorites and command history
- import/export in TwinShell-compatible JSON
- Git sync through `IGitSyncService`
- Send to Terminal through `ToolContext.SendCommandAction`
- runbooks: ordered library steps or literal commands, stored as TwinShell
  command batches and run one step at a time into the session's terminal

System seed actions are protected: edit/delete actions are hidden for
non-user-created commands, and import merge skips system actions.

## Where To Find Things

- `src/Heimdall.App/Services/ToolRegistry.cs`: built-in registry and dynamic
  external registration.
- `src/Heimdall.Core/Models/ToolDescriptor.cs`: tool metadata record.
- `src/Heimdall.Core/Models/IToolView.cs`: tool view contract.
- `src/Heimdall.App/Services/ToolsTabPopulationService.cs`: full Tools tab
  and sidebar Tools population.
- `src/Heimdall.App/ViewModels/SidebarToolsViewModels.cs`: sidebar Tools
  tree view models.
- `src/Heimdall.App/Services/ToolGatewayConnector.cs`: SSH gateway routing
  helper for tools.
- `src/Heimdall.Core/Configuration/ExternalToolProvider.cs`: external provider
  model and interface.
- `src/Heimdall.Core/Configuration/SysinternalsToolProvider.cs`: Sysinternals
  provider.
- `src/Heimdall.Core/Configuration/NirSoftToolProvider.cs`: NirSoft provider.
- `src/Heimdall.Core/Configuration/NanaRunToolProvider.cs`: NanaRun provider.
- `src/Heimdall.App/Services/ExternalToolProviderService.cs`: provider
  aggregation and scan orchestration.
- `src/Heimdall.App/Views/Tools/ExternalToolWrapperView.xaml.cs`: generic
  external tool host.
- `src/Heimdall.App/Services/SecNumCloudAuditEngine.cs`: SecNumCloud audit
  orchestration.
- `src/Heimdall.App/Services/HtmlReportGenerator.cs`: HTML audit report.
- `src/Heimdall.App/Services/CsvEvidenceExporter.cs`: CSV evidence export.
- `src/Heimdall.Core/Discovery/DrawIoExporter.cs`: Draw.io topology export.
- `src/Heimdall.App/Services/TwinShellBootstrapper.cs`: TwinShell DI and seed
  initialization.
- `src/Heimdall.App/Views/Tools/CommandLibraryView.xaml.cs`: Command Library
  tool view.
- `src/Heimdall.App/ViewModels/CommandLibraryViewModel*.cs`: Command Library
  view-model slices; `.Runbooks.cs` holds the Runbooks panel and its store
  operations, `.RunbookRun.cs` starts a run and wires it to the terminal.
- `src/Heimdall.App/ViewModels/CommandLibrary/RunbookRunViewModel.cs`: one
  runbook run, step by step, with its statuses and history records.
- `src/Heimdall.App/ViewModels/CommandLibrary/RunbookVariables.cs`: the
  `{{name}}` substitution in runbook step values.
- `src/Heimdall.App/ViewModels/Dialogs/RunbookDialogViewModel.cs` and
  `src/Heimdall.App/Views/Dialogs/RunbookDialog.xaml`: the runbook editor.
- `src/TwinShell.Core/Models/CommandBatch.cs` and `BatchCommandItem.cs`: the
  runbook model, stored by `BatchService` and `BatchRepository`.
- `data/seed/actions/`: command library seed actions.
- `data/packs/`: optional command library packs, imported by the operator.
