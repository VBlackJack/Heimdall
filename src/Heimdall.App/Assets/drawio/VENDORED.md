# Vendored Draw.io Assets

Embedded diagram editor loaded in WebView2 for the Diagram Editor tool.

| Component     | Version    | Upstream                                  | License     |
|---------------|------------|-------------------------------------------|-------------|
| draw.io embed | 31.4.5     | https://github.com/jgraph/drawio          | Apache-2.0  |

Last reviewed: 2026-09-16.

The version above is the one `js/app.min.js` reports in `EditorUi.VERSION`.
`DiagramEditorGuardTests` reads that constant and fails when this table, or
either `THIRD-PARTY-NOTICES` file, disagrees with it. The previous manifest
claimed 26.0.9 for a tree that shipped 24.8.6, which is what the guard exists
to prevent.

## Heimdall-specific edits

The tree is upstream's `src/main/webapp` with two edits, both of which must be
re-applied on every version bump:

1. `js/bootstrap.js`: `checkAllLoaded()` passes a callback to `App.main` that
   assigns `window.heimdallDrawioApp = app`. The iframe host page
   (`heimdall-host.html`) reaches the editor's action registry through it, to
   drive undo/redo, zoom and the context menu from the WPF toolbar.
2. `index.html`: a `Content-Security-Policy` meta tag that keeps every request
   on the local virtual host. Upstream's own policy is only emitted for the
   Electron build and allows `img-src *`, `media-src *` and the diagrams.net
   endpoints, which would let a crafted diagram beacon out.

`heimdall-host.html` is Heimdall's own file and has no upstream counterpart. It
loads the editor with `offline=1&stealth=1`, and passes Heimdall's locale and
theme through `lang`, `ui` and `dark`.

## Pruning

Heimdall ships a subset of the upstream distribution to keep the installer
small. The subset is defined by `scripts/Vendor-DrawIo.ps1`, which is also the
supported way to upgrade: it copies exactly these paths and re-applies both
edits above.

### What is shipped

- `index.html`, `favicon.ico`
- `js/`: `bootstrap.js`, `main.js`, `PreConfig.js`, `PostConfig.js`,
  `app.min.js`, `extensions.min.js`, `stencils.min.js`,
  `shapes-14-6-5.min.js`, `math-print.js`, `orgchart.min.js`, and the
  `orgchart/`, `mermaid/`, `gliffy/`, `elk/` and `jquery/` folders that
  `app.min.js` loads by relative path at runtime.
- `styles/`, `images/`, `img/`, `mxgraph/css/`
- `resources/`: `dia.txt` (English base and fallback), `dia_fr.txt` (French),
  `dia_i18n.txt` (key manifest), `README.md`.

### What is deliberately left out

- **Other locales** (55 `dia_*.txt` files, about 3 MB). Heimdall is English and
  French only, and draw.io falls back to `dia.txt` for anything missing.
- **Cloud integrations** (`js/dropbox/`, `js/onedrive/`, `js/simplepeer/`).
  Loaded only when the editor is online; `offline=1` never requests them.
- **Server-dependent extras** (`js/plantuml/`, `math4/`, `templates/`,
  `WEB-INF/`, `service-worker.js`, `connect/`, `stencils/` as a folder tree).
  PlantUML and MathJax need a render server Heimdall does not run; the stencil
  shapes ship compiled inside `stencils.min.js`. The editor still asks for
  `math4/es5/startup.js` once at startup and gets a 404 from the virtual host;
  it is the only request the bundle makes that nothing answers, it is local, and
  the editor carries on without it. Vendoring `math4/` would add about 3 MB to
  enable LaTeX in diagram labels.
- **Viewer bundles** (`js/viewer.min.js`, `js/viewer-static.min.js`,
  `js/integrate.min.js`). Heimdall embeds the editor, not the viewer.
- **Sources** (`js/grapheditor/`, `js/diagramly/`, `mxgraph/src/`): only loaded
  with `dev=1`.

Removing anything else needs a runtime test of the tool: open, edit, save,
export PNG and SVG, shape picker, image picker, context menu.

## Pinning

Pinned in `VENDORED.json`: `js/app.min.js` individually, and a digest of the whole tree
(every file except `heimdall-host.html`, which is Heimdall's own). After running
`scripts/Vendor-DrawIo.ps1`, run `VendoredAssetManifestTests` and copy the values it prints
into `VENDORED.json`. See "Pinning" in `../Tools/VENDORED.md`.
