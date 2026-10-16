# WinAppClear privacy policy

Effective date: 15 October 2026. Applies to the WinAppClear desktop application maintained at https://github.com/ARYANJATHAR/WinAppClear.

## Information processed on your device

WinAppClear inspects installed software and related Windows configuration to provide its features. Depending on the feature used, this can include program names, publishers and versions; installation dates and usage indicators; file and folder paths; registry keys and values; services, scheduled tasks, startup entries and firewall rules; supported browser-extension metadata; and application/package information from an Android device you connect and authorize through ADB.

Installation recording compares before-and-after snapshots. Other changes made on the computer during a recording can appear in the results. Snapshots, cleanup backups and logs may contain your Windows username, personal file paths, configuration values and other sensitive information. Treat them as private data.

## Local storage and retention

Settings, history, logs, snapshots and cleanup backups are stored locally under `%LOCALAPPDATA%\WinAppClear`. An existing `%LOCALAPPDATA%\ClearOut` directory may be reused for compatibility. WinAppClear does not provide an account or cloud synchronization and does not intentionally upload this information to the project maintainer. The application contains no advertising or analytics SDK.

Cleanup backups are normally pruned after the configured retention period (30 days by default); changing the retention setting affects this behavior. Other local records remain until removed by you or an applicable application action. Uninstalling WinAppClear leaves local history and backups in place so recovery data is not silently destroyed. You can delete these folders yourself after closing the application and deciding that you no longer need any saved backups. Deletion or expiry of a backup removes that recovery option.

## Network access and other programs

Features that use Windows Package Manager (`winget`) can contact package sources and software publishers to check for or install updates. Browser-extension links, program web searches and support links open external websites. ADB communicates with an authorized connected device. The app can also launch a program's own uninstaller. These tools, websites, publishers and Microsoft Store have their own privacy practices; their data handling is not controlled by WinAppClear.

WinAppClear does not sell personal information. No payment information is collected by the desktop application.

## Your choices

You choose which features to run, what cleanup items to select and whether to connect an Android device. Review selected paths before removal. Avoid sharing raw logs, snapshots or backups publicly; remove personal details first. Force Delete and File Shredder do not create ordinary cleanup backups.

## Contact and changes

For privacy questions, open an issue at https://github.com/ARYANJATHAR/WinAppClear/issues without including sensitive information. This is a public issue tracker. Changes to this policy will be published in this repository with an updated effective date.
