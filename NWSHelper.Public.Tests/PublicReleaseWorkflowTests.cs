using System;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class PublicReleaseWorkflowTests
{
    [Fact]
    public void PublicReleaseWorkflow_MsixAssetsAreOptInForGitHubReleases()
    {
        var workflowPath = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "public-release-orchestration.yml");
        Assert.True(File.Exists(workflowPath), $"Expected workflow at {workflowPath}");

        var workflow = File.ReadAllText(workflowPath);
        var normalizedWorkflow = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        var canarySubmitSection = GetSection(normalizedWorkflow, "  partner-center-canary-submit:\n", "  partner-center-production-prepare:\n");
        var productionPrepareSection = GetSection(normalizedWorkflow, "  partner-center-production-prepare:\n", "  partner-center-production-submit:\n");
        var productionSubmitSection = GetSection(normalizedWorkflow, "  partner-center-production-submit:\n");

        Assert.Contains("INCLUDE_GITHUB_RELEASE_MSIX", workflow, StringComparison.Ordinal);
        Assert.Contains("build_msix_package", workflow, StringComparison.Ordinal);
        Assert.Contains("include_msix_in_release", workflow, StringComparison.Ordinal);
        Assert.Contains("$packagingParameters = @{", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishDirectory = $env:GUI_PUBLISH_DIR", workflow, StringComparison.Ordinal);
        Assert.Contains("$packagingParameters.SkipMsix = $true", workflow, StringComparison.Ordinal);
        Assert.Contains("& ./scripts/package-gui-distribution.ps1 @packagingParameters", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($env:INCLUDE_MSIX_IN_RELEASE -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("if ('${{ steps.msix-policy.outputs.build_msix_package }}' -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("if ('${{ steps.msix-policy.outputs.include_msix_in_release }}' -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("MSIX_SIGNING_CERTIFICATE_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("MSIX_SIGNING_CERTIFICATE_PASSWORD", workflow, StringComparison.Ordinal);
        Assert.Contains("PARTNER_CENTER_PRODUCTION_SUBMISSION_NOTES", workflow, StringComparison.Ordinal);
        Assert.Contains("prepare_canary_flight", workflow, StringComparison.Ordinal);
        Assert.Contains("submit_canary_flight", workflow, StringComparison.Ordinal);
        Assert.Contains("should_prepare_canary", workflow, StringComparison.Ordinal);
        Assert.Contains("should_submit_canary", workflow, StringComparison.Ordinal);
        Assert.Contains("'${{ needs.release-gate.outputs.should_prepare_canary }}' -eq 'true'", workflow, StringComparison.Ordinal);
        Assert.Contains("partner-center-canary-prepare:", workflow, StringComparison.Ordinal);
        Assert.Contains("partner-center-canary-submit:", workflow, StringComparison.Ordinal);
        Assert.Contains("canary-package-flight", workflow, StringComparison.Ordinal);
        Assert.Contains("PARTNER_CENTER_CANARY_FLIGHT_ID", workflow, StringComparison.Ordinal);
        Assert.Contains("PARTNER_CENTER_CANARY_SUBMISSION_NOTES", workflow, StringComparison.Ordinal);
        Assert.Contains("partner-center-canary-package-${{ env.VERSION }}", workflow, StringComparison.Ordinal);
        Assert.Contains("partner-center-canary-submission-${{ env.VERSION }}", workflow, StringComparison.Ordinal);
        Assert.Contains("SubmissionTarget = 'Flight'", workflow, StringComparison.Ordinal);
        Assert.Contains("prepare_canary_flight:\n        description: Stage the canary private-flight package and run local identity validation during workflow_dispatch\n        required: false\n        default: 'true'", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains("submit_canary_flight:\n        description: Submit the prepared canary private flight to Partner Center during workflow_dispatch\n        required: false\n        default: 'true'", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains("prepare_store_publish:\n        description: Stage production Store submission artifacts and run local identity validation during workflow_dispatch\n        required: false\n        default: 'true'", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains("Manual GitHub Release, canary flight, and production Store operations must run from a v* tag ref.", workflow, StringComparison.Ordinal);
        Assert.Contains("$shouldPublishGitHubRelease = $true\n                $shouldPackageBundle = $true\n                $shouldPrepareCanary = $true\n                $shouldSubmitCanary = $true\n                $shouldPrepareStore = $true\n                $shouldSubmitStore = $true", normalizedWorkflow, StringComparison.Ordinal);
        Assert.Contains("ForceReplacePendingSubmission = $true", canarySubmitSection, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.force_replace_pending_store_submission", canarySubmitSection, StringComparison.Ordinal);
        Assert.Contains("environment:\n      name: ${{ 'public-store-release' }}", productionPrepareSection, StringComparison.Ordinal);
        Assert.Contains("environment:\n      name: ${{ 'public-store-release' }}", productionSubmitSection, StringComparison.Ordinal);
        Assert.Contains("GH_REPO: ${{ github.repository }}", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release view $tag --repo $env:GH_REPO", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release upload $tag $assets --repo $env:GH_REPO --clobber", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-WorkflowRunsForSha 'public-core-compatibility.yml' $sourceSha", workflow, StringComparison.Ordinal);
        Assert.Contains("public-core-compatibility failed for SHA $sourceSha", workflow, StringComparison.Ordinal);
        Assert.Contains("required successful public-ci and public-core-compatibility", workflow, StringComparison.Ordinal);
        Assert.Contains("Stage published release assets for metadata generation", workflow, StringComparison.Ordinal);
        Assert.Contains("RELEASE_METADATA_STAGING_DIR", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $assetPath -Destination (Join-Path $resolvedReleaseMetadataStagingDirectory (Split-Path -Path $assetPath -Leaf)) -Force", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/generate-artifact-checksums.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $checksumsPath -Destination (Join-Path $resolvedReleaseMetadataStagingDirectory 'checksums.sha256') -Force", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/generate-update-metadata.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-ArtifactsPath $resolvedReleaseMetadataStagingDirectory", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/compute-version.ps1 -TagBuild", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/compute-version.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Validate tag matches version metadata", workflow, StringComparison.Ordinal);
        Assert.Contains("Release tag '$env:RELEASE_TAG' does not match version.json version '$expectedVersionPrefix'", workflow, StringComparison.Ordinal);
        Assert.Contains("$tagVersion.StartsWith(\"$expectedVersionPrefix-\"", workflow, StringComparison.Ordinal);
        Assert.Contains("$submissionParameters = @{", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-ForceReplacePendingSubmission:${{ inputs.force_replace_pending_store_submission == 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("else {", workflow, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string GetSection(string content, string startMarker, string? endMarker = null)
    {
        var startIndex = content.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Expected to find section start '{startMarker}'.");

        if (endMarker is null)
        {
            return content[startIndex..];
        }

        var endIndex = content.IndexOf(endMarker, startIndex + startMarker.Length, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Expected to find section end '{endMarker}'.");
        return content[startIndex..endIndex];
    }
}