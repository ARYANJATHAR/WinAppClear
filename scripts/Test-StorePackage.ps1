[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath,[Parameter(Mandatory)][string]$ExpectedExe,[string]$OutputDirectory,[switch]$RequireSignature)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$MsiPath=(Resolve-Path -LiteralPath $MsiPath).Path
$ExpectedExe=(Resolve-Path -LiteralPath $ExpectedExe).Path
if (!$OutputDirectory) { $OutputDirectory=Join-Path (Split-Path $MsiPath) 'validation' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$extract=Join-Path $OutputDirectory ('msi-extracted-'+[Guid]::NewGuid().ToString('N'))
$wix=Join-Path $repo 'artifacts/tools/wix/wix.exe'
& $wix msi decompile $MsiPath -x $extract -o (Join-Path $OutputDirectory 'decompiled.wxs')
if ($LASTEXITCODE -ne 0) { throw 'MSI decompilation/extraction failed' }
$packed=Get-ChildItem $extract -Recurse -Filter WinAppClear.exe | Select-Object -First 1
if (!$packed) {
    # WiX exports cabinet members using their MSI File identifier, not their installed filename.
    $member=Get-ChildItem $extract -Recurse -File | Where-Object Name -eq 'WinAppClearExe' | Select-Object -First 1
    if ($member) { Copy-Item $member.FullName (Join-Path $extract 'WinAppClear.exe'); $packed=Get-Item (Join-Path $extract 'WinAppClear.exe') }
}
if (!$packed) { throw 'Installer does not contain WinAppClear.exe' }
if ((Get-FileHash $packed.FullName).Hash -ne (Get-FileHash $ExpectedExe).Hash) { throw 'Embedded application differs from published executable' }
$installer=New-Object -ComObject WindowsInstaller.Installer
$db=$installer.OpenDatabase($MsiPath,0)
function Read-Table([string]$sql,[int]$columns=1) {
    $view=$db.OpenView($sql);$null=$view.Execute();$items=@()
    while ($null -ne ($row=$view.Fetch())) { $values=@();for($i=1;$i -le $columns;$i++){$values+= $row.StringData($i)};$items+= ,$values }
    $null=$view.Close();return ,$items
}
$properties=@{};foreach($row in (Read-Table 'SELECT `Property`,`Value` FROM `Property`' 2)){$properties[$row[0]]=$row[1]}
if ($properties['ProductName'] -ne 'WinAppClear' -or $properties['ALLUSERS'] -ne '1') { throw 'Unexpected MSI product/scope' }
$tables=Read-Table 'SELECT `Name` FROM `_Tables`'
$customActions=@()
if ($tables | Where-Object {$_[0] -eq 'CustomAction'}) { $customActions=Read-Table 'SELECT `Action` FROM `CustomAction`' }
if ($customActions.Count -ne 0) { throw 'Unexpected MSI custom action. Review before shipping.' }
$files=Read-Table 'SELECT `FileName` FROM `File`'
foreach($name in @('LICENSE','PRIVACY.md','THIRD-PARTY-NOTICES.md','THIRD-PARTY-LICENSES.txt')) { if (!($files | Where-Object {$_[0] -like "*$name"})) { throw "Missing installed document: $name" } }
$signatures=@(Get-AuthenticodeSignature $MsiPath; Get-ChildItem $extract -Recurse -File | Where-Object Extension -in '.exe','.dll' | ForEach-Object {Get-AuthenticodeSignature $_.FullName})
if ($RequireSignature -and ($signatures | Where-Object Status -ne 'Valid')) { throw 'A package or payload signature is untrusted/missing' }
$report=[ordered]@{ProductName=$properties['ProductName'];Version=$properties['ProductVersion'];ProductCode=$properties['ProductCode'];UpgradeCode=$properties['UpgradeCode'];Scope='perMachine';PayloadHashMatches=$true;OfflineEmbeddedPayload=$true;CustomActionCount=$customActions.Count;SignatureGateRequired=[bool]$RequireSignature;Signatures=@($signatures|Select-Object Path,@{n='Status';e={$_.Status.ToString()}});HostInstallPerformed=$false}
$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $OutputDirectory 'package-verification.json') -Encoding utf8
Write-Host 'MSI payload and metadata checks passed. Install/uninstall tests require a disposable VM.'
