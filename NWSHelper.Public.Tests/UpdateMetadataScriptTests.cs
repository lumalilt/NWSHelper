using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace NWSHelper.Tests;

public class UpdateMetadataScriptTests
{
    [Fact]
    public void GenerateUpdateMetadataScript_CreatesExpectedMetadata()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "NWSHelperUpdateMetadata", Guid.NewGuid().ToString("N"));
        var guiPath = Path.Combine(outputRoot, "gui", "NWSHelper.Gui.exe");
        var cliPath = Path.Combine(outputRoot, "cli", "NWSHelper.Cli.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(guiPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);

        File.WriteAllText(guiPath, "gui-payload");
        File.WriteAllText(cliPath, "cli-payload");

        const string version = "1.2.3";
        const string generatedAtUtc = "2026-03-01T12:34:56Z";
        const string baseDownloadUrl = "https://example.invalid/releases/v1.2.3";

        try
        {
            var output = RunPowerShellScript(
                scriptName: "generate-update-metadata.ps1",
                arguments:
                [
                    "-ArtifactsPath", outputRoot,
                    "-Version", version,
                    "-OutputFile", "update/update-metadata.json",
                    "-GeneratedAtUtc", generatedAtUtc,
                    "-BaseDownloadUrl", baseDownloadUrl
                ]);

            Assert.Equal(version, output["Version"]);
            Assert.Equal(generatedAtUtc, output["GeneratedAtUtc"]);
            Assert.Equal("2", output["FileCount"]);

            var metadataPath = output["MetadataFile"];
            Assert.True(File.Exists(metadataPath), $"Expected metadata file at {metadataPath}");

            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;

            Assert.Equal(version, root.GetProperty("version").GetString());
            Assert.Equal(generatedAtUtc, root.GetProperty("generatedAtUtc").GetString());
            Assert.Equal("SHA256", root.GetProperty("checksumAlgorithm").GetString());
            Assert.Equal(2, root.GetProperty("fileCount").GetInt32());
            Assert.Equal(baseDownloadUrl, root.GetProperty("downloadBaseUrl").GetString());

            var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.Equal(2, artifacts.Length);

            Assert.Equal("cli/NWSHelper.Cli.exe", artifacts[0].GetProperty("path").GetString());
            Assert.Equal(new FileInfo(cliPath).Length, artifacts[0].GetProperty("sizeBytes").GetInt64());
            Assert.Equal(ComputeSha256Lower(cliPath), artifacts[0].GetProperty("sha256").GetString());
            Assert.Equal(
                $"{baseDownloadUrl}/cli/NWSHelper.Cli.exe",
                artifacts[0].GetProperty("downloadUrl").GetString());

            Assert.Equal("gui/NWSHelper.Gui.exe", artifacts[1].GetProperty("path").GetString());
            Assert.Equal(new FileInfo(guiPath).Length, artifacts[1].GetProperty("sizeBytes").GetInt64());
            Assert.Equal(ComputeSha256Lower(guiPath), artifacts[1].GetProperty("sha256").GetString());
            Assert.Equal(
                $"{baseDownloadUrl}/gui/NWSHelper.Gui.exe",
                artifacts[1].GetProperty("downloadUrl").GetString());
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
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

    private static string ComputeSha256Lower(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
