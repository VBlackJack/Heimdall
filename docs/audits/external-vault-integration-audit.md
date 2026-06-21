# Audit - External Vault Integration

**Scope**: External credential provider feature (KeePassXC, Bitwarden CLI, 1Password CLI, `pass`)
**Date**: 2026-06-20
**Status**: Wired and unit-tested, but unusable out of the box with the shipped presets.

---

## 1. What the feature is

Heimdall can resolve a server password at connect time by executing an external
command-line password manager, instead of storing the password in its own
DPAPI vault.

| Component | File |
|---|---|
| Abstraction | `src/Heimdall.Core/Security/ICredentialProvider.cs` |
| Implementation | `src/Heimdall.Core/Security/CommandCredentialProvider.cs` |
| Built-in presets | `src/Heimdall.App/Services/CredentialProviderPresetService.cs` |
| Settings (model) | `src/Heimdall.Core/Configuration/AppSettings.cs:250-253`, `61` |
| Settings (UI/VM) | `MainWindow.xaml:2870-2895`, `SettingsViewModel.cs:1843` |
| Connect wiring (single) | `ServerListViewModel.cs:718` |
| Connect wiring (bulk) | `ServerListViewModel.Bulk.cs:991` |
| Target resolution | `ServerListViewModel.cs:943-982` (`GetCredentialTarget`) |
| Unit tests | `tests/Heimdall.Core.Tests/CommandCredentialProviderTests.cs` |

**Flow**: on connect, if `UseExternalCredentialProvider` is on and the profile has
no stored password for its protocol, `TryResolveExternalCredentialsAsync`
(`ServerListViewModel.cs:996`) runs the configured command, captures stdout,
re-encrypts it with DPAPI (`CredentialProtector.Protect`) and injects it into the
in-memory DTO so all downstream code works unchanged.

**Conclusion on wiring**: the plumbing is present and correct in both the single
and bulk connect paths. The reason the feature "never works" is **not** the wiring.

---

## 2. Findings

### 🔴 Critical - the shipped presets cannot work as-is

**V-1 - No stdin is ever redirected.**
`CommandCredentialProvider.cs:88-96` redirects only stdout and stderr, never
`RedirectStandardInput`. Any tool that needs an interactive unlock fails:
KeePassXC asks for the database master password, `pass` may ask for the GPG
passphrase. The process either errors on EOF or blocks until the 10s timeout.

**V-2 - Presets assume an already-unlocked session that Heimdall never establishes.**
`CredentialProviderPresetService.cs:27-30`:
- `bw get password` requires `BW_SESSION` (set after `bw unlock`).
- `op read` requires a prior `op signin` session.
- `pass show` requires an active `gpg-agent`.
- `keepassxc-cli show` requires the master password (see V-1).

Launched "cold" from the app, all four return a non-zero exit code → `null` →
the **Test** button reports "no result" and connections silently get no password.

**V-3 - The vault entry name is hard-bound to `{Title}` = Heimdall DisplayName.**
`GetCredentialAsync` substitutes `{Title}` with the server's display name. If the
vault entry is not named exactly like the Heimdall entry, the lookup fails. There
is no per-profile "vault entry name / reference" field.

### 🟠 Medium

**V-4 - Username is never read from the vault.**
`CommandCredentialProvider.cs:146` returns `new CredentialResult(username ?? "", password)`
 - it echoes the hint back. `SetUsernameIfEmpty` (`ServerListViewModel.cs:1048`) is
therefore effectively a no-op. Only the password ever comes from the vault.

**V-5 - Protocol coverage is incomplete.**
`GetCredentialTarget` (`ServerListViewModel.cs:943-982`) handles SSH/SFTP, RDP/Citrix,
WinRM (credential mode) and FTP - but **not Telnet and not VNC**. Those profiles
silently ignore the provider.

**V-6 - The Test button ignores the configured timeout.**
`SettingsViewModel.cs:1855` constructs the provider without `timeoutMs`, so it
defaults to 10s, while the real connect path uses
`settings.CredentialProviderTimeoutMs`. A Test can pass/fail differently from a
real connection when a custom timeout is set.

### 🟡 Minor

**V-7 - No DI registration.** The provider is `new`-ed in three places
(`ServerListViewModel`, `ServerListViewModel.Bulk`, `SettingsViewModel`).
`ICredentialProvider` is never registered, so the path is not mockable via DI and
the construction is duplicated.

**V-8 - No integration test on the wiring.** Only `CommandCredentialProvider` is
covered. A regression in `TryResolveExternalCredentialsAsync` (target resolution,
injection, skip semantics) would go unnoticed.

**V-9 - Silent skip in bulk connect.** `skipOnFailure: true`
(`ServerListViewModel.Bulk.cs:995`) is by design, but combined with V-2 the bulk
path skips servers with only an Info log and no visible feedback.

### ✅ Strengths (keep)

- stderr is drained but never logged (credential fragments protected);
  `CommandCredentialProvider.cs:103-127`.
- Timed-out processes are killed before disposal (`TryKillProcess`), so a hung
  tool does not orphan a database lock.
- Context-aware sanitization: strict stripping for shell targets, relaxed for
  regular executables, double quotes always stripped (`ExpandTemplate`, lines 169-210).
- No credential field is ever written to logs; only exit codes / host names.
- Password is re-encrypted with DPAPI before injection.

---

## 3. Recommendations (by priority)

1. **Make presets usable out of the box (V-1, V-2).** Either:
   - add `RedirectStandardInput` and an optional "unlock secret" fed to the tool's
     stdin (master password / passphrase), **or**
   - ship presets that are genuinely non-interactive and document the prerequisite
     session (`bw unlock` / `op signin` / key-file KeePass DB / gpg-agent), shown
     inline in the Settings help text.
2. **Add a per-profile "vault entry reference" field (V-3)** so `{Title}` is not
   forced to equal the Heimdall display name.
3. **Cover Telnet and VNC, or document the exclusion (V-5).**
4. **Use the configured timeout in the Test button (V-6).**
5. **Register `ICredentialProvider` in DI and add an integration test (V-7, V-8).**
6. **Optionally support username retrieval from the vault (V-4).**

---

## 4. Verdict

The feature is architecturally sound and safely implemented, but the default
experience is broken: the four built-in presets all depend on an unlock step that
Heimdall neither performs nor surfaces, and no stdin channel exists to provide
one. Fixing V-1 + V-2 is by far the highest-leverage change.
