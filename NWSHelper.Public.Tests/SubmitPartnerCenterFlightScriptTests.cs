using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace NWSHelper.Tests;

public class SubmitPartnerCenterFlightScriptTests
{
    [Fact]
    public void SubmitPartnerCenterFlightScript_ValidateOnlyProduction_WritesEvidenceAndOutputsIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-validation.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellScript(
                scriptPathSegments: ["scripts", "store", "submit-partner-center-flight.ps1"],
                arguments:
                [
                    "-SubmissionTarget", "Production",
                    "-ApplicationId", "public-store-app",
                    "-PackagePath", packagePath,
                    "-ExpectedPackageIdentityName", "NWSHelper.NWSHelper",
                    "-ExpectedPackagePublisher", "CN=NWS Helper",
                    "-TargetPublishMode", "Manual",
                    "-EvidenceOutputPath", evidencePath,
                    "-ValidateOnly"
                ]);

            Assert.Equal("ValidationComplete", output["Status"]);
            Assert.Equal("Production", output["SubmissionTarget"]);
            Assert.Equal("public-store-app", output["ApplicationId"]);
            Assert.Equal("NWSHelper.NWSHelper", output["PackageIdentityName"]);
            Assert.Equal("CN=NWS Helper", output["PackagePublisher"]);
            Assert.Equal("1.2.3.0", output["PackageVersion"]);
            Assert.Equal("Manual", output["TargetPublishMode"]);
            Assert.Equal("true", output["WillWriteEvidence"]);

            Assert.True(File.Exists(evidencePath), $"Expected evidence file at {evidencePath}");

            using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var rootElement = document.RootElement;

            Assert.Equal("ValidationComplete", rootElement.GetProperty("status").GetString());
            Assert.Equal("Production", rootElement.GetProperty("submissionTarget").GetString());
            Assert.Equal("public-store-app", rootElement.GetProperty("applicationId").GetString());
            Assert.Equal("NWSHelper.NWSHelper", rootElement.GetProperty("packageIdentityName").GetString());
            Assert.Equal("CN=NWS Helper", rootElement.GetProperty("packagePublisher").GetString());
            Assert.Equal("1.2.3.0", rootElement.GetProperty("packageVersion").GetString());
            Assert.Equal("Manual", rootElement.GetProperty("targetPublishMode").GetString());
            Assert.False(rootElement.GetProperty("willReplacePendingSubmission").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_WithMissingPendingSubmissionProperty_Completes()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildMissingPendingFlightSubmissionBootstrap(packagePath, evidencePath));

            Assert.True(
                output.TryGetValue("Status", out var status),
                $"Expected Status in PowerShell output.{Environment.NewLine}STDOUT:{Environment.NewLine}{output["__rawStdout"]}{Environment.NewLine}STDERR:{Environment.NewLine}{output["__rawStderr"]}");

