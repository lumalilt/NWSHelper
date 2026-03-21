using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NWSHelper.Tests;

public class ComputeVersionScriptTests
{
    [Fact]
    public void DirectoryBuildProps_DefinesVersionFallbacks()
    {
        var propsPath = Path.Combine(GetRepositoryRoot(), "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"Expected Directory.Build.props at {propsPath}");

        var document = XDocument.Load(propsPath);
        var propertyGroup = document.Root?.Element("PropertyGroup");
        Assert.NotNull(propertyGroup);

        AssertPropertyExistsWith(
            propertyGroup!,
            elementName: "VersionPrefixFromJson",
            expectedCondition: "'$(VersionPrefixFromJson)' == '' and '$(VersionMajor)' != '' and '$(VersionMinor)' != '' and '$(VersionPatch)' != ''",
            expectedValue: "$(VersionMajor).$(VersionMinor).$(VersionPatch)");

        AssertPropertyExistsWith(
            propertyGroup,
            elementName: "Version",
            expectedCondition: "'$(Version)' == '' and '$(VersionPrefixFromJson)' != ''",
            expectedValue: "$(VersionPrefixFromJson)");

        AssertPropertyExistsWith(
            propertyGroup,
            elementName: "FileVersion",
            expectedCondition: "'$(FileVersion)' == '' and '$(VersionPrefixFromJson)' != ''",
            expectedValue: "$(VersionPrefixFromJson).0");

        AssertPropertyExistsWith(
            propertyGroup,
            elementName: "Version",
            expectedCondition: "'$(Version)' == ''",
            expectedValue: "1.0.0");

        AssertPropertyExistsWith(
            propertyGroup,
            elementName: "FileVersion",
            expectedCondition: "'$(FileVersion)' == ''",
            expectedValue: "1.0.0.0");

        AssertProperty(
            propertyGroup,
            elementName: "InformationalVersion",
            expectedCondition: "'$(InformationalVersion)' == ''",
            expectedValue: "$(Version)");

        AssertProperty(
            propertyGroup,
            elementName: "AssemblyVersion",
            expectedCondition: "'$(AssemblyVersion)' == ''",
            expectedValue: "1.0.0.0");
    }

    [Fact]
    public void ComputeVersionScript_NonTagBuild_OutputsCiVersionAndMetadata()
    {
        var version = GetCurrentVersionInfo();
        var githubOutputPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var output = RunComputeVersionScript(
                tagBuild: false,
                environment: new Dictionary<string, string>
                {
                    ["GITHUB_REF"] = "refs/heads/main",
                    ["GITHUB_RUN_NUMBER"] = "42",
                    ["GITHUB_SHA"] = "0123456789abcdef0123456789abcdef01234567",
                    ["GITHUB_OUTPUT"] = githubOutputPath
                });

            Assert.Equal(version.Prefix, output["VersionPrefix"]);
            Assert.Equal("42", output["RunNumber"]);
            Assert.Equal("01234567", output["ShortSha"]);
            Assert.Equal("false", output["IsTagBuild"]);
            Assert.Matches($"^{Regex.Escape(version.Prefix)}-ci\\.\\d{{8}}\\.t\\d{{4}}\\.42$", output["Version"]);
            Assert.Matches($"^{version.Major}\\.{version.Minor}\\.\\d{{5}}\\.\\d+$", output["FileVersion"]);
            Assert.Matches($"^{Regex.Escape(version.Prefix)}\\+build\\.\\d{{12}}\\.42\\.sha\\.01234567$", output["InformationalVersion"]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", output["UtcNow"]);

            var githubOutput = ParseOutputLines(File.ReadAllLines(githubOutputPath));
            Assert.Equal(output["Version"], githubOutput["Version"]);
            Assert.Equal(output["FileVersion"], githubOutput["FileVersion"]);
            Assert.Equal(output["InformationalVersion"], githubOutput["InformationalVersion"]);
        }
        finally
        {
            if (File.Exists(githubOutputPath))
            {
                File.Delete(githubOutputPath);
            }
        }
    }

    [Fact]
    public void ComputeVersionScript_TagBuildSwitch_UsesStableVersion()
    {
        var version = GetCurrentVersionInfo();
        var output = RunComputeVersionScript(
            tagBuild: true,
            environment: new Dictionary<string, string>
            {
                ["GITHUB_REF"] = "refs/heads/main",
                ["GITHUB_RUN_NUMBER"] = "77",
                ["GITHUB_SHA"] = "89abcdef0123456789abcdef0123456789abcdef"
            });

        Assert.Equal("true", output["IsTagBuild"]);
            Assert.Equal(version.Prefix, output["Version"]);
    }

    [Fact]
    public void ComputeVersionScript_GitHubTagRef_DetectsTagBuild()
    {
            var version = GetCurrentVersionInfo();
        var output = RunComputeVersionScript(
            tagBuild: false,
            environment: new Dictionary<string, string>
            {
                    ["GITHUB_REF"] = $"refs/tags/v{version.Prefix}",
                ["GITHUB_RUN_NUMBER"] = "99",
                ["GITHUB_SHA"] = "fedcba9876543210fedcba9876543210fedcba98"
            });

        Assert.Equal("true", output["IsTagBuild"]);
            Assert.Equal(version.Prefix, output["Version"]);
    }

    private static Dictionary<string, string> RunComputeVersionScript(
        bool tagBuild,
        IDictionary<string, string> environment)
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "compute-version.ps1");
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
        if (tagBuild)
        {
            startInfo.ArgumentList.Add("-TagBuild");
        }

