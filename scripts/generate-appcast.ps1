[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDirectory,

    [string]$OutputFile = 'appcast.xml',

    [string]$SignatureOutputFile = 'appcast.xml.signature',

    [string]$BaseDownloadUrl,

    [string]$AppcastUrl,

    [string]$SignatureKey,

    [string]$SignatureKeyEnvironmentVariable = 'NWSHELPER_APPCAST_SIGNING_KEY',

    [string]$GeneratedAtUtc
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

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $base = $BasePath
    if (-not $base.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $base = $base + [System.IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [System.Uri]::new($base)
    $pathUri = [System.Uri]::new($Path)
    $relative = [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
    return $relative.Replace('\\', '/').Replace('\', '/')
}

function Resolve-GeneratedAtUtc {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [DateTimeOffset]::UtcNow
    }

    $parsed = [DateTimeOffset]::Parse(
        $Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal)

    return $parsed.ToUniversalTime()
}

function Build-DownloadUrl {
    param(
        [string]$Base,
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($Base)) {
        return $null
    }

    $normalizedBase = $Base.TrimEnd('/')
    $segments = $RelativePath.Split('/') | ForEach-Object {
        [System.Uri]::EscapeDataString($_)
    }

    return "$normalizedBase/$($segments -join '/')"
}

function Escape-Xml {
    param([string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return ''
    }

    return [System.Security.SecurityElement]::Escape($Value)
}

function Resolve-SignatureKey {
    param(
        [string]$InlineKey,
        [string]$EnvironmentVariableName
    )

    if (-not [string]::IsNullOrWhiteSpace($InlineKey)) {
        return $InlineKey.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($EnvironmentVariableName)) {
        $envValue = [System.Environment]::GetEnvironmentVariable($EnvironmentVariableName)
        if (-not [string]::IsNullOrWhiteSpace($envValue)) {
            return $envValue.Trim()
        }
    }

    return ''
}

function Convert-ToKeyBytes {
    param([string]$KeyMaterial)

    if ([string]::IsNullOrWhiteSpace($KeyMaterial)) {
        throw 'Appcast signing key is required. Provide -SignatureKey or set NWSHELPER_APPCAST_SIGNING_KEY.'
    }

    try {
        return [System.Convert]::FromBase64String($KeyMaterial)
    }
    catch {
        throw 'Appcast signing key must be Base64-encoded bytes (32-byte seed or 64-byte expanded Ed25519 private key).'
    }
}

function Resolve-ChaosNaClAssemblyPath {
    $nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES
    }
    else {
        Join-Path $env:USERPROFILE '.nuget\packages'
    }

    $packageRoot = Join-Path $nugetRoot 'netsparkleupdater.chaos.nacl'
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Could not locate NetSparkle Chaos.NaCl package at '$packageRoot'. Run 'dotnet restore' first."
    }

    $versionDirectories = @(
        Get-ChildItem -LiteralPath $packageRoot -Directory |
        Sort-Object Name -Descending)

    $frameworkPreference = @()
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        $frameworkPreference = @('net462', 'netstandard2.0', 'net6.0', 'net7.0', 'net8.0', 'net9.0')
    }
    else {
        $runtimeMajor = [System.Environment]::Version.Major

        if ($runtimeMajor -ge 9) { $frameworkPreference += 'net9.0' }
        if ($runtimeMajor -ge 8) { $frameworkPreference += 'net8.0' }
        if ($runtimeMajor -ge 7) { $frameworkPreference += 'net7.0' }
        if ($runtimeMajor -ge 6) { $frameworkPreference += 'net6.0' }

        $frameworkPreference += 'netstandard2.0'
        $frameworkPreference += 'net462'
    }

    foreach ($framework in $frameworkPreference) {
        foreach ($versionDirectory in $versionDirectories) {
            $candidate = Join-Path $versionDirectory.FullName (Join-Path 'lib' (Join-Path $framework 'Chaos.NaCl.dll'))
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    $fallback = @(
        Get-ChildItem -LiteralPath $packageRoot -Filter 'Chaos.NaCl.dll' -File -Recurse |
        Sort-Object FullName |
        Select-Object -First 1)

    if ($fallback.Count -gt 0) {
        return $fallback[0].FullName
    }

    throw "Could not resolve Chaos.NaCl.dll under '$packageRoot'."
}

function Resolve-Ed25519Type {
    param([Parameter(Mandatory = $true)][System.Reflection.Assembly]$Assembly)

    $direct = $Assembly.GetType('Chaos.NaCl.Ed25519', $false, $true)
    if ($null -ne $direct) {
        return $direct
    }

    $typedSign = [System.Type[]]@([byte[]], [byte[]])
    $scanCandidate = $Assembly.GetTypes() | Where-Object {
        $_.Name -eq 'Ed25519' -and
        $null -ne $_.GetMethod('Sign', $typedSign)
    } | Select-Object -First 1

    if ($null -ne $scanCandidate) {
        return $scanCandidate
    }

    $candidateNames = @(
        $Assembly.GetTypes() |
        Where-Object { $_.FullName -like '*Ed25519*' } |
        Select-Object -First 10 -ExpandProperty FullName)

    $candidateSummary = if ($candidateNames.Count -gt 0) {
        $candidateNames -join ', '
    }
    else {
        '<none>'
    }

    throw "Unable to resolve an Ed25519 signing type from assembly '$($Assembly.Location)'. Candidates=$candidateSummary"
}

function Compute-Ed25519Signature {
    param(
        [Parameter(Mandatory = $true)][byte[]]$ContentBytes,
        [Parameter(Mandatory = $true)][string]$KeyMaterial
    )

    $keyBytes = Convert-ToKeyBytes -KeyMaterial $KeyMaterial
    $assemblyPath = Resolve-ChaosNaClAssemblyPath
    $loadedAssembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $ed25519Type = Resolve-Ed25519Type -Assembly $loadedAssembly

    [byte[]]$signatureBytes = @()
    if ($keyBytes.Length -eq 32) {
        $publicKey = $null
        $expandedPrivateKey = $null
        $ed25519Type::KeyPairFromSeed([ref]$publicKey, [ref]$expandedPrivateKey, $keyBytes)
        $signatureBytes = $ed25519Type::Sign($ContentBytes, $expandedPrivateKey)
    }
    elseif ($keyBytes.Length -eq 64) {
        $signatureBytes = $ed25519Type::Sign($ContentBytes, $keyBytes)
    }
    else {
        throw "Appcast signing key decoded to $($keyBytes.Length) bytes. Expected 32-byte seed or 64-byte expanded private key."
    }

    return [pscustomobject]@{
        Algorithm = 'ED25519'
        Value = [System.Convert]::ToBase64String($signatureBytes)
    }
}

$resolvedArtifactsPath = Resolve-ExistingDirectory `
    -Path $ArtifactsPath `
    -ErrorMessage "Artifacts path '$ArtifactsPath' does not exist."

$resolvedOutputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $resolvedArtifactsPath 'update'
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $resolvedArtifactsPath $OutputDirectory))
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$resolvedAppcastPath = if ([System.IO.Path]::IsPathRooted($OutputFile)) {
    [System.IO.Path]::GetFullPath($OutputFile)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $resolvedOutputDirectory $OutputFile))
}

