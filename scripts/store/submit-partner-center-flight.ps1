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

    [switch]$MarkSupersededPackagesPendingDelete,

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

function Get-PartnerCenterErrorResponseBodyText {
    param([Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord)

    $responseBodyText = ''

    try {
        $responseProperty = $ErrorRecord.Exception.PSObject.Properties['Response']
        $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
        if ($null -eq $response) {
            return ''
        }

        $contentProperty = $response.PSObject.Properties['Content']
        $content = if ($null -ne $contentProperty) { $contentProperty.Value } else { $null }
        if ($null -ne $content) {
            $readAsStringAsyncMethod = $content.PSObject.Methods['ReadAsStringAsync']
            if ($null -ne $readAsStringAsyncMethod) {
                $readTask = $content.ReadAsStringAsync()
                if ($null -ne $readTask) {
                    $responseBodyText = [string]$readTask.GetAwaiter().GetResult()
                }
            }
        }

        if ([string]::IsNullOrWhiteSpace($responseBodyText)) {
            $getResponseStreamMethod = $response.PSObject.Methods['GetResponseStream']
            if ($null -ne $getResponseStreamMethod) {
                $responseStream = $response.GetResponseStream()
                if ($null -ne $responseStream) {
                    try {
                        $streamReader = [System.IO.StreamReader]::new($responseStream)
                        try {
                            $responseBodyText = $streamReader.ReadToEnd()
                        }
                        finally {
                            $streamReader.Dispose()
                        }
                    }
                    finally {
                        $responseStream.Dispose()
                    }
                }
            }
        }
    }
    catch {
        return ''
    }

    return $responseBodyText
}

function Get-PartnerCenterErrorStatusCode {
    param([Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord)

    $statusCodeCandidates = @()

    try {
        $responseProperty = $ErrorRecord.Exception.PSObject.Properties['Response']
        $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
        if ($null -ne $response) {
            $statusCodeProperty = $response.PSObject.Properties['StatusCode']
            if ($null -ne $statusCodeProperty -and $null -ne $statusCodeProperty.Value) {
                $statusCodeCandidates += [string]$statusCodeProperty.Value
                $valueProperty = $statusCodeProperty.Value.PSObject.Properties['value__']
                if ($null -ne $valueProperty -and $null -ne $valueProperty.Value) {
                    $statusCodeCandidates += [string]$valueProperty.Value
                }
            }
        }
    }
    catch {
    }

    if ($null -ne $ErrorRecord.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.ErrorDetails.Message)) {
        $statusCodeCandidates += [string]$ErrorRecord.ErrorDetails.Message
    }

    $statusCodeCandidates += @(
        [string]$ErrorRecord.Exception.Message,
        [string]$ErrorRecord.ToString()
    )

    foreach ($candidate in $statusCodeCandidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $match = [regex]::Match($candidate, '(?<!\d)(408|429|500|502|503|504)(?!\d)')
        if ($match.Success) {
            return [int]$match.Groups[1].Value
        }
    }

    return $null
}

function Test-IsTransientPartnerCenterError {
    param(
        [Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord,
        [string]$ResponseBodyText,
        [string]$ErrorDetailsMessage
    )

    $statusCode = Get-PartnerCenterErrorStatusCode -ErrorRecord $ErrorRecord
    if ($null -ne $statusCode -and $statusCode -in @(408, 429, 500, 502, 503, 504)) {
        return $true
    }

    $candidateTexts = @(
        $ResponseBodyText,
        $ErrorDetailsMessage,
        [string]$ErrorRecord.Exception.Message,
        [string]$ErrorRecord.ToString()
    )

    foreach ($candidateText in $candidateTexts) {
        if ([string]::IsNullOrWhiteSpace($candidateText)) {
            continue
        }

        if ($candidateText -match 'Gateway Timeout|Service unavailable|Azure Front Door|OriginTimeout|temporarily unavailable|timed out') {
            return $true
        }
    }

    return $false
}

function Invoke-PartnerCenterRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$AccessToken,
        [object]$Body
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            if ($PSBoundParameters.ContainsKey('Body')) {
                $jsonBody = $Body | ConvertTo-Json -Depth 30
                return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json' -Body $jsonBody
            }

            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
        }
        catch {
            $responseBodyText = Get-PartnerCenterErrorResponseBodyText -ErrorRecord $_
            $errorDetailsMessage = if ($null -ne $_.ErrorDetails) { [string]$_.ErrorDetails.Message } else { '' }
            if ($attempt -lt $maxAttempts -and (Test-IsTransientPartnerCenterError -ErrorRecord $_ -ResponseBodyText $responseBodyText -ErrorDetailsMessage $errorDetailsMessage)) {
                $delaySeconds = 5 * $attempt
                Write-Warning "Transient Partner Center request failure on attempt $attempt/$maxAttempts for $Method $Uri. Retrying in $delaySeconds seconds."
                Start-Sleep -Seconds $delaySeconds
                continue
            }

            if ([string]::IsNullOrWhiteSpace($responseBodyText) -or -not [string]::IsNullOrWhiteSpace($errorDetailsMessage)) {
                throw
            }

            $enrichedError = [System.Management.Automation.ErrorRecord]::new($_.Exception, $_.FullyQualifiedErrorId, $_.CategoryInfo.Category, $_.TargetObject)
            $enrichedError.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($responseBodyText)
            throw $enrichedError
        }
    }
}

