[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsPath,

    [ValidateSet('SHA256', 'SHA384', 'SHA512', 'MD5')]
    [string]$Algorithm = 'SHA256',

    [string]$OutputFile,

    [switch]$NoRecurse
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

$resolvedArtifactsPath = Resolve-ExistingDirectory `
    -Path $ArtifactsPath `
    -ErrorMessage "Artifacts path '$ArtifactsPath' does not exist."

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $outputFileName = "checksums.$($Algorithm.ToLowerInvariant())"
    $resolvedOutputFilePath = Join-Path $resolvedArtifactsPath $outputFileName
}
elseif ([System.IO.Path]::IsPathRooted($OutputFile)) {
    $resolvedOutputFilePath = [System.IO.Path]::GetFullPath($OutputFile)
}
else {
    $resolvedOutputFilePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedArtifactsPath $OutputFile))
}

$resolvedOutputFilePath = [System.IO.Path]::GetFullPath($resolvedOutputFilePath)
$resolvedOutputDirectory = Split-Path -Path $resolvedOutputFilePath -Parent
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$files = @(Get-ChildItem `
    -LiteralPath $resolvedArtifactsPath `
    -File `
    -Recurse:(!$NoRecurse.IsPresent) |
    Where-Object { -not [System.StringComparer]::OrdinalIgnoreCase.Equals($_.FullName, $resolvedOutputFilePath) } |
    Sort-Object FullName)

if ($files.Count -eq 0) {
    throw "No files were found under '$resolvedArtifactsPath' to hash."
}

$lines = foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm $Algorithm).Hash.ToLowerInvariant()
    $relativePath = Get-RelativePath -BasePath $resolvedArtifactsPath -Path $file.FullName
    "$hash *$relativePath"
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllLines($resolvedOutputFilePath, $lines, $utf8NoBom)

Write-Output "ArtifactRoot=$resolvedArtifactsPath"
Write-Output "Algorithm=$Algorithm"
Write-Output "FileCount=$($files.Count)"
Write-Output "ChecksumFile=$resolvedOutputFilePath"
