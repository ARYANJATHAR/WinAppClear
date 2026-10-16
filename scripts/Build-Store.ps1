[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$CertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/store' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
$release = Join-Path $out $version
$payload = Join-Path $release 'payload'
$reportDir = Join-Path $release 'validation'
New-Item -ItemType Directory -Force $payload,$reportDir | Out-Null
$msi = Join-Path $release "WinAppClear-$version-win-x64.msi"
if (Test-Path -LiteralPath $msi) { throw "Output already exists: $msi. Choose a fresh -OutputDirectory. Submitted binaries must never be replaced." }
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited with $LASTEXITCODE" }
}
Push-Location $repo
try {
    Invoke-Checked dotnet @('restore','WinAppClear.sln')
    $auditJson = & dotnet list WinAppClear.sln package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'Dependency advisory check failed' }
    $auditText = $auditJson -join "`n"
    $auditText | Set-Content (Join-Path $reportDir 'dependency-audit.json') -Encoding utf8
    $audit = $auditText | ConvertFrom-Json
    foreach ($project in $audit.projects) { foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages)+@($framework.transitivePackages)) {
            if ($package.vulnerabilities) { throw "Dependency advisory found: $($package.id). Update or investigate before packaging." }
        }
    } }
    Invoke-Checked dotnet @('build','WinAppClear.sln','-c','Release','--no-restore','-m:1','-p:UseSharedCompilation=false')
    Invoke-Checked dotnet @('test','tests/WinAppClear.Core.Tests/WinAppClear.Core.Tests.csproj','-c','Release','--no-build','--logger','trx;LogFileName=store-tests.trx','--results-directory',$reportDir)
    Invoke-Checked dotnet @('publish','src/WinAppClear.App/WinAppClear.App.csproj','-c','Release','-p:PublishProfile=Portable','-o',$payload)
    # Extract notices from the dependency/runtime versions actually resolved by restore.
    $assetGraph = Get-Content 'src/WinAppClear.App/obj/project.assets.json' -Raw | ConvertFrom-Json
    $noticeParts = @('Third-party licenses and notices for this self-contained WinAppClear build.')
    $noticeCount = 0
    foreach ($package in $assetGraph.libraries.PSObject.Properties) {
        if ($package.Value.type -ne 'package') { continue }
        foreach ($root in $assetGraph.packageFolders.PSObject.Properties.Name) {
            $packageDir = Join-Path $root $package.Value.path
            if (!(Test-Path $packageDir)) { continue }
            $notices = Get-ChildItem $packageDir -File | Where-Object Name -Match '^(LICENSE|LICENCE|ThirdPartyNotices|THIRD-PARTY-NOTICES)(\.|$)'
            foreach ($notice in $notices) { $noticeParts += "`r`n===== $($package.Name) / $($notice.Name) =====`r`n"; $noticeParts += Get-Content $notice.FullName -Raw; $noticeCount++ }
        }
    }
    foreach ($framework in $assetGraph.project.frameworks.PSObject.Properties.Value) {
        foreach ($download in $framework.downloadDependencies) {
            if ($download.name -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') { continue }
            $resolvedVersion = ($download.version.Trim('[',']') -split ',')[0].Trim()
            foreach ($root in $assetGraph.packageFolders.PSObject.Properties.Name) {
                $runtimeDir = Join-Path $root ($download.name.ToLowerInvariant()+'/'+$resolvedVersion)
                if (!(Test-Path $runtimeDir)) { throw "Runtime notice directory missing: $runtimeDir" }
                foreach ($notice in (Get-ChildItem $runtimeDir -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.|$)')) {
                    $noticeParts += "`r`n===== $($download.name)/$resolvedVersion / $($notice.Name) =====`r`n"; $noticeParts += Get-Content $notice.FullName -Raw; $noticeCount++
                }
            }
        }
    }
    if ($noticeCount -lt 4) { throw 'Expected restored dependency/runtime license notices were not found.' }
    $noticeParts | Set-Content (Join-Path $payload 'THIRD-PARTY-LICENSES.txt') -Encoding utf8
    $exe = Join-Path $payload 'WinAppClear.exe'
    $wix = Join-Path $repo 'artifacts/tools/wix/wix.exe'
    if (!(Test-Path $wix)) {
        Invoke-Checked dotnet @('tool','install','wix','--tool-path',(Split-Path $wix), '--version','6.0.2')
    }
    if ($CertificateThumbprint) {
        if (!$SignToolPath) {
            $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
            $candidate = Get-ChildItem $sdk -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]x64[\\/]' } | Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { $SignToolPath = $candidate.FullName }
        }
        if (!$SignToolPath -or !(Test-Path $SignToolPath)) { throw 'Install Windows SDK Signing Tools, or pass -SignToolPath.' }
        Invoke-Checked $SignToolPath @('sign','/sha1',$CertificateThumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256',$exe)
        if ((Get-AuthenticodeSignature $exe).Status -ne 'Valid') { throw 'Application signature is not trusted. Do not submit.' }
    }
    Invoke-Checked $wix @('build','packaging/Product.wxs','-arch','x64','-d',"ProductVersion=$version",'-d',"PayloadDir=$payload",'-d',"RepoRoot=$repo",'-o',$msi)
    if ($CertificateThumbprint) {
        Invoke-Checked $SignToolPath @('sign','/sha1',$CertificateThumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256',$msi)
        Invoke-Checked $SignToolPath @('verify','/pa','/all','/v',$exe)
        Invoke-Checked $SignToolPath @('verify','/pa','/all','/v',$msi)
    }
    & (Join-Path $PSScriptRoot 'Test-StorePackage.ps1') -MsiPath $msi -ExpectedExe $exe -OutputDirectory $reportDir -RequireSignature:([bool]$CertificateThumbprint)
    Get-ChildItem $release -File | Where-Object Extension -eq '.msi' | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name
    } | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
    $state = [ordered]@{ Version=$version; Installer=$msi; TrustedSignatures=([bool]$CertificateThumbprint); AutomatedChecks='Passed'; VmInstallUpgradeRemoval='Not run by build'; StoreCertification='Not submitted'; BuiltUtc=[DateTime]::UtcNow.ToString('o') }
    $state | ConvertTo-Json | Set-Content (Join-Path $release 'release-status.json') -Encoding utf8
    Copy-Item (Join-Path $repo 'docs/store') (Join-Path $release 'submission') -Recurse
    Copy-Item (Join-Path $repo 'LICENSE') $release
    if (!$CertificateThumbprint) { 'UNSIGNED TEST BUILD. Do not submit or publish this MSI as the Store installer. Rebuild into a NEW output directory with a trusted code-signing certificate; the app must be signed BEFORE embedding it in the MSI.' | Set-Content (Join-Path $release 'UNSIGNED-DO-NOT-SUBMIT.txt') }
    Write-Host "Installer: $msi"
} finally { Pop-Location }
