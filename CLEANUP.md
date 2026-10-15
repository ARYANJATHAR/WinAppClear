# Folder cleanup - 15 October 2026

Removed generated build output, verbose diagnostic logs, unnecessary release debug symbols and the duplicate unpacked 0.1.1 release. The previous ZIP was verified against its EXE before cleanup.

Removed items:

- src/ClearOut.App/bin (219.58 MiB)
- src/ClearOut.App/obj (11.62 MiB)
- src/ClearOut.Core/bin (0.84 MiB)
- src/ClearOut.Core/obj (1.25 MiB)
- src/ClearOut.Cli/bin (25.86 MiB)
- src/ClearOut.Cli/obj (0.21 MiB)
- tests/ClearOut.Core.Tests/bin (59.89 MiB)
- tests/ClearOut.Core.Tests/obj (0.63 MiB)
- artifacts/ClearOut-0.1.1-win-x64 (74.83 MiB)
- TestResults/vstest.log (0.25 MiB)
- TestResults/vstest.host.26-10-15_11-35-23_14840_5.log (0.17 MiB)
- artifacts/ClearOut-0.1.2-win-x64/ClearOut.Core.pdb (0.1 MiB)

Total reclaimed: 395.2 MiB.

Preserved: source, solution/project files, icons, test code, test result reports, screenshots, startup evidence, current release EXE/ZIP and previous release ZIP. No files were removed from the normal ClearOut app-data folder.

The bin/obj folders are reproducible. Run dotnet restore ClearOut.sln before using commands with --no-restore. README.md contains normal build commands that restore automatically.

The old release folder is empty, but Windows is holding a directory handle; its files were removed and its ZIP is retained.

## Follow-up artifact cleanup

At the user's request, the superseded `artifacts/ClearOut-0.1.1-win-x64.zip` was also removed, reclaiming another 69.4 MiB. The current 0.1.2 EXE, ZIP and recorded SHA-256 were verified to match before deletion. Only the current EXE/ZIP, README and checksum are retained. The old 0.1.1 directory is empty but remains locked by another process; both normal and elevated deletion attempts failed. No process was terminated to remove that empty directory.

## WinAppClear 0.1.3 rename release

The renamed EXE and ZIP were verified to contain the same executable. Superseded ClearOut packages and generated WinAppClear build output were removed after the 68 passing tests and published-app startup check. The latest WinAppClear EXE/ZIP, README, checksum, source and test evidence are retained.
Empty folders still held by Windows: artifacts/ClearOut-0.1.1-win-x64.
