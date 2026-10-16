# Real demo installation and uninstall test

16 October 2026. Result: **21 integration checks passed**.

The test installs a harmless, uniquely identified per-user fixture through a separate installer process. It creates an app payload, settings file, real HKCU uninstall registration and a real settings registry key. WinAppClear's production inventory discovers the registration; its uninstaller runner invokes the registered command in another process. The fixture deliberately leaves settings behind so cleanup is exercised rather than assumed.

Production snapshot enumeration captures before/after state using explicitly restricted test filesystem and registry roots, then production Install Monitor diff/log persistence records the added paths. This does not exercise whole-machine snapshot scope, the monitor UI buttons or installer launch through the UI.

The production leftover scanner finds the remaining install folder, roaming-data folder and settings registry key. Cleanup targets are asserted to stay within this run's owned fixture. The production cleanup executor backs up and removes all three. Tests then confirm the original folders/key are absent, the backup payload and manifest exist, and restore all three. The restored settings file matches its original SHA-256; restored registry string and DWORD values match. An unrelated sentinel file remains unchanged throughout. Restoring leftovers does not recreate the app's uninstall registration.

The harness removes only its uniquely named fixture directory and its own registry entries in a finally block. Evidence, logs, snapshots and backups are retained separately under `artifacts/integration-evidence-20261016-verified/`. Normal WinAppClear history, settings and backups are not used.

Run from the repository root on Windows with .NET 8:

```powershell
dotnet run --project tests/WinAppClear.Integration/WinAppClear.Integration.csproj -c Release -- artifacts/integration-evidence-new-run
```

Use a fresh evidence directory for each run. `results.json` lists each assertion and the scope; `installation-log.json`, the before/after snapshots and isolated app-data backup records provide supporting evidence. The fixture never adds services, tasks, firewall rules, startup entries or an autorun, and never requests administrator rights.

## Scope and remaining validation

This is a real per-user EXE installer/uninstaller fixture on the development host, **not an MSI lifecycle test or disposable Windows VM test**. There is no downloaded third-party application involved. The first two harness development attempts failed due to test-process invocation and an incorrect assertion about coalesced parent folders; both were corrected before the successful run. Those failures were in the new harness, not proof of a product failure.

Windows 10/11 VM installation, repair, MSI upgrades/downgrades and removal of WinAppClear itself remain pending. Full-machine snapshots, child-process edge cases, services/tasks/firewall cleanup, UAC behavior and the signed release still require their own acceptance tests. This passing fixture is useful end-to-end evidence, not a guarantee that every real application's leftovers are detected.
