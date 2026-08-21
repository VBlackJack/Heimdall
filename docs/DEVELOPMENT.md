<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Development Guide

*Also available in French: [fr/DEVELOPMENT.md](fr/DEVELOPMENT.md).*

Shared development reference for Heimdall. This file is versioned project
documentation; keep machine-local paths, credentials, editor preferences, and
temporary workflow notes out of it.

## Project Overview

Heimdall is a .NET 10 WPF Windows connection manager for RDP, SSH, SFTP,
VNC, Telnet, FTP, Citrix, and local shell sessions. The app uses MVVM via
CommunityToolkit.Mvvm, SSH.NET 2025.1.0 for SSH/SFTP, WebView2 for embedded
terminals and VNC, and DPAPI + HMAC-SHA256 for local credential protection.

The solution file is `Heimdall.slnx`. Source projects live under `src/` and
test projects live under `tests/`.

## Build, Test, And Run

Common commands:

```powershell
dotnet build
dotnet test
dotnet run --project src/Heimdall.App
powershell -File Build.ps1
powershell -File Build.ps1 -Mode Release
powershell -File Build.ps1 -Mode Release -Publish
powershell -File Build.ps1 -Mode Release -DryRun
powershell -File Build.ps1 -Mode Release -Version 2026.033101
powershell -File Build.ps1 -SkipTests
```

Batch shortcuts are also available:

- `Run.bat` - build and launch
- `Test.bat` - run tests
- `Build.bat` - debug build
- `Release.bat` - release pipeline

The current full-suite baseline is:

```powershell
dotnet test Heimdall.slnx --no-build
```

Expected result: 5,453 passing tests and 6 known skipped WPF
`ThemeServiceTests` that require a live `Application` context.

Per-project TRX summaries can hide skipped tests or report smaller totals.
Use the aggregated solution command when checking the real baseline.

## Build Script Gotchas

`Build.ps1 -SkipTests` skips the test pass and also skips building test
assemblies. Running `dotnet test --no-build` immediately afterwards can run
stale binaries or fail to find tests.

When iterating on tests after a skipped build, run:

```powershell
dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false
dotnet test Heimdall.slnx --no-build
```

The build script updates `src/Heimdall.App/Heimdall.App.csproj` version
metadata before building. If you run `Build.ps1` during a development pass and
do not intend to keep the auto-bumped version, restore the project values
before committing.

## Version Conventions

`Heimdall.App.csproj` carries:

- `<Version>1.0.MMDD.xx</Version>`
- `<InformationalVersion>YYYY.MMDDxx</InformationalVersion>`

`Build.ps1` auto-increments `xx` based on existing distribution folders, the
current project version, and recent GitHub release tags when available. The
`-Version` flag overrides auto-increment and expects the `YYYY.MMDDxx` format.

Build outputs are ignored and written under:

- `Dist/debug/`
- `Dist/release/`
- `Dist/installers/`

## Code Standards

- License: Apache 2.0, author "Julien Bombled" on new files.
- Code, comments, and versioned docs use English.
- Nullable reference types are enabled project-wide.
- Warnings are errors via `Directory.Build.props`.
- Prefer async APIs and do not block the UI thread.
- Keep WPF logic in ViewModels; code-behind should remain minimal event
  wiring unless the platform integration requires otherwise.
- User-facing strings belong in `locales/en.json` and `locales/fr.json`.
- Shell arguments must go through `InputValidator.EscapeShellArg()` or a
  structured argument API such as `ProcessStartInfo.ArgumentList`.
- Prefer existing project patterns and helper APIs over new abstractions.

## Documentation Conventions

Three categories, and a file belongs to exactly one of them.

**Private and working documents** live in `local/` at the repository root, which is gitignored.
Agent briefs, audits in progress, resumption notes, diagnostic captures, operation logs. Nothing
there is published.

