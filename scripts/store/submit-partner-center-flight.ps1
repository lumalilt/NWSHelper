[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationId = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_APPLICATION_ID'),

    [string]$FlightId = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_FLIGHT_ID'),

    [ValidateSet('Flight', 'Production')]
    [string]$SubmissionTarget = 'Flight',

    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$TenantId = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_TENANT_ID'),

    [string]$ClientId = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_CLIENT_ID'),

    [string]$ClientSecret = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_CLIENT_SECRET'),

    [string]$SubmissionNotes = [Environment]::GetEnvironmentVariable('PARTNER_CENTER_SUBMISSION_NOTES'),

    [ValidateSet('Immediate', 'Manual', 'SpecificDate')]
    [string]$TargetPublishMode = 'Immediate',

    [datetime]$TargetPublishDate,

    [string]$ExpectedPackageIdentityName = [Environment]::GetEnvironmentVariable('MSIX_PACKAGE_IDENTITY_NAME'),

    [string]$ExpectedPackagePublisher = [Environment]::GetEnvironmentVariable('MSIX_PACKAGE_PUBLISHER'),

    [switch]$ForceReplacePendingSubmission,

    [int]$StatusPollIntervalSeconds = 15,

    [int]$CommitStatusTimeoutMinutes = 10,

    [string]$EvidenceOutputPath,

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

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

    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-OptionalFileParent {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Path $fullPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    return $fullPath
}

function Get-MsixIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $packageStream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($packageStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $manifestEntry = $archive.Entries | Where-Object { $_.FullName -ieq 'AppxManifest.xml' } | Select-Object -First 1
            if ($null -eq $manifestEntry) {
                throw "Package '$Path' does not contain AppxManifest.xml."
            }

            $manifestStream = $manifestEntry.Open()
            try {
                $xmlDocument = New-Object System.Xml.XmlDocument
                $xmlDocument.PreserveWhitespace = $true
                $xmlDocument.Load($manifestStream)

                $namespaceManager = New-Object System.Xml.XmlNamespaceManager($xmlDocument.NameTable)
                $namespaceManager.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

                $identityNode = $xmlDocument.SelectSingleNode('/appx:Package/appx:Identity', $namespaceManager)
                if ($null -eq $identityNode) {
                    throw "Package '$Path' does not declare a Package/Identity element."
                }

                return [pscustomobject]@{
                    Name = $identityNode.Attributes['Name'].Value
                    Publisher = $identityNode.Attributes['Publisher'].Value
                    Version = $identityNode.Attributes['Version'].Value
                }
            }
            finally {
                $manifestStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $packageStream.Dispose()
    }
}

function Get-PartnerCenterAccessToken {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedTenantId,
        [Parameter(Mandatory = $true)][string]$ResolvedClientId,
        [Parameter(Mandatory = $true)][string]$ResolvedClientSecret
    )

    $tokenUri = "https://login.microsoftonline.com/$ResolvedTenantId/oauth2/token"
    $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenUri -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'client_credentials'
        client_id = $ResolvedClientId
        client_secret = $ResolvedClientSecret
        resource = 'https://manage.devcenter.microsoft.com'
    }

    if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
        throw 'Partner Center token response did not contain an access token.'
    }

    return $tokenResponse.access_token
}

function Invoke-PartnerCenterRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$AccessToken,
        [object]$Body
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $jsonBody = $Body | ConvertTo-Json -Depth 30
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json' -Body $jsonBody
    }

    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
}

