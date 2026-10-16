# Search fix and validation — 0.1.5

16 October 2026.

Fixed the doubled text inset in the shared WPF text-box template. The template's content host added the same padding already applied by TextBoxView. Typed text now aligns with the search placeholder and search icon.

The program summary now uses visible rows and their sizes rather than always showing the entire inventory. Search ignores surrounding whitespace and matches names/publishers case-insensitively. Empty searches restore the page summary, and zero matches display a clear explanation. Summary totals also refresh when measured sizes change.

Validation performed:

- Release solution build: zero warnings and zero errors.
- All 68 regression tests pass; the WPF test additionally checks matching counts, no-match state, whitespace/case handling, clearing the search, and zero extra content-host margin. The existing suite covers isolated cleanup/backup/restore, parsing, snapshots, protected-path handling and all 13 page layouts.
- Actual application UI rendered in the isolated WPF capture host: `no` finds Orbit Notes with `1 matching program · 86 MB`; `zz-no-match` displays `0 matching programs · 0 B` with an empty-state message; padded uppercase `NEBULA` finds the expected app.
- NuGet advisory gate ran during packaging; reports are under `artifacts/store-search-fix/0.1.5/validation`.
- Offline EXE and MSI rebuilt under `artifacts/store-search-fix/0.1.5`. MSI extraction/hash and metadata verification run as the build gate.

Remaining tests: this environment is not elevated and has no Windows Sandbox executable, Hyper-V tools, VirtualBox or VMware installation available. Therefore Windows 10/11 VM install/repair/upgrade/uninstall and privileged real-fixture cleanup integrations have not run. Signing remains unavailable. No real installed programs were removed during this validation.

`Test-StoreInstall.ps1 -AllowUnsignedTestBuild` permits preliminary testing of this unsigned candidate inside a disposable VM; administrator and VM guards remain required. Omit the switch for final acceptance of the trusted-signed release. Passing unsigned candidate tests does not replace checking the final signed artifact.
