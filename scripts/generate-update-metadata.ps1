[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputFile = 'update-metadata.json',

    [string]$BaseDownloadUrl,

    [string]$GeneratedAtUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FileSha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = $null
    $hasher = $null

    try {
        $stream = [System.IO.File]::OpenRead($Path)
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        $hashBytes = $hasher.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        if ($hasher) {
            $hasher.Dispose()
        }

        if ($stream) {
            $stream.Dispose()
        }
    }
}

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
        return (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }

    $parsed = [DateTimeOffset]::Parse(
        $Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal)

    return $parsed.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

function Get-DownloadUrl {
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

$resolvedArtifactsPath = Resolve-ExistingDirectory `
    -Path $ArtifactsPath `
    -ErrorMessage "Artifacts path '$ArtifactsPath' does not exist."

if ([System.IO.Path]::IsPathRooted($OutputFile)) {
    $resolvedOutputFilePath = [System.IO.Path]::GetFullPath($OutputFile)
}
else {
    $resolvedOutputFilePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedArtifactsPath $OutputFile))
}

$resolvedOutputDirectory = Split-Path -Path $resolvedOutputFilePath -Parent
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$files = @(Get-ChildItem `
    -LiteralPath $resolvedArtifactsPath `
    -File `
    -Recurse |
    Where-Object { -not [System.StringComparer]::OrdinalIgnoreCase.Equals($_.FullName, $resolvedOutputFilePath) } |
    Sort-Object FullName)

if ($files.Count -eq 0) {
    throw "No files were found under '$resolvedArtifactsPath' to include in update metadata."
}

$generatedAt = Resolve-GeneratedAtUtc -Value $GeneratedAtUtc

$entries = foreach ($file in $files) {
    $relativePath = Get-RelativePath -BasePath $resolvedArtifactsPath -Path $file.FullName
    $sha256 = Get-FileSha256Hex -Path $file.FullName
    $downloadUrl = Get-DownloadUrl -Base $BaseDownloadUrl -RelativePath $relativePath

    $entry = [ordered]@{
        path = $relativePath
        sizeBytes = [long]$file.Length
        sha256 = $sha256
    }

    if (-not [string]::IsNullOrWhiteSpace($downloadUrl)) {
        $entry.downloadUrl = $downloadUrl
    }

    [pscustomobject]$entry
}

$metadata = [ordered]@{
    version = $Version
    generatedAtUtc = $generatedAt
    checksumAlgorithm = 'SHA256'
    fileCount = $entries.Count
    artifacts = @($entries)
}

if (-not [string]::IsNullOrWhiteSpace($BaseDownloadUrl)) {
    $metadata.downloadBaseUrl = $BaseDownloadUrl.TrimEnd('/')
}

$json = [pscustomobject]$metadata | ConvertTo-Json -Depth 8
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($resolvedOutputFilePath, $json, $utf8NoBom)

Write-Output "Version=$Version"
Write-Output "GeneratedAtUtc=$generatedAt"
Write-Output "FileCount=$($entries.Count)"
Write-Output "MetadataFile=$resolvedOutputFilePath"
