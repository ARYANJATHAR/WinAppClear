[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath,[string]$PreviousMsiPath,[switch]$AllowUnsignedTestBuild)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator inside a disposable Windows VM.' }
$computer=Get-CimInstance Win32_ComputerSystem
if (($computer.Manufacturer+' '+$computer.Model) -notmatch 'Virtual Machine|VirtualBox|VMware|KVM|QEMU|Parallels|HVM') { throw 'This does not look like a disposable VM. Refusing install/removal testing on this machine.' }
$installer=New-Object -ComObject WindowsInstaller.Installer
function Check-Msi([string]$path) {
    $resolved=(Resolve-Path -LiteralPath $path).Path
    if (!$AllowUnsignedTestBuild -and (Get-AuthenticodeSignature $resolved).Status -ne 'Valid') { throw 'Use the signed release for final acceptance, or -AllowUnsignedTestBuild for preliminary disposable-VM testing.' }
    $db=$installer.OpenDatabase($resolved,0)
    $view=$db.OpenView('SELECT `Property`,`Value` FROM `Property`');$null=$view.Execute();$p=@{}
    while($null-ne($row=$view.Fetch())){$p[$row.StringData(1)]=$row.StringData(2)};$null=$view.Close()
    if ($p['ProductName'] -ne 'WinAppClear' -or $p['UpgradeCode'] -ne '{E81C1F56-6F4F-47DB-91F9-21F7595B23E4}') { throw 'Not the expected WinAppClear installer' }
    return @{Path=$resolved;ProductCode=$p['ProductCode'];Version=$p['ProductVersion']}
}
$target=Check-Msi $MsiPath
$previous=if($PreviousMsiPath){Check-Msi $PreviousMsiPath}else{$null}
if($previous -and [version]$previous.Version -ge [version]$target.Version){throw 'The upgrade fixture must be an older MSI version.'}
$programDir=Join-Path $env:ProgramFiles 'WinAppClear'
if(Test-Path $programDir){throw 'WinAppClear already exists. Revert the VM to a clean snapshot first.'}
$run=Join-Path $env:TEMP ('WinAppClear-StoreValidation-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run|Out-Null
function Msi-Run([string]$operation,[string]$file,[string]$name) {
    $args="$operation `"$file`" /qn /norestart /L*v `"$(Join-Path $run ($name+'.log'))`""
    $p=Start-Process msiexec.exe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if($p.ExitCode -notin 0,3010){throw "$name failed: MSI exit $($p.ExitCode). See $run"}
}
if($previous){Msi-Run '/i' $previous.Path 'install-previous'}
Msi-Run '/i' $target.Path 'install-current'
$exe=Join-Path $programDir 'WinAppClear.exe'
if(!(Test-Path $exe)){throw 'Installed executable missing'}
if(!$AllowUnsignedTestBuild -and (Get-AuthenticodeSignature $exe).Status -ne 'Valid'){throw 'Installed application signature is invalid'}
Msi-Run '/fa' $target.Path 'repair'
$shot=Join-Path $run 'startup.png';$data=Join-Path $run 'isolated-appdata'
$app=Start-Process -FilePath $exe -ArgumentList "--data-dir `"$data`" --snapshot `"$shot`" --delay 8000" -PassThru -WindowStyle Hidden
if(!$app.WaitForExit(90000)){$app.Kill();throw 'Application did not exit after its startup snapshot'}
if($app.ExitCode -ne 0 -or !(Test-Path $shot)){throw 'Startup check failed'}
if(Get-ChildItem (Join-Path $data 'Logs') -Filter *.log | Select-String -Pattern '\[ERROR\]'){throw 'Startup log contains an error'}
$retained=Join-Path $env:LOCALAPPDATA 'WinAppClear'
New-Item -ItemType Directory -Force $retained|Out-Null
$sentinel=Join-Path $retained ('store-validation-'+[Guid]::NewGuid().ToString('N')+'.txt')
'User recovery data retention sentinel'|Set-Content $sentinel
Msi-Run '/x' $target.Path 'uninstall'
if(Test-Path $exe){throw 'Executable still present after uninstall'}
if(!(Test-Path $sentinel)){throw 'Uninstaller removed retained user data'}
$shortcut=Join-Path $env:ProgramData 'Microsoft/Windows/Start Menu/Programs/WinAppClear.lnk'
if(Test-Path $shortcut){throw 'Start menu shortcut still present'}
$report=[ordered]@{Os=(Get-CimInstance Win32_OperatingSystem).Caption;Version=$target.Version;Sha256=(Get-FileHash $target.Path).Hash;SilentInstall='Passed';Repair='Passed';IsolatedStartup='Passed';Uninstall='Passed';UserDataRetained='Passed';Upgrade=if($previous){'Passed'}else{'Not tested: no previous MSI supplied'};FeatureIntegration='Complete VM-VALIDATION.md separately';CompletedUtc=[DateTime]::UtcNow.ToString('o')}
$report|ConvertTo-Json|Set-Content (Join-Path $run 'vm-results.json') -Encoding utf8
Write-Host "MSI lifecycle checks passed. Evidence retained at $run"