function Get-OptionalObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$InputObject,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-ObjectEntryCount {
    param([AllowNull()][object]$InputObject)

    if ($null -eq $InputObject) {
        return 0
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject.Count
    }

    if ($InputObject -is [System.Array]) {
        return @($InputObject).Count
    }

    $propertyNames = @($InputObject.PSObject.Properties | ForEach-Object { $_.Name })
    if ($propertyNames.Count -gt 0) {
        return $propertyNames.Count
    }

    return 1
}

function Test-IsPartnerCenterReferenceType {
    param([AllowNull()][object]$InputObject)

    if ($null -eq $InputObject) {
        return $false
    }

    if ($InputObject -is [string]) {
        return $false
    }

    return -not $InputObject.GetType().IsValueType
}

function Test-PartnerCenterReferencePathContains {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$ReferencePath,
        [Parameter(Mandatory = $true)][object]$Candidate
    )

    foreach ($reference in $ReferencePath) {
        if ([object]::ReferenceEquals($reference, $Candidate)) {
            return $true
        }
    }

    return $false
}

function New-ProductionSubmissionCreateSeed {
    param([Parameter(Mandatory = $true)][AllowNull()][object]$PublishedSubmissionDetails)

    $mutablePropertyNames = @(
        'applicationCategory',
        'pricing',
        'visibility',
        'targetPublishMode',
        'targetPublishDate',
        'listings',
        'hardwarePreferences',
        'automaticBackupEnabled',
        'canInstallOnRemovableMedia',
        'isGameDvrEnabled',
        'hasExternalInAppProducts',
        'meetAccessibilityGuidelines',
        'notesForCertification',
        'applicationPackages',
        'packageDeliveryOptions',
        'enterpriseLicensing',
        'allowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies',
        'allowTargetFutureDeviceFamilies',
        'trailers'
    )

    $createSeed = [ordered]@{}
    foreach ($propertyName in $mutablePropertyNames) {
        $propertyValue = Get-OptionalObjectPropertyValue -InputObject $PublishedSubmissionDetails -PropertyName $propertyName
        if ($null -ne $propertyValue) {
            $createSeed[$propertyName] = Convert-ToPartnerCenterJsonCompatibleValue -InputObject $propertyValue
        }
    }

    return [pscustomobject]$createSeed
}

function Convert-ToPartnerCenterJsonCompatibleValue {
    param([AllowNull()][object]$InputObject)

    $referencePath = [System.Collections.Generic.List[object]]::new()
    return Convert-ToPartnerCenterJsonCompatibleValueInternal -InputObject $InputObject -ReferencePath $referencePath
}

