# Notes for Microsoft Store certification

WinAppClear is an MIT-licensed Windows desktop utility. It intentionally needs administrator permission to uninstall machine-wide software and perform selected cleanup operations. Its application manifest requests elevation. The MSI is an offline, per-machine x64 installer without a custom bootstrapper, downloads, bundled unrelated applications, automatic launch, scheduled tasks, services or advertising. It installs the app and documentation and adds a Start menu entry.

Install: `msiexec.exe /i "WinAppClear-0.1.4-win-x64.msi" /qn /norestart`

Uninstall: `msiexec.exe /x "WinAppClear-0.1.4-win-x64.msi" /qn /norestart`

Installation scope: System / per-machine. Operating systems supported by the application: Windows 10 version 1809 or later and Windows 11, x64. Language: English. Runtime: self-contained .NET 8; no runtime download during setup. Success codes: 0 (success), 3010 (success, restart required). Code 1641 means restart initiated; `/norestart` is always recommended. Other installer codes indicate failure and must not be mapped to success. The MSI's ProductCode is recorded in `validation/package-verification.json` and is another option for silent uninstallation.

The application does not require a login, subscription or purchase. No non-Microsoft service or kernel driver is installed or required. Optional functionality: Windows Package Manager / App Installer and internet for software updates; separately installed Google Android Platform Tools plus authorized USB debugging for Android features; supported installed browsers for extension management. Missing optional tools produce prerequisite messages. Individual third-party uninstallers may present their own UI or communicate with their publishers.

Core review scenario: launch from the Start menu, accept UAC, review All Programs and use the page's information icon. In a disposable VM, install a harmless fixture first, uninstall only that fixture, review leftover confidence labels and selected paths, then inspect History & Backups. Start Install Monitor before installing another fixture; install it; finish recording and inspect the changes. Snapshot recording is manual and can include unrelated changes. Backups cover supported WinAppClear cleanup actions, not every action of third-party uninstallers. Do not test destructive tools against operating system or personal files.

Uninstalling WinAppClear preserves user-created app data/backups under `%LOCALAPPDATA%\WinAppClear` (or legacy ClearOut). This is intentional and documented in the privacy policy. No executable, service, task or autorun is left behind by the MSI. Users can remove retained data themselves when they no longer need their recovery records.

Screenshots use fictional demonstration records in the actual application UI. The accompanying animated walkthrough recreates the interface and illustrates a shortened workflow; it is not a recording of successful certification or live system cleanup.

Submission must use the trusted-signed build, not the unsigned local candidate. The maintainer must complete VM integration testing before submitting and report any required reviewer instructions honestly.
