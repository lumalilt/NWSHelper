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
        Assert.Contains("$arguments += '-SkipMsix'", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($env:INCLUDE_MSIX_IN_RELEASE -eq 'true')", workflow, StringComparison.Ordinal);
        Assert.Contains("if ('${{ steps.msix-policy.outputs.build_msix_package }}' -eq 'true')", workflow, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}