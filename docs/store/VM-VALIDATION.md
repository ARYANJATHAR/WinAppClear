# Disposable Windows VM acceptance checks

Run against the **final signed** MSI. Record OS version, release/hash, date, outcome and logs. Never use a personal or production computer for removal tests. The automated VM script refuses common non-VM machines and requires administrator rights.

| Check | Expected result | Current status |
| --- | --- | --- |
| Windows 10 x64 offline silent install | No setup UI/download, exit 0 or 3010, registered app and Start menu shortcut | Pending VM |
| Windows 11 x64 offline silent install | Same as Windows 10 | Pending VM |
| Launch with UAC consent | Real UI opens and inventories software; no startup error | Pending elevated VM |
| Decline UAC | Windows prevents elevated launch; no cleanup occurs | Pending VM |
| MSI repair | Application stays runnable; retained data unchanged | Pending VM |
| Upgrade from a previous MSI | One registered product, same installation location, user data retained | Pending previous signed MSI |
| Downgrade attempt | Installer refuses older product; current installation retained | Pending two MSI versions |
| MSI uninstall | App files and shortcut removed; settings/backups preserved | Pending VM |
| Reinstall | Retained data still readable | Pending VM |
| Harmless fixture MSI/EXE removal | Only selected fixture is removed; its own uninstaller is handled correctly | Pending VM |
| Leftover confidence review | Uncertain/shared/personal paths remain unchecked and can be kept | Pending VM |
| Backup/restore of fixture leftovers | Files/registry restore correctly; newer files are not overwritten | Pending VM |
| Locked files and cancellation | Useful error/cancel state; no unbacked cleanup authorization | Pending VM |
| Install Monitor before/after fixture | Start before setup, finish afterward, inspect log and unrelated changes | Pending VM |
| Optional tools unavailable | winget/ADB/browser prerequisites explained without crashing | Pending VM |
| Optional integrations present | Verify supported update, extension/Store/ADB actions only on disposable fixtures | Pending VM |
| Force Delete and Shredder | Disposable files only; clear irreversible-action confirmation | Pending VM |
| Accessibility and scaling | Keyboard focus, readable 100/150/200% scaling and information tips | Pending manual review |

The existing regression suite checks isolated core behavior; passing it is not evidence that these end-to-end integrations have passed. Include your completed outcomes in the release validation report before certification.
