using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace NWSHelper.Tests;

public class PackageGuiDistributionScriptTests
{
    [Fact]
    public void PackageGuiDistributionScript_ValidateOnly_ValidatesBothPackagersAndGeneratesChecksums()
    {
        var root = Path.Combine(Path.GetTempPath(), "NWSHelperGuiPackagingScript", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(root, "publish");
        var installerDirectory = Path.Combine(root, "installer");
        var msixDirectory = Path.Combine(root, "msix");

        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "NWSHelper.Gui.exe"), "gui-exe");
        File.WriteAllText(Path.Combine(publishDirectory, "dependency.dll"), "dep");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "package-gui-distribution.ps1",
                arguments:
                [
                    "-PublishDirectory", publishDirectory,
                    "-Version", "1.2.3",
                    "-InstallerOutputDirectory", installerDirectory,
                    "-MsixOutputDirectory", msixDirectory,
                    "-ChecksumArtifactsPath", publishDirectory,
                    "-ValidateOnly"
                ]);

            Assert.Equal("ValidateOnly", output["Mode"]);
            Assert.Equal("ValidateOnly", output["InstallerMode"]);
            Assert.Equal("ValidateOnly", output["MsixMode"]);
            Assert.Equal("NWSHelper-Setup-1.2.3.exe", output["ExpectedInstallerName"]);
            Assert.True(File.Exists(output["SetupIconPath"]), $"Expected setup icon at {output["SetupIconPath"]}");
            Assert.EndsWith(Path.Combine("NWSHelper.Gui", "Assets", "nwsh_multi.ico"), output["SetupIconPath"], StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("msix", "NWSHelper-1.2.3.0.msix"), output["ExpectedPackagePath"], StringComparison.OrdinalIgnoreCase);

            var checksumFile = output["ChecksumFile"];
            Assert.True(File.Exists(checksumFile), $"Expected checksum file at {checksumFile}");
            Assert.Equal("2", output["ChecksumFileCount"]);
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
    public void PackageGuiDistributionScript_PackageMode_CreatesInstallerMsixAndChecksums()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "NWSHelperGuiPackagingScript", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(root, "publish");
        var installerDirectory = Path.Combine(root, "release", "installer");
        var msixDirectory = Path.Combine(root, "release", "msix");
        var fakeIsccPath = Path.Combine(root, "fake-iscc.cmd");
        var fakeIsccInnerPath = Path.Combine(root, "fake-iscc-inner.ps1");
        var isccArgumentsPath = Path.Combine(root, "iscc-args.txt");
        var fakeMakeAppxPath = Path.Combine(root, "fake-makeappx.cmd");

        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "NWSHelper.Gui.exe"), "gui-exe");
        File.WriteAllText(Path.Combine(publishDirectory, "dependency.dll"), "dep");

        File.WriteAllText(
            fakeIsccInnerPath,
            "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)\r\n" +
            "$Arguments | Set-Content -LiteralPath $env:ISCC_ARGS_PATH\r\n" +
            "$appVersion = $null\r\n" +
            "$outputDir = $null\r\n" +
            "foreach ($argument in $Arguments) {\r\n" +
            "    if ($argument.StartsWith('/DAppVersion=')) { $appVersion = $argument.Substring('/DAppVersion='.Length) }\r\n" +
            "    if ($argument.StartsWith('/DOutputDir=')) { $outputDir = $argument.Substring('/DOutputDir='.Length) }\r\n" +
            "}\r\n" +
            "if ([string]::IsNullOrWhiteSpace($appVersion) -or [string]::IsNullOrWhiteSpace($outputDir)) { exit 1 }\r\n" +
            "New-Item -ItemType Directory -Path $outputDir -Force | Out-Null\r\n" +
            "Set-Content -LiteralPath (Join-Path $outputDir \"NWSHelper-Setup-$appVersion.exe\") -Value 'fake-installer'\r\n");

        File.WriteAllText(
            fakeIsccPath,
            "@echo off\r\n" +
            "set ISCC_ARGS_PATH=%~dp0iscc-args.txt\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-iscc-inner.ps1\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n");

        File.WriteAllText(
            fakeMakeAppxPath,
            "@echo off\r\n" +
            "setlocal\r\n" +
            "set \"package=\"\r\n" +
            ":loop\r\n" +
            "if \"%~1\"==\"\" goto done\r\n" +
            "if /I \"%~1\"==\"/p\" (\r\n" +
            "  set \"package=%~2\"\r\n" +
            "  shift\r\n" +
            ")\r\n" +
            "shift\r\n" +
            "goto loop\r\n" +
            ":done\r\n" +
            "if \"%package%\"==\"\" exit /b 1\r\n" +
            ">\"%package%\" echo fake-msix\r\n" +
            "exit /b 0\r\n");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "package-gui-distribution.ps1",
                arguments:
                [
                    "-PublishDirectory", publishDirectory,
                    "-Version", "1.2.3",
                    "-InstallerOutputDirectory", installerDirectory,
                    "-MsixOutputDirectory", msixDirectory,
                    "-ChecksumArtifactsPath", Path.Combine(root, "release"),
                    "-IsccPath", fakeIsccPath,
                    "-MakeAppxPath", fakeMakeAppxPath
                ]);

            Assert.Equal("Package", output["Mode"]);

            var installerPath = output["InstallerPath"];
            var msixPath = output["PackagePath"];
            var checksumFile = output["ChecksumFile"];

            Assert.True(File.Exists(installerPath), $"Expected installer at {installerPath}");
            Assert.True(File.Exists(msixPath), $"Expected msix at {msixPath}");
            Assert.True(File.Exists(checksumFile), $"Expected checksum file at {checksumFile}");
            Assert.True(File.Exists(isccArgumentsPath), $"Expected ISCC args capture at {isccArgumentsPath}");

            var isccArguments = File.ReadAllText(isccArgumentsPath);
            Assert.Contains("/DSetupIconFile=", isccArguments, StringComparison.Ordinal);
            Assert.Contains("nwsh_multi.ico", isccArguments, StringComparison.OrdinalIgnoreCase);

            var checksumContents = File.ReadAllText(checksumFile);
            Assert.Contains("installer/NWSHelper-Setup-1.2.3.exe", checksumContents, StringComparison.Ordinal);
            Assert.Contains("msix/NWSHelper-1.2.3.0.msix", checksumContents, StringComparison.Ordinal);
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
    public void PackageGuiDistributionScript_PackageMode_EscapesManifestValuesForMsix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "NWSHelperGuiPackagingScript", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(root, "publish");
        var installerDirectory = Path.Combine(root, "release", "installer");
        var msixDirectory = Path.Combine(root, "release", "msix");
        var fakeIsccPath = Path.Combine(root, "fake-iscc.cmd");
        var fakeIsccInnerPath = Path.Combine(root, "fake-iscc-inner.ps1");
        var fakeMakeAppxPath = Path.Combine(root, "fake-makeappx.cmd");
        var fakeMakeAppxInnerPath = Path.Combine(root, "fake-makeappx-inner.ps1");
        var manifestCapturePath = Path.Combine(root, "captured-AppxManifest.xml");

        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "NWSHelper.Gui.exe"), "gui-exe");
        File.WriteAllText(Path.Combine(publishDirectory, "dependency.dll"), "dep");

        File.WriteAllText(
            fakeIsccInnerPath,
            "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)\r\n" +
            "$appVersion = $null\r\n" +
            "$outputDir = $null\r\n" +
            "foreach ($argument in $Arguments) {\r\n" +
            "    if ($argument.StartsWith('/DAppVersion=')) { $appVersion = $argument.Substring('/DAppVersion='.Length) }\r\n" +
            "    if ($argument.StartsWith('/DOutputDir=')) { $outputDir = $argument.Substring('/DOutputDir='.Length) }\r\n" +
            "}\r\n" +
            "if ([string]::IsNullOrWhiteSpace($appVersion) -or [string]::IsNullOrWhiteSpace($outputDir)) { exit 1 }\r\n" +
            "New-Item -ItemType Directory -Path $outputDir -Force | Out-Null\r\n" +
            "Set-Content -LiteralPath (Join-Path $outputDir \"NWSHelper-Setup-$appVersion.exe\") -Value 'fake-installer'\r\n");

        File.WriteAllText(
            fakeMakeAppxInnerPath,
            "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)\r\n" +
            "$package = $null\r\n" +
            "$staging = $null\r\n" +
            "for ($i = 0; $i -lt $Arguments.Length; $i++) {\r\n" +
            "    if ($Arguments[$i] -eq '/p' -and $i + 1 -lt $Arguments.Length) { $package = $Arguments[$i + 1] }\r\n" +
            "    if ($Arguments[$i] -eq '/d' -and $i + 1 -lt $Arguments.Length) { $staging = $Arguments[$i + 1] }\r\n" +
            "}\r\n" +
            "if ([string]::IsNullOrWhiteSpace($package) -or [string]::IsNullOrWhiteSpace($staging)) { exit 1 }\r\n" +
            "Copy-Item -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Destination (Join-Path $PSScriptRoot 'captured-AppxManifest.xml') -Force\r\n" +
            "Set-Content -LiteralPath $package -Value 'fake-msix'\r\n");

        File.WriteAllText(
            fakeIsccPath,
            "@echo off\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-iscc-inner.ps1\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n");

        File.WriteAllText(
            fakeMakeAppxPath,
            "@echo off\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-makeappx-inner.ps1\" %*\r\n" +
            "exit /b 0\r\n");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "package-gui-distribution.ps1",
                arguments:
                [
                    "-PublishDirectory", publishDirectory,
                    "-Version", "1.2.3",
                    "-InstallerOutputDirectory", installerDirectory,
                    "-MsixOutputDirectory", msixDirectory,
                    "-ChecksumArtifactsPath", Path.Combine(root, "release"),
                    "-IsccPath", fakeIsccPath,
                    "-MakeAppxPath", fakeMakeAppxPath,
                    "-PackageDisplayName", "NWS Helper & Tools",
                    "-PackageDescription", "Addresses & more!"
                ]);

            Assert.Equal("Package", output["Mode"]);
            Assert.True(File.Exists(manifestCapturePath), $"Expected captured manifest at {manifestCapturePath}");

            var manifest = File.ReadAllText(manifestCapturePath);
            Assert.Contains("<DisplayName>NWS Helper &amp; Tools</DisplayName>", manifest, StringComparison.Ordinal);
            Assert.Contains("<Description>Addresses &amp; more!</Description>", manifest, StringComparison.Ordinal);
            Assert.Contains("DisplayName=\"NWS Helper &amp; Tools\"", manifest, StringComparison.Ordinal);
            Assert.Contains("Description=\"Addresses &amp; more!\"", manifest, StringComparison.Ordinal);
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
    public void PackageGuiDistributionScript_PackageMode_PassesThroughMsixSigningOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "NWSHelperGuiPackagingScript", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(root, "publish");
        var installerDirectory = Path.Combine(root, "release", "installer");
        var msixDirectory = Path.Combine(root, "release", "msix");
        var fakeIsccPath = Path.Combine(root, "fake-iscc.cmd");
        var fakeIsccInnerPath = Path.Combine(root, "fake-iscc-inner.ps1");
        var fakeMakeAppxPath = Path.Combine(root, "fake-makeappx.cmd");
        var fakeSignToolPath = Path.Combine(root, "fake-signtool.cmd");
        var fakeSignToolInnerPath = Path.Combine(root, "fake-signtool-inner.ps1");
        var signArgumentsPath = Path.Combine(root, "signtool-args.txt");
        var certificatePath = Path.Combine(root, "signing-cert.pfx");

        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "NWSHelper.Gui.exe"), "gui-exe");
        File.WriteAllText(Path.Combine(publishDirectory, "dependency.dll"), "dep");
        File.WriteAllText(certificatePath, "fake-cert");

        File.WriteAllText(
            fakeIsccInnerPath,
            "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)\r\n" +
            "$appVersion = $null\r\n" +
            "$outputDir = $null\r\n" +
            "foreach ($argument in $Arguments) {\r\n" +
            "    if ($argument.StartsWith('/DAppVersion=')) { $appVersion = $argument.Substring('/DAppVersion='.Length) }\r\n" +
            "    if ($argument.StartsWith('/DOutputDir=')) { $outputDir = $argument.Substring('/DOutputDir='.Length) }\r\n" +
            "}\r\n" +
            "if ([string]::IsNullOrWhiteSpace($appVersion) -or [string]::IsNullOrWhiteSpace($outputDir)) { exit 1 }\r\n" +
            "New-Item -ItemType Directory -Path $outputDir -Force | Out-Null\r\n" +
            "Set-Content -LiteralPath (Join-Path $outputDir \"NWSHelper-Setup-$appVersion.exe\") -Value 'fake-installer'\r\n");

        File.WriteAllText(
            fakeIsccPath,
            "@echo off\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-iscc-inner.ps1\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n");

        File.WriteAllText(
            fakeMakeAppxPath,
            "@echo off\r\n" +
            "setlocal\r\n" +
            "set \"package=\"\r\n" +
            ":loop\r\n" +
            "if \"%~1\"==\"\" goto done\r\n" +
            "if /I \"%~1\"==\"/p\" (\r\n" +
            "  set \"package=%~2\"\r\n" +
            "  shift\r\n" +
            ")\r\n" +
            "shift\r\n" +
            "goto loop\r\n" +
            ":done\r\n" +
            "if \"%package%\"==\"\" exit /b 1\r\n" +
            ">\"%package%\" echo fake-msix\r\n" +
            "exit /b 0\r\n");

        File.WriteAllText(
            fakeSignToolInnerPath,
            "$args | Set-Content -LiteralPath $env:SIGN_ARGS_PATH\r\n" +
            "exit 0\r\n");

        File.WriteAllText(
            fakeSignToolPath,
            "@echo off\r\n" +
            "set SIGN_ARGS_PATH=%~dp0signtool-args.txt\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-signtool-inner.ps1\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "package-gui-distribution.ps1",
                arguments:
                [
                    "-PublishDirectory", publishDirectory,
                    "-Version", "1.2.3",
                    "-InstallerOutputDirectory", installerDirectory,
                    "-MsixOutputDirectory", msixDirectory,
                    "-ChecksumArtifactsPath", Path.Combine(root, "release"),
                    "-IsccPath", fakeIsccPath,
                    "-MakeAppxPath", fakeMakeAppxPath,
                    "-SignMsix",
                    "-SignToolPath", fakeSignToolPath,
                    "-CertificatePath", certificatePath,
                    "-CertificatePassword", "test-password",
                    "-TimestampServerUrl", "https://timestamp.example.invalid"
                ]);

            Assert.Equal("true", output["PackageSigned"]);
            Assert.True(File.Exists(signArgumentsPath), $"Expected signtool arguments file at {signArgumentsPath}");
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
    public void PackageGuiDistributionScript_PackageMode_SkipMsixSkipsMsixPackaging()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "NWSHelperGuiPackagingScript", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(root, "publish");
        var installerDirectory = Path.Combine(root, "release", "installer");
        var msixDirectory = Path.Combine(root, "release", "msix");
        var fakeIsccPath = Path.Combine(root, "fake-iscc.cmd");
        var fakeIsccInnerPath = Path.Combine(root, "fake-iscc-inner.ps1");

        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "NWSHelper.Gui.exe"), "gui-exe");
        File.WriteAllText(Path.Combine(publishDirectory, "dependency.dll"), "dep");

        File.WriteAllText(
            fakeIsccInnerPath,
            "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)\r\n" +
            "$appVersion = $null\r\n" +
            "$outputDir = $null\r\n" +
            "foreach ($argument in $Arguments) {\r\n" +
            "    if ($argument.StartsWith('/DAppVersion=')) { $appVersion = $argument.Substring('/DAppVersion='.Length) }\r\n" +
            "    if ($argument.StartsWith('/DOutputDir=')) { $outputDir = $argument.Substring('/DOutputDir='.Length) }\r\n" +
            "}\r\n" +
            "if ([string]::IsNullOrWhiteSpace($appVersion) -or [string]::IsNullOrWhiteSpace($outputDir)) { exit 1 }\r\n" +
            "New-Item -ItemType Directory -Path $outputDir -Force | Out-Null\r\n" +
            "Set-Content -LiteralPath (Join-Path $outputDir \"NWSHelper-Setup-$appVersion.exe\") -Value 'fake-installer'\r\n");

        File.WriteAllText(
            fakeIsccPath,
            "@echo off\r\n" +
            "pwsh -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-iscc-inner.ps1\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "package-gui-distribution.ps1",
                arguments:
                [
                    "-PublishDirectory", publishDirectory,
                    "-Version", "1.2.3",
                    "-InstallerOutputDirectory", installerDirectory,
                    "-MsixOutputDirectory", msixDirectory,
                    "-ChecksumArtifactsPath", Path.Combine(root, "release"),
                    "-IsccPath", fakeIsccPath,
                    "-SkipMsix"
                ]);

            Assert.Equal("Package", output["Mode"]);
            Assert.Equal("Skipped", output["MsixMode"]);
            Assert.True(File.Exists(output["InstallerPath"]), $"Expected installer at {output["InstallerPath"]}");
            Assert.False(Directory.Exists(msixDirectory) && Directory.EnumerateFiles(msixDirectory, "*.msix").Any(), "Did not expect an MSIX when SkipMsix is used.");

            var checksumContents = File.ReadAllText(output["ChecksumFile"]);
            Assert.Contains("installer/NWSHelper-Setup-1.2.3.exe", checksumContents, StringComparison.Ordinal);
            Assert.DoesNotContain("msix/", checksumContents, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> RunPowerShellScript(string scriptName, IReadOnlyList<string> arguments)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", scriptName);
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
            $"{scriptName} failed with exit code {process.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");

        return ParseOutputLines(stdout);
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