        foreach (var key in GetVersionEnvironmentKeys())
        {
            startInfo.Environment[key] = string.Empty;
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"compute-version.ps1 failed with exit code {process.ExitCode}{System.Environment.NewLine}STDOUT:{System.Environment.NewLine}{stdout}{System.Environment.NewLine}STDERR:{System.Environment.NewLine}{stderr}");

        var lines = stdout
            .Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        return ParseOutputLines(lines);
    }

    private static Dictionary<string, string> ParseOutputLines(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(System.StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf('=');
            Assert.True(separatorIndex > 0, $"Expected key=value output line, got: {line}");

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];
            values[key] = value;
        }

        foreach (var requiredKey in new[]
                 {
                     "Version",
                     "FileVersion",
                     "InformationalVersion",
                     "VersionPrefix",
                     "RunNumber",
                     "ShortSha",
                     "IsTagBuild",
                     "UtcNow"
                 })
        {
            Assert.True(values.ContainsKey(requiredKey), $"Missing output key '{requiredKey}'.");
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
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
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

    private static string[] GetVersionEnvironmentKeys()
    {
        return
        [
            "GITHUB_REF",
            "GITHUB_REF_TYPE",
            "GITHUB_RUN_NUMBER",
            "GITHUB_SHA",
            "GITHUB_OUTPUT",
            "BUILD_SOURCEBRANCH",
            "BUILD_SOURCEBRANCHNAME",
            "BUILD_BUILDID",
            "BUILD_BUILDNUMBER",
            "BUILD_SOURCEVERSION",
            "CI_COMMIT_TAG"
        ];
    }

    private static (int Major, int Minor, int Patch, string Prefix) GetCurrentVersionInfo()
    {
        var versionPath = Path.Combine(GetRepositoryRoot(), "version.json");
        Assert.True(File.Exists(versionPath), $"Expected version source file at {versionPath}");

        using var json = JsonDocument.Parse(File.ReadAllText(versionPath));
        var root = json.RootElement;
        var major = root.GetProperty("major").GetInt32();
        var minor = root.GetProperty("minor").GetInt32();
        var patch = root.GetProperty("patch").GetInt32();

        return (major, minor, patch, $"{major}.{minor}.{patch}");
    }

    private static void AssertProperty(
        XElement propertyGroup,
        string elementName,
        string expectedCondition,
        string expectedValue)
    {
        var element = propertyGroup.Element(elementName);
        Assert.NotNull(element);
        Assert.Equal(expectedCondition, (string?)element!.Attribute("Condition"));
        Assert.Equal(expectedValue, element.Value);
    }

    private static void AssertPropertyExistsWith(
        XElement propertyGroup,
        string elementName,
        string expectedCondition,
        string expectedValue)
    {
        var element = propertyGroup
            .Elements(elementName)
            .SingleOrDefault(item =>
                string.Equals((string?)item.Attribute("Condition"), expectedCondition, System.StringComparison.Ordinal) &&
                string.Equals(item.Value, expectedValue, System.StringComparison.Ordinal));

        Assert.NotNull(element);
    }
}