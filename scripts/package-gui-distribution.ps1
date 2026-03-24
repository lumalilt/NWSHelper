[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$InstallerOutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'installer'),

    [string]$MsixOutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'msix'),

    [string]$ChecksumArtifactsPath,

    [string]$ChecksumOutputFile = 'checksums.sha256',

    [string]$PackageIdentityName,

    [string]$PackageDisplayName,

    [string]$PackagePublisher,

    [string]$PackagePublisherDisplayName,

    [string]$PackageDescription,

    [string]$LogoSourcePath,

    [string]$IsccPath,

    [string]$MakeAppxPath,

    [switch]$SignMsix,

    [string]$SignToolPath,

    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$TimestampServerUrl,

    [switch]$SkipMsix,

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw $ErrorMessage
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw $ErrorMessage
    }

    return (Resolve-Path -LiteralPath $Path).Path
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

$resolvedPublishDirectory = Resolve-ExistingDirectory `
    -Path $PublishDirectory `
    -ErrorMessage "Publish directory '$PublishDirectory' does not exist. Run dotnet publish first."

$resolvedInstallerOutputDirectory = [System.IO.Path]::GetFullPath($InstallerOutputDirectory)
$resolvedMsixOutputDirectory = [System.IO.Path]::GetFullPath($MsixOutputDirectory)

if ([string]::IsNullOrWhiteSpace($ChecksumArtifactsPath)) {
    $ChecksumArtifactsPath = $resolvedPublishDirectory
}

$resolvedChecksumArtifactsPath = [System.IO.Path]::GetFullPath($ChecksumArtifactsPath)
New-Item -ItemType Directory -Path $resolvedChecksumArtifactsPath -Force | Out-Null
$resolvedChecksumArtifactsPath = Resolve-ExistingDirectory `
    -Path $resolvedChecksumArtifactsPath `
    -ErrorMessage "Checksum artifacts path '$resolvedChecksumArtifactsPath' does not exist."

$installerScriptPath = Resolve-ExistingFile `
    -Path (Join-Path $PSScriptRoot 'package-installer-inno.ps1') `
    -ErrorMessage 'package-installer-inno.ps1 was not found.'

$checksumsScriptPath = Resolve-ExistingFile `
    -Path (Join-Path $PSScriptRoot 'generate-artifact-checksums.ps1') `
    -ErrorMessage 'generate-artifact-checksums.ps1 was not found.'

if ($SkipMsix.IsPresent -and $SignMsix.IsPresent) {
    throw 'SkipMsix and SignMsix cannot be used together.'
}

$installerOutputLines =
    if ($ValidateOnly.IsPresent) {
        if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
            & $installerScriptPath -PublishDirectory $resolvedPublishDirectory -Version $Version -OutputDirectory $resolvedInstallerOutputDirectory -IsccPath $IsccPath -ValidateOnly
        }
        else {
            & $installerScriptPath -PublishDirectory $resolvedPublishDirectory -Version $Version -OutputDirectory $resolvedInstallerOutputDirectory -ValidateOnly
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
        & $installerScriptPath -PublishDirectory $resolvedPublishDirectory -Version $Version -OutputDirectory $resolvedInstallerOutputDirectory -IsccPath $IsccPath
    }
    else {
        & $installerScriptPath -PublishDirectory $resolvedPublishDirectory -Version $Version -OutputDirectory $resolvedInstallerOutputDirectory
    }

$installerOutputLines = $installerOutputLines | ForEach-Object { $_.ToString() }
$installerValues = Convert-OutputLinesToDictionary -OutputLines $installerOutputLines

$msixValues = @{}

