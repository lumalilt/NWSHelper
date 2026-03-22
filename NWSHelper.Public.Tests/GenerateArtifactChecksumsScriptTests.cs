using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace NWSHelper.Tests;

public class GenerateArtifactChecksumsScriptTests
{
    [Fact]
    public void GenerateArtifactChecksumsScript_CreatesExpectedChecksums()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "NWSHelperChecksums", Guid.NewGuid().ToString("N"));
        var nestedDirectory = Path.Combine(artifactsRoot, "nested");
        Directory.CreateDirectory(nestedDirectory);

        var topLevelFile = Path.Combine(artifactsRoot, "top.txt");
        var nestedFile = Path.Combine(nestedDirectory, "child.bin");
        File.WriteAllText(topLevelFile, "top-level-payload");
        File.WriteAllText(nestedFile, "nested-payload");

        try
        {
            var output = RunPowerShellScript(
                scriptName: "generate-artifact-checksums.ps1",
                arguments:
                [
                    "-ArtifactsPath", artifactsRoot,
                    "-Algorithm", "SHA256",
                    "-OutputFile", "out/checksums.sha256"
                ]);

            Assert.Equal("SHA256", output["Algorithm"]);
            Assert.Equal("2", output["FileCount"]);

            var checksumFile = output["ChecksumFile"];
            Assert.True(File.Exists(checksumFile), $"Expected checksum file at {checksumFile}");

            var lines = File.ReadAllLines(checksumFile);
            Assert.Equal(2, lines.Length);
            Assert.Contains($"{ComputeSha256Lower(nestedFile)} *nested/child.bin", lines, StringComparer.Ordinal);
            Assert.Contains($"{ComputeSha256Lower(topLevelFile)} *top.txt", lines, StringComparer.Ordinal);
        }
        finally
        {
            if (Directory.Exists(artifactsRoot))
            {
                Directory.Delete(artifactsRoot, recursive: true);
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