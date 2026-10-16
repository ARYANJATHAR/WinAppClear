# Offline Microsoft Store MSI build

From the repository root in PowerShell on Windows x64 with .NET SDK 8:

```powershell
./scripts/Build-Store.ps1
```

This restores, checks dependency advisories, builds, runs the regression suite, publishes a self-contained EXE, combines dependency/runtime license notices, builds an embedded-cabinet MSI with WiX 6.0.2, and inspects the MSI without installing it. WiX is installed locally under ignored `artifacts/tools/wix` if missing. No SDK, ADB, winget, compiler, video tools or unrelated apps are included in the installer. WiX is a build dependency, not a runtime dependency.

The default output is `artifacts/store/0.1.4/`. Each build needs a fresh output directory: an existing MSI is never overwritten. To create another candidate use `-OutputDirectory artifacts/store-candidate-2`. The completed local candidate for this preparation run is under `artifacts/store-final/0.1.4/`.

For the final signed build:

```powershell
./scripts/Build-Store.ps1 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT -OutputDirectory artifacts/store-signed
```

The signing identity must have a private key available to SignTool and chain to a CA trusted for Microsoft Store submissions. Supply `-SignToolPath` for a non-default SDK location. Timestamping uses RFC 3161 with SHA-256. No password, certificate or private key belongs in this repository or CI workflow. Hardware/provider authentication is handled by your signing provider. Sign the EXE before embedding it in the MSI, then sign the MSI; the script performs both and verifies the resulting signatures. A custom local trust root is not proof of Microsoft Store eligibility.

`Test-StorePackage.ps1 -RequireSignature` rejects unsigned/untrusted packages. Unsigned candidates contain an explicit do-not-submit marker. The GitHub workflow produces only unsigned build artifacts and never publishes a release or claims certification. Updates require incrementing `Directory.Build.props`'s version, a new fixed installer URL, and a new Store submission. Keep `UpgradeCode` and component GUIDs stable.

MSI behavior: per-machine x64, `%ProgramFiles%\WinAppClear`, embedded payload, Start menu shortcut, Apps & Features registration, no automatic launch and no custom actions. Major upgrades remove the previous MSI after `InstallInitialize`, within the transaction. Lower versions are blocked. Standard Windows Installer repair/uninstall is supported. App data and backups are intentionally retained. Build validation checks cabinet contents, app hash, MSI scope, documents and signatures; VM lifecycle checks remain separate.

Run actual-interface asset capture with:

```powershell
dotnet run --project packaging/StoreAssets/StoreAssets.csproj -c Release -- docs/store/assets
```

The capture host renders the actual WPF views with synthetic records and an isolated data root. It does not uninstall programs. Screenshots are 1536×960 with a sample-data label. Screenshots are not evidence of successful privileged cleanup.

See `docs/store/FINAL-FIVE-STEPS.md` for identity, signing, VM validation, hosting and Store submission. MSI requirements: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements.