function New-PackageUploadArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePackagePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $packageFileName = [System.IO.Path]::GetFileName($SourcePackagePath)
    $archiveRoot = Join-Path $DestinationDirectory 'upload-root'
    $archivePath = Join-Path $DestinationDirectory ([System.IO.Path]::GetFileNameWithoutExtension($packageFileName) + '.zip')

    if (Test-Path -LiteralPath $archiveRoot) {
        Remove-Item -LiteralPath $archiveRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    Copy-Item -LiteralPath $SourcePackagePath -Destination (Join-Path $archiveRoot $packageFileName) -Force

    Compress-Archive -Path (Join-Path $archiveRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

    return $archivePath
}

function Upload-PartnerCenterPackage {
    param(
        [Parameter(Mandatory = $true)][string]$UploadUrl,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    Invoke-WebRequest -Method Put -Uri $UploadUrl -InFile $ArchivePath -ContentType 'application/octet-stream' -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
}

function Test-IsCommitFailureStatus {
    param([Parameter(Mandatory = $true)][string]$Status)

    return $Status -in @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'PublishFailed', 'ReleaseFailed', 'Canceled')
}

function Test-IsCommitAcceptedStatus {
    param([Parameter(Mandatory = $true)][string]$Status)

    return $Status -in @('PreProcessing', 'Certification', 'PendingPublication', 'Publishing', 'Published', 'Release')
}

function Get-StatusDetailsText {
    param([object]$StatusDetails)

    if ($null -eq $StatusDetails) {
        return ''
    }

    $parts = @()
    foreach ($collectionName in @('errors', 'warnings')) {
        $collection = $StatusDetails.$collectionName
        if ($null -eq $collection) {
            continue
        }

        foreach ($item in $collection) {
            if ($null -eq $item) {
                continue
            }

            $parts += ('{0}:{1}:{2}' -f $collectionName, $item.code, $item.details)
        }
    }

    return ($parts -join ' | ')
}

$resolvedPackagePath = Resolve-ExistingFile -Path $PackagePath -ErrorMessage "Package '$PackagePath' was not found."
$msixIdentity = Get-MsixIdentity -Path $resolvedPackagePath

if ($SubmissionTarget -eq 'Flight' -and [string]::IsNullOrWhiteSpace($FlightId)) {
    throw 'FlightId is required when SubmissionTarget is Flight.'
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedPackageIdentityName) -and $msixIdentity.Name -ne $ExpectedPackageIdentityName) {
    throw "Package identity name '$($msixIdentity.Name)' does not match expected '$ExpectedPackageIdentityName'."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedPackagePublisher) -and $msixIdentity.Publisher -ne $ExpectedPackagePublisher) {
    throw "Package publisher '$($msixIdentity.Publisher)' does not match expected '$ExpectedPackagePublisher'."
}

if ($TargetPublishMode -eq 'SpecificDate' -and -not $PSBoundParameters.ContainsKey('TargetPublishDate')) {
    throw 'TargetPublishDate is required when TargetPublishMode is SpecificDate.'
}

if ($StatusPollIntervalSeconds -lt 1) {
    throw 'StatusPollIntervalSeconds must be at least 1.'
}

if ($CommitStatusTimeoutMinutes -lt 1) {
    throw 'CommitStatusTimeoutMinutes must be at least 1.'
}

