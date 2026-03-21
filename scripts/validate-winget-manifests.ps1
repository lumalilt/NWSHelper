[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ManifestRoot = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'winget-manifests'),

    [string]$PackageIdentifier = 'NWSHelper.NWSHelper',

    [string]$DefaultLocale = 'en-US'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Assert-ContentMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$ErrorMessage
    )

    if ($Content -notmatch $Pattern) {
        throw $ErrorMessage
    }
}

$resolvedManifestRoot = Resolve-ExistingDirectory `
    -Path $ManifestRoot `
    -ErrorMessage "Manifest root '$ManifestRoot' was not found."

$manifestDirectory = Join-Path (Join-Path $resolvedManifestRoot $PackageIdentifier) $Version
$resolvedManifestDirectory = Resolve-ExistingDirectory `
    -Path $manifestDirectory `
    -ErrorMessage "Manifest directory '$manifestDirectory' was not found."

$versionManifestPath = Resolve-ExistingFile `
    -Path (Join-Path $resolvedManifestDirectory "$PackageIdentifier.yaml") `
    -ErrorMessage "Version manifest is missing for package '$PackageIdentifier' version '$Version'."

$installerManifestPath = Resolve-ExistingFile `
    -Path (Join-Path $resolvedManifestDirectory "$PackageIdentifier.installer.yaml") `
    -ErrorMessage "Installer manifest is missing for package '$PackageIdentifier' version '$Version'."

$localeManifestPath = Resolve-ExistingFile `
    -Path (Join-Path $resolvedManifestDirectory "$PackageIdentifier.locale.$DefaultLocale.yaml") `
    -ErrorMessage "Locale manifest is missing for package '$PackageIdentifier' version '$Version'."

$versionManifestContent = (Get-Content -LiteralPath $versionManifestPath -Raw) -replace "`r", ''
$installerManifestContent = (Get-Content -LiteralPath $installerManifestPath -Raw) -replace "`r", ''
$localeManifestContent = (Get-Content -LiteralPath $localeManifestPath -Raw) -replace "`r", ''

$escapedIdentifier = [regex]::Escape($PackageIdentifier)
$escapedVersion = [regex]::Escape($Version)
$escapedLocale = [regex]::Escape($DefaultLocale)

Assert-ContentMatch -Content $versionManifestContent -Pattern '(?m)^# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json$' -ErrorMessage 'Version manifest schema header is missing or incorrect.'
Assert-ContentMatch -Content $versionManifestContent -Pattern "(?m)^PackageIdentifier:\s+'$escapedIdentifier'$" -ErrorMessage 'Version manifest is missing PackageIdentifier.'
Assert-ContentMatch -Content $versionManifestContent -Pattern "(?m)^PackageVersion:\s+'$escapedVersion'$" -ErrorMessage 'Version manifest is missing PackageVersion.'
Assert-ContentMatch -Content $versionManifestContent -Pattern "(?m)^DefaultLocale:\s+'$escapedLocale'$" -ErrorMessage 'Version manifest is missing DefaultLocale.'
Assert-ContentMatch -Content $versionManifestContent -Pattern '(?m)^ManifestType:\s+version$' -ErrorMessage 'Version manifest must declare ManifestType: version.'
Assert-ContentMatch -Content $versionManifestContent -Pattern '(?m)^ManifestVersion:\s+1.9.0$' -ErrorMessage 'Version manifest must declare ManifestVersion: 1.9.0.'

Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json$' -ErrorMessage 'Installer manifest schema header is missing or incorrect.'
Assert-ContentMatch -Content $installerManifestContent -Pattern "(?m)^PackageIdentifier:\s+'$escapedIdentifier'$" -ErrorMessage 'Installer manifest is missing PackageIdentifier.'
Assert-ContentMatch -Content $installerManifestContent -Pattern "(?m)^PackageVersion:\s+'$escapedVersion'$" -ErrorMessage 'Installer manifest is missing PackageVersion.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^Installers:$' -ErrorMessage 'Installer manifest is missing Installers collection.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^- Architecture:\s+''[A-Za-z0-9\.-]+''$' -ErrorMessage 'Installer manifest is missing Architecture.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^  InstallerUrl:\s+''https?://[^''\s]+''$' -ErrorMessage 'Installer manifest must define an HTTP/HTTPS InstallerUrl.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^  InstallerSha256:\s+[A-F0-9]{64}$' -ErrorMessage 'Installer manifest must define a 64-character SHA256 value.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^ManifestType:\s+installer$' -ErrorMessage 'Installer manifest must declare ManifestType: installer.'
Assert-ContentMatch -Content $installerManifestContent -Pattern '(?m)^ManifestVersion:\s+1.9.0$' -ErrorMessage 'Installer manifest must declare ManifestVersion: 1.9.0.'

Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json$' -ErrorMessage 'Locale manifest schema header is missing or incorrect.'
Assert-ContentMatch -Content $localeManifestContent -Pattern "(?m)^PackageIdentifier:\s+'$escapedIdentifier'$" -ErrorMessage 'Locale manifest is missing PackageIdentifier.'
Assert-ContentMatch -Content $localeManifestContent -Pattern "(?m)^PackageVersion:\s+'$escapedVersion'$" -ErrorMessage 'Locale manifest is missing PackageVersion.'
Assert-ContentMatch -Content $localeManifestContent -Pattern "(?m)^PackageLocale:\s+'$escapedLocale'$" -ErrorMessage 'Locale manifest is missing PackageLocale.'
Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^Publisher:\s+''.+''$' -ErrorMessage 'Locale manifest is missing Publisher.'
Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^PackageName:\s+''.+''$' -ErrorMessage 'Locale manifest is missing PackageName.'
Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^ShortDescription:\s+''.+''$' -ErrorMessage 'Locale manifest is missing ShortDescription.'
Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^ManifestType:\s+defaultLocale$' -ErrorMessage 'Locale manifest must declare ManifestType: defaultLocale.'
Assert-ContentMatch -Content $localeManifestContent -Pattern '(?m)^ManifestVersion:\s+1.9.0$' -ErrorMessage 'Locale manifest must declare ManifestVersion: 1.9.0.'

Write-Output "ValidationResult=Success"
Write-Output "ManifestDirectory=$resolvedManifestDirectory"
Write-Output "VersionManifestPath=$versionManifestPath"
Write-Output "InstallerManifestPath=$installerManifestPath"
Write-Output "LocaleManifestPath=$localeManifestPath"
