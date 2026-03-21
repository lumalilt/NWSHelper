[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$InnoScriptPath = (Join-Path $PSScriptRoot 'inno\NWSHelper.iss'),

    [string]$OutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'installer'),

    [string]$IsccPath,

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

function Resolve-IsccPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "ISCC.exe was not found at '$ExplicitPath'."
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $isccCommand = Get-Command -Name 'iscc.exe' -ErrorAction SilentlyContinue
    if ($null -ne $isccCommand -and (Test-Path -LiteralPath $isccCommand.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $isccCommand.Source).Path
    }

    throw 'Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -IsccPath <path>.'
}

$resolvedPublishDirectory = Resolve-ExistingDirectory `
    -Path $PublishDirectory `
    -ErrorMessage "Publish directory '$PublishDirectory' does not exist. Run dotnet publish first."

$resolvedInnoScriptPath = Resolve-ExistingFile `
    -Path $InnoScriptPath `
    -ErrorMessage "Inno Setup script '$InnoScriptPath' does not exist."

$mainExecutable = Join-Path $resolvedPublishDirectory 'NWSHelper.Gui.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "Publish directory '$resolvedPublishDirectory' is missing NWSHelper.Gui.exe."
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

if ($ValidateOnly.IsPresent) {
    Write-Output 'Mode=ValidateOnly'
    Write-Output "InnoScriptPath=$resolvedInnoScriptPath"
    Write-Output "PublishDirectory=$resolvedPublishDirectory"
    Write-Output "OutputDirectory=$resolvedOutputDirectory"
    Write-Output "ExpectedInstallerName=NWSHelper-Setup-$Version.exe"
    return
}

$resolvedIsccPath = Resolve-IsccPath -ExplicitPath $IsccPath

$arguments = @(
    '/Qp',
    "/DAppVersion=$Version",
    "/DSourceDir=$resolvedPublishDirectory",
    "/DOutputDir=$resolvedOutputDirectory",
    $resolvedInnoScriptPath
)

& $resolvedIsccPath @arguments

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup packaging failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $resolvedOutputDirectory "NWSHelper-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup completed but expected installer '$installerPath' was not found."
}

Write-Output 'Mode=Package'
Write-Output "InstallerPath=$installerPath"
