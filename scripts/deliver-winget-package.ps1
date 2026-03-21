[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$InstallerUrl,

    [string]$ManifestRoot = (Join-Path (Join-Path $PSScriptRoot '..\artifacts') 'winget-delivery'),

    [string]$PackageIdentifier = 'NWSHelper.NWSHelper',

    [string]$PackageName = 'NWS Helper',

    [string]$Publisher = 'NWS Helper',

    [string]$PullRequestTitle,

    [string]$EvidenceOutputPath,

    [string]$GenerateManifestScriptPath = (Join-Path $PSScriptRoot 'generate-winget-manifests.ps1'),

    [string]$ValidateManifestScriptPath = (Join-Path $PSScriptRoot 'validate-winget-manifests.ps1'),

    [string]$WingetCreatePath,

    [string]$GitHubToken = [Environment]::GetEnvironmentVariable('WINGET_CREATE_GITHUB_TOKEN'),

    [switch]$Submit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Resolve-WingetCreatePath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "wingetcreate executable '$ExplicitPath' was not found."
    }

    foreach ($commandName in @('wingetcreate.exe', 'wingetcreate')) {
        $command = Get-Command -Name $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $command.Source).Path
        }
    }

    throw 'wingetcreate was not found. Install it or pass -WingetCreatePath <path>.'
}

$resolvedInstallerPath = Resolve-ExistingFile `
    -Path $InstallerPath `
    -ErrorMessage "Installer '$InstallerPath' was not found."

$resolvedGenerateManifestScriptPath = Resolve-ExistingFile `
    -Path $GenerateManifestScriptPath `
    -ErrorMessage "generate-winget-manifests script '$GenerateManifestScriptPath' was not found."

$resolvedValidateManifestScriptPath = Resolve-ExistingFile `
    -Path $ValidateManifestScriptPath `
    -ErrorMessage "validate-winget-manifests script '$ValidateManifestScriptPath' was not found."

$resolvedManifestRoot = [System.IO.Path]::GetFullPath($ManifestRoot)
New-Item -ItemType Directory -Path $resolvedManifestRoot -Force | Out-Null
$resolvedManifestRoot = Resolve-ExistingDirectory `
    -Path $resolvedManifestRoot `
    -ErrorMessage "Manifest root '$resolvedManifestRoot' was not found."

$installerSha256 = (Get-FileHash -LiteralPath $resolvedInstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()

$generateOutputLines = & $resolvedGenerateManifestScriptPath `
    -Version $Version `
    -OutputDirectory $resolvedManifestRoot `
    -PackageIdentifier $PackageIdentifier `
    -PackageName $PackageName `
    -Publisher $Publisher `
    -InstallerUrl $InstallerUrl `
    -InstallerSha256 $installerSha256

$generateValues = Convert-OutputLinesToDictionary -OutputLines ($generateOutputLines | ForEach-Object { $_.ToString() })
if (-not $generateValues.ContainsKey('ManifestDirectory')) {
    throw 'generate-winget-manifests.ps1 did not report ManifestDirectory.'
}

$resolvedManifestDirectory = Resolve-ExistingDirectory `
    -Path $generateValues['ManifestDirectory'] `
    -ErrorMessage "Generated manifest directory '$($generateValues['ManifestDirectory'])' was not found."

$validationOutputLines = & $resolvedValidateManifestScriptPath `
    -Version $Version `
    -ManifestRoot $resolvedManifestRoot `
    -PackageIdentifier $PackageIdentifier

$validationValues = Convert-OutputLinesToDictionary -OutputLines ($validationOutputLines | ForEach-Object { $_.ToString() })

$resolvedEvidenceOutputPath = if ([string]::IsNullOrWhiteSpace($EvidenceOutputPath)) {
    Join-Path $resolvedManifestRoot 'winget-delivery.json'
}
else {
    [System.IO.Path]::GetFullPath($EvidenceOutputPath)
}

$evidenceParent = Split-Path -Path $resolvedEvidenceOutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($evidenceParent)) {
    New-Item -ItemType Directory -Path $evidenceParent -Force | Out-Null
}

$submissionLogPath = $null
$status = 'Prepared'
$resolvedWingetCreatePath = $null

if ($Submit.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($PullRequestTitle)) {
        $PullRequestTitle = "$PackageIdentifier $Version"
    }

    if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
        throw 'GitHubToken is required when -Submit is used. Set WINGET_CREATE_GITHUB_TOKEN or pass -GitHubToken.'
    }

    $resolvedWingetCreatePath = Resolve-WingetCreatePath -ExplicitPath $WingetCreatePath
    $submissionLogPath = Join-Path $resolvedManifestRoot 'wingetcreate-submit.log'

    $previousWingetToken = [Environment]::GetEnvironmentVariable('WINGET_CREATE_GITHUB_TOKEN')
    try {
        [Environment]::SetEnvironmentVariable('WINGET_CREATE_GITHUB_TOKEN', $GitHubToken)

        $wingetCreateOutput = & $resolvedWingetCreatePath submit --prtitle $PullRequestTitle --no-open $resolvedManifestDirectory 2>&1
        $wingetCreateExitCode = $LASTEXITCODE

        ($wingetCreateOutput | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath $submissionLogPath
        if ($wingetCreateExitCode -ne 0) {
            throw "wingetcreate submit failed with exit code $wingetCreateExitCode."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('WINGET_CREATE_GITHUB_TOKEN', $previousWingetToken)
    }

    $status = 'Submitted'
}

$evidence = [pscustomobject]@{
    status = $status
    preparedAtUtc = [DateTime]::UtcNow.ToString('o')
    version = $Version
    installerPath = $resolvedInstallerPath
    installerUrl = $InstallerUrl
    installerSha256 = $installerSha256
    manifestRoot = $resolvedManifestRoot
    manifestDirectory = $resolvedManifestDirectory
    packageIdentifier = $PackageIdentifier
    packageName = $PackageName
    publisher = $Publisher
    validationResult = if ($validationValues.ContainsKey('ValidationResult')) { $validationValues['ValidationResult'] } else { $null }
    pullRequestTitle = $PullRequestTitle
    submissionLogPath = $submissionLogPath
    wingetCreatePath = $resolvedWingetCreatePath
}
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resolvedEvidenceOutputPath

Write-Output "Status=$status"
Write-Output "InstallerPath=$resolvedInstallerPath"
Write-Output "InstallerUrl=$InstallerUrl"
Write-Output "InstallerSha256=$installerSha256"
Write-Output "ManifestRoot=$resolvedManifestRoot"
Write-Output "ManifestDirectory=$resolvedManifestDirectory"

if ($validationValues.ContainsKey('ValidationResult')) {
    Write-Output "ValidationResult=$($validationValues['ValidationResult'])"
}

if (-not [string]::IsNullOrWhiteSpace($PullRequestTitle)) {
    Write-Output "PullRequestTitle=$PullRequestTitle"
}

if (-not [string]::IsNullOrWhiteSpace($submissionLogPath)) {
    Write-Output "SubmissionLogPath=$submissionLogPath"
}

Write-Output "EvidenceOutputPath=$resolvedEvidenceOutputPath"