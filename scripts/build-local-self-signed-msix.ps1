[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'NWSHelper.Gui') 'NWSHelper.Gui.csproj'),

    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$TargetFramework = 'net10.0-windows10.0.19041.0',

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$ArtifactsRoot = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'local-self-signed-msix'),

    [string]$PublishDirectory,

    [string]$MsixOutputDirectory,

    [string]$PackageIdentityName = '51949LumaLilt.NWSHelper',

    [string]$PackageDisplayName = 'NWS Helper',

    [string]$PackagePublisher = 'CN=D4894D8B-8976-46A8-9EE3-93D603B2BFF4',

    [string]$PackagePublisherDisplayName = 'LumaLilt',

    [string]$PackageDescription = 'NWS Helper',

    [Parameter(Mandatory = $true)]
    [string]$CertificatePassword,

    [string]$CertificateFriendlyName = 'NWS Helper Local Test Signing Certificate',

    [string]$CertificateOutputPath,

    [switch]$ForceNewCertificate,

    [switch]$ImportCertificateToTrustedPeople,

    [switch]$SkipPublish,

    [switch]$InstallPackage,

    [switch]$SkipLaunchAfterInstall,
    [switch]$RefreshShellIconCache,

    [string]$MakeAppxPath,

    [string]$SignToolPath,

    [string]$TimestampServerUrl,

    [string]$CorePackageVersion,

    [string]$CorePackageSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'build-local-self-signed-msix.ps1 is only supported on Windows because it uses certificate store and MSIX installation cmdlets.'
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ErrorMessage
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw $ErrorMessage
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ErrorMessage
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw $ErrorMessage
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-OutputDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    return (Resolve-Path -LiteralPath $fullPath).Path
}

function Resolve-OutputFileParent {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Path $fullPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    return $fullPath
}

function Convert-OutputLinesToDictionary {
    param([string[]]$OutputLines)

    $values = @{}
    foreach ($line in $OutputLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -le 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex)
        $value = $line.Substring($separatorIndex + 1)
        $values[$key] = $value
    }

    return $values
}

function Get-DefaultVersion {
    param([Parameter(Mandatory = $true)][string]$VersionJsonPath)

    $versionData = Get-Content -LiteralPath $VersionJsonPath -Raw | ConvertFrom-Json
    foreach ($requiredProperty in @('major', 'minor', 'patch')) {
        if ($null -eq $versionData.$requiredProperty) {
            throw "version.json is missing '$requiredProperty'."
        }
    }

    $timestamp = Get-Date
    $dateStampedPatch = ($versionData.patch * 1000) + $timestamp.DayOfYear
    if ($dateStampedPatch -gt 65535) {
        throw "The timestamp-derived patch segment '$dateStampedPatch' exceeds the MSIX limit (65535). Pass -Version explicitly."
    }

    $revision = [int]$timestamp.ToString('HHmm')
    return "$($versionData.major).$($versionData.minor).$dateStampedPatch.$revision"
}

function Test-IsAdministrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-OrCreateSigningCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$Subject,
        [Parameter(Mandatory = $true)][string]$FriendlyName,
        [Parameter(Mandatory = $true)][string]$PfxPath,
        [Parameter(Mandatory = $true)][securestring]$Password,
        [switch]$ForceNewCertificate
    )

    $certificate = $null
    if (-not $ForceNewCertificate.IsPresent) {
        $certificate = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
            Where-Object { $_.Subject -eq $Subject } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
    }

    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Subject `
            -FriendlyName $FriendlyName `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy Exportable `
            -KeyUsage DigitalSignature `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(2)
    }

    Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password $Password -Force | Out-Null
    return $certificate
}

