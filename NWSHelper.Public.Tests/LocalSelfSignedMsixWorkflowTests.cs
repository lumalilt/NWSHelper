using System;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class LocalSelfSignedMsixWorkflowTests
{
    [Fact]
    public void LocalSelfSignedMsixScript_DefinesRepeatablePackagingWorkflow()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "build-local-self-signed-msix.ps1");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("New-SelfSignedCertificate", script, StringComparison.Ordinal);
        Assert.Contains("Export-PfxCertificate", script, StringComparison.Ordinal);
        Assert.Contains("Export-Certificate", script, StringComparison.Ordinal);
        Assert.Contains("Import-Certificate", script, StringComparison.Ordinal);
        Assert.Contains("dotnet", script, StringComparison.Ordinal);
        Assert.Contains("package-msix.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage", script, StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process", script, StringComparison.Ordinal);
        Assert.Contains("SkipLaunchAfterInstall", script, StringComparison.Ordinal);
        Assert.Contains("RefreshShellIconCache", script, StringComparison.Ordinal);
        Assert.Contains("Refresh-ExplorerIconCache", script, StringComparison.Ordinal);
        Assert.Contains("Confirm-ExplorerIconCacheRefresh", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft\\Windows\\Explorer", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $explorerProcess.Id -Force", script, StringComparison.Ordinal);
        Assert.Contains("Your screen may flash", script, StringComparison.Ordinal);
        Assert.Contains("[System.Environment]::OSVersion.Platform", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsAdministrator", script, StringComparison.Ordinal);
        Assert.Contains("Install-LocalMsixPackage", script, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage failed after importing the signing certificate into CurrentUser\\TrustedPeople and CurrentUser\\Root", script, StringComparison.Ordinal);
        Assert.Contains("InstallUsedLocalMachineFallback", script, StringComparison.Ordinal);
        Assert.Contains("$timestamp = Get-Date", script, StringComparison.Ordinal);
        Assert.Contains("$dateStampedPatch = ($versionData.patch * 1000) + $timestamp.DayOfYear", script, StringComparison.Ordinal);
        Assert.Contains("$revision = [int]$timestamp.ToString('HHmm')", script, StringComparison.Ordinal);
        Assert.Contains("$($versionData.major).$($versionData.minor).$dateStampedPatch.$revision", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\TrustedPeople", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\Root", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\Root", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\TrustedPeople", script, StringComparison.Ordinal);
        Assert.Contains("Test-ImportedStorePathMatch", script, StringComparison.Ordinal);
        Assert.Contains("-ImportedStorePaths $importedStorePaths -Pattern '*TrustedPeople'", script, StringComparison.Ordinal);
        Assert.Contains("\"$isAdministrator\".ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.Contains("\"$launchedInstalledApp\".ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.Contains("(([string](($InstallPackage.IsPresent) -and (-not $SkipLaunchAfterInstall.IsPresent))).ToLowerInvariant())", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$isAdministrator.ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$launchedInstalledApp.ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$InstallPackage.IsPresent.ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("(($importedStorePaths | Where-Object", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$($versionData.patch).1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("package-gui-distribution.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("submit-partner-center-flight.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$IsWindows", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_DocumentsSupportedPublicSurface()
    {
        var readmePath = Path.Combine(GetRepositoryRoot(), "README.md");
        Assert.True(File.Exists(readmePath), $"Expected README at {readmePath}");

        var readme = File.ReadAllText(readmePath);

        Assert.Contains("# NWS Helper", readme, StringComparison.Ordinal);
        Assert.Contains("This repository is intended to contain the public GUI, CLI, packaging scripts, release automation, and public-facing documentation.", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationId", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("FlightId", readme, StringComparison.Ordinal);

        if (readme.Contains("build-local-self-signed-msix.ps1", StringComparison.Ordinal))
        {
            Assert.Contains("self-signed", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ImportCertificateToTrustedPeople", readme, StringComparison.Ordinal);
            Assert.Contains("InstallPackage", readme, StringComparison.Ordinal);
            Assert.Contains("SkipLaunchAfterInstall", readme, StringComparison.Ordinal);
            Assert.Contains("RefreshShellIconCache", readme, StringComparison.Ordinal);
            Assert.Contains("launch the installed app", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CurrentUser\\Root", readme, StringComparison.Ordinal);
            Assert.Contains("Administrator", readme, StringComparison.Ordinal);
            Assert.Contains("current user stores", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("re-run the helper as Administrator", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MSIX-safe timestamped local version", readme, StringComparison.Ordinal);
            Assert.Contains("day-of-year", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HHmm", readme, StringComparison.Ordinal);
            Assert.Contains("restarting Windows Explorer", readme, StringComparison.Ordinal);
            Assert.Contains("resources.pri", readme, StringComparison.Ordinal);
            Assert.Contains("unplated AppList icon qualifiers", readme, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revision `.1`", readme, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("## Features", readme, StringComparison.Ordinal);
        Assert.Contains("## Repo", readme, StringComparison.Ordinal);
        Assert.Contains("## Development", readme, StringComparison.Ordinal);
        Assert.Contains("[SUPPORT.md](./SUPPORT.md)", readme, StringComparison.Ordinal);
        Assert.Contains("[SECURITY.md](./SECURITY.md)", readme, StringComparison.Ordinal);
        Assert.Contains("[docs/development.md](./docs/development.md)", readme, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}