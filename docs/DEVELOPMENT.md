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

## Branch Naming

A branch is named after **what the change is**, never after who or what produced it.

```
<type>/<short-kebab-description>
```

`<type>` is the same vocabulary the commit subjects already use, so a branch and its commits
agree: `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `ci`, `build`, `style`, `chore`,
`release`.

```
feat/rdp-hardware-rendering-setting
fix/bulk-connect-summary-truth
docs/rdp-memory-measured
test/credential-protector-isolation
```

Do **not** prefix a branch with the name of a tool or an assistant. `claude/...` and `codex/...`
were used until 2026-08-25 and are gone: they said who typed, which nobody needs, and hid what
changed, which everybody does. A reader listing thirty branches should be able to tell what each
one is for without opening it.

There is deliberately no `add/`. It means the same thing as `feat/`, and two prefixes for one
meaning is how a convention rots.

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

French documents use French accents, the ones an AZERTY keyboard produces.

Two kinds of character are refused, in both languages, and for the same reason: a plain ASCII
character says the same thing and survives a Windows terminal, a diff, a console code page and a
CI log.

| Refused | Write |
|---|---|
| em dash, en dash, unicode hyphen, minus sign | `-` |
| curly quotes, low quotes, **French guillemets** | `"` |
| curly apostrophe | `'` |
| single-character ellipsis | `...` |
| no-break, narrow and thin spaces, zero-width space, byte order mark | a plain space, or nothing |
| **oe and ae ligatures** | `oe`, `ae` |

The guillemets and the ligatures are on that list because the AZERTY layout does not produce
them: writing one means reaching for a substitute the reader's terminal may not render.
`scripts/NotesTypographyGuard.ps1` lists them as deliberately absent, with the same remedies.

**Arrows, box-drawing characters, ballot boxes and emoji are welcome.** A dependency graph, a
directory tree and a checklist read better with them, and they carry something no pair of ASCII
characters carries as clearly. That is a deliberate exception rather than an oversight.

`DocumentationTypographyGuardTests` enforces this over `README*.md`, `SECURITY*.md` and all of
`docs/`, **recursively**. The recursion is asserted separately, because a hand-run sweep of
`docs/*.md` once passed while two subdirectories went unexamined.

**Release notes are held to a stricter rule**: ASCII and AZERTY characters only, arrows included
in the refusal. `scripts/NotesTypographyGuard.ps1` is authoritative there and runs fail-closed
inside `Build.ps1 -Mode Release`.

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
