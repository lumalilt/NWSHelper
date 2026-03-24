using System;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class PublicCoreCompatibilityWorkflowTests
{
    [Fact]
    public void PublicCoreCompatibilityWorkflow_ValidatesPinnedCoreVersionAndCanPushUpdates()
    {
        var workflowPath = Path.Combine(GetRepositoryRoot(), ".github", "workflows", "public-core-compatibility.yml");
        Assert.True(File.Exists(workflowPath), $"Expected workflow at {workflowPath}");

        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("name: public-core-compatibility", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("core_package_version:", workflow, StringComparison.Ordinal);
        Assert.Contains("push_pin_update:", workflow, StringComparison.Ordinal);
        Assert.Contains("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("- main", workflow, StringComparison.Ordinal);
        Assert.Contains("corePackageVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("only allows patch updates in major.minor band", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build .\\NWSHelper.Public.slnx --configuration Release --nologo", workflow, StringComparison.Ordinal);
        Assert.Contains("/p:CorePackageVersion=$env:EFFECTIVE_CORE_PACKAGE_VERSION", workflow, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_REPO_AUTOMATION_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("git push $remoteUrl HEAD:main", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicProjects_RequireExplicitPinnedCorePackageVersion()
    {
        foreach (var projectPath in new[]
                 {
                     Path.Combine(GetRepositoryRoot(), "NWSHelper.Cli", "NWSHelper.Cli.csproj"),
                     Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "NWSHelper.Gui.csproj")
                 })
        {
            Assert.True(File.Exists(projectPath), $"Expected project at {projectPath}");

            var project = File.ReadAllText(projectPath);

            Assert.Contains("<PackageReference Include=\"NWSHelper.Core\" Version=\"$(CorePackageVersion)\" />", project, StringComparison.Ordinal);
            Assert.Contains("Core package version is required. Set corePackageVersion in version.json or pass /p:CorePackageVersion=...", project, StringComparison.Ordinal);
            Assert.DoesNotContain("<CorePackageVersion Condition=\"'$(CorePackageVersion)' == ''\">$(Version)</CorePackageVersion>", project, StringComparison.Ordinal);
        }
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}