# Vendored Milkdown Editor (source and built bundle)

`dist/index.html` is the Markdown editor of the Notes tool: a single HTML file built by Vite
from `src/` and the dependencies pinned in `package-lock.json`. The built file is committed
and copied to `../milkdown/index.html`, which is what ships (`VENDORED.md` there).

| Component | Version | Upstream                              | License |
|-----------|---------|---------------------------------------|---------|
| Milkdown  | 7.23.0  | https://github.com/Milkdown/milkdown  | MIT     |

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
warns below high and fails at high or critical. `npm-audit-baseline.json` lists, with a reason,
the advisories accepted instead of fixed; it is empty since the dependencies were updated within
their majors (Milkdown 7.19.2 to 7.23.0) and the bundle rebuilt. An entry is added only for an
advisory with no patched version in range, and removed as soon as the lock clears it.

`@vue/server-renderer`, `postcss` and `source-map-js` are in the lock only because `vue` (a
dependency of Crepe) declares them; the bundle imports none of them (it renders on the client,
and the CSS tooling is vue's SFC compiler), so they are audit surface, not shipped code.

## Pinning

Pinned in `VENDORED.json`: the lock file (with the exact `@milkdown/core` resolution) and the
built bundle. See "Pinning" in `../Tools/VENDORED.md`.