if ($ValidateOnly.IsPresent) {
    $validationEvidencePath = $null
    if (-not [string]::IsNullOrWhiteSpace($EvidenceOutputPath)) {
        $validationEvidencePath = Resolve-OptionalFileParent -Path $EvidenceOutputPath
        $validationEvidenceObject = [pscustomobject]@{
            status = 'ValidationComplete'
            submissionTarget = $SubmissionTarget
            applicationId = $ApplicationId
            flightId = if ($SubmissionTarget -eq 'Flight') { $FlightId } else { $null }
            packagePath = $resolvedPackagePath
            packageFileName = [System.IO.Path]::GetFileName($resolvedPackagePath)
            packageIdentityName = $msixIdentity.Name
            packagePublisher = $msixIdentity.Publisher
            packageVersion = $msixIdentity.Version
            targetPublishMode = $TargetPublishMode
            willReplacePendingSubmission = $ForceReplacePendingSubmission.IsPresent
        }

        ($validationEvidenceObject | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $validationEvidencePath -Encoding UTF8
    }

    Write-Output 'Status=ValidationComplete'
    Write-Output "SubmissionTarget=$SubmissionTarget"
    Write-Output "ApplicationId=$ApplicationId"
    if ($SubmissionTarget -eq 'Flight') {
        Write-Output "FlightId=$FlightId"
    }
    Write-Output "PackagePath=$resolvedPackagePath"
    Write-Output "PackageFileName=$([System.IO.Path]::GetFileName($resolvedPackagePath))"
    Write-Output "PackageIdentityName=$($msixIdentity.Name)"
    Write-Output "PackagePublisher=$($msixIdentity.Publisher)"
    Write-Output "PackageVersion=$($msixIdentity.Version)"
    Write-Output "TargetPublishMode=$TargetPublishMode"
    Write-Output "WillReplacePendingSubmission=$($ForceReplacePendingSubmission.IsPresent.ToString().ToLowerInvariant())"
    Write-Output "WillWriteEvidence=$((-not [string]::IsNullOrWhiteSpace($EvidenceOutputPath)).ToString().ToLowerInvariant())"
    if (-not [string]::IsNullOrWhiteSpace($validationEvidencePath)) {
        Write-Output "ValidationEvidencePath=$validationEvidencePath"
    }
    return
}

$requiredValues = @(
    @{ Name = 'ApplicationId'; Value = $ApplicationId },
    @{ Name = 'TenantId'; Value = $TenantId },
    @{ Name = 'ClientId'; Value = $ClientId },
    @{ Name = 'ClientSecret'; Value = $ClientSecret }
)

if ($SubmissionTarget -eq 'Flight') {
    $requiredValues += @{ Name = 'FlightId'; Value = $FlightId }
}

foreach ($requiredValue in $requiredValues) {
    if ([string]::IsNullOrWhiteSpace($requiredValue.Value)) {
        throw "$($requiredValue.Name) is required for Partner Center submission."
    }
}

$partnerCenterBaseUri = 'https://manage.devcenter.microsoft.com/v1.0/my'

if ($SubmissionTarget -eq 'Flight') {
    $submissionOwnerUri = "$partnerCenterBaseUri/applications/$ApplicationId/flights/$FlightId"
    $submissionOwnerLabel = "Flight '$FlightId'"
    $pendingSubmissionPropertyName = 'pendingFlightSubmission'
    $publishedSubmissionPropertyName = 'lastPublishedFlightSubmission'
    $packageCollectionPropertyName = 'flightPackages'
}
else {
    $submissionOwnerUri = "$partnerCenterBaseUri/applications/$ApplicationId"
    $submissionOwnerLabel = "Application '$ApplicationId'"
    $pendingSubmissionPropertyName = 'pendingApplicationSubmission'
    $publishedSubmissionPropertyName = 'lastPublishedApplicationSubmission'
    $packageCollectionPropertyName = 'applicationPackages'
}

$submissionsUri = "$submissionOwnerUri/submissions"

$token = Get-PartnerCenterAccessToken -ResolvedTenantId $TenantId -ResolvedClientId $ClientId -ResolvedClientSecret $ClientSecret
$submissionOwner = Invoke-PartnerCenterRequest -Method Get -Uri $submissionOwnerUri -AccessToken $token

$pendingSubmission = $submissionOwner.$pendingSubmissionPropertyName
$publishedSubmission = $submissionOwner.$publishedSubmissionPropertyName

$pendingSubmissionId = if ($null -ne $pendingSubmission) { [string]$pendingSubmission.id } else { '' }
$publishedSubmissionId = if ($null -ne $publishedSubmission) { [string]$publishedSubmission.id } else { '' }

if (-not [string]::IsNullOrWhiteSpace($pendingSubmissionId) -and -not $ForceReplacePendingSubmission.IsPresent) {
    throw "$submissionOwnerLabel already has a pending submission ($pendingSubmissionId). Re-run with -ForceReplacePendingSubmission to replace it."
}

if (-not [string]::IsNullOrWhiteSpace($pendingSubmissionId) -and $ForceReplacePendingSubmission.IsPresent) {
    Invoke-PartnerCenterRequest -Method Delete -Uri "$submissionsUri/$pendingSubmissionId" -AccessToken $token | Out-Null
}

if ([string]::IsNullOrWhiteSpace($publishedSubmissionId)) {
    throw "$submissionOwnerLabel does not have a published submission to clone."
}

$newSubmission = Invoke-PartnerCenterRequest -Method Post -Uri $submissionsUri -AccessToken $token -Body @{}
$submissionId = [string]$newSubmission.id
if ([string]::IsNullOrWhiteSpace($submissionId)) {
    throw 'Partner Center did not return a submission id.'
}

$submissionUri = "$submissionsUri/$submissionId"
$submission = Invoke-PartnerCenterRequest -Method Get -Uri $submissionUri -AccessToken $token

$fileUploadUrl = [string]$submission.fileUploadUrl
if ([string]::IsNullOrWhiteSpace($fileUploadUrl)) {
    throw 'Partner Center submission did not provide a fileUploadUrl.'
}

$workingDirectory = Join-Path $env:TEMP ([System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null

try {
    $archivePath = New-PackageUploadArchive -SourcePackagePath $resolvedPackagePath -DestinationDirectory $workingDirectory
    Upload-PartnerCenterPackage -UploadUrl $fileUploadUrl -ArchivePath $archivePath
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}

$packageReference = [pscustomobject]@{ fileName = [System.IO.Path]::GetFileName($resolvedPackagePath) }
$submission.$packageCollectionPropertyName = @($packageReference)

if (-not [string]::IsNullOrWhiteSpace($SubmissionNotes)) {
    $submission.notesForCertification = $SubmissionNotes
}

switch ($TargetPublishMode) {
    'Immediate' {
        $submission.targetPublishMode = 'Immediate'
        $submission.targetPublishDate = $null
    }
    'Manual' {
        $submission.targetPublishMode = 'Manual'
        $submission.targetPublishDate = $null
    }
    'SpecificDate' {
        $submission.targetPublishMode = 'SpecificDate'
        $submission.targetPublishDate = $TargetPublishDate.ToUniversalTime().ToString('o')
    }
}

$updatedSubmission = Invoke-PartnerCenterRequest -Method Put -Uri $submissionUri -AccessToken $token -Body $submission

$commitUri = "$submissionUri/commit"
Invoke-PartnerCenterRequest -Method Post -Uri $commitUri -AccessToken $token -Body @{} | Out-Null

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$finalStatus = ''
$finalStatusDetails = $null

do {
    Start-Sleep -Seconds $StatusPollIntervalSeconds
    $currentSubmission = Invoke-PartnerCenterRequest -Method Get -Uri $submissionUri -AccessToken $token
    $finalStatus = [string]$currentSubmission.status
    $finalStatusDetails = $currentSubmission.statusDetails

    if (Test-IsCommitFailureStatus -Status $finalStatus) {
        $details = Get-StatusDetailsText -StatusDetails $finalStatusDetails
        throw "Partner Center submission $submissionId failed with status '$finalStatus'. $details"
    }

    if (Test-IsCommitAcceptedStatus -Status $finalStatus) {
        break
    }
}
while ($stopwatch.Elapsed.TotalMinutes -lt $CommitStatusTimeoutMinutes)

if (-not (Test-IsCommitAcceptedStatus -Status $finalStatus)) {
    throw "Timed out waiting for Partner Center submission $submissionId to advance from status '$finalStatus'."
}

$resolvedEvidenceOutputPath = $null
if (-not [string]::IsNullOrWhiteSpace($EvidenceOutputPath)) {
    $resolvedEvidenceOutputPath = Resolve-OptionalFileParent -Path $EvidenceOutputPath
    $evidence = [pscustomobject]@{
        status = 'SubmissionCommitted'
        submissionTarget = $SubmissionTarget
        applicationId = $ApplicationId
        flightId = if ($SubmissionTarget -eq 'Flight') { $FlightId } else { $null }
        submissionId = $submissionId
        packagePath = $resolvedPackagePath
        packageIdentityName = $msixIdentity.Name
        packagePublisher = $msixIdentity.Publisher
        packageVersion = $msixIdentity.Version
        targetPublishMode = $TargetPublishMode
        targetPublishDate = if ($TargetPublishMode -eq 'SpecificDate') { $TargetPublishDate.ToUniversalTime().ToString('o') } else { $null }
        submissionStatus = $finalStatus
        submissionStatusDetails = $finalStatusDetails
        willReplacePendingSubmission = $ForceReplacePendingSubmission.IsPresent
        committedAtUtc = [DateTime]::UtcNow.ToString('o')
    }

    ($evidence | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $resolvedEvidenceOutputPath -Encoding UTF8
}

Write-Output 'Status=SubmissionCommitted'
Write-Output "SubmissionTarget=$SubmissionTarget"
Write-Output "ApplicationId=$ApplicationId"
if ($SubmissionTarget -eq 'Flight') {
    Write-Output "FlightId=$FlightId"
}
Write-Output "SubmissionId=$submissionId"
Write-Output "PackagePath=$resolvedPackagePath"
Write-Output "PackageFileName=$([System.IO.Path]::GetFileName($resolvedPackagePath))"
Write-Output "PackageIdentityName=$($msixIdentity.Name)"
Write-Output "PackagePublisher=$($msixIdentity.Publisher)"
Write-Output "PackageVersion=$($msixIdentity.Version)"
Write-Output "TargetPublishMode=$TargetPublishMode"
if ($TargetPublishMode -eq 'SpecificDate') {
    Write-Output "TargetPublishDate=$($TargetPublishDate.ToUniversalTime().ToString('o'))"
}
Write-Output "WillReplacePendingSubmission=$($ForceReplacePendingSubmission.IsPresent.ToString().ToLowerInvariant())"
Write-Output "SubmissionStatus=$finalStatus"
if (-not [string]::IsNullOrWhiteSpace($resolvedEvidenceOutputPath)) {
    Write-Output "EvidenceOutputPath=$resolvedEvidenceOutputPath"
}