function Convert-ToPartnerCenterJsonCompatibleValueInternal {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$ReferencePath
    )

    if ($null -eq $InputObject) {
        return $null
    }

    if (-not (Test-IsPartnerCenterReferenceType -InputObject $InputObject)) {
        return $InputObject
    }

    if (Test-PartnerCenterReferencePathContains -ReferencePath $ReferencePath -Candidate $InputObject) {
        return $null
    }

    $ReferencePath.Add($InputObject)
    try {
        if ($InputObject -is [System.Array]) {
            $convertedItems = @()
            foreach ($item in @($InputObject)) {
                $convertedItem = Convert-ToPartnerCenterJsonCompatibleValueInternal -InputObject $item -ReferencePath $ReferencePath
                if ($null -ne $item -and $null -eq $convertedItem) {
                    continue
                }

                $convertedItems += ,$convertedItem
            }

            return ,([object[]]$convertedItems)
        }

        if ($InputObject -is [System.Collections.IDictionary]) {
            $convertedMap = [ordered]@{}
            foreach ($key in $InputObject.Keys) {
                $rawValue = $InputObject[$key]
                $convertedValue = Convert-ToPartnerCenterJsonCompatibleValueInternal -InputObject $rawValue -ReferencePath $ReferencePath
                if ($null -ne $rawValue -and $null -eq $convertedValue) {
                    continue
                }

                $convertedMap[[string]$key] = $convertedValue
            }

            return [pscustomobject]$convertedMap
        }

        $propertyNames = @($InputObject.PSObject.Properties | ForEach-Object { $_.Name })
        if ($propertyNames.Count -gt 0) {
            $convertedObject = [ordered]@{}
            foreach ($propertyName in $propertyNames) {
                $rawValue = $InputObject.PSObject.Properties[$propertyName].Value
                $convertedValue = Convert-ToPartnerCenterJsonCompatibleValueInternal -InputObject $rawValue -ReferencePath $ReferencePath
                if ($null -ne $rawValue -and $null -eq $convertedValue) {
                    continue
                }

                $convertedObject[$propertyName] = $convertedValue
            }

            return [pscustomobject]$convertedObject
        }

        return $InputObject
    }
    finally {
        $ReferencePath.RemoveAt($ReferencePath.Count - 1)
    }
}

function Set-OptionalObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$InputObject,
        [Parameter(Mandatory = $true)][string]$PropertyName,
        [AllowNull()][object]$Value
    )

    if ($null -eq $InputObject) {
        throw "Cannot set property '$PropertyName' on a null object."
    }

    $property = $InputObject.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        Add-Member -InputObject $InputObject -NotePropertyName $PropertyName -NotePropertyValue $Value
        return
    }

    $property.Value = $Value
}

function New-PartnerCenterPackageEntry {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$FileStatus,
        [AllowNull()][object]$ExistingPackage
    )

    $packageEntry = [ordered]@{
        fileName = $FileName
        fileStatus = $FileStatus
        minimumDirectXVersion = 'None'
        minimumSystemRam = 'None'
    }

    if ($null -ne $ExistingPackage) {
        $existingPackageId = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'id')
        if (-not [string]::IsNullOrWhiteSpace($existingPackageId)) {
            $packageEntry.id = $existingPackageId
        }

        $existingMinimumDirectXVersion = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'minimumDirectXVersion')
        if (-not [string]::IsNullOrWhiteSpace($existingMinimumDirectXVersion)) {
            $packageEntry.minimumDirectXVersion = $existingMinimumDirectXVersion
        }

        $existingMinimumSystemRam = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'minimumSystemRam')
        if (-not [string]::IsNullOrWhiteSpace($existingMinimumSystemRam)) {
            $packageEntry.minimumSystemRam = $existingMinimumSystemRam
        }
    }

    return [pscustomobject]$packageEntry
}

function Get-PartnerCenterExistingPackageFileStatus {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$ExistingPackage,
        [switch]$MarkSupersededPackagesPendingDelete
    )

    if ($MarkSupersededPackagesPendingDelete.IsPresent) {
        return 'PendingDelete'
    }

    $existingFileStatus = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'fileStatus')
    if (-not [string]::IsNullOrWhiteSpace($existingFileStatus)) {
        return $existingFileStatus
    }

    return 'Uploaded'
}

function New-PartnerCenterExistingPackageSeed {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$ExistingPackage,
        [string]$PackageIdOverride
    )

    if ($null -eq $ExistingPackage -and [string]::IsNullOrWhiteSpace($PackageIdOverride)) {
        return $null
    }

    $packageSeed = [ordered]@{}

    $existingPackageId = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'id')
    if (-not [string]::IsNullOrWhiteSpace($PackageIdOverride)) {
        $packageSeed.id = $PackageIdOverride
    }
    elseif (-not [string]::IsNullOrWhiteSpace($existingPackageId)) {
        $packageSeed.id = $existingPackageId
    }

    $existingMinimumDirectXVersion = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'minimumDirectXVersion')
    if (-not [string]::IsNullOrWhiteSpace($existingMinimumDirectXVersion)) {
        $packageSeed.minimumDirectXVersion = $existingMinimumDirectXVersion
    }

    $existingMinimumSystemRam = [string](Get-OptionalObjectPropertyValue -InputObject $ExistingPackage -PropertyName 'minimumSystemRam')
    if (-not [string]::IsNullOrWhiteSpace($existingMinimumSystemRam)) {
        $packageSeed.minimumSystemRam = $existingMinimumSystemRam
    }

    return [pscustomobject]$packageSeed
}

