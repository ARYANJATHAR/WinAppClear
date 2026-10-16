# Store preparation validation — 0.1.4

Prepared on 15 October 2026. This is a local unsigned candidate, not a Microsoft-certified release.

## Checks performed

- Release solution build: zero warnings and zero errors.
- Updated test SDK/xUnit dependencies: all 68 tests pass, no failures or skips.
- NuGet advisory query for all solution projects, including transitive dependencies: no known vulnerable packages reported by the current configured source. Two advisories in the old test tooling were removed by upgrading the test packages. This is an advisory lookup, not proof that the application has no security vulnerabilities.
- Actual WPF UI opened in the isolated capture host; All Programs, leftover review, Install Monitor and History & Backups rendered with sample records. Four fresh 1536×960 screenshots were captured.
- Offline MSI compiled for x64 with embedded application/documents and stable upgrade identity.
- MSI was decompiled/extracted without installing on this machine; its embedded EXE hash was compared with the published EXE. Product/scope/documents/custom-action checks are part of the packaging gate.
- Signing verification explicitly reports the candidate as unsigned. Production signing must occur before Store submission.
- Negative signing-gate check: requiring trusted signatures rejects the unsigned installer and embedded application as expected.

Build evidence: `artifacts/store-final/0.1.4/validation/` (test TRX, dependency audit JSON, package verification JSON and MSI metadata). The submission kit copies these reports. `release-status.json` records which gates have run.

## Not completed here

Trusted code signing, identity verification, name reservation, public hosting of the new release/documents, Windows 10/11 VM installation/repair/upgrade/uninstall, privileged feature acceptance and Microsoft certification have not been completed. This development environment is non-administrative, Windows Sandbox is unavailable and no disposable VM is available. No real programs were uninstalled or modified during this preparation.

Run the supplied VM script against the signed release and complete `VM-VALIDATION.md` before submission. An upgrade test additionally needs a previous MSI; the old portable ZIP/EXE is not an MSI upgrade fixture. Until then, do not describe the installer or all integrations as fully validated or Store-ready for submission.

## Signing order

1. Rebuild and sign the published EXE with a trusted identity.
2. Embed that exact signed EXE in the MSI.
3. Sign and timestamp the MSI.
4. Verify both installed payload and installer signatures.
5. Generate checksums, test that exact release and host it at a fixed version-specific URL.

The automatic build blocks dependency advisories and rejects signature failures when signing is requested. No automatic Store publication is configured.
