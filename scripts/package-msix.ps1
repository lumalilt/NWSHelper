[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ManifestTemplatePath = (Join-Path $PSScriptRoot 'msix\AppxManifest.template.xml'),

    [string]$ManifestAssetsPath = (Join-Path $PSScriptRoot 'msix\Assets'),

    [string]$OutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'msix'),

    [string]$PackageIdentityName = '51949LumaLilt.NWSHelper',

    [string]$PackageDisplayName = 'NWS Helper',

    [string]$PackagePublisher = 'CN=D4894D8B-8976-46A8-9EE3-93D603B2BFF4',

    [string]$PackagePublisherDisplayName = 'LumaLilt',

    [string]$PackageDescription = 'NWS Helper',

    [string]$MakeAppxPath,

    [string]$MakePriPath,

    [switch]$SignPackage,

    [string]$SignToolPath,

    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$TimestampServerUrl = 'http://timestamp.acs.microsoft.com/',

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Escape-XmlValue {
    param([string]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return [System.Security.SecurityElement]::Escape($Value)
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

function Convert-ToMsixVersion {
    param([Parameter(Mandatory = $true)][string]$RawVersion)

    if ($RawVersion -match '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<revision>\d+))?(?:[-+].*)?$') {
        $major = [int]$Matches['major']
        $minor = [int]$Matches['minor']
        $patch = [int]$Matches['patch']
        $revision = 0
        if ($Matches['revision']) {
            $revision = [int]$Matches['revision']
        }

        foreach ($segment in @($major, $minor, $patch, $revision)) {
            if ($segment -lt 0 -or $segment -gt 65535) {
                throw "Version '$RawVersion' is out of MSIX segment range (0-65535)."
            }
        }

        return "$major.$minor.$patch.$revision"
    }

    throw "Version '$RawVersion' is not valid. Expected 'major.minor.patch[.revision]' optionally followed by prerelease/build metadata."
}

function Resolve-MakeAppxPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "makeappx.exe was not found at '$ExplicitPath'."
    }

    $command = Get-Command -Name 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidates = @(
            Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending
        )
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK or pass -MakeAppxPath <path>.'
}

function Resolve-MakePriPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "makepri.exe was not found at '$ExplicitPath'."
    }

    $command = Get-Command -Name 'makepri.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidates = @(
            Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'makepri.exe' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending
        )
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw 'makepri.exe was not found. Install the Windows 10/11 SDK or pass -MakePriPath <path>.'
}

function Resolve-SignToolPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "signtool.exe was not found at '$ExplicitPath'."
    }

    $command = Get-Command -Name 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $command.Source).Path
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidates = @(
            Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending
        )
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw 'signtool.exe was not found. Install the Windows 10/11 SDK or pass -SignToolPath <path>.'
}


$resolvedPublishDirectory = Resolve-ExistingDirectory `
    -Path $PublishDirectory `
    -ErrorMessage "Publish directory '$PublishDirectory' does not exist. Run dotnet publish first."

$mainExecutable = Join-Path $resolvedPublishDirectory 'NWSHelper.Gui.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "Publish directory '$resolvedPublishDirectory' is missing NWSHelper.Gui.exe."
}

$resolvedManifestTemplatePath = Resolve-ExistingFile `
    -Path $ManifestTemplatePath `
    -ErrorMessage "MSIX manifest template '$ManifestTemplatePath' does not exist."

$resolvedManifestAssetsPath = Resolve-ExistingDirectory `
    -Path $ManifestAssetsPath `
    -ErrorMessage "MSIX asset directory '$ManifestAssetsPath' does not exist."

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$msixVersion = Convert-ToMsixVersion -RawVersion $Version
$stagingDirectory = Join-Path $resolvedOutputDirectory "staging-$msixVersion"
$manifestOutputPath = Join-Path $stagingDirectory 'AppxManifest.xml'
$priConfigPath = Join-Path $stagingDirectory 'priconfig.xml'
$resourcesPriPath = Join-Path $stagingDirectory 'resources.pri'
$packagePath = Join-Path $resolvedOutputDirectory "NWSHelper-$msixVersion.msix"

