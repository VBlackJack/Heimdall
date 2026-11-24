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

| ID | Name | Pack |
|---|---|---|
| `PING` | Ping Monitor | Network diagnostics |
| `DNS` | DNS Lookup | Network diagnostics |
| `CERT` | Certificate Inspector | Network diagnostics |
| `PORTSCAN` | Port Scanner | Audit and reconnaissance |
| `SUBNET` | Subnet Calculator | Essentials |
| `IPCONV` | IP Address Converter | Essentials |
| `HTTP` | HTTP Status Codes | Essentials |
| `WHOIS` | WHOIS Lookup | Network diagnostics |
| `HTTPHEADERS` | HTTP Header Analyzer | Network diagnostics |
| `BANNER` | Banner Grabber | Audit and reconnaissance |
| `TCPTRACE` | Traceroute | Network diagnostics |
| `SNMPWALK` | SNMP Walker | Audit and reconnaissance |
| `ARPMON` | ARP Monitor | Network diagnostics |
| `FWTEST` | Firewall Tester | Audit and reconnaissance |
| `NETMAP` | Network Cartography | Audit and reconnaissance |
| `NETCALC` | Network Calculator | Essentials |
| `TCPPING` | TCP Ping | Network diagnostics |

### Security

| ID | Name | Pack |
|---|---|---|
| `HASH` | Hash Generator | Essentials |
| `HMAC` | HMAC Generator | Essentials |
| `PASSWORD` | Password Generator | Essentials |
| `SSHKEY` | SSH Key Generator | Essentials |
| `CERTGEN` | Certificate Generator | Essentials |
| `JWT` | JWT Parser | Essentials |
| `TOTP` | TOTP Generator | Essentials |
| `PWDAUDIT` | Password Auditor | Essentials |
| `SSHAUDIT` | SSH Key Auditor | Essentials |
| `TLSAUDIT` | TLS Auditor | Audit and reconnaissance |
| `DNSSEC` | DNS Security Checker | Network diagnostics |
| `SMBENUM` | SMB Enumerator | Audit and reconnaissance |
| `DEFAULTCREDS` | Default Credential Scanner | Audit and reconnaissance |
| `CVELOOKUP` | CVE Lookup | Essentials |
| `SECNUMCLOUD` | SecNumCloud Audit | Audit and reconnaissance |

### Encoding

| ID | Name | Pack |
|---|---|---|
| `BASE64` | Base64 Encoder / Decoder | Essentials |
| `URLENC` | URL Encoder / Decoder | Essentials |
| `JSON` | JSON Formatter | Essentials |
| `REGEX` | Regex Tester | Essentials |
| `DIFF` | Text Diff | Essentials |
| `TEXTCASE` | Text Case Converter | Essentials |

### System

| ID | Name | Pack |
|---|---|---|
| `CHMOD` | Chmod Calculator | Essentials |
| `DATETIME` | DateTime Converter | Essentials |
| `UUID` | UUID Generator | Essentials |
| `ULID` | ULID Generator | Essentials |
| `CRONTAB` | Crontab Builder | Essentials |
| `LOGVIEW` | Log Viewer | Administration |
| `HOSTS` | Hosts File Editor | Administration |
| `SSHCONFIG` | SSH Config Generator | Essentials |
| `CRONJOB` | Cron Job Manager | Administration |
| `SERVICES` | Service Status Dashboard | Administration |
| `NOTES` | Notes | Core (permanent) |
| `DIAGRAM` | Diagram Editor | Administration |
| `CMDLIB` | Command Library | Core (permanent) |
| `PRIVLAUNCH` | Privilege Launcher | Audit and reconnaissance |

### External Native

| ID | Name | Pack |
|---|---|---|
| `WOL` | Wake-on-LAN | Network diagnostics |
| `OPENPORTS` | Open Ports | Network diagnostics |
| `NETIF` | Network Interfaces | Network diagnostics |
| `ROUTES` | Route Table | Network diagnostics |
| `DNSBATCH` | DNS Batch Resolver | Network diagnostics |
| `WIFI` | WiFi Networks | Network diagnostics |

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
- `Pack`: the `ToolPack` the tool belongs to (`Core`, `Essentials`,
  `NetworkDiagnostics`, `Administration`, `Audit` or `Recipes`); null for
  detected external tools, which belong to no pack