$resolvedSignaturePath = if ([System.IO.Path]::IsPathRooted($SignatureOutputFile)) {
    [System.IO.Path]::GetFullPath($SignatureOutputFile)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $resolvedOutputDirectory $SignatureOutputFile))
}

New-Item -ItemType Directory -Path (Split-Path -Path $resolvedAppcastPath -Parent) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Path $resolvedSignaturePath -Parent) -Force | Out-Null

$matchingInstallers = @(
    Get-ChildItem -LiteralPath $resolvedArtifactsPath -File -Recurse |
    Where-Object { [System.StringComparer]::OrdinalIgnoreCase.Equals($_.Name, "NWSHelper-Setup-$Version.exe") } |
    Sort-Object FullName)

if ($matchingInstallers.Count -eq 0) {
    throw "Could not find installer 'NWSHelper-Setup-$Version.exe' under '$resolvedArtifactsPath'."
}

$installerPath = $matchingInstallers[0].FullName
$installerLength = [long]$matchingInstallers[0].Length
$installerRelativePath = Get-RelativePath -BasePath $resolvedArtifactsPath -Path $installerPath
$installerDownloadUrl = Build-DownloadUrl -Base $BaseDownloadUrl -RelativePath $installerRelativePath
if ([string]::IsNullOrWhiteSpace($installerDownloadUrl)) {
    $installerDownloadUrl = $installerRelativePath
}

