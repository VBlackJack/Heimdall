# Vendored Binaries

These binaries are bundled as fallbacks when the user does not have them installed system-wide.
They are **not** auto-updated - check upstream releases periodically.

| Binary     | Version        | Upstream                                             | License     |
|------------|----------------|------------------------------------------------------|-------------|
| plink.exe  | 0.83 (Release) | https://www.chiark.greenend.org.uk/~sgtatham/putty/  | MIT         |
| gsudo.exe  | 2.5.1          | https://github.com/gerardog/gsudo                    | MIT         |

Last reviewed: 2026-03-30

## Pinning

`VENDORED.json` next to this file records the version and the SHA-256 of every file listed
above. `VendoredAssetManifestTests` fails when a file changes without its entry, when a file
appears that no entry covers, and when a recorded version disagrees with the file's own
version resource. The same manifest exists beside every vendored component under `Assets/`
(`vnc`, `Terminal`, `drawio`, `milkdown`, `milkdown-editor`).

Text files are hashed with CRLF folded to LF, so the value does not depend on the checkout's
line endings; binaries are hashed as they are.

To update a component: replace the files, change the version in the table above and in
`VENDORED.json`, run the test, review the difference it reports, then copy the new SHA-256
values it prints into `VENDORED.json`. The diff of that file is the review record.
