using System;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class PackageMsixScriptTests
{
    [Fact]
    public void PackageMsixScript_NormalizesSdkToolCandidatesToArrays()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "package-msix.ps1");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("$candidates = @(\r\n            Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'makeappx.exe'", script, StringComparison.Ordinal);
        Assert.Contains("$candidates = @(\r\n            Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'signtool.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageMsixScript_GeneratesResourcesPriBeforePacking()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "package-msix.ps1");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("[string]$MakePriPath", script, StringComparison.Ordinal);
        Assert.Contains("function Resolve-MakePriPath", script, StringComparison.Ordinal);
        Assert.Contains("$priConfigPath = Join-Path $stagingDirectory 'priconfig.xml'", script, StringComparison.Ordinal);
        Assert.Contains("$resourcesPriPath = Join-Path $stagingDirectory 'resources.pri'", script, StringComparison.Ordinal);
        Assert.Contains("& $resolvedMakePriPath createconfig /cf $priConfigPath /dq 'lang-en-US' /pv 10.0.0 /o", script, StringComparison.Ordinal);
        Assert.Contains("& $resolvedMakePriPath new /pr $stagingDirectory /cf $priConfigPath /mn $manifestOutputPath /of $resourcesPriPath /o", script, StringComparison.Ordinal);
        Assert.Contains("throw \"MakePri createconfig failed with exit code $LASTEXITCODE.\"", script, StringComparison.Ordinal);
        Assert.Contains("MakePri new failed with exit code", script, StringComparison.Ordinal);

        var makePriIndex = script.IndexOf("& $resolvedMakePriPath new /pr $stagingDirectory /cf $priConfigPath /mn $manifestOutputPath /of $resourcesPriPath /o", StringComparison.Ordinal);
        var makeAppxIndex = script.IndexOf("& $resolvedMakeAppxPath pack /d $stagingDirectory /p $packagePath /o", StringComparison.Ordinal);

        Assert.True(makePriIndex >= 0, "Expected MakePri new invocation.");
        Assert.True(makeAppxIndex >= 0, "Expected makeappx pack invocation.");
        Assert.True(makePriIndex < makeAppxIndex, "resources.pri must be generated before makeappx packs the staging directory.");
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}