if ($ValidateOnly.IsPresent) {
    Write-Output 'Mode=ValidateOnly'
    Write-Output "PublishDirectory=$resolvedPublishDirectory"
    Write-Output "ManifestTemplatePath=$resolvedManifestTemplatePath"
    Write-Output "ManifestAssetsPath=$resolvedManifestAssetsPath"
    Write-Output "OutputDirectory=$resolvedOutputDirectory"
    Write-Output "PackageIdentityName=$PackageIdentityName"
    Write-Output "PackageVersion=$msixVersion"
    Write-Output "ExpectedPackagePath=$packagePath"
    return
}

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

Copy-Item -Path (Join-Path $resolvedPublishDirectory '*') -Destination $stagingDirectory -Recurse -Force

$stagingAssetsDirectory = Join-Path $stagingDirectory 'Assets'
New-Item -ItemType Directory -Path $stagingAssetsDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $resolvedManifestAssetsPath '*') -Destination $stagingAssetsDirectory -Recurse -Force

$manifestTemplate = Get-Content -LiteralPath $resolvedManifestTemplatePath -Raw
$manifest = $manifestTemplate
$manifest = $manifest.Replace('__IDENTITY_NAME__', (Escape-XmlValue -Value $PackageIdentityName))
$manifest = $manifest.Replace('__IDENTITY_PUBLISHER__', (Escape-XmlValue -Value $PackagePublisher))
$manifest = $manifest.Replace('__IDENTITY_VERSION__', (Escape-XmlValue -Value $msixVersion))
$manifest = $manifest.Replace('__DISPLAY_NAME__', (Escape-XmlValue -Value $PackageDisplayName))
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', (Escape-XmlValue -Value $PackagePublisherDisplayName))
$manifest = $manifest.Replace('__DESCRIPTION__', (Escape-XmlValue -Value $PackageDescription))

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestOutputPath, $manifest, $utf8NoBom)

$resolvedMakePriPath = Resolve-MakePriPath -ExplicitPath $MakePriPath
& $resolvedMakePriPath createconfig /cf $priConfigPath /dq 'lang-en-US' /pv 10.0.0 /o

if ($LASTEXITCODE -ne 0) {
    throw "MakePri createconfig failed with exit code $LASTEXITCODE."
}

& $resolvedMakePriPath new /pr $stagingDirectory /cf $priConfigPath /mn $manifestOutputPath /of $resourcesPriPath /o

if ($LASTEXITCODE -ne 0) {
    throw "MakePri new failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
    Remove-Item -LiteralPath $packagePath -Force
}

$resolvedMakeAppxPath = Resolve-MakeAppxPath -ExplicitPath $MakeAppxPath
& $resolvedMakeAppxPath pack /d $stagingDirectory /p $packagePath /o

if ($LASTEXITCODE -ne 0) {
    throw "MSIX packaging failed with exit code $LASTEXITCODE."
}

if ($SignPackage.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
        throw 'Signing was requested but -CertificatePath was not provided.'
    }

    $resolvedCertificatePath = Resolve-ExistingFile -Path $CertificatePath -ErrorMessage "Certificate '$CertificatePath' was not found."
    $resolvedSignToolPath = Resolve-SignToolPath -ExplicitPath $SignToolPath

    $signArguments = @(
        'sign',
        '/fd',
        'SHA256',
        '/f',
        $resolvedCertificatePath
    )

    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $signArguments += @('/p', $CertificatePassword)
    }

    if (-not [string]::IsNullOrWhiteSpace($TimestampServerUrl)) {
        $signArguments += @(
            '/tr',
            $TimestampServerUrl,
            '/td',
            'SHA256'
        )
    }

    $signArguments += $packagePath
    & $resolvedSignToolPath @signArguments

    if ($LASTEXITCODE -ne 0) {
        throw "MSIX signing failed with exit code $LASTEXITCODE."
    }
}

Write-Output 'Mode=Package'
Write-Output "PackagePath=$packagePath"
Write-Output "PackageVersion=$msixVersion"
Write-Output "PackageSigned=$(([string]$SignPackage.IsPresent).ToLowerInvariant())"