if ($SkipMsix.IsPresent) {
    $msixValues['Mode'] = 'Skipped'
}
else {
    $msixScriptPath = Resolve-ExistingFile `
        -Path (Join-Path $PSScriptRoot 'package-msix.ps1') `
        -ErrorMessage 'package-msix.ps1 was not found.'

    $msixInvocationParameters = @{
        PublishDirectory = $resolvedPublishDirectory
        Version = $Version
        OutputDirectory = $resolvedMsixOutputDirectory
    }

    if (-not [string]::IsNullOrWhiteSpace($PackageIdentityName)) {
        $msixInvocationParameters.PackageIdentityName = $PackageIdentityName
    }

    if (-not [string]::IsNullOrWhiteSpace($PackageDisplayName)) {
        $msixInvocationParameters.PackageDisplayName = $PackageDisplayName
    }

    if (-not [string]::IsNullOrWhiteSpace($PackagePublisher)) {
        $msixInvocationParameters.PackagePublisher = $PackagePublisher
    }

    if (-not [string]::IsNullOrWhiteSpace($PackagePublisherDisplayName)) {
        $msixInvocationParameters.PackagePublisherDisplayName = $PackagePublisherDisplayName
    }

    if (-not [string]::IsNullOrWhiteSpace($PackageDescription)) {
        $msixInvocationParameters.PackageDescription = $PackageDescription
    }

    if (-not [string]::IsNullOrWhiteSpace($LogoSourcePath)) {
        $msixInvocationParameters.LogoSourcePath = $LogoSourcePath
    }

    if (-not [string]::IsNullOrWhiteSpace($MakeAppxPath)) {
        $msixInvocationParameters.MakeAppxPath = $MakeAppxPath
    }

    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $msixInvocationParameters.SignToolPath = $SignToolPath
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $msixInvocationParameters.CertificatePath = $CertificatePath
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $msixInvocationParameters.CertificatePassword = $CertificatePassword
    }

    if (-not [string]::IsNullOrWhiteSpace($TimestampServerUrl)) {
        $msixInvocationParameters.TimestampServerUrl = $TimestampServerUrl
    }

    if ($SignMsix.IsPresent) {
        $msixInvocationParameters.SignPackage = $true
    }

    if ($ValidateOnly.IsPresent) {
        $msixInvocationParameters.ValidateOnly = $true
    }

    $msixOutputLines = & $msixScriptPath @msixInvocationParameters
    $msixOutputLines = $msixOutputLines | ForEach-Object { $_.ToString() }
    $msixValues = Convert-OutputLinesToDictionary -OutputLines $msixOutputLines
}

$checksumOutputLines = & $checksumsScriptPath -ArtifactsPath $resolvedChecksumArtifactsPath -Algorithm SHA256 -OutputFile $ChecksumOutputFile
$checksumOutputLines = $checksumOutputLines | ForEach-Object { $_.ToString() }
$checksumValues = Convert-OutputLinesToDictionary -OutputLines $checksumOutputLines

Write-Output "Mode=$(if ($ValidateOnly.IsPresent) { 'ValidateOnly' } else { 'Package' })"
Write-Output "PublishDirectory=$resolvedPublishDirectory"
Write-Output "InstallerOutputDirectory=$resolvedInstallerOutputDirectory"
Write-Output "MsixOutputDirectory=$resolvedMsixOutputDirectory"
Write-Output "ChecksumArtifactsPath=$resolvedChecksumArtifactsPath"

if ($installerValues.ContainsKey('Mode')) {
    Write-Output "InstallerMode=$($installerValues['Mode'])"
}

if ($installerValues.ContainsKey('ExpectedInstallerName')) {
    Write-Output "ExpectedInstallerName=$($installerValues['ExpectedInstallerName'])"
}

if ($installerValues.ContainsKey('InstallerPath')) {
    Write-Output "InstallerPath=$($installerValues['InstallerPath'])"
}

if ($msixValues.ContainsKey('Mode')) {
    Write-Output "MsixMode=$($msixValues['Mode'])"
}

if ($msixValues.ContainsKey('ExpectedPackagePath')) {
    Write-Output "ExpectedPackagePath=$($msixValues['ExpectedPackagePath'])"
}

if ($msixValues.ContainsKey('PackagePath')) {
    Write-Output "PackagePath=$($msixValues['PackagePath'])"
}

if ($msixValues.ContainsKey('PackageVersion')) {
    Write-Output "PackageVersion=$($msixValues['PackageVersion'])"
}

if ($msixValues.ContainsKey('PackageSigned')) {
    Write-Output "PackageSigned=$($msixValues['PackageSigned'])"
}

if ($checksumValues.ContainsKey('ChecksumFile')) {
    Write-Output "ChecksumFile=$($checksumValues['ChecksumFile'])"
}

if ($checksumValues.ContainsKey('FileCount')) {
    Write-Output "ChecksumFileCount=$($checksumValues['FileCount'])"
}