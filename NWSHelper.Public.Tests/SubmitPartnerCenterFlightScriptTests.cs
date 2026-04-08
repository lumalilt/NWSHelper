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
    public void SubmitPartnerCenterFlightScript_UploadUsesBasicParsing()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");
        var content = File.ReadAllText(scriptPath);

        Assert.Contains("Invoke-WebRequest -Method Put", content, StringComparison.Ordinal);
        Assert.Contains("-UseBasicParsing", content, StringComparison.Ordinal);
    }

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
    public void SubmitPartnerCenterFlightScript_FlightSubmit_TreatsCommitStartedAsAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildCommitStartedAcceptedBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("Flight", output["SubmissionTarget"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-commit-started", output["SubmissionId"]);
            Assert.Equal("CommitStarted", output["SubmissionStatus"]);

            Assert.True(File.Exists(evidencePath), $"Expected evidence file at {evidencePath}");

            using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var rootElement = document.RootElement;

            Assert.Equal("SubmissionCommitted", rootElement.GetProperty("status").GetString());
            Assert.Equal("CommitStarted", rootElement.GetProperty("submissionStatus").GetString());
            Assert.Equal("submission-commit-started", rootElement.GetProperty("submissionId").GetString());
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
    public void SubmitPartnerCenterFlightScript_FlightSubmit_CanMarkSupersededPackagePendingDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildExistingPackageReplacementBootstrap(packagePath, evidencePath, markSupersededPackagesPendingDelete: true));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("true", output["WillMarkSupersededPackagesPendingDelete"]);
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
    public void SubmitPartnerCenterFlightScript_ProductionSubmit_PreservesListingsFromPublishedSubmission()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-production-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildProductionSubmissionListingsBootstrap(packagePath, evidencePath));
            var rawStdout = output["__rawStdout"];

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("Production", output["SubmissionTarget"]);
            Assert.Equal("public-store-app", output["ApplicationId"]);
            Assert.Equal("submission-production-1", output["SubmissionId"]);
            Assert.Equal("Manual", output["TargetPublishMode"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);

            AssertProgressStageOrder(
                rawStdout,
                "ProgressStage=CreateSubmissionRequested",
                "ProgressStage=CreateSubmissionCompleted",
                "ProgressStage=PackageUploadStarted",
                "ProgressStage=PackageUploadCompleted",
                "ProgressStage=SubmissionUpdateStarted",
                "ProgressStage=SubmissionUpdateCompleted",
                "ProgressStage=CommitRequested",
                "ProgressStage=CommitStatus:PreProcessing",
                "Status=SubmissionCommitted");

            Assert.True(File.Exists(evidencePath), $"Expected evidence file at {evidencePath}");

            using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var rootElement = document.RootElement;

            Assert.Equal("SubmissionCommitted", rootElement.GetProperty("status").GetString());
            Assert.Equal("Production", rootElement.GetProperty("submissionTarget").GetString());
            Assert.Equal("submission-production-1", rootElement.GetProperty("submissionId").GetString());
            Assert.Equal("PreProcessing", rootElement.GetProperty("submissionStatus").GetString());
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
    public void SubmitPartnerCenterFlightScript_ProductionSubmit_OmitsCyclicPublishedSubmissionProperties()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-production-cyclic-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildProductionSubmissionCyclicPricingBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("Production", output["SubmissionTarget"]);
            Assert.Equal("submission-production-cyclic", output["SubmissionId"]);
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
    public void SubmitPartnerCenterFlightScript_ProductionSubmit_RetriesTransientGatewayTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-production-transient-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildProductionTransientGatewayTimeoutBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("Production", output["SubmissionTarget"]);
            Assert.Equal("submission-production-transient", output["SubmissionId"]);
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

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetryWithPendingDeleteModePreservesExistingPackageId()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPartnerCenterErrorRetryBootstrap(packagePath, evidencePath, markSupersededPackagesPendingDelete: true));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("true", output["WillMarkSupersededPackagesPendingDelete"]);
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

    [Fact]
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetriesUsingPackageIdFromPartnerCenterErrorDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPartnerCenterErrorDetailsRetryBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-5", output["SubmissionId"]);
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
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetriesUsingPackageIdFromWebExceptionResponseBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPartnerCenterWebExceptionRetryBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-6", output["SubmissionId"]);
            Assert.Equal("PreProcessing", output["SubmissionStatus"]);
            Assert.Contains("Retrying with the server-reported package id preserved.", output["__rawStdout"], StringComparison.Ordinal);
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
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetriesUsingPackageIdFromWrappedPartnerCenterErrorDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildWrappedPartnerCenterErrorDetailsRetryBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-7", output["SubmissionId"]);
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
    public void SubmitPartnerCenterFlightScript_FlightSubmit_RetriesUsingPackageIdFromPowerShellFormattedPartnerCenterError()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperStoreSubmission", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "NWSHelper-1.2.3.0.msix");
        var evidencePath = Path.Combine(root, "evidence", "partner-center-submission.json");

        Directory.CreateDirectory(root);
        WriteTestMsix(packagePath, identityName: "NWSHelper.NWSHelper", publisher: "CN=NWS Helper", version: "1.2.3.0");

        try
        {
            var output = RunPowerShellBootstrap(BuildPowerShellFormattedPartnerCenterErrorRetryBootstrap(packagePath, evidencePath));

            Assert.Equal("SubmissionCommitted", output["Status"]);
            Assert.Equal("test-flight", output["FlightId"]);
            Assert.Equal("submission-8", output["SubmissionId"]);
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

    private static string BuildExistingPackageReplacementBootstrap(string packagePath, string evidencePath, bool markSupersededPackagesPendingDelete = false)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");
        var expectedExistingPackageStatus = markSupersededPackagesPendingDelete ? "PendingDelete" : "Uploaded";
        var pruneArgument = markSupersededPackagesPendingDelete ? "  -MarkSupersededPackagesPendingDelete `\r\n" : string.Empty;

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
        if ($request.flightPackages.Count -ne 2) {
            throw 'Expected existing uploaded package plus new pending-upload package in update payload.'
        }

        $existingPackage = @($request.flightPackages | Where-Object { ($_.PSObject.Properties.Name -contains 'id') -and [string]$_.id -eq '2000000000093982100' }) | Select-Object -First 1
        if ($null -eq $existingPackage -or $existingPackage.fileName -ne 'NWSHelper-1.0.28.msix' -or $existingPackage.fileStatus -ne '{{expectedExistingPackageStatus}}') {
            throw 'Expected existing uploaded package entry to be preserved in update payload.'
        }

        $pendingUploadPackage = @($request.flightPackages | Where-Object { ($_.PSObject.Properties.Name -contains 'fileStatus') -and [string]$_.fileStatus -eq 'PendingUpload' }) | Select-Object -First 1
        $pendingUploadPackageId = if (($null -ne $pendingUploadPackage) -and ($pendingUploadPackage.PSObject.Properties.Name -contains 'id')) { [string]$pendingUploadPackage.id } else { '' }
        if ($null -eq $pendingUploadPackage -or -not [string]::IsNullOrWhiteSpace($pendingUploadPackageId) -or $pendingUploadPackage.fileName -ne 'NWSHelper-1.2.3.0.msix') {
            throw 'Expected appended pending-upload package entry for the new MSIX.'
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
{{pruneArgument}}  -ForceReplacePendingSubmission
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

    private static string BuildPartnerCenterErrorRetryBootstrap(string packagePath, string evidencePath, bool markSupersededPackagesPendingDelete = false)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");
        var expectedExistingPackageStatus = markSupersededPackagesPendingDelete ? "PendingDelete" : "Uploaded";
        var pruneArgument = markSupersededPackagesPendingDelete ? "  -MarkSupersededPackagesPendingDelete `\r\n" : string.Empty;

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
        if ($request.flightPackages.Count -ne 2) {
            throw 'Expected existing package plus new pending-upload package in update payload.'
        }

        $existingPackage = @($request.flightPackages | Where-Object { [string]$_.fileName -eq 'NWSHelper-1.0.30.0.msix' }) | Select-Object -First 1
        if ($null -eq $existingPackage) {
            throw 'Expected existing package entry in update payload.'
        }

        $existingPackageId = if ($existingPackage.PSObject.Properties.Name -contains 'id') { [string]$existingPackage.id } else { '' }
        $pendingUploadPackage = @($request.flightPackages | Where-Object { ($_.PSObject.Properties.Name -contains 'fileStatus') -and [string]$_.fileStatus -eq 'PendingUpload' }) | Select-Object -First 1
        $pendingUploadPackageId = if (($null -ne $pendingUploadPackage) -and ($pendingUploadPackage.PSObject.Properties.Name -contains 'id')) { [string]$pendingUploadPackage.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            if (-not [string]::IsNullOrWhiteSpace($existingPackageId)) {
                throw 'Expected first update payload to lack a package id.'
            }

            if ($existingPackage.fileStatus -ne '{{expectedExistingPackageStatus}}') {
                throw 'Expected existing package to use the requested retained or pending-delete status on first update.'
            }

            if ($null -eq $pendingUploadPackage -or -not [string]::IsNullOrWhiteSpace($pendingUploadPackageId) -or $pendingUploadPackage.fileName -ne 'NWSHelper-1.2.3.0.msix') {
                throw 'Expected appended pending-upload package entry for the new MSIX on first update.'
            }

            throw '{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }'
        }

        if ($existingPackageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by Partner Center.'
        }

        if ($existingPackage.fileStatus -ne '{{expectedExistingPackageStatus}}') {
            throw 'Expected retry payload to preserve the requested retained or pending-delete status for the existing package.'
        }

        if ($null -eq $pendingUploadPackage -or -not [string]::IsNullOrWhiteSpace($pendingUploadPackageId) -or $pendingUploadPackage.fileName -ne 'NWSHelper-1.2.3.0.msix') {
            throw 'Expected retry payload to keep the appended pending-upload package entry for the new MSIX.'
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
{{pruneArgument}}  -ForceReplacePendingSubmission
""";
    }

    private static string BuildPartnerCenterErrorDetailsRetryBootstrap(string packagePath, string evidencePath)
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
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-5' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-5' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-5') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-5'
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
            id = 'submission-5'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-5') {
        return [pscustomobject]@{
            id = 'published-5'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.31.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-5') {
        $global:UpdatePutCount++
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]
        $packageId = if ($package.PSObject.Properties.Name -contains 'id') { [string]$package.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            $exception = [System.Exception]::new('The remote server returned an error: (400) Bad Request.')
            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'InvalidParameterValue', [System.Management.Automation.ErrorCategory]::InvalidData, $null)
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new('{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }')
            throw $errorRecord
        }

        if ($packageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by Partner Center error details.'
        }

        return [pscustomobject]@{ id = 'submission-5' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-5/commit') {
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

    private static string BuildProductionSubmissionListingsBootstrap(string packagePath, string evidencePath)
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

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app') {
        return [pscustomobject]@{
            lastPublishedApplicationSubmission = [pscustomobject]@{ id = 'published-production-1' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions') {
        return [pscustomobject]@{ id = 'submission-production-1' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-1') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-production-1'
                fileUploadUrl = 'https://example.invalid/upload'
                applicationPackages = @()
                listings = @()
                targetPublishMode = 'Manual'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-production-1'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/published-production-1') {
        return [pscustomobject]@{
            id = 'published-production-1'
            pricing = [pscustomobject]@{
                basePrice = 'Free'
            }
            listings = [pscustomobject]@{
                'en-us' = [pscustomobject]@{
                    baseListing = [pscustomobject]@{
                        title = 'NWS Helper'
                    }
                }
                'en' = [pscustomobject]@{
                    baseListing = [pscustomobject]@{
                        title = 'NWS Helper'
                    }
                }
            }
            applicationPackages = @(
                [pscustomobject]@{
                    id = '2000000000093982101'
                    fileName = 'NWSHelper-1.0.30.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions') {
        $request = $Body | ConvertFrom-Json

        if ($null -eq $request.pricing -or [string]$request.pricing.basePrice -ne 'Free') {
            throw 'Expected production create payload to preserve published pricing.'
        }

        if ($request.listings -is [System.Array]) {
            throw 'Expected production create payload listings to preserve the locale-keyed object shape.'
        }

        if (@($request.listings.PSObject.Properties.Name).Count -ne 2) {
            throw 'Expected production create payload to preserve both published locale listings.'
        }

        if (@($request.applicationPackages).Count -ne 1) {
            throw 'Expected production create payload to preserve the published application packages.'
        }

        if ($null -ne $request.PSObject.Properties['gamingOptions']) {
            throw 'Expected production create payload to omit gamingOptions and let Partner Center clone that state server-side.'
        }

        return [pscustomobject]@{ id = 'submission-production-1' }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-1') {
        $request = $Body | ConvertFrom-Json

        if ($request.listings -is [System.Array]) {
            throw 'Expected production update payload listings to preserve the locale-keyed object shape.'
        }

        if (@($request.listings.PSObject.Properties.Name).Count -ne 2) {
            throw 'Expected production update payload to preserve both published locale listings.'
        }

        if (@($request.applicationPackages).Count -ne 2) {
            throw 'Expected production update payload to include the existing package and the new pending-upload package.'
        }

        $listing = $request.listings.'en-us'
        if ($null -eq $listing -or [string]$listing.baseListing.title -ne 'NWS Helper') {
            throw 'Expected production update payload to preserve the published en-us listing.'
        }

        return [pscustomobject]@{ id = 'submission-production-1' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-1/commit') {
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
  -SubmissionTarget Production `
  -ApplicationId public-store-app `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Manual `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1
""";
    }

    private static string BuildProductionTransientGatewayTimeoutBootstrap(string packagePath, string evidencePath)
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

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app') {
        return [pscustomobject]@{
            lastPublishedApplicationSubmission = [pscustomobject]@{ id = 'published-production-transient' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions') {
        return [pscustomobject]@{ id = 'submission-production-transient' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-transient') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-production-transient'
                fileUploadUrl = 'https://example.invalid/upload'
                applicationPackages = @()
                listings = @()
                targetPublishMode = 'Manual'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-production-transient'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/published-production-transient') {
        return [pscustomobject]@{
            id = 'published-production-transient'
            pricing = [pscustomobject]@{
                basePrice = 'Free'
            }
            listings = [pscustomobject]@{
                'en-us' = [pscustomobject]@{
                    baseListing = [pscustomobject]@{
                        title = 'NWS Helper'
                    }
                }
                'en' = [pscustomobject]@{
                    baseListing = [pscustomobject]@{
                        title = 'NWS Helper'
                    }
                }
            }
            applicationPackages = @(
                [pscustomobject]@{
                    id = '2000000000093982102'
                    fileName = 'NWSHelper-1.0.30.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions') {
        $request = $Body | ConvertFrom-Json

        if ($null -eq $request.pricing -or [string]$request.pricing.basePrice -ne 'Free') {
            throw 'Expected transient production create payload to preserve published pricing.'
        }

        if ($request.listings -is [System.Array]) {
            throw 'Expected transient production create payload listings to preserve the locale-keyed object shape.'
        }

        if (@($request.listings.PSObject.Properties.Name).Count -ne 2) {
            throw 'Expected transient production create payload to preserve both published locale listings.'
        }

        if (@($request.applicationPackages).Count -ne 1) {
            throw 'Expected transient production create payload to preserve the published application packages.'
        }

        if ($null -ne $request.PSObject.Properties['gamingOptions']) {
            throw 'Expected transient production create payload to omit gamingOptions and let Partner Center clone that state server-side.'
        }

        return [pscustomobject]@{ id = 'submission-production-transient' }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-transient') {
        $global:UpdatePutCount++

        if ($global:UpdatePutCount -eq 1) {
            $exception = [System.Exception]::new('The remote server returned an error: (504) Gateway Timeout.')
            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'GatewayTimeout', [System.Management.Automation.ErrorCategory]::OperationTimeout, $null)
            $htmlMessage = @'
body {
    font-family: Arial;
}
Service unavailable
504 The service behind this page isn't responding to Azure Front Door.
Gateway Timeout
Azure Front Door cannot connect to the origin server at this time.
Error Info:OriginTimeout
'@
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($htmlMessage)
            throw $errorRecord
        }

        $request = $Body | ConvertFrom-Json
        if ($request.listings -is [System.Array]) {
            throw 'Expected retry payload listings to preserve the locale-keyed object shape.'
        }

        if (@($request.listings.PSObject.Properties.Name).Count -ne 2) {
            throw 'Expected retry payload to preserve both published locale listings after transient gateway timeout.'
        }

        if (@($request.applicationPackages).Count -ne 2) {
            throw 'Expected retry payload to preserve package entries after transient gateway timeout.'
        }

        return [pscustomobject]@{ id = 'submission-production-transient' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-transient/commit') {
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
  -SubmissionTarget Production `
  -ApplicationId public-store-app `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Manual `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1
""";
    }

    private static string BuildProductionSubmissionCyclicPricingBootstrap(string packagePath, string evidencePath)
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

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app') {
        return [pscustomobject]@{
            lastPublishedApplicationSubmission = [pscustomobject]@{ id = 'published-production-cyclic' }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/published-production-cyclic') {
        $pricing = [pscustomobject]@{
            basePrice = 'Free'
        }

        $pricing | Add-Member -NotePropertyName self -NotePropertyValue $pricing

        return [pscustomobject]@{
            id = 'published-production-cyclic'
            pricing = $pricing
            listings = [pscustomobject]@{
                'en-us' = [pscustomobject]@{
                    baseListing = [pscustomobject]@{
                        title = 'NWS Helper'
                    }
                }
            }
            applicationPackages = @(
                [pscustomobject]@{
                    id = '2000000000093982103'
                    fileName = 'NWSHelper-1.0.41.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions') {
        $request = $Body | ConvertFrom-Json

        if ($null -eq $request.pricing -or [string]$request.pricing.basePrice -ne 'Free') {
            throw 'Expected production create payload to preserve pricing when published submission contains a self reference.'
        }

        if ($null -ne $request.pricing.PSObject.Properties['self']) {
            throw 'Expected production create payload to omit cyclic pricing references.'
        }

        return [pscustomobject]@{ id = 'submission-production-cyclic' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-cyclic') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-production-cyclic'
                fileUploadUrl = 'https://example.invalid/upload'
                applicationPackages = @()
                listings = @()
                targetPublishMode = 'Manual'
                targetPublishDate = $null
                notesForCertification = ''
                status = 'PendingCommit'
                statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
            }
        }

        return [pscustomobject]@{
            id = 'submission-production-cyclic'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-cyclic') {
        $request = $Body | ConvertFrom-Json

        if (@($request.applicationPackages).Count -ne 2) {
            throw 'Expected production update payload to include the published package and the new pending-upload package.'
        }

        return [pscustomobject]@{ id = 'submission-production-cyclic' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/submissions/submission-production-cyclic/commit') {
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
  -SubmissionTarget Production `
  -ApplicationId public-store-app `
  -PackagePath {{ToPowerShellLiteral(packagePath)}} `
  -TenantId tenant-id `
  -ClientId client-id `
  -ClientSecret client-secret `
  -ExpectedPackageIdentityName NWSHelper.NWSHelper `
  -ExpectedPackagePublisher 'CN=NWS Helper' `
  -TargetPublishMode Manual `
  -EvidenceOutputPath {{ToPowerShellLiteral(evidencePath)}} `
  -StatusPollIntervalSeconds 1 `
  -CommitStatusTimeoutMinutes 1
""";
    }

    private static string BuildPartnerCenterWebExceptionRetryBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
$global:SubmissionGetCount = 0
$global:UpdatePutCount = 0
$global:errorPayload = '{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your update: 2000000000093982100", "target": "packages" }'

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
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-6' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-6' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-6') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-6'
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
            id = 'submission-6'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-6') {
        return [pscustomobject]@{
            id = 'published-6'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.31.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-6') {
        $global:UpdatePutCount++
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]
        $packageId = if ($package.PSObject.Properties.Name -contains 'id') { [string]$package.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            if (-not [string]::IsNullOrWhiteSpace($packageId)) {
                throw 'Expected first update payload to lack a package id.'
            }

            $fakeResponse = [pscustomobject]@{
                Payload = $global:errorPayload
            }
            $fakeResponse | Add-Member -MemberType ScriptMethod -Name GetResponseStream -Value {
                return [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($this.Payload))
            }

            $exception = [System.Exception]::new('The remote server returned an error: (400) Bad Request.')
            $exception | Add-Member -MemberType NoteProperty -Name Response -Value $fakeResponse
            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'InvalidParameterValue', [System.Management.Automation.ErrorCategory]::InvalidData, $null)
            throw $errorRecord
        }

        if ($packageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by the web exception response body.'
        }

        return [pscustomobject]@{ id = 'submission-6' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-6/commit') {
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

    private static string BuildWrappedPartnerCenterErrorDetailsRetryBootstrap(string packagePath, string evidencePath)
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
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-7' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-7' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-7') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-7'
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
            id = 'submission-7'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-7') {
        return [pscustomobject]@{
            id = 'published-7'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.31.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-7') {
        $global:UpdatePutCount++
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]
        $packageId = if ($package.PSObject.Properties.Name -contains 'id') { [string]$package.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            $exception = [System.Exception]::new('The remote server returned an error: (400) Bad Request.')
            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'InvalidParameterValue', [System.Management.Automation.ErrorCategory]::InvalidData, $null)
            $wrappedMessage = @'
{ "code": "InvalidParameterValue", "message": "Please keep all file entries for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are missing in your 
update: 2000000000093982100", "target": "packages" }
'@
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($wrappedMessage)
            throw $errorRecord
        }

        if ($packageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by wrapped Partner Center error details.'
        }

        return [pscustomobject]@{ id = 'submission-7' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-7/commit') {
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

    private static string BuildCommitStartedAcceptedBootstrap(string packagePath, string evidencePath)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "store", "submit-partner-center-flight.ps1");

        return $$"""
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
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-commit-started' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-commit-started' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-commit-started') {
        return [pscustomobject]@{
            id = 'submission-commit-started'
            fileUploadUrl = 'https://example.invalid/upload'
            flightPackages = @()
            targetPublishMode = 'Immediate'
            targetPublishDate = $null
            notesForCertification = ''
            status = 'CommitStarted'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-commit-started') {
        return [pscustomobject]@{
            id = 'published-commit-started'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.38.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-commit-started') {
        return [pscustomobject]@{ id = 'submission-commit-started' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-commit-started/commit') {
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

    private static string BuildPowerShellFormattedPartnerCenterErrorRetryBootstrap(string packagePath, string evidencePath)
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
            lastPublishedFlightSubmission = [pscustomobject]@{ id = 'published-8' }
        }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions') {
        return [pscustomobject]@{ id = 'submission-8' }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-8') {
        $global:SubmissionGetCount++

        if ($global:SubmissionGetCount -eq 1) {
            return [pscustomobject]@{
                id = 'submission-8'
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
            id = 'submission-8'
            status = 'PreProcessing'
            statusDetails = [pscustomobject]@{ errors = @(); warnings = @() }
        }
    }

    if ($Method -eq 'Get' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/published-8') {
        return [pscustomobject]@{
            id = 'published-8'
            flightPackages = @(
                [pscustomobject]@{
                    fileName = 'NWSHelper-1.0.31.0.msix'
                    fileStatus = 'Uploaded'
                    minimumDirectXVersion = 'None'
                    minimumSystemRam = 'None'
                }
            )
        }
    }

    if ($Method -eq 'Put' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-8') {
        $global:UpdatePutCount++
        $request = $Body | ConvertFrom-Json
        $package = $request.flightPackages[0]
        $packageId = if ($package.PSObject.Properties.Name -contains 'id') { [string]$package.id } else { '' }

        if ($global:UpdatePutCount -eq 1) {
            $exception = [System.Exception]::new('The remote server returned an error: (400) Bad Request.')
            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'InvalidParameterValue', [System.Management.Automation.ErrorCategory]::InvalidData, $null)
            $formattedMessage = @'
     | { "code": "InvalidParameterValue",   "message": "Please keep all file entries
     | for existing packages. If you wish to remove a package, mark it as PendingDelete. The following packages are
     | missing in your 
     | update: 2000000000093982100",   "target": "packages" }
'@
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($formattedMessage)
            throw $errorRecord
        }

        if ($packageId -ne '2000000000093982100') {
            throw 'Expected retry payload to preserve the package id reported by PowerShell-formatted Partner Center error text.'
        }

        return [pscustomobject]@{ id = 'submission-8' }
    }

    if ($Method -eq 'Post' -and $Uri -eq 'https://manage.devcenter.microsoft.com/v1.0/my/applications/public-store-app/flights/test-flight/submissions/submission-8/commit') {
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

    private static void AssertProgressStageOrder(string stdout, params string[] markers)
    {
        var nextSearchIndex = 0;

        foreach (var marker in markers)
        {
            var markerIndex = stdout.IndexOf(marker, nextSearchIndex, StringComparison.Ordinal);
            Assert.True(
                markerIndex >= 0,
                $"Expected stdout to contain marker '{marker}' after index {nextSearchIndex}.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}");
            nextSearchIndex = markerIndex + marker.Length;
        }
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