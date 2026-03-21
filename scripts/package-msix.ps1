[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ManifestTemplatePath = (Join-Path $PSScriptRoot 'msix\AppxManifest.template.xml'),

    [string]$ManifestAssetsPath = (Join-Path $PSScriptRoot 'msix\Assets'),

    [string]$LogoSourcePath,

    [string]$OutputDirectory = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') 'msix'),

    [string]$PackageIdentityName = '51949LumaLilt.NWSHelper',

    [string]$PackageDisplayName = 'NWS Helper',

    [string]$PackagePublisher = 'CN=D4894D8B-8976-46A8-9EE3-93D603B2BFF4',

    [string]$PackagePublisherDisplayName = 'LumaLilt',

    [string]$PackageDescription = 'NWS Helper',

    [string]$MakeAppxPath,

    [switch]$SignPackage,

    [string]$SignToolPath,

    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$TimestampServerUrl = 'http://timestamp.acs.microsoft.com/',

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
        $candidates = Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK or pass -MakeAppxPath <path>.'
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
        $candidates = Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending
        if ($candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw 'signtool.exe was not found. Install the Windows 10/11 SDK or pass -SignToolPath <path>.'
}

function Resolve-LogoSourcePath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-ExistingFile -Path $ExplicitPath -ErrorMessage "Logo source PNG '$ExplicitPath' was not found."
    }

    $repositoryRoot = Split-Path $PSScriptRoot -Parent
    $candidatePaths = @(
        (Join-Path $repositoryRoot 'NWSHelper.Gui\Assets\nwsh_orig.png'),
        (Join-Path $repositoryRoot 'NWSHelper.Gui\Assets\nwsh.png')
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidatePath).Path
        }
    }

    throw 'MSIX logo source PNG was not found. Pass -LogoSourcePath <path>.'
}

function New-MsixLogoAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,

        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    Add-Type -AssemblyName System.Drawing

    $sourceImage = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $bitmap = New-Object System.Drawing.Bitmap $Size, $Size
        try {
            $horizontalResolution = if ($sourceImage.HorizontalResolution -gt 0) { $sourceImage.HorizontalResolution } else { 96 }
            $verticalResolution = if ($sourceImage.VerticalResolution -gt 0) { $sourceImage.VerticalResolution } else { 96 }
            $bitmap.SetResolution($horizontalResolution, $verticalResolution)

            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($sourceImage, 0, 0, $Size, $Size)

                if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
                    Remove-Item -LiteralPath $DestinationPath -Force
                }

                $bitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $graphics.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
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

$resolvedLogoSourcePath = Resolve-LogoSourcePath -ExplicitPath $LogoSourcePath

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$msixVersion = Convert-ToMsixVersion -RawVersion $Version
$stagingDirectory = Join-Path $resolvedOutputDirectory "staging-$msixVersion"
$manifestOutputPath = Join-Path $stagingDirectory 'AppxManifest.xml'
$packagePath = Join-Path $resolvedOutputDirectory "NWSHelper-$msixVersion.msix"

if ($ValidateOnly.IsPresent) {
    Write-Output 'Mode=ValidateOnly'
    Write-Output "PublishDirectory=$resolvedPublishDirectory"
    Write-Output "ManifestTemplatePath=$resolvedManifestTemplatePath"
    Write-Output "ManifestAssetsPath=$resolvedManifestAssetsPath"
    Write-Output "LogoSourcePath=$resolvedLogoSourcePath"
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

foreach ($logoSpecification in @(
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'StoreLogo.png'; Size = 50 }
)) {
    New-MsixLogoAsset `
        -SourcePath $resolvedLogoSourcePath `
        -DestinationPath (Join-Path $stagingAssetsDirectory $logoSpecification.Name) `
        -Size $logoSpecification.Size
}

$manifestTemplate = Get-Content -LiteralPath $resolvedManifestTemplatePath -Raw
$manifest = $manifestTemplate
$manifest = $manifest.Replace('__IDENTITY_NAME__', $PackageIdentityName)
$manifest = $manifest.Replace('__IDENTITY_PUBLISHER__', $PackagePublisher)
$manifest = $manifest.Replace('__IDENTITY_VERSION__', $msixVersion)
$manifest = $manifest.Replace('__DISPLAY_NAME__', $PackageDisplayName)
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $PackagePublisherDisplayName)
$manifest = $manifest.Replace('__DESCRIPTION__', $PackageDescription)

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestOutputPath, $manifest, $utf8NoBom)

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
Write-Output "LogoSourcePath=$resolvedLogoSourcePath"
Write-Output "PackageSigned=$(([string]$SignPackage.IsPresent).ToLowerInvariant())"
