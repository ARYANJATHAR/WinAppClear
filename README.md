# WinAppClear

A Windows x64 desktop uninstaller built with C#/.NET 8 and WPF.

## Run

Open `artifacts/WinAppClear-0.1.3-win-x64/WinAppClear.exe` and accept the administrator prompt. This is a self-contained portable app; no separate .NET installation is required. A ZIP package is available alongside that folder.

Hover over the small information icon beside a sidebar section, or hover/click the information button at the top right of a page, for an explanation of what it does and any prerequisites.

Application settings, history, logs and backups normally live in `%LOCALAPPDATA%\WinAppClear`.

When upgrading from ClearOut, the existing `%LOCALAPPDATA%\ClearOut` data folder is reused if no WinAppClear folder exists. This keeps saved settings, history and absolute backup paths usable without moving your data.

## Source folders

- `src/WinAppClear.App`: WPF interface.
- `src/WinAppClear.Core`: inventory, uninstall, cleanup, safety and backup logic.
- `src/WinAppClear.Cli`: diagnostic command-line interface.
- `tests/WinAppClear.Core.Tests`: isolated demo-data regression and WPF smoke tests.
- `TestResults`: retained test evidence and startup screenshots.
- `artifacts`: the current 0.1.3 EXE/ZIP package, README and checksum. Superseded packages are removed after the new build is verified.

`bin` and `obj` folders are generated build output and can be recreated. They are excluded from version control, along with release artifacts and test output.

## Build and test

Install the .NET 8 SDK, then run from this folder:

```powershell
dotnet restore WinAppClear.sln
dotnet test tests/WinAppClear.Core.Tests/WinAppClear.Core.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet publish src/WinAppClear.App/WinAppClear.App.csproj -c Release -p:PublishProfile=Portable -o artifacts/WinAppClear-0.1.3-win-x64
```

See `TESTING.md` for validation details and integration coverage limits.
