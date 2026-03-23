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

    private static Dictionary<string, string> RunPowerShellScript(IReadOnlyList<string> scriptPathSegments, IReadOnlyList<string> arguments)
    {
        var scriptPath = Path.Combine([GetRepositoryRoot(), .. scriptPathSegments]);
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