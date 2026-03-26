using System;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class BrandingAssetTests
{
    [Fact]
    public void GuiProject_EmbedsWindowsApplicationIcon()
    {
        var projectPath = Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "NWSHelper.Gui.csproj");
        Assert.True(File.Exists(projectPath), $"Expected project at {projectPath}");

        var project = File.ReadAllText(projectPath);

        Assert.Contains("<ApplicationIcon>Assets\\nwsh_multi.ico</ApplicationIcon>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScript_UsesExplicitSetupIconDefine()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "inno", "NWSHelper.iss");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("#ifndef SetupIconFile", script, StringComparison.Ordinal);
        Assert.Contains("SetupIconFile={#SetupIconFile}", script, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}