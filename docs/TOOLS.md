<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Tools Reference

Developer reference for Heimdall's built-in tools, external tool
providers, and tool-hosting infrastructure.

## Overview

Heimdall ships 59 built-in tools registered by `ToolRegistry`. The raw
`Entry(` token appears more often in `ToolRegistry.cs` because it also matches
the internal `ToolEntry` record, the dynamic external-tool registration path,
and the private helper method. The effective built-in count is the set of
registry entries in the constructor:

- Network: 17
- Security: 15
- Encoding: 6
- System: 15
- External native tools: 6

External provider tools are detected at runtime and appended to the registry
after startup scans. They are not part of the 59 built-in count.

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
| `HACKERSIM` | Hacker Simulator |
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
ignored; the current seed set contains 514 action JSON files.

Key user flows:

- fuzzy search with platform, category, and risk filters
- parameterized command generation
- favorites and command history
- import/export in TwinShell-compatible JSON
- Git sync through `IGitSyncService`
- Send to Terminal through `ToolContext.SendCommandAction`

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
  view-model slices.
- `data/seed/actions/`: command library seed actions.