function New-PartnerCenterUpdatedPackageEntries {
    param(
        [Parameter(Mandatory = $true)][string]$NewPackageFileName,
        [AllowNull()][object[]]$ExistingPackages,
        [switch]$MarkSupersededPackagesPendingDelete,
        [string]$RetryMissingPackageId
    )

    $updatedPackageEntries = @()
    $missingPackageIdApplied = $false

    foreach ($existingPackage in @($ExistingPackages)) {
        if ($null -eq $existingPackage) {
            continue
        }

        $existingPackageId = [string](Get-OptionalObjectPropertyValue -InputObject $existingPackage -PropertyName 'id')
        $packageIdOverride = ''
        if (-not $missingPackageIdApplied -and -not [string]::IsNullOrWhiteSpace($RetryMissingPackageId) -and [string]::IsNullOrWhiteSpace($existingPackageId)) {
            $packageIdOverride = $RetryMissingPackageId
            $missingPackageIdApplied = $true
        }

        $existingFileName = [string](Get-OptionalObjectPropertyValue -InputObject $existingPackage -PropertyName 'fileName')
        if ([string]::IsNullOrWhiteSpace($existingFileName)) {
            throw 'Partner Center existing package entry did not provide a fileName.'
        }

        $updatedPackageEntries += New-PartnerCenterPackageEntry `
            -FileName $existingFileName `
            -FileStatus (Get-PartnerCenterExistingPackageFileStatus -ExistingPackage $existingPackage -MarkSupersededPackagesPendingDelete:$MarkSupersededPackagesPendingDelete.IsPresent) `
            -ExistingPackage (New-PartnerCenterExistingPackageSeed -ExistingPackage $existingPackage -PackageIdOverride $packageIdOverride)
    }

    if (-not [string]::IsNullOrWhiteSpace($RetryMissingPackageId) -and -not $missingPackageIdApplied -and @($ExistingPackages).Count -gt 0) {
        throw "Partner Center reported missing package id '$RetryMissingPackageId', but no existing package entry was available to preserve it."
    }

    $updatedPackageEntries += New-PartnerCenterPackageEntry -FileName $NewPackageFileName -FileStatus 'PendingUpload' -ExistingPackage $null
    return ,@($updatedPackageEntries)
}

function Get-PartnerCenterPackageEntries {
    param(
        [AllowNull()][object]$SubmissionObject,
        [Parameter(Mandatory = $true)][string]$PackageCollectionPropertyName
    )

    $packageEntries = Get-OptionalObjectPropertyValue -InputObject $SubmissionObject -PropertyName $PackageCollectionPropertyName
    if ($null -eq $packageEntries) {
        return ,@()
    }

    return ,@($packageEntries)
}

function Get-MissingPackageIdsFromPartnerCenterError {
    param([Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$ErrorRecord)

    $responseBodyText = Get-PartnerCenterErrorResponseBodyText -ErrorRecord $ErrorRecord

    $errorDetailsMessage = ''
    if ($null -ne $ErrorRecord.ErrorDetails) {
        $errorDetailsMessage = [string]$ErrorRecord.ErrorDetails.Message
    }

    $formattedErrorText = ''
    try {
        $formattedErrorText = [string]($ErrorRecord | Out-String)
    }
    catch {
        $formattedErrorText = ''
    }

    $candidateTexts = @(
        $responseBodyText,
        [string]$ErrorRecord.Exception.Message,
        $errorDetailsMessage,
        $formattedErrorText,
        [string]$ErrorRecord.ToString()
    )

    foreach ($candidateText in $candidateTexts) {
        if ([string]::IsNullOrWhiteSpace($candidateText)) {
            continue
        }

        $normalizedCandidateText = [regex]::Replace($candidateText, '\r?\n\s*\|\s*', ' ')
        $missingPackagesMatch = [regex]::Match($normalizedCandidateText, 'missing\s+in\s+your\s+update:\s*(?<ids>[0-9,\s]+)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $missingPackagesMatch.Success) {
            continue
        }

        return @(
            $missingPackagesMatch.Groups['ids'].Value.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
    }

    return @()
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

    Invoke-WebRequest -Method Put -Uri $UploadUrl -InFile $ArchivePath -ContentType 'application/octet-stream' -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -UseBasicParsing | Out-Null
}

function Test-IsCommitFailureStatus {
    param([Parameter(Mandatory = $true)][string]$Status)

    return $Status -in @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'PublishFailed', 'ReleaseFailed', 'Canceled')
}

function Test-IsCommitAcceptedStatus {
    param([Parameter(Mandatory = $true)][string]$Status)

    return $Status -in @('CommitStarted', 'PreProcessing', 'Certification', 'PendingPublication', 'Publishing', 'Published', 'Release')
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
            willMarkSupersededPackagesPendingDelete = $MarkSupersededPackagesPendingDelete.IsPresent
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
    Write-Output "WillMarkSupersededPackagesPendingDelete=$($MarkSupersededPackagesPendingDelete.IsPresent.ToString().ToLowerInvariant())"
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

$pendingSubmission = Get-OptionalObjectPropertyValue -InputObject $submissionOwner -PropertyName $pendingSubmissionPropertyName
$publishedSubmission = Get-OptionalObjectPropertyValue -InputObject $submissionOwner -PropertyName $publishedSubmissionPropertyName

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

$publishedSubmissionDetails = $null
$createSubmissionBody = @{}

if ($SubmissionTarget -eq 'Production') {
    $publishedSubmissionUri = "$submissionsUri/$publishedSubmissionId"
    $publishedSubmissionDetails = Invoke-PartnerCenterRequest -Method Get -Uri $publishedSubmissionUri -AccessToken $token
    $createSubmissionBody = New-ProductionSubmissionCreateSeed -PublishedSubmissionDetails $publishedSubmissionDetails
}

try {
    Write-Output 'ProgressStage=CreateSubmissionRequested'
    $newSubmission = Invoke-PartnerCenterRequest -Method Post -Uri $submissionsUri -AccessToken $token -Body $createSubmissionBody
}
catch {
    if ($SubmissionTarget -ne 'Production') {
        throw
    }

    $submissionOwnerAfterCreate = Invoke-PartnerCenterRequest -Method Get -Uri $submissionOwnerUri -AccessToken $token
    $pendingSubmissionAfterCreate = Get-OptionalObjectPropertyValue -InputObject $submissionOwnerAfterCreate -PropertyName $pendingSubmissionPropertyName
    $pendingSubmissionIdAfterCreate = if ($null -ne $pendingSubmissionAfterCreate) { [string]$pendingSubmissionAfterCreate.id } else { '' }
    if ([string]::IsNullOrWhiteSpace($pendingSubmissionIdAfterCreate)) {
        throw
    }

    $newSubmission = [pscustomobject]@{ id = $pendingSubmissionIdAfterCreate }
    Write-Output 'ProgressStage=CreateSubmissionRecoveredFromPendingDraft'
}

$submissionId = [string]$newSubmission.id
if ([string]::IsNullOrWhiteSpace($submissionId)) {
    throw 'Partner Center did not return a submission id.'
}

Write-Output 'ProgressStage=CreateSubmissionCompleted'

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
    Write-Output 'ProgressStage=PackageUploadStarted'
    Upload-PartnerCenterPackage -UploadUrl $fileUploadUrl -ArchivePath $archivePath
    Write-Output 'ProgressStage=PackageUploadCompleted'
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}

if ($SubmissionTarget -eq 'Production') {
    $submissionListings = Get-OptionalObjectPropertyValue -InputObject $submission -PropertyName 'listings'
    if ((Get-ObjectEntryCount -InputObject $submissionListings) -eq 0 -and -not [string]::IsNullOrWhiteSpace($publishedSubmissionId)) {
        if ($null -eq $publishedSubmissionDetails) {
            $publishedSubmissionUri = "$submissionsUri/$publishedSubmissionId"
            $publishedSubmissionDetails = Invoke-PartnerCenterRequest -Method Get -Uri $publishedSubmissionUri -AccessToken $token
        }
        $submissionListings = Get-OptionalObjectPropertyValue -InputObject $publishedSubmissionDetails -PropertyName 'listings'
    }

    if ((Get-ObjectEntryCount -InputObject $submissionListings) -gt 0) {
        Set-OptionalObjectPropertyValue -InputObject $submission -PropertyName 'listings' -Value $submissionListings
    }
}

$existingPackages = Get-PartnerCenterPackageEntries -SubmissionObject $submission -PackageCollectionPropertyName $packageCollectionPropertyName
if ($existingPackages.Count -eq 0) {
    $existingPackages = Get-PartnerCenterPackageEntries -SubmissionObject $newSubmission -PackageCollectionPropertyName $packageCollectionPropertyName
}

if ($existingPackages.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($publishedSubmissionId)) {
    if ($null -eq $publishedSubmissionDetails) {
        $publishedSubmissionUri = "$submissionsUri/$publishedSubmissionId"
        $publishedSubmissionDetails = Invoke-PartnerCenterRequest -Method Get -Uri $publishedSubmissionUri -AccessToken $token
    }

    $existingPackages = Get-PartnerCenterPackageEntries -SubmissionObject $publishedSubmissionDetails -PackageCollectionPropertyName $packageCollectionPropertyName
}

$newPackageFileName = [System.IO.Path]::GetFileName($resolvedPackagePath)
$submission.$packageCollectionPropertyName = New-PartnerCenterUpdatedPackageEntries `
    -NewPackageFileName $newPackageFileName `
    -ExistingPackages $existingPackages `
    -MarkSupersededPackagesPendingDelete:$MarkSupersededPackagesPendingDelete.IsPresent

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

try {
    Write-Output 'ProgressStage=SubmissionUpdateStarted'
    $updatedSubmission = Invoke-PartnerCenterRequest -Method Put -Uri $submissionUri -AccessToken $token -Body $submission
    Write-Output 'ProgressStage=SubmissionUpdateCompleted'
}
catch {
    $missingPackageIds = @(Get-MissingPackageIdsFromPartnerCenterError -ErrorRecord $_)
    if ($missingPackageIds.Count -ne 1) {
        throw
    }

    $missingPackageId = $missingPackageIds[0]
    $currentPackageIds = @(
        @($submission.$packageCollectionPropertyName) |
            ForEach-Object { [string](Get-OptionalObjectPropertyValue -InputObject $_ -PropertyName 'id') } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($currentPackageIds -contains $missingPackageId) {
        throw
    }

    Write-Host "Partner Center update rejected the payload because package id $missingPackageId was missing. Retrying with the server-reported package id preserved."
    $submission.$packageCollectionPropertyName = New-PartnerCenterUpdatedPackageEntries `
        -NewPackageFileName $newPackageFileName `
        -ExistingPackages $existingPackages `
        -MarkSupersededPackagesPendingDelete:$MarkSupersededPackagesPendingDelete.IsPresent `
        -RetryMissingPackageId $missingPackageId
    Write-Output 'ProgressStage=SubmissionUpdateRetryStarted'
    $updatedSubmission = Invoke-PartnerCenterRequest -Method Put -Uri $submissionUri -AccessToken $token -Body $submission
    Write-Output 'ProgressStage=SubmissionUpdateCompleted'
}

$commitUri = "$submissionUri/commit"
Write-Output 'ProgressStage=CommitRequested'
Invoke-PartnerCenterRequest -Method Post -Uri $commitUri -AccessToken $token -Body @{} | Out-Null

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$finalStatus = ''
$finalStatusDetails = $null
$lastReportedStatus = ''

do {
    Start-Sleep -Seconds $StatusPollIntervalSeconds
    $currentSubmission = Invoke-PartnerCenterRequest -Method Get -Uri $submissionUri -AccessToken $token
    $finalStatus = [string]$currentSubmission.status
    $finalStatusDetails = $currentSubmission.statusDetails

    if ($finalStatus -ne $lastReportedStatus) {
        Write-Output "ProgressStage=CommitStatus:$finalStatus"
        $lastReportedStatus = $finalStatus
    }

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
        willMarkSupersededPackagesPendingDelete = $MarkSupersededPackagesPendingDelete.IsPresent
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
Write-Output "WillMarkSupersededPackagesPendingDelete=$($MarkSupersededPackagesPendingDelete.IsPresent.ToString().ToLowerInvariant())"
Write-Output "SubmissionStatus=$finalStatus"
if (-not [string]::IsNullOrWhiteSpace($resolvedEvidenceOutputPath)) {
    Write-Output "EvidenceOutputPath=$resolvedEvidenceOutputPath"
}