function Import-CertificateForLocalMsixTrustIfNeeded {
    param(
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][string[]]$StorePaths
    )

    $publicCertificatePath = Join-Path ([System.IO.Path]::GetTempPath()) ("nwshelper-local-msix-" + [Guid]::NewGuid().ToString('N') + '.cer')
        $importedStorePaths = [System.Collections.Generic.List[string]]::new()

    try {
        Export-Certificate -Cert $Certificate -FilePath $publicCertificatePath -Force | Out-Null

        foreach ($storePath in $StorePaths) {
            $existingCertificate = Get-ChildItem -Path $storePath |
                Where-Object { $_.Thumbprint -eq $Thumbprint } |
                Select-Object -First 1

            if ($null -eq $existingCertificate) {
                Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation $storePath | Out-Null
                $importedStorePaths.Add($storePath)
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $publicCertificatePath -PathType Leaf) {
            Remove-Item -LiteralPath $publicCertificatePath -Force
        }
    }

    return @($importedStorePaths)
}

function Test-ImportedStorePathMatch {
    param(
        [string[]]$ImportedStorePaths,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    return @($ImportedStorePaths | Where-Object { $_ -like $Pattern }).Count -gt 0
}

function Install-LocalMsixPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageIdentityName,
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    $existingPackage = Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $existingPackage) {
        Remove-AppxPackage -Package $existingPackage.PackageFullName
    }

    Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown
}

function Refresh-ExplorerIconCache {
    $explorerCacheDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
    if (-not (Test-Path -LiteralPath $explorerCacheDirectory -PathType Container)) {
        return $false
    }

    $explorerProcesses = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)
    foreach ($explorerProcess in $explorerProcesses) {
        Stop-Process -Id $explorerProcess.Id -Force
    }

    Get-ChildItem -LiteralPath $explorerCacheDirectory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'iconcache*' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Start-Process -FilePath 'explorer.exe' | Out-Null
    return $true
}

function Confirm-ExplorerIconCacheRefresh {
    $title = 'Rebuild Explorer icon cache?'
    $message = 'This will stop and restart Windows Explorer to rebuild the shell icon cache. Your screen may flash, open File Explorer windows will close, and shell-hosted UI may redraw or restart. Continue?'
    $choices = [System.Management.Automation.Host.ChoiceDescription[]]@(
        [System.Management.Automation.Host.ChoiceDescription]::new('&Refresh', 'Restart Explorer and rebuild the icon cache.'),
        [System.Management.Automation.Host.ChoiceDescription]::new('&Skip', 'Leave the current Explorer icon cache unchanged.')
    )

    return $Host.UI.PromptForChoice($title, $message, $choices, 1) -eq 0
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$versionJsonPath = Resolve-ExistingFile -Path (Join-Path $repoRoot 'version.json') -ErrorMessage 'version.json was not found.'
$packageScriptPath = Resolve-ExistingFile -Path (Join-Path $PSScriptRoot 'package-msix.ps1') -ErrorMessage 'package-msix.ps1 was not found.'
$resolvedProjectPath = Resolve-ExistingFile -Path $ProjectPath -ErrorMessage "Project '$ProjectPath' was not found."

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultVersion -VersionJsonPath $versionJsonPath
}

$resolvedArtifactsRoot = Resolve-OutputDirectory -Path $ArtifactsRoot
$resolvedPublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    [System.IO.Path]::GetFullPath((Join-Path $resolvedArtifactsRoot (Join-Path 'publish' $RuntimeIdentifier)))
}
else {
    [System.IO.Path]::GetFullPath($PublishDirectory)
}
$resolvedMsixOutputDirectory = Resolve-OutputDirectory -Path $(if ([string]::IsNullOrWhiteSpace($MsixOutputDirectory)) { Join-Path $resolvedArtifactsRoot 'msix' } else { $MsixOutputDirectory })
$resolvedCertificateOutputPath = Resolve-OutputFileParent -Path $(if ([string]::IsNullOrWhiteSpace($CertificateOutputPath)) { Join-Path (Join-Path $resolvedArtifactsRoot 'certificates') 'nwshelper-local-test-signing.pfx' } else { $CertificateOutputPath })