            Assert.Equal("SubmissionCommitted", status);
            Assert.Equal("Flight", output["SubmissionTarget"]);
            Assert.Equal("public-store-app", output["ApplicationId"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-1", output["SubmissionId"]);
            Assert.Equal("NWSHelper.NWSHelper", output["PackageIdentityName"]);
            Assert.Equal("CN=NWS Helper", output["PackagePublisher"]);
            Assert.Equal("1.2.3.0", output["PackageVersion"]);
            Assert.Equal("Manual", output["TargetPublishMode"]);
            Assert.Equal("true", output["WillReplacePendingSubmission"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);

            Assert.True(File.Exists(evidencePath), $"Expected evidence file at {evidencePath}");

            using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var rootElement = document.RootElement;

            Assert.Equal("SubmissionCommitted", rootElement.GetProperty("status").GetString());
            Assert.Equal("Flight", rootElement.GetProperty("submissionTarget").GetString());
            Assert.Equal("public-store-app", rootElement.GetProperty("applicationId").GetString());
            Assert.Equal("test-flight", rootElement.GetProperty("flightId").GetString());
            Assert.Equal("submission-1", rootElement.GetProperty("submissionId").GetString());
            Assert.True(rootElement.GetProperty("willReplacePendingSubmission").GetBoolean());
            Assert.Equal("PreProcessing", rootElement.GetProperty("submissionStatus").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_PreservesExistingPackageIdDuringUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildExistingPackageReplacementBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-2", output["SubmissionId"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_FallsBackToPublishedSubmissionPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPublishedSubmissionFallbackBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-3", output["SubmissionId"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetriesUsingPackageIdFromPartnerCenterError()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPartnerCenterErrorRetryBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-4", output["SubmissionId"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteTestMsix(string packagePath, string identityName, string publisher, string version)
    {
        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var entry = archive.CreateEntry("AppxManifest.xml");

        using var writer = new StreamWriter(entry.Open());
        writer.Write(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\n" +
            $"  <Identity Name=\"{identityName}\" Publisher=\"{publisher}\" Version=\"{version}\" />\n" +
            "</Package>");
    }

    private static string BuildMissingPendingFlightSubmissionBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
    $global:SubmissionGetCount = 0

function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$ContentType,
        $Body
    )

    if ($Uri -like 'https://login.microsoftonline.com/*/oauth2/token') {
        return [pscustomobject]@{ access_token = 'test-token' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight') {
        return [pscustomobject]@{
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-1' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-1' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-1') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-1'
                fileUploadUrl = 'https://example.invalid/upload'
                flightPackages = @()
                targetPublishMode = 'Manual'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-1'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-1') {
        return [pscustomobject]@{
            id = 'published-1'
            flightPackages = @(
                [pscustomobject]@{
                    id = '2000000000093982100'
                    fileName = 'NWSHelper-1.0.25.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-1') {
        return [pscustomobject]@{ id = 'submission-1' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-1/commit') {
        return [pscustomobject]@{}
    }

    throw "Unexpected Invoke-RestMethod call: $Method $Uri"
}

function Invoke-WebRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$InFile,
        [string]$ContentType,
        [hashtable]$Headers
    )

    return [pscustomobject]@{ StatusCode = 201 }
}

function Start-Sleep {
    param([int]$Seconds)
}

& {{ToPowerShellLiteral(scriptPath)}} `
  -SubmissionTarget Flight `
  -ApplicationId public-store-app `
  -FlightId test-flight `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Manual `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1 `
  -ForceReplacePendingSubmission
""";
    }

    private static string BuildExistingPackageReplacementBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
$global:SubmissionGetCount = 0

function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$ContentType,
        $Body
    )

    if ($Uri -like 'https://login.microsoftonline.com/*/oauth2/token') {
        return [pscustomobject]@{ access_token = 'test-token' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight') {
        return [pscustomobject]@{
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-2' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-2' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-2') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-2'
                fileUploadUrl = 'https://example.invalid/upload'
                flightPackages = @(
                    [pscustomobject]@{
                        id = '2000000000093982100'
                        fileName = 'NWSHelper-1.0.28.msix'
                        fileStatus = 'Uploaded'
                        minimumDirectXVersion = 'None'
                        minimumSystemRam = 'None'
                    }
                )
                targetPublishMode = 'Immediate'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-2'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-2') {
        $request = $Body | ConvertFrom-Json
        if ($request.flightPackages.Count -ne 1) {
            throw 'Expected exactly one flight package in update payload.'
        }

        $package = $request.flightPackages[0]
        if ($package.id -ne '2000000000093982100') {
            throw '{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }'
        }

        if ($package.fileName -ne 'NWSHelper-1.2.3.0.msix' -or $package.fileStatus -ne 'PendingUpload') {
            throw 'Expected replacement package entry with PendingUpload status.'
        }

        return [pscustomobject]@{ id = 'submission-2' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-2/commit') {
        return [pscustomobject]@{ status = 'CommitStarted' }
    }

    throw "Unexpected Invoke-RestMethod call: $Method $Uri"
}

function Invoke-WebRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$InFile,
        [string]$ContentType,
        [hashtable]$Headers
    )

    return [pscustomobject]@{ StatusCode = 201 }
}

function Start-Sleep {
    param([int]$Seconds)
}

& {{ToPowerShellLiteral(scriptPath)}} `
  -SubmissionTarget Flight `
  -ApplicationId public-store-app `
  -FlightId test-flight `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Immediate `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1 `
  -ForceReplacePendingSubmission
""";
    }

    private static string BuildPublishedSubmissionFallbackBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
$global:SubmissionGetCount = 0

function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$ContentType,
        $Body
    )

    if ($Uri -like 'https://login.microsoftonline.com/*/oauth2/token') {
        return [pscustomobject]@{ access_token = 'test-token' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight') {
        return [pscustomobject]@{
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-3' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-3' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-3') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-3'
                fileUploadUrl = 'https://example.invalid/upload'
                flightPackages = @()
                targetPublishMode = 'Immediate'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-3'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-3') {
        return [pscustomobject]@{
            id = 'published-3'
            flightPackages = @(
                [pscustomobject]@{
                    id = '2000000000093982100'
                    fileName = 'NWSHelper-1.0.25.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-3') {
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]

        if ($package.id -ne '2000000000093982100') {
            throw '{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }'
        }

        return [pscustomobject]@{ id = 'submission-3' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-3/commit') {
        return [pscustomobject]@{ status = 'CommitStarted' }
    }

    throw "Unexpected Invoke-RestMethod call: $Method $Uri"
}

function Invoke-WebRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$InFile,
        [string]$ContentType,
        [hashtable]$Headers
    )

    return [pscustomobject]@{ StatusCode = 201 }
}

function Start-Sleep {
    param([int]$Seconds)
}

& {{ToPowerShellLiteral(scriptPath)}} `
  -SubmissionTarget Flight `
  -ApplicationId public-store-app `
  -FlightId test-flight `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Immediate `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1 `
  -ForceReplacePendingSubmission
""";
    }

    private static string BuildPartnerCenterErrorRetryBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
$global:SubmissionGetCount = 0
$global:UpdatePutCount = 0

function Invoke-RestMethod {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$ContentType,
        $Body
    )

    if ($Uri -like 'https://login.microsoftonline.com/*/oauth2/token') {
        return [pscustomobject]@{ access_token = 'test-token' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight') {
        return [pscustomobject]@{
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-4' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-4' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-4') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-4'
                fileUploadUrl = 'https://example.invalid/upload'
                flightPackages = @()
                targetPublishMode = 'Immediate'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-4'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-4') {
        return [pscustomobject]@{
            id = 'published-4'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.30.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-4') {
        $global:UpdatePutCount++
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]
        $packageId = if ($package.PSObject.Properties.Name -contains 'id') { [string]$package.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            if (-not [string]::IsNullOrWhiteSpace($packageId)) {
                throw 'Expected first update payload to lack a package id.'
            }

            throw '{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }'
        }

        if ($packageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by Partner Center.'
        }

        return [pscustomobject]@{ id = 'submission-4' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-4/commit') {
        return [pscustomobject]@{ status = 'CommitStarted' }
    }

    throw "Unexpected Invoke-RestMethod call: $Method $Uri"
}

function Invoke-WebRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$InFile,
        [string]$ContentType,
        [hashtable]$Headers
    )

    return [pscustomobject]@{ StatusCode = 201 }
}

function Start-Sleep {
    param([int]$Seconds)
}

& {{ToPowerShellLiteral(scriptPath)}} `
  -SubmissionTarget Flight `
  -ApplicationId public-store-app `
  -FlightId test-flight `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Immediate `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1 `
  -ForceReplacePendingSubmission
""";
    }

    private static Dictionary<string, string> RunPowerShellScript(IReadOnlyList<string> scriptPathSegments, IReadOnlyList<string> arguments)
    {
        var scriptPath = Path.Combine([GetRepositoryRoot(), .. scriptPathSegments]);
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        return RunPowerShellFile(scriptPath, arguments);
    }

    private static Dictionary<string, string> RunPowerShellBootstrap(string bootstrapScript)
    {
        var bootstrapPath = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", $"bootstrap-{Guid.NewGuid():N}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapPath)!);
        File.WriteAllText(bootstrapPath, bootstrapScript);

        try
        {
            return RunPowerShellFile(bootstrapPath, Array.Empty<string>());
        }
        finally
        {
            if (File.Exists(bootstrapPath))
            {
                File.Delete(bootstrapPath);
            }
        }
    }

    private static Dictionary<string, string> RunPowerShellFile(string scriptPath, IReadOnlyList<string> arguments)
    {
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"submit-partner-center-flight.ps1 failed with exit code {process.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");

        var values = ParseOutputLines(stdout);
        values["__rawStdout"] = stdout;
        values["__rawStderr"] = stderr;
        return values;
    }

    private static string ToPowerShellLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static Dictionary<string, string> ParseOutputLines(string stdout)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var lines = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.Contains('='));

        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf('=');
            Assert.True(separatorIndex > 0, $"Expected key=value output line, got: {line}");

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            values[key] = value;
        }

        return values;
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string ResolvePowerShellExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsPowerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            if (File.Exists(windowsPowerShell))
            {
                return windowsPowerShell;
            }
        }

        return "pwsh";
    }
}