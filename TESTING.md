# WinAppClear validation

Real demo installation/uninstall validation on 16 October 2026: **21 integration checks passed**, including actual HKCU installation discovery, registered uninstaller execution, leftover scanning, file/registry backup, cleanup and restoration. See [demo integration results](docs/store/DEMO-INTEGRATION-RESULTS.md) for scope, commands and evidence. Machine-wide MSI/VM acceptance remains separate.

Latest search/UI fix: **0.1.5**. See [search-fix verification](docs/store/SEARCH-FIX-0.1.5.md) for the new tests, screenshots, artifacts and remaining VM-only checks.

For the newer **0.1.4 Microsoft Store candidate**, see [Store validation](docs/store/VALIDATION.md). Its build passes all 68 tests with the updated test tooling and includes an inspected offline MSI. The candidate is unsigned, and Windows VM lifecycle/privileged integration checks remain pending. The following 0.1.3 results are retained as historical release evidence.

Release: **0.1.3**, Windows x64. Verified on 15 October 2026 using .NET SDK 8.0.424.

## Build and tests

The renamed solution builds with **zero warnings and zero errors**. The Release regression suite passes **68/68 tests**, with zero failures or skips. Results: `TestResults/winappclear-0.1.3-tests.trx`.

```powershell
dotnet restore WinAppClear.sln
dotnet build WinAppClear.sln -c Release -m:1 -p:UseSharedCompilation=false
dotnet test tests/WinAppClear.Core.Tests/WinAppClear.Core.Tests.csproj -c Release --logger "trx;LogFileName=winappclear-0.1.3-tests.trx" --results-directory TestResults
dotnet publish src/WinAppClear.App/WinAppClear.App.csproj -c Release -p:PublishProfile=Portable -o artifacts/WinAppClear-0.1.3-win-x64
```

## Rename and compatibility

The solution, project directories/files, assemblies, namespaces, XAML resource references, window/dialog titles, application text, asset filenames, EXE metadata and CLI name now use WinAppClear. The source workspace folder remains in its existing location.

Fresh installs use `%LOCALAPPDATA%\WinAppClear`. If that directory does not exist but the previous `%LOCALAPPDATA%\ClearOut` directory exists, the app reuses the previous directory. This preserves settings/history and the absolute paths saved inside existing backup manifests. Tests cover fresh installs, legacy reuse and preference for an existing new directory. No real user data was moved during development.

An added resource regression check exposed an existing lowercase JSON loading issue. The blocklist loader now accepts the bundled lowercase field names and the same format in custom lists. Both bundled matching and custom-list matching are tested.

## Isolated test coverage

Each test creates a unique `%TEMP%\WinAppClear-DemoTests\<guid>` sandbox and overrides the app-data location. Tests run sequentially because the override is global. Teardown verifies the sandbox boundary before removing generated data.

Covered areas:

- Program/publisher matching and generic-name exclusions.
- Absolute paths, drive roots, sibling boundaries and protected locations.
- Uninstall command parsing and MSI/Inno flags, without running uninstallers.
- Demo folder scanning, sizes, cancellation and shared-program ownership.
- Actual demo file/folder cleanup, backups and restoration.
- Locked-file backup failures, refusal to schedule deletion without a backup, preservation of newer files and retryable restore conflicts.
- Registry value serialization, including empty multi-strings, empty elements and legacy backup decoding, without registry writes.
- History accounting, malformed JSON recovery and saved install logs.
- Synthetic snapshot round trips and added/modified/unchanged changes.
- Disposable-file shredding with an unrelated sentinel preserved.
- Synthetic winget and ADB output parsing.
- Initialization/layout of all 13 WPF page controls and demo program search.
- Legacy app-data compatibility and bundled/custom blocklist loading.

Tests do not uninstall real programs, write registry keys, delete services/tasks/firewall rules, schedule reboot changes, modify browser profiles, upgrade software, access a phone or create restore points. The protected-path case supplies the Windows directory solely to verify refusal before mutation.

## Existing fixes retained

Backup copy failures cannot authorize reboot deletion. Folder restoration does not overwrite newer files and preserves conflicts for retry. Drive-root containment handles the root separator correctly. Registry multi-strings use explicitly marked JSON while legacy backups remain readable. Task cleanup refuses deletion if task XML cannot be backed up.

## Release package

The self-contained, single-file app includes .NET 8.0.30 and WPF native libraries. No separate .NET installation is required. Files are in `artifacts/WinAppClear-0.1.3-win-x64`; the neighboring ZIP contains the EXE, README and SHA-256 checksum.

The final published EXE was checked using isolated `--data-dir`, `--snapshot` and `--delay 8000` arguments. It displayed the renamed main window, loaded inventory and exited with code **0**. Its log contains `WinAppClear 0.1.3.0 starting` and no warnings or errors; EXE product metadata also identifies WinAppClear. Startup evidence is saved under `TestResults/winappclear-startup-0.1.3*`.

EXE SHA-256: `35411E9F3F4105458403AFE71DD1ABBA0D9C235C46390DB2C3F5DA8698194BCF`. The executable inside the ZIP was verified to match. Generated build output and superseded packages were removed after validation.

## Coverage limits

This is not a guarantee that every Windows integration is error-free. Real MSI/EXE removal and child-process handling, full machine snapshots, restore points, privileged registry/service/task/firewall changes, Store removal, browser-extension removal, hibernation, winget upgrades, ADB removal and reboot deletion still need a disposable Windows VM with representative fixtures.

The WPF unit test initializes resources without calling Application.Run/OnStartup, so it avoids real inventory loading and backup pruning. It checks page layout and demo search; it does not automate every button. The published-app startup check exercises the real main window and reads program inventory but does not perform removal actions.

Historical test reports/screenshots under the former product name remain as prior evidence. Generated bin/obj directories can be recreated by restoring and building; they are not part of the portable release. The sandboxed VSTest connection previously timed out, so successful test runs used approved execution outside that sandbox.
