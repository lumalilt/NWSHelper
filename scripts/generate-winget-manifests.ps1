[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'winget-manifests'),

    [string]$PackageIdentifier = 'NWSHelper.NWSHelper',

    [string]$PackageName = 'NWS Helper',

    [string]$Publisher = 'NWS Helper',

    [string]$Moniker = 'nwshelper',

    [string]$ShortDescription = 'NWS Helper desktop application.',

    [string]$License = 'Proprietary',

    [string]$LicenseUrl,

    [string]$DefaultLocale = 'en-US',

    [string]$InstallerType = 'inno',

    [string]$Architecture = 'x64',

    [string]$Scope = 'machine',

    [string]$InstallerUrl,

    [string]$InstallerSha256,

    [string]$InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-OutputDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    return (Resolve-Path -LiteralPath $Path).Path
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

function Convert-ToYamlSingleQuoted {
    param([Parameter(Mandatory = $false)][string]$Value)

    if ($null -eq $Value) {
        return "''"
    }

    $escaped = $Value.Replace("'", "''")
    return "'$escaped'"
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[A-Za-z0-9\.-]*)?$') {
    throw "Version '$Version' is not valid for WinGet manifest scaffolding. Expected semantic version format."
}

$resolvedOutputDirectory = Resolve-OutputDirectory -Path $OutputDirectory

$resolvedInstallerUrl = $InstallerUrl
if ([string]::IsNullOrWhiteSpace($resolvedInstallerUrl)) {
    $resolvedInstallerUrl = "https://example.invalid/NWSHelper/$Version/NWSHelper-setup.exe"
}

$resolvedInstallerSha256 = $InstallerSha256
if ([string]::IsNullOrWhiteSpace($resolvedInstallerSha256)) {
    if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
        $resolvedInstallerPath = Resolve-ExistingFile `
            -Path $InstallerPath `
            -ErrorMessage "InstallerPath '$InstallerPath' was not found."

        $resolvedInstallerSha256 = (Get-FileHash -LiteralPath $resolvedInstallerPath -Algorithm SHA256).Hash
    }
    else {
        $resolvedInstallerSha256 = ('0' * 64)
    }
}

if ($resolvedInstallerSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'InstallerSha256 must contain exactly 64 hexadecimal characters.'
}

$resolvedInstallerSha256 = $resolvedInstallerSha256.ToUpperInvariant()

$manifestDirectory = Join-Path (Join-Path $resolvedOutputDirectory $PackageIdentifier) $Version
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null

$versionManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.yaml"
$installerManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.installer.yaml"
$localeManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.locale.$DefaultLocale.yaml"

$versionManifestLines = @(
    '# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json'
    "PackageIdentifier: $(Convert-ToYamlSingleQuoted -Value $PackageIdentifier)"
    "PackageVersion: $(Convert-ToYamlSingleQuoted -Value $Version)"
    "DefaultLocale: $(Convert-ToYamlSingleQuoted -Value $DefaultLocale)"
    'ManifestType: version'
    'ManifestVersion: 1.9.0'
)

$installerManifestLines = @(
    '# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json'
    "PackageIdentifier: $(Convert-ToYamlSingleQuoted -Value $PackageIdentifier)"
    "PackageVersion: $(Convert-ToYamlSingleQuoted -Value $Version)"
    "InstallerType: $(Convert-ToYamlSingleQuoted -Value $InstallerType)"
    'Installers:'
    "- Architecture: $(Convert-ToYamlSingleQuoted -Value $Architecture)"
    "  InstallerUrl: $(Convert-ToYamlSingleQuoted -Value $resolvedInstallerUrl)"
    "  InstallerSha256: $resolvedInstallerSha256"
    "  Scope: $(Convert-ToYamlSingleQuoted -Value $Scope)"
    'ManifestType: installer'
    'ManifestVersion: 1.9.0'
)

$localeManifestLines = @(
    '# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json'
    "PackageIdentifier: $(Convert-ToYamlSingleQuoted -Value $PackageIdentifier)"
    "PackageVersion: $(Convert-ToYamlSingleQuoted -Value $Version)"
    "PackageLocale: $(Convert-ToYamlSingleQuoted -Value $DefaultLocale)"
    "Publisher: $(Convert-ToYamlSingleQuoted -Value $Publisher)"
    "PackageName: $(Convert-ToYamlSingleQuoted -Value $PackageName)"
    "Moniker: $(Convert-ToYamlSingleQuoted -Value $Moniker)"
    "ShortDescription: $(Convert-ToYamlSingleQuoted -Value $ShortDescription)"
    "License: $(Convert-ToYamlSingleQuoted -Value $License)"
)

if (-not [string]::IsNullOrWhiteSpace($LicenseUrl)) {
    $localeManifestLines += "LicenseUrl: $(Convert-ToYamlSingleQuoted -Value $LicenseUrl)"
}

$localeManifestLines += @(
    'ManifestType: defaultLocale'
    'ManifestVersion: 1.9.0'
)

$lineEnding = [System.Environment]::NewLine
$versionManifestContent = ([string]::Join($lineEnding, $versionManifestLines)) + $lineEnding
$installerManifestContent = ([string]::Join($lineEnding, $installerManifestLines)) + $lineEnding
$localeManifestContent = ([string]::Join($lineEnding, $localeManifestLines)) + $lineEnding

Write-Utf8NoBom -Path $versionManifestPath -Content $versionManifestContent
Write-Utf8NoBom -Path $installerManifestPath -Content $installerManifestContent
Write-Utf8NoBom -Path $localeManifestPath -Content $localeManifestContent

Write-Output "ManifestDirectory=$manifestDirectory"
Write-Output "VersionManifestPath=$versionManifestPath"
Write-Output "InstallerManifestPath=$installerManifestPath"
Write-Output "LocaleManifestPath=$localeManifestPath"
Write-Output "InstallerUrl=$resolvedInstallerUrl"
Write-Output "InstallerSha256=$resolvedInstallerSha256"
