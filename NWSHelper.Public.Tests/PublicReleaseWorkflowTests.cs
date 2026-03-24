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

        Assert.Contains("INCLUDE_GITHUB_RELEASE_MSIX", workflow, StringComparison.Ordinal);
        Assert.Contains("build_msix_package", workflow, StringComparison.Ordinal);
        Assert.Contains("include_msix_in_release", workflow, StringComparison.Ordinal);
        Assert.Contains("$packagingParameters = @{", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishDirectory = $env:GUI_PUBLISH_DIR", workflow, StringComparison.Ordinal);
        Assert.Contains("$packagingParameters.SkipMsix = $true", workflow, StringComparison.Ordinal);
        Assert.Contains("& ./scripts/package-gui-distribution.ps1 @packagingParameters", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($env:INCLUDE_MSIX_IN_RELEASE -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("if ('${{ steps.msix-policy.outputs.build_msix_package }}' -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("GH_REPO: ${{ github.repository }}", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release view $tag --repo $env:GH_REPO", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release upload $tag $assets --repo $env:GH_REPO --clobber", workflow, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}