The Pack column of the catalog tables above shows the pack of each built-in
tool. A tool whose pack is turned off in Settings, or forbidden by the machine
policy, is hidden from every listing and refused when something tries to open
it; the registry itself keeps the full catalogue. See
[Tool packs](FEATURES.md#tool-packs) for the user side,
[Tool Architecture](ARCHITECTURE.md#tool-architecture) for the mechanism and
[SECURITY.md](SECURITY.md) for the machine policy and its limits.

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

## Recipes

A recipe is one text file that describes a tool: a program that is already on
the computer, the fields the user fills in, the arguments given to the program
and the way its output is shown. Importing the file adds the tool to Heimdall,
next to the built-in ones. The short version is in
[FEATURES.md](FEATURES.md#recipes); what the rules below do and do not protect
against is in [SECURITY.md](SECURITY.md#recipes).

### What a recipe is, and what it is not

- It is one JSON file, in UTF-8 (a byte order mark is accepted), named
  `*.heimdall-recipe.json`, at most 64 KiB.
- It runs a program that is already on the computer. It ships no script and no
  binary, and nothing is downloaded for it.
- It never runs through a shell. The program is started directly, and each
  token of the argument list reaches it as one separate argument: nothing is
  split, quoted or joined on the way.
- It is not signed and not reviewed. The author written in the file is shown as
  unverified wherever the tool appears.
- It is as dangerous as the program it names and the arguments it gives. The
  rules below narrow what a recipe can ask for; they do not make one safe. The
  import preview is the real barrier: read it, and import only what you trust.

### Using a recipe

**Turn the pack on.** The Recipes pack is off by default, on a fresh install
and on an install that already existed (a one-time step at the first start of
this version lists it among the turned-off packs, and never turns it off again
once you have turned it on). The pack counts as off until that step has run or
you have turned the switch on and saved, so a start where the step could not run
(a settings file written by a newer Heimdall is never written to) does not leave
it on. The switch is in
`Settings > Advanced > Tools & integrations > Tool packs` and carries a warning
line. When the organisation forbids the pack by policy, the switch is off and
disabled and import is refused (see [SECURITY.md](SECURITY.md#recipes)).

A settings file imported with **Import settings** never turns the pack on by
itself. A file exported by an earlier version leaves it as it is. A file exported
by this version turns it on only when it comes from an installation where the pack
was on; the confirmation then shows a line of its own, "Turns the Recipes pack
on", among the lines to check first.

**Import.** In `Settings > Advanced > Tools & integrations > Recipes`, choose
**Import a recipe...** and pick the file. Import is possible while the pack is
off: the recipe is stored, and the confirmation says it will not appear in the
Tools tab until the pack is on. Heimdall then:

1. refuses the file, listing every problem with its line and column, when it is
   not a valid recipe (see "What is refused" below);
2. resolves the program and refuses the file when that program may not run;
3. shows a preview and waits for a confirmation. Nothing is written before it.

**Read the preview.** It shows, in this order: a warning that the recipe was
written by someone else and cannot be verified; for a replacement, what
changes; the program that will run, as the full path found on this computer;
every argument, one per line between brackets, exactly as written in the file,
with a nested list marked as one that is kept or dropped as a whole; the name,
the author (marked unverified), the id, the version and the description; every
field with its type, whether it is required, its default, its choices, its
pattern and its maximum length, and the arguments a switch adds when it is on;
every translation of the recipe (the language, the name, the description and the
field labels), since the tool shows it instead of the English text when the
interface is in that language; whether the recipe reads the host and the port of
the selected session; how the output is shown; and the timeout. A control or invisible character is written
as `\uXXXX`. In the program, the arguments and the defaults, every character
outside printable ASCII is written as `\uXXXX` too, so a look-alike letter
cannot pass for the ASCII one.

**Where the tool appears.** In the Tools tab under a Recipes heading, in the
sidebar Tools tree and in the command palette, marked "Unverified recipe by
{author}" on the card, the row and the tab title. Open it, fill the fields and
choose **Run**; **Stop** ends a run under way. The output is shown as text or as
a table (see `output`). When the recipe keeps standard error apart, it is shown
under "Error output". The status line says whether the run succeeded, failed
with an exit code, timed out, was cancelled or could not be started.

**What happens at each run.** Before anything starts, Heimdall checks that the
pack is on and the recipe is still imported, that a session is selected when
the recipe needs one, and every value typed (see "What is refused" below). It
then resolves the program again and compares the result with the path recorded
at import: when the program now resolves to another file, or no longer
resolves, the run is refused and the message asks you to import the recipe
again. It creates a fresh, empty working folder for the run under
`Heimdall-recipe-runs` in your temporary folder (in a subfolder named after the
data folder in use, so that two instances never share it), not in the data folder (so a
program does not start next to the settings of Heimdall), and
removes it afterwards (the leftovers of an earlier crash are removed at start).
The program starts without a window, with its standard input closed, with the
rights and the environment of Heimdall itself, placed in a Windows job object right
after it starts, which ends the processes it started when the run ends (a
process launched in that very first instant can escape it); when Windows refuses to create
the job, only a run that is stopped or times out kills the process tree. At most
1 MiB of output is kept per stream (2 MiB in all when standard error is kept
apart): the rest is read and discarded, and a line says the output was cut. The
run ends at the timeout of the recipe, and also when the pack is turned off or
becomes forbidden.

**Replacing or removing a recipe that has a tab open.** A tab keeps the recipe
it was opened with. Once that recipe is replaced, or removed and imported
again, the tab says "This recipe was replaced or removed after this tab was
opened. Close this tab and open the tool again." in its header, **Run** is
disabled, and a run refuses to start. Opening the tool never focuses such a
tab: it opens a new one.

**Replace.** Importing a file whose `id` is already imported is a replacement.
The preview is titled "Replace recipe" and states the old and the new version,
and whether the arguments, the fields and the program changed. Each of the
author, the name, the description, the translations, the output and its
encoding, the way standard error is kept, the success exit codes and the timeout
is also said on a line of its own, as "unchanged" or "changed", comparing the
parsed recipes and not the bytes.

**Export.** The export button of a recipe in the Settings list writes a copy of
the file exactly as it was imported. It refuses when the stored file no longer
matches what was imported.

**Remove.** The remove button asks first. The tool disappears, and anything
saved that uses it becomes unavailable.

**Where recipes are stored.** One file per recipe in the `recipes` folder of the
data folder (named after the id), plus an index that holds, for each recipe,
the SHA-256 of the imported bytes and the program path resolved at import. A
recipe whose file was changed, or that was dropped into the folder by hand, is
not loaded. At most 200 recipes can be imported. The Settings export does not
carry recipes.

**The folders recipes may run programs from.** A recipe may always run a program
from the Windows folder, Program Files and Program Files (x86). To allow another
folder, use **Add a folder...** in the same Recipes section of the Settings,
then save the settings and import the recipe again. A folder must be a full path
on a drive letter (`D:\Tools`) and not the root of a drive (`D:\`), at most 240
characters, with no network path, no device path, no `.` or `..` part, no `%`,
no wildcard and no part that ends with a dot or a space. At most 32 folders are
kept. Adding a folder that is, lies under or contains the Windows folder,
Program Files or Program Files (x86) is accepted, and Settings shows a notice:
programs under those folders stay checked. A settings file that carries this
list is flagged as sensitive in the import preview.

### Writing a recipe

A recipe is written by hand, as a text file, from the format below. There is no
editor in Heimdall. JSON has no comments (a comment or a trailing comma is a
syntax error), so the example is followed by a table that explains it.

```json
{
  "schemaVersion": 1,
  "id": "example.name-lookup",
  "version": "1.0.0",
  "author": "Example",
  "name": "Name lookup",
  "description": "Looks up a DNS record for a name, and can ask a DNS server of your choice.",
  "labels": {
    "fr": {
      "name": "Recherche de nom",
      "description": "Recherche un enregistrement DNS pour un nom, et peut interroger le serveur DNS de votre choix.",
      "fields": {
        "query": "Nom à rechercher",
        "record": "Type d'enregistrement",
        "server": "Serveur DNS (facultatif)",
        "details": "Afficher les détails"
      }
    }
  },
  "program": "nslookup.exe",
  "arguments": [
    "-type={in:record}",
    "{in:details}",
    "{in:query}",
    ["{in:server}"]
  ],
  "inputs": [
    { "name": "query", "type": "hostname", "label": "Name to look up", "required": true },
    { "name": "record", "type": "choice", "label": "Record type", "choices": ["A", "AAAA", "MX", "TXT"], "default": "A" },
    { "name": "server", "type": "hostname", "label": "DNS server (optional)" },
    { "name": "details", "type": "bool", "label": "Show details", "default": false, "whenTrue": ["-debug"] }
  ],
  "output": "text",
  "outputEncoding": "oem",
  "stderr": "merge",
  "successExitCodes": [0],
  "timeoutSeconds": 20,
  "icon": "search"
}
```

| Part of the example | What it does |
|---|---|
| `schemaVersion`, `id`, `version`, `author` | The format version, the identity `author.name` that is never renamed, a semantic version, and the author, which is displayed and always marked unverified |
| `name`, `description` | English texts, mandatory |
| `labels` | Optional translations of the name, the description and the field labels, keyed by language tag |
| `program` | A bare file name: Heimdall finds `nslookup.exe` itself, in Windows, Program Files or an allowed folder |
| `"-type={in:record}"` | One argument made of fixed text and a field. The value of `record` is chosen from a closed list, so it can never be read as an option |
| `"{in:details}"` | A switch placed alone as a whole token. When it is on, it adds the tokens of its `whenTrue` (here `-debug`); when it is off, it adds nothing |
| `"{in:query}"` | A required field that fills one whole argument. A typed value that starts with `-`, `/` or `@` is refused here |
| `["{in:server}"]` | A nested list. `server` is optional, so the whole list is left out when the field is empty |
| `inputs` | The fields, in the order they are shown |
| `output`, `outputEncoding`, `stderr` | The output is shown as text, decoded with the OEM code page of the console, with standard error merged into it |
| `successExitCodes`, `timeoutSeconds` | The exit codes that count as success, and the seconds after which the run is stopped |
| `icon` | One of the built-in icon keys |

#### Top-level properties

Property names are case-sensitive. A property the format does not define is
refused, and so is a property that appears twice in the same object.

| Property | Type | Required | Rule |
|---|---|---|---|
| `schemaVersion` | integer | yes | `1` is the only version this build reads. A higher number is refused, and the message names the version supported |
| `id` | string | yes | `author.name`: two parts separated by one dot, each 1 to 32 characters among lowercase letters, digits and hyphens, not starting or ending with a hyphen. It identifies the recipe (replacement, favorites, saved entries) |
| `version` | string | yes | A semantic version such as `1.2.3`, with optional pre-release and build parts; at most 64 characters. Display and replacement preview only |
| `author` | string | yes | At most 64 characters. Displayed, always as unverified |
| `name` | string | yes | At most 64 characters |
| `description` | string | yes | At most 500 characters |
| `labels` | object | no | Translations, see below. At most 16 languages |
| `program` | string | yes | A bare file name or a full path with a drive letter; at most 260 characters. See "Which program may run" |
| `arguments` | array | no | The argument list, see below. At most 64 tokens in all |
| `inputs` | array | no | The fields, see below. At most 16 |
| `output` | string | no | `text` (default), `csv` or `json`. The last two are shown as a table |
| `outputEncoding` | string | no | `utf8` (default) or `oem`, the OEM code page of the console, which most Windows console tools write in |
| `stderr` | string | no | `separate` (default): kept apart and shown as error output. `merge`: mixed into the output |
| `successExitCodes` | array of integers | no | `[0]` by default. 1 to 16 entries. An exit code outside the list is reported as a failure |
| `timeoutSeconds` | integer | no | 60 by default, from 1 to 600. The program is stopped when it elapses |
| `icon` | string | no | `tool` (default), `terminal`, `network`, `search`, `document`, `shield`, `clock` or `server` |

Every text that is shown (author, name, description, labels) must be non-empty
and must not hold a control character, an invisible formatting character (such
as the right-to-left override), a line or paragraph separator or an unpaired
surrogate.

#### Fields

Each entry of `inputs` is an object with these properties:

| Property | Type | Required | Rule |
|---|---|---|---|
| `name` | string | yes | A letter, then letters, digits or underscores; at most 32 characters; unique in the recipe. It is what `{in:name}` refers to |
| `type` | string | yes | `string`, `int`, `number`, `bool`, `hostname`, `ipaddress`, `path` or `choice` |
| `label` | string | yes | At most 64 characters |
| `required` | boolean | no | `false` by default. A default counts as a supplied value |
| `default` | string, number or boolean | no | A string (any type except `bool`), a number (`int` and `number` only) or `true` or `false` (`bool` only). At most 512 characters. Held to the same fixed-text rules as a token, and it must be valid for the type of its field |
| `pattern` | string | `string` only | A regular expression that the whole value must match; at most 256 characters. A value for which the match lasts more than 250 ms is refused |
| `maxLength` | integer | `string` only | From 1 to 1024. 256 when absent |
| `choices` | array of strings | `choice` only, then mandatory | 1 to 64 values, each at most 128 characters, no duplicates |
| `whenTrue` | array of strings | `bool` only, then mandatory | The tokens the switch adds when it is on. They follow the rules of argument tokens |

A property that is not allowed for the type of its field is refused. A `bool`
field must be placed in `arguments` as a whole token, or the file is refused.

What each type accepts at run time:

| Type | Accepts |
|---|---|
| `string` | Any text within `maxLength` that matches `pattern` when there is one |
| `int` | A 32-bit integer, with an optional sign, at most 64 characters |
| `number` | A finite decimal number written with a dot, an exponent allowed, at most 64 characters |
| `bool` | On or off. A blank value is off |
| `hostname` | A valid host name, with no space before or after |
| `ipaddress` | An IPv4 address or an IPv6 address (hexadecimal digits, colons and an optional dotted tail), with no zone id (`%`), no brackets and no space before or after |
| `path` | A local path. Refused: a network path, a device path (`\\?\`, `\\.\`), a path that starts with a single separator and no drive letter, a colon anywhere but right after the drive letter, a part that ends with a dot or a space, and a part that is a DOS device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`, `LPT1` to `LPT9`) |
| `choice` | One of the values of `choices`, compared exactly |

#### Arguments, placeholders and nested lists

`arguments` is an array. Each entry is either a string (a token) or an array of
strings (a nested list of tokens). One token is exactly one argument of the
program: Heimdall quotes nothing, splits nothing and joins nothing, so a value
that holds spaces or quotes still reaches the program as one argument.

Inside a token, a placeholder is replaced by a value:

| Written | Replaced by |
|---|---|
| `{in:name}` | The value of the field `name`, typed by the user or taken from its default |
| `{Host}` | The host of the selected session |
| `{Port}` | The port of the selected session |
| `{{` and `}}` | A literal `{` and a literal `}` |

The syntax is closed and case-sensitive: any other use of a brace, a placeholder
that names no field, and an empty token are refused at import. No environment
variable is ever expanded, and a `%` in fixed text is refused.

A field is *optional* when it is not a switch, not required and has no
non-empty default. An optional field cannot be used in a token on its own,
because the argument list would be ambiguous when it is empty; put the token in
a nested list instead. A nested list is dropped as a whole when any optional
field it uses is empty. `whenTrue` tokens follow the same rule. A required
field, or a field with a default, can be used anywhere.

A `bool` field is placed in `arguments` as a whole bare token (`"{in:details}"`)
and expands to its `whenTrue` tokens when it is on, to nothing when it is off.
It cannot be mixed with other text in a token, nor placed in a nested list.

At most 64 tokens in all (bare tokens, nested-list members and `whenTrue`
tokens together), each at most 512 characters.

#### Session values

`{Host}` and `{Port}` are the only values a recipe can read from a session, and
they come from the session selected in the tree. When a recipe uses either and
no session is selected, the tool says so and does not run: select a session,
then open the tool again. The host goes through the strictest host name
validation and the port must be a valid port number.

#### Translations

`labels` maps a language tag (such as `fr` or `fr-CA`: a first part of 2 to 8
letters, then parts of 1 to 8 letters or digits, 35 characters at most) to an
object with the optional properties `name`, `description` and `fields` (a map
from a field name to its translated label). Heimdall uses the entry whose tag is
the language of the interface, else the first entry with the same primary
language, else the English texts of the recipe. A translation of a field that
does not exist is refused.

#### Which program may run

`program` is a bare file name (`nslookup`, `ping.exe`; `.exe` is added when the
name has no dot) or a full path that starts with a drive letter. Heimdall
resolves it itself to a full path; the operating system search order is never
used. It must satisfy all of this:

- the file ends in `.exe` (no `.cmd`, `.bat`, `.com`, `.scr`);
- it exists, and once every link or junction is followed its final location is
  under the Windows folder, Program Files, Program Files (x86) or a folder
  allowed in Settings. A link that leads outside them is refused;
- under the Windows folder, Program Files or Program Files (x86), neither the
  file nor any folder above it (up to that root) may let a user who is not an
  administrator change it, and the permissions must be readable. The trust you
  give a folder allowed in Settings depends on where the program lies, not on
  where the folder lies: a program outside Windows and Program Files under such
  a folder is trusted as a whole and is not checked this way, while a program
  under Windows or Program Files is always checked, even when an allowed folder
  covers it;
- a bare name is looked for only in the entries of the `PATH` variable that are
  themselves under those folders, in `PATH` order; the first file found
  decides. The current folder and the folder of Heimdall are never searched;
- it is not a shell or an interpreter (see the list below);
- the original file name recorded in the version resource of the file is not on
  that list either.

The program text cannot contain `%` or a control character, and cannot be a
relative or a network path.

The list of refused names is a safety net against a common mistake, not a
boundary (see [SECURITY.md](SECURITY.md#recipes)). It holds: `cmd`,
`powershell`, `pwsh`, `bash`, `sh`, `zsh`, `wsl`, `cscript`, `wscript`, `mshta`,
`python`, `pythonw`, `py`, `node`, `perl`, `ruby`, `php`, `java`, `javaw`,
`msiexec`, `rundll32`, `regsvr32`, `regsvcs`, `regasm`, `installutil`,
`msbuild`, `certutil`, `bitsadmin`, `wmic`, `forfiles`, `schtasks`, `sc`, `reg`,
`net`, `net1`, `curl`, `ssh`, `scp`, `sftp`, `ftp`, `tftp`, `git`, `tar`,
`robocopy`, `xcopy`, `expand`, `extrac32`, `makecab`, `hh`, `control`,
`explorer`, `conhost`, `cmstp`, `odbcconf`, `pcalua`, `scriptrunner`, `at`,
`taskkill`, `msedge`, `chrome`, `firefox`, `brave`, `opera`,
`SyncAppvPublishingServer`, `wt`, `tttracer`, `pcwrun`, `esentutl`, `replace`,
`mmc`, `msdt`, `runas`, `dotnet`, `csc`, `vbc`, `jsc`, `ie4uinit` and
`presentationhost`, plus the shell targets the rest of Heimdall already refuses (such
as the script extensions `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`, `.wsf` and
`.hta`).

#### Schema version

`schemaVersion` is `1`. Any addition to the format bumps it, so a file written
for a newer format is never half-understood: a version higher than the one this
build reads is refused, with a message that gives both numbers and asks you to
update Heimdall. The list of icon keys is part of the format, and widening it
bumps the version.

### What is refused

**In the file, at import.** Heimdall reports every problem it finds (up to 50),
each with its line and column when it is known.

- The file is empty, larger than 64 KiB, not valid JSON or UTF-8, or nested
  deeper than 8 levels.
- A property is unknown or appears twice; a mandatory property is missing; a
  value has the wrong type; a text, a list or a number is outside its limits.
- The `id`, the `version`, a field name or a language tag is badly formed; two
  fields share a name; a translation names a field that does not exist; the icon
  is not in the list.
- A property is used on a type that does not allow it, a `choice` has no list or
  a default outside it, a `bool` has no `whenTrue` or is never placed in the
  arguments, a pattern does not compile, a default is not valid for its type.
- A placeholder is unknown or malformed, names no field, or an optional field
  stands alone in a token (put it in a nested list); a switch is mixed with
  other text.
- The fixed text (the program, a token, a `whenTrue` token, a default, a choice)
  holds two separators in a row (a double backslash, a double slash, or a
  backslash next to a slash in either order: a network path), `://` (a URL), the
  system path prefix (a separator, two question marks and a separator), a `%`
  (an environment variable), a character that looks like a plain one (a
  typographic quote, a long dash, a full-width letter, a no-break space, or any
  character the ANSI code page of the machine turns into an ASCII one), or
  starts with `@` (a response file). The entry at fault is named. The look-alike
  rule follows the ANSI code page of the machine (the one a program reads its
  command line in), so some accented capitals and ligatures can be refused in an
  argument on some machines and accepted on others.
- A choice or a default would be refused at every run by the rule a typed value
  meets (see below): it holds a control or look-alike character, it starts with
  `-`, `/`, `@`, a backslash or a space where its field starts an argument, or
  it joins the text next to it into a doubled separator. The file is refused at
  import. Keep the character in the fixed text of the token and list only the
  bare values: write `"-type={in:record}"` with the choices `A` and `MX`, not
  `"{in:record}"` with the choices `-type=A` and `-type=MX`.

**In the program, at import.** The program is not a `.exe`, is not a bare name
or a drive path, is not found, lies outside Windows, Program Files and the
allowed folders, is a link that leads outside them, or is a shell, an
interpreter or a proxy executable (by its file name or by the original name in
its version resource). The message names the program and, when the cause is the
folder, tells you to add the folder in Settings, save, and import again.

**A typed value, at each run.** A required field that is empty; a value with a
control or invisible character, or a look-alike one; a value that starts with
`-`, `/`, `@`, a backslash or a space where its placeholder starts the argument
(a leading `-` is how a value gets read as an option, a leading `@` as a
response file); a value that does not fit its
type (see the table above); a value that is too long, outside its choices or
that does not match its pattern; a value that, joined to the fixed text next to
it, would make a doubled separator (a network path or the start of a URL); and
any final argument that holds two separators in a row (in either form and in
any mix), the system path prefix or `://`, whatever its source. The field shows
the reason.

**The program has changed, at each run.** When the program no longer resolves to
the path recorded at import (another file now answers to the same name, or it
was moved, deleted or is no longer allowed), the run does not start. Import the
recipe again to accept the new location.

### Source files

- `src/Heimdall.Core/Recipes/`: the format (`RecipeFormat`, `RecipeLimits`,
  `RecipeIcons`), the strict parser and validator, the value rule, the
  expander, the program resolver, the list of refused programs and the runner.
- `src/Heimdall.App/Services/RecipeStore.cs`: storage and loading.
- `src/Heimdall.App/Services/RecipeImportService.cs` and
  `RecipeImportPreview.cs`: import, replacement and the preview text.
- `src/Heimdall.App/Services/RecipeToolRegistrar.cs`: registration of the
  imported recipes as tools.

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
