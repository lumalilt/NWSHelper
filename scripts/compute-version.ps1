[CmdletBinding()]
param(
    [switch]$TagBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-VersionData([string]$versionFilePath) {
    if (-not (Test-Path -LiteralPath $versionFilePath)) {
        throw "Could not find version source file: $versionFilePath"
    }

    $json = Get-Content -LiteralPath $versionFilePath -Raw
    $data = $json | ConvertFrom-Json

    foreach ($propertyName in @('major', 'minor', 'patch')) {
        if (-not ($data.PSObject.Properties.Name -contains $propertyName)) {
            throw "version.json is missing required property '$propertyName'."
        }
    }

    return @{
        Major = [int]$data.major
        Minor = [int]$data.minor
        Patch = [int]$data.patch
    }
}

function Test-IsTagBuildFromEnvironment {
    if ($env:GITHUB_REF_TYPE -eq 'tag') {
        return $true
    }

    if ($env:GITHUB_REF -match '^refs/tags/') {
        return $true
    }

    if ($env:BUILD_SOURCEBRANCH -match '^refs/tags/') {
        return $true
    }

    if (-not [string]::IsNullOrWhiteSpace($env:CI_COMMIT_TAG)) {
        return $true
    }

    return $false
}

function Get-RunNumber {
    $candidates = @(
        $env:GITHUB_RUN_NUMBER,
        $env:BUILD_BUILDID,
        $env:BUILD_BUILDNUMBER
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $trimmed = $candidate.Trim()
        $parsed = 0L
        if ([long]::TryParse($trimmed, [ref]$parsed) -and $parsed -ge 0) {
            return [string]$parsed
        }

        $match = [regex]::Match($trimmed, '\d+')
        if ($match.Success) {
            return $match.Value
        }
    }

    return '0'
}

function Get-ShortSha {
    $shaCandidates = @(
        $env:GITHUB_SHA,
        $env:BUILD_SOURCEVERSION
    )

    foreach ($candidate in $shaCandidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $trimmed = $candidate.Trim()
        if ($trimmed.Length -ge 8) {
            return $trimmed.Substring(0, 8)
        }

        return $trimmed
    }

    try {
        $gitSha = git rev-parse --short=8 HEAD 2>$null
        if (-not [string]::IsNullOrWhiteSpace($gitSha)) {
            return $gitSha.Trim()
        }
    }
    catch {
    }

    return 'unknown'
}

function Get-FileVersionRunSegment([string]$runNumber) {
    $parsed = 0L
    if (-not [long]::TryParse($runNumber, [ref]$parsed)) {
        return 0
    }

    if ($parsed -lt 0) {
        return 0
    }

    if ($parsed -gt 65535) {
        return 65535
    }

    return [int]$parsed
}

$repoRoot = Get-RepositoryRoot
$versionFilePath = Join-Path $repoRoot 'version.json'
$versionData = Get-VersionData -versionFilePath $versionFilePath

$major = $versionData.Major
$minor = $versionData.Minor
$patch = $versionData.Patch

$utcNow = (Get-Date).ToUniversalTime()
$datePart = $utcNow.ToString('yyyyMMdd')
$timePart = $utcNow.ToString('HHmm')
$dateTimePart = $utcNow.ToString('yyyyMMddHHmm')
$yyddd = '{0}{1:000}' -f $utcNow.ToString('yy'), $utcNow.DayOfYear

$versionPrefix = "$major.$minor.$patch"
$runNumber = Get-RunNumber
$fileVersionRun = Get-FileVersionRunSegment -runNumber $runNumber
$shortSha = Get-ShortSha
$isTagBuild = $TagBuild.IsPresent -or (Test-IsTagBuildFromEnvironment)

if ($isTagBuild) {
    $version = $versionPrefix
}
else {
    # Prefix the time segment to avoid numeric identifiers with leading zeros,
    # which NuGet rejects for SemVer prerelease labels.
    $version = "$versionPrefix-ci.$datePart.t$timePart.$runNumber"
}

$fileVersion = "$major.$minor.$yyddd.$fileVersionRun"
$informationalVersion = "$versionPrefix+build.$dateTimePart.$runNumber.sha.$shortSha"

$outputs = [ordered]@{
    Version = $version
    FileVersion = $fileVersion
    InformationalVersion = $informationalVersion
    VersionPrefix = $versionPrefix
    RunNumber = $runNumber
    ShortSha = $shortSha
    IsTagBuild = $isTagBuild.ToString().ToLowerInvariant()
    UtcNow = $utcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

foreach ($entry in $outputs.GetEnumerator()) {
    $line = "$($entry.Key)=$($entry.Value)"
    Write-Output $line

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value $line
    }
}