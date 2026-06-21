# Vendored Draw.io Assets

Embedded diagram editor loaded in WebView2 for the Diagram Editor tool.

| Component     | Version    | Upstream                                  | License     |
|---------------|------------|-------------------------------------------|-------------|
| draw.io embed | 26.0.9     | https://github.com/jgraph/drawio          | Apache-2.0  |

Last reviewed: 2026-04-13

## Pruning

Heimdall only ships a subset of the upstream draw.io distribution to keep the
installer small. Anything removed here is either unused by our embed integration
or falls back gracefully to defaults at runtime.

### Already pruned (safe)

- **Locales** (`resources/dia_*.txt`) - kept only `dia.txt` (English base /
  fallback), `dia_fr.txt` (French), and `dia_i18n.txt` (auto-generated key
  manifest). Removed ~55 other language files (~2.95 MB). Heimdall is
  English/French only; draw.io falls back to `dia.txt` for any missing locale,
  so no runtime behaviour changes.

### Candidates for further pruning (NOT yet removed - needs manual testing)

These require running the Diagram Editor tool end-to-end to verify they are
actually dead weight before deletion. Do NOT remove without a smoke test of
open / edit / save / export / shape-picker / image-picker.

- **Viewer bundles** (`js/viewer-static.min.js` ~3.5 MB,
  `js/viewer.min.js` ~2.1 MB). Heimdall embeds the editor via `app.min.js`,
  but upstream `app.min.js` may lazy-load the viewer for export/preview paths.
  Potential saving: ~5.6 MB.
- **Shape bundle duplication** (`js/shapes-14-6-5.min.js` vs `js/shapes.min.js`).
  It is unclear which one is actually loaded at runtime; removing the wrong
  one breaks the shape picker. Potential saving: ~1.4 MB.
- **Clipart image libraries** (`img/` subfolders: `finance`, `people`,
  `google-app`, etc., ~10 MB total). `stencils.min.js` may reference paths
  under these folders; removing categories at random breaks stencil lookup.
  Potential saving: up to ~8 MB after selective pruning.

### Kept intentionally

Everything under `js/`, `styles/`, `stencils/`, `shapes/`, `math/`,
`mxgraph/`, `plugins/`, and the full `img/` tree. These are either loaded on
startup or referenced indirectly (e.g. shape lookup by relative path) and
should only be touched with a runtime test plan.