**Public documents** are versioned and exist in two languages. English stays at its usual location
(`README.md`, `docs/*.md`); French mirrors it (`README.fr.md`, `docs/fr/*.md`). Each version links
to the other under its title. A change to one is not finished until the other says the same thing.

**Published release notes** are English by default, in `docs/release-notes/v<version>.md`.

`docs/CHANGELOG.md` is English only. It is a chronological engineering record of the same genre as
the release notes, and translating it would impose permanent double maintenance with no reader.

### Characters

ASCII only, with one exception: French accented letters producible on an AZERTY keyboard, in French
documents.

Forbidden everywhere, English included:

| Instead of | Write |
|---|---|
| em dash, en dash | `-` |
| curly quotes and apostrophes | `"` and `'` |
| single-character ellipsis | `...` |
| non-breaking or thin space | a plain space |
| Unicode arrows | `->` and `<->` |
| box-drawing characters | `+--` and `\--` |
| Unicode multiplication, minus, greater-or-equal | `x`, `-`, `>=` |

These characters break in Windows terminals, in diffs, in console code pages and in CI logs. It is
the same reason the locale files use `\uXXXX` escapes.

`scripts/NotesTypographyGuard.ps1` is authoritative for the banned set and is run by
`Build.ps1 -Mode Release` against the release notes, fail-closed.

## Localization And I18n

Locale files currently contain 5,489 leaf keys each, and CI enforces EN/FR key
parity.

Key conventions:

- CamelCase keys by context, for example `ErrorPlinkNotFound` or
  `BtnConnect`.
- New XAML should prefer `{loc:Translate Key}` for live locale updates.
- Legacy imperative `ApplyLocalization()` paths still exist; migration is
  incremental.
- Tool descriptions may use `ToolDescriptor.DescriptionKey`; otherwise the
  default convention is `ToolDesc{Id}`.

## Namespace Conventions

Before creating a new namespace under `Heimdall.Core.*`, check the chosen name
against .NET top-level namespaces such as `System`, `IO`, `Net`, `Threading`,
`Linq`, `Text`, `Collections`, `Diagnostics`, `Security`, `Runtime`, and
`Globalization`.

If the name collides, choose a disambiguated project-specific namespace and
match the folder path to it. Example: use `Heimdall.Core.SystemInfo` with
`src/Heimdall.Core/SystemInfo/`, not `Heimdall.Core.System`.

## Where To Find Things

- `src/Heimdall.Core/` - shared models, config, security, localization,
  state machine, discovery, validation.
- `src/Heimdall.Ssh/` - SSH.NET integration, Pageant, Plink fallback,
  host-key trust, tunnel manager.
- `src/Heimdall.Sftp/` - SFTP/FTP `IRemoteBrowser` implementations and
  remote file editing.
- `src/Heimdall.Rdp/` - RDP ActiveX host and credential autofill helpers.
- `src/Heimdall.Terminal/` - local shell, pipe mode, Telnet, terminal
  abstractions.
- `src/Heimdall.App/` - WPF composition root, services, handlers, view models,
  views, themes, localization.
- `src/TwinShell.*` - command library persistence, core models, and
  integration.
- `tests/` - xUnit test projects matching the source areas.
- `docs/ARCHITECTURE.md` - high-level architecture and design decisions.
- `docs/SECURITY.md` - threat model, security controls, limitations, and
  security test references.
- `docs/TOOLS.md` - built-in tool catalog, tool registry, external provider,
  SecNumCloud, and Command Library reference.
- `docs/TROUBLESHOOTING.md` - known development/runtime issues and fixes.

## CI Expectations

CI should enforce:

- restore/build/test with warnings as errors;
- full solution test suite;
- JSON locale key parity;
- formatting checks;
- informational vulnerable package scan.

Manual dependency review command:

```powershell
dotnet list Heimdall.slnx package --vulnerable --include-transitive
```

Vulnerability scans can include advisories without an immediate upgrade path;
review results before turning them into release blockers.
