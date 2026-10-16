[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseDirectory,[string]$Destination)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$release=(Resolve-Path -LiteralPath $ReleaseDirectory).Path
$state=Get-Content (Join-Path $release 'release-status.json') -Raw|ConvertFrom-Json
if(!$Destination){$Destination=Join-Path $repo ("artifacts/WinAppClear-"+$state.Version+'-Store-Submission')}
$dest=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $dest){throw 'Choose a new destination; existing submission kits are never overwritten.'}
New-Item -ItemType Directory $dest|Out-Null
Copy-Item (Join-Path $release '*.msi') $dest
foreach($name in @('SHA256SUMS.txt','release-status.json','UNSIGNED-DO-NOT-SUBMIT.txt')){
    $file=Join-Path $release $name;if(Test-Path $file){Copy-Item $file $dest}
}
Copy-Item (Join-Path $repo 'LICENSE') $dest
Copy-Item (Join-Path $repo 'docs/store') (Join-Path $dest 'submission') -Recurse
Copy-Item (Join-Path $release 'payload/THIRD-PARTY-LICENSES.txt') $dest
$validation=Join-Path $dest 'validation';New-Item -ItemType Directory $validation|Out-Null
Get-ChildItem (Join-Path $release 'validation') -File | Where-Object Extension -in '.json','.trx','.wxs' | Copy-Item -Destination $validation
$signingNote=if($state.TrustedSignatures){'Signature checks passed. Complete VM acceptance of this exact release before submitting.'}else{'UNSIGNED CANDIDATE: do not submit. Rebuild with a trusted signing identity so the embedded application is signed before MSI packaging.'}
@"
WinAppClear $($state.Version) — Microsoft Store preparation kit

Start with submission/FINAL-FIVE-STEPS.md.

Installer signatures present: $($state.TrustedSignatures).
Automated checks: $($state.AutomatedChecks).
Windows VM lifecycle/integration validation: pending maintainer execution.
Microsoft Store submission/certification: not performed.

$signingNote
The final signed release must be rebuilt from the source repository using scripts/Build-Store.ps1.
Do not just sign this MSI: the embedded app must be signed first, before MSI packaging.
Source: https://github.com/ARYANJATHAR/WinAppClear

The submission directory includes listing copy, privacy policy, certification notes,
the final five steps, VM acceptance matrix, and real WPF screenshots with sample data.
The validation directory contains the local test/advisory/package inspection reports.
No private signing key, password, account token or certificate is included.
"@|Set-Content (Join-Path $dest 'START-HERE.txt') -Encoding utf8
$zip=$dest+'.zip';if(Test-Path $zip){throw 'Archive already exists'}
Compress-Archive -Path (Join-Path $dest '*') -DestinationPath $zip -CompressionLevel Fastest
Write-Host "Submission kit: $zip"