$certificatePasswordSecureString = ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
$certificate = Get-OrCreateSigningCertificate `
    -Subject $PackagePublisher `
    -FriendlyName $CertificateFriendlyName `
    -PfxPath $resolvedCertificateOutputPath `
    -Password $certificatePasswordSecureString `
    -ForceNewCertificate:$ForceNewCertificate.IsPresent

$isAdministrator = Test-IsAdministrator
$currentUserTrustStorePaths = @('Cert:\CurrentUser\TrustedPeople', 'Cert:\CurrentUser\Root')
$localMachineTrustStorePaths = @('Cert:\LocalMachine\TrustedPeople', 'Cert:\LocalMachine\Root')
$importedStorePaths = @()

if ($ImportCertificateToTrustedPeople.IsPresent -or $InstallPackage.IsPresent) {
    $importedStorePaths += @(Import-CertificateForLocalMsixTrustIfNeeded -Certificate $certificate -Thumbprint $certificate.Thumbprint -StorePaths $currentUserTrustStorePaths)
}

if ($SkipPublish.IsPresent) {
    $resolvedPublishDirectory = Resolve-ExistingDirectory -Path $resolvedPublishDirectory -ErrorMessage "Publish directory '$resolvedPublishDirectory' does not exist. Omit -SkipPublish to build it automatically."
}
else {
    New-Item -ItemType Directory -Path $resolvedPublishDirectory -Force | Out-Null

    $publishArguments = @(
        'publish',
        $resolvedProjectPath,
        '-c', $Configuration,
        '-f', $TargetFramework,
        '-r', $RuntimeIdentifier,
        '--self-contained', 'false',
        '-o', $resolvedPublishDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($CorePackageVersion)) {
        $publishArguments += ('-p:CorePackageVersion=' + $CorePackageVersion)
    }

    if (-not [string]::IsNullOrWhiteSpace($CorePackageSource)) {
        $publishArguments += ('-p:CorePackageSource=' + $CorePackageSource)
    }

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $resolvedPublishDirectory = Resolve-ExistingDirectory -Path $resolvedPublishDirectory -ErrorMessage "Publish directory '$resolvedPublishDirectory' does not exist after publish."
}

$packageArguments = @{
    PublishDirectory = $resolvedPublishDirectory
    Version = $Version
    OutputDirectory = $resolvedMsixOutputDirectory
    PackageIdentityName = $PackageIdentityName
    PackageDisplayName = $PackageDisplayName
    PackagePublisher = $PackagePublisher
    PackagePublisherDisplayName = $PackagePublisherDisplayName
    PackageDescription = $PackageDescription
    SignPackage = $true
    CertificatePath = $resolvedCertificateOutputPath
    CertificatePassword = $CertificatePassword
}

if (-not [string]::IsNullOrWhiteSpace($MakeAppxPath)) {
    $packageArguments.MakeAppxPath = $MakeAppxPath
}

if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    $packageArguments.SignToolPath = $SignToolPath
}

if (-not [string]::IsNullOrWhiteSpace($TimestampServerUrl)) {
    $packageArguments.TimestampServerUrl = $TimestampServerUrl
}

$packageOutputLines = & $packageScriptPath @packageArguments
$packageValues = Convert-OutputLinesToDictionary -OutputLines ($packageOutputLines | ForEach-Object { $_.ToString() })

if (-not $packageValues.ContainsKey('PackagePath')) {
    throw 'package-msix.ps1 did not report PackagePath.'
}

$resolvedPackagePath = Resolve-ExistingFile -Path $packageValues['PackagePath'] -ErrorMessage "Signed MSIX '$($packageValues['PackagePath'])' was not created."

$launchedInstalledApp = $false
$installedAppPath = ''
$shellIconCacheRefreshRequested = $false
$shellIconCacheRefreshDeclined = $false
$shellIconCacheRefreshed = $false
$installUsedLocalMachineFallback = $false

if ($InstallPackage.IsPresent) {
    try {
        Install-LocalMsixPackage -PackageIdentityName $PackageIdentityName -PackagePath $resolvedPackagePath
    }
    catch {
        if (-not $isAdministrator) {
            throw "Add-AppxPackage failed after importing the signing certificate into CurrentUser\TrustedPeople and CurrentUser\Root. Re-run the helper as Administrator to import LocalMachine\TrustedPeople and LocalMachine\Root and retry automatically. Underlying error: $($_.Exception.Message)"
        }

        $importedStorePaths += @(Import-CertificateForLocalMsixTrustIfNeeded -Certificate $certificate -Thumbprint $certificate.Thumbprint -StorePaths $localMachineTrustStorePaths)
        Install-LocalMsixPackage -PackageIdentityName $PackageIdentityName -PackagePath $resolvedPackagePath
        $installUsedLocalMachineFallback = $true
    }

    if ($RefreshShellIconCache.IsPresent) {
        $shellIconCacheRefreshRequested = $true

        if (Confirm-ExplorerIconCacheRefresh) {
            $shellIconCacheRefreshed = Refresh-ExplorerIconCache
        }
        else {
            $shellIconCacheRefreshDeclined = $true
        }
    }

    if (-not $SkipLaunchAfterInstall.IsPresent) {
        $installedPackage = Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $installedPackage) {
            throw "Installed package '$PackageIdentityName' could not be resolved after Add-AppxPackage completed."
        }

        $installedAppPath = Join-Path $installedPackage.InstallLocation 'NWSHelper.Gui.exe'
        $installedAppPath = Resolve-ExistingFile -Path $installedAppPath -ErrorMessage "Installed app executable '$installedAppPath' was not found."
        Start-Process -FilePath $installedAppPath | Out-Null
        $launchedInstalledApp = $true
    }
}

Write-Output 'Mode=LocalSelfSignedMsix'
Write-Output "ProjectPath=$resolvedProjectPath"
Write-Output "Version=$Version"
Write-Output "PublishDirectory=$resolvedPublishDirectory"
Write-Output "MsixOutputDirectory=$resolvedMsixOutputDirectory"
Write-Output "PackagePath=$resolvedPackagePath"
Write-Output "PackageIdentityName=$PackageIdentityName"
Write-Output "PackagePublisher=$PackagePublisher"
Write-Output "CertificatePath=$resolvedCertificateOutputPath"
Write-Output "CertificateThumbprint=$($certificate.Thumbprint)"
Write-Output "IsAdministrator=$("$isAdministrator".ToLowerInvariant())"
Write-Output "CertificateImportedToTrustedPeople=$(([string](Test-ImportedStorePathMatch -ImportedStorePaths $importedStorePaths -Pattern '*TrustedPeople')).ToLowerInvariant())"
Write-Output "CertificateImportedToRoot=$(([string](Test-ImportedStorePathMatch -ImportedStorePaths $importedStorePaths -Pattern '*Root')).ToLowerInvariant())"
Write-Output "CertificateImportedToLocalMachine=$(([string](Test-ImportedStorePathMatch -ImportedStorePaths $importedStorePaths -Pattern 'Cert:\LocalMachine\*')).ToLowerInvariant())"
Write-Output "InstallPackage=$("$($InstallPackage.IsPresent)".ToLowerInvariant())"
Write-Output "InstallUsedLocalMachineFallback=$("$installUsedLocalMachineFallback".ToLowerInvariant())"
Write-Output "LaunchAfterInstall=$(([string](($InstallPackage.IsPresent) -and (-not $SkipLaunchAfterInstall.IsPresent))).ToLowerInvariant())"
Write-Output "ShellIconCacheRefreshRequested=$("$shellIconCacheRefreshRequested".ToLowerInvariant())"
Write-Output "ShellIconCacheRefreshDeclined=$("$shellIconCacheRefreshDeclined".ToLowerInvariant())"
Write-Output "ShellIconCacheRefreshed=$("$shellIconCacheRefreshed".ToLowerInvariant())"
Write-Output "LaunchedInstalledApp=$("$launchedInstalledApp".ToLowerInvariant())"
if (-not [string]::IsNullOrWhiteSpace($installedAppPath)) {
    Write-Output "InstalledAppPath=$installedAppPath"
}
if (-not $InstallPackage.IsPresent) {
    Write-Output "InstallCommand=Add-AppxPackage -Path '$resolvedPackagePath'"
}