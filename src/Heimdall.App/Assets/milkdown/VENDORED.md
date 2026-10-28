# Vendored Milkdown Bundle

`index.html` is the built Markdown editor loaded by the Notes tool. It is a byte-for-byte copy
of `../milkdown-editor/dist/index.html`; build, version and dependency audit are described in
`../milkdown-editor/VENDORED.md`.

| Component | Version | Upstream                              | License |
|-----------|---------|---------------------------------------|---------|
| Milkdown  | 7.23.0  | https://github.com/Milkdown/milkdown  | MIT     |

Last reviewed: 2026-10-28

Pinned in `VENDORED.json`. `VendoredAssetManifestTests` also checks that this file and
`../milkdown-editor/dist/index.html` stay identical.