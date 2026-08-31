<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# Code signing policy

*Also available in French: [fr/CODE-SIGNING-POLICY.md](fr/CODE-SIGNING-POLICY.md).*

**Status: no Heimdall release is code-signed yet.** This policy states how signing
is governed once it is in place, and takes effect with the first signed release.
Until then, every published artifact is unsigned and Windows SmartScreen will warn
about it. Verify downloads against the `SHA256SUMS.txt` published with each
release.

## Scope

When signing is active, these artifacts are signed:

- `Heimdall.exe` and `Heimdall.dll`
- the Inno Setup installers (`Heimdall_<version>_Standard_Setup.exe` and
  `Heimdall_<version>_SelfContained_Setup.exe`)
- the WiX MSI package (`Heimdall_<version>.msi`)

Only artifacts built from this repository are signed. Third-party binaries that
Heimdall redistributes keep their publishers' own signatures and are never
re-signed; they are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Roles

Heimdall is maintained by a single person. That is stated plainly rather than
dressed up as a team, because it determines how much separation of duties is
actually available.

| Role | Who |
|---|---|
| Committer | Julien Bombled (GitHub `VBlackJack`) |
| Reviewer | Julien Bombled |
| Approver for signing | Julien Bombled |

The same person owns the source repository, controls the release process, and
approves each signing request. Commits appear under two git identities
(`Julien Bombled` and `VBlackJack`); both are this maintainer.

If additional maintainers join, this table is updated in the same commit that
grants them access.

## Account security

The maintainer uses multi-factor authentication on the GitHub account that owns
this repository and on the code signing service account. Signing credentials are
never stored in the repository, in CI configuration, or in any build artifact.

## How releases are built

Release builds are produced locally by `Build.ps1 -Mode Release -Publish`, not by
a hosted pipeline. The script builds the solution, publishes the Standard and
SelfContained variants, generates the Inno Setup installers and the WiX MSI,
computes `SHA256SUMS.txt` from the real build outputs, and creates the GitHub
release.

Continuous integration builds and tests every push and pull request, but it does
not produce released artifacts and holds no signing credentials.

## Approval

Every signing request is approved manually by the maintainer named above. No
automated process signs an artifact without that approval, and no artifact is
signed from a branch that has not been merged to `master`.

## Privacy

This policy concerns the signing of published artifacts. Heimdall itself sends no
telemetry and collects no personal data; what it stores locally is described in
the [User Guide](USER-GUIDE.md). Applying for a code signing service involves
sharing the maintainer's identity with that service for validation. No user data
is shared with it, because none is collected.

## Attribution

Once signing is active through the SignPath Foundation programme, this section
will read:

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by
> [SignPath Foundation](https://signpath.org).

It is written in the future tense on purpose: the attribution is not yet true, and
publishing it before acceptance would be a false claim.

## Reporting a problem

If you obtain a Heimdall artifact whose signature is missing, broken, or does not
match this policy, open an issue at
https://github.com/VBlackJack/Heimdall/issues. If you believe a signed artifact
was tampered with, say so in the issue rather than only by email, so the report is
public and dated.