$appcastRelativePath = Get-RelativePath -BasePath $resolvedArtifactsPath -Path $resolvedAppcastPath
$computedAppcastUrl = Build-DownloadUrl -Base $BaseDownloadUrl -RelativePath $appcastRelativePath
$finalAppcastUrl = if (-not [string]::IsNullOrWhiteSpace($AppcastUrl)) {
    $AppcastUrl.Trim()
}
elseif (-not [string]::IsNullOrWhiteSpace($computedAppcastUrl)) {
    $computedAppcastUrl
}
else {
    $appcastRelativePath
}

$generatedAt = Resolve-GeneratedAtUtc -Value $GeneratedAtUtc
$generatedAtIso = $generatedAt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$pubDate = $generatedAt.UtcDateTime.ToString('r', [System.Globalization.CultureInfo]::InvariantCulture)

$effectiveSignatureKey = Resolve-SignatureKey -InlineKey $SignatureKey -EnvironmentVariableName $SignatureKeyEnvironmentVariable
$installerBytes = [System.IO.File]::ReadAllBytes($installerPath)
$installerSignature = Compute-Ed25519Signature -ContentBytes $installerBytes -KeyMaterial $effectiveSignatureKey

$escapedVersion = Escape-Xml -Value $Version
$escapedInstallerDownloadUrl = Escape-Xml -Value $installerDownloadUrl
$escapedAppcastUrl = Escape-Xml -Value $finalAppcastUrl
$escapedInstallerSignature = Escape-Xml -Value $installerSignature.Value

$appcastXml = @"
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>NWS Helper Updates</title>
    <link>$escapedAppcastUrl</link>
    <description>NWS Helper update feed</description>
    <item>
      <title>NWS Helper $escapedVersion</title>
      <pubDate>$pubDate</pubDate>
        <enclosure url="$escapedInstallerDownloadUrl" sparkle:version="$escapedVersion" sparkle:shortVersionString="$escapedVersion" sparkle:os="windows" length="$installerLength" type="application/octet-stream" sparkle:signature="$escapedInstallerSignature" sparkle:edSignature="$escapedInstallerSignature" />
    </item>
  </channel>
</rss>
"@

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$contentBytes = $utf8NoBom.GetBytes($appcastXml)
[System.IO.File]::WriteAllText($resolvedAppcastPath, $appcastXml, $utf8NoBom)

$signature = Compute-Ed25519Signature -ContentBytes $contentBytes -KeyMaterial $effectiveSignatureKey
[System.IO.File]::WriteAllText($resolvedSignaturePath, $signature.Value, $utf8NoBom)

Write-Output "Version=$Version"
Write-Output "GeneratedAtUtc=$generatedAtIso"
Write-Output "InstallerPath=$installerPath"
Write-Output "InstallerRelativePath=$installerRelativePath"
Write-Output "InstallerDownloadUrl=$installerDownloadUrl"
Write-Output "AppcastFile=$resolvedAppcastPath"
Write-Output "AppcastRelativePath=$appcastRelativePath"
Write-Output "AppcastUrl=$finalAppcastUrl"
Write-Output "SignatureFile=$resolvedSignaturePath"
Write-Output "InstallerSignatureAlgorithm=$($installerSignature.Algorithm)"
Write-Output "SignatureAlgorithm=$($signature.Algorithm)"
