# Vendored Milkdown Editor (source and built bundle)

`dist/index.html` is the Markdown editor of the Notes tool: a single HTML file built by Vite
from `src/` and the dependencies pinned in `package-lock.json`. The built file is committed
and copied to `../milkdown/index.html`, which is what ships (`VENDORED.md` there).

| Component | Version | Upstream                              | License |
|-----------|---------|---------------------------------------|---------|
| Milkdown  | 7.19.2  | https://github.com/Milkdown/milkdown  | MIT     |

Last reviewed: 2026-10-28

## Rebuilding

```
npm ci
npm run build
```

Then copy `dist/index.html` to `../milkdown/index.html`, and update the SHA-256 of the three
files in both `VENDORED.json` files. `VendoredAssetManifestTests` prints the new values.

## Dependency audit

NuGet's vulnerability scan cannot see the npm dependencies inside the bundle. CI runs
`scripts/Invoke-NpmAuditCheck.ps1`, which audits `package-lock.json` alone (no install),
warns below high and fails at high or critical. The advisories that were already in the lock
when the check was introduced are listed with a reason in `npm-audit-baseline.json`; clearing
them takes an updated lock and a rebuilt, retested bundle, and each entry is removed as it
clears.

## Pinning

Pinned in `VENDORED.json`: the lock file (with the exact `@milkdown/core` resolution) and the
built bundle. See "Pinning" in `../Tools/VENDORED.md`.