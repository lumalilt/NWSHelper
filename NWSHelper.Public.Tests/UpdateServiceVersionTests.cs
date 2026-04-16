using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class UpdateServiceVersionTests
{
    [Theory]
    [InlineData("1.0.13+build.202603251723.26.sha.885baf87.885baf873bd577d16373e5e151641a12dfb6caec", "1.0.13")]
    [InlineData("1.0.13-20260325.235458.42+build.202603251723.26.sha.885baf87", "1.0.13-20260325.235458.42")]
    [InlineData("1.0.13", "1.0.13")]
    public void NormalizeForUpdateComparison_StripsOnlyBuildMetadata(string input, string expected)
    {
        Assert.Equal(expected, AppVersionProvider.NormalizeForUpdateComparison(input));
    }

    [Theory]
    [InlineData("https://github.com/dmealo/NWSHelper/releases/latest/download/appcast.xml", "https://github.com/lumalilt/NWSHelper/releases/latest/download/appcast.xml")]
    [InlineData("https://github.com/lumalilt/NWSHelper/releases/latest/download/appcast.xml", "https://github.com/lumalilt/NWSHelper/releases/latest/download/appcast.xml")]
    [InlineData("https://example.invalid/appcast.xml", "https://example.invalid/appcast.xml")]
    public void NormalizeAppcastUrl_RewritesOnlyLegacyPrivateGitHubFeed(string input, string expected)
    {
        Assert.Equal(expected, NetSparkleUpdateService.NormalizeAppcastUrl(input));
    }

    [Fact]
    public void NetSparkleUpdateService_OverridesInstalledVersionWithNormalizedValue()
    {
        const string rawCurrentVersion = "1.0.13+build.202603251723.26.sha.885baf87.885baf873bd577d16373e5e151641a12dfb6caec";
        var service = new NetSparkleUpdateService(currentVersionOverride: rawCurrentVersion);

        try
        {
            var createUpdater = typeof(NetSparkleUpdateService).GetMethod("GetOrCreateSparkleUpdater", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(createUpdater);

            var updater = createUpdater!.Invoke(service, new object[]
            {
                "https://example.invalid/appcast.xml",
                Convert.ToBase64String(new byte[32])
            });

            Assert.NotNull(updater);

            var configurationProperty = updater!.GetType().GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(configurationProperty);

            var configuration = configurationProperty!.GetValue(updater);
            Assert.NotNull(configuration);

            var installedVersionProperty = configuration!.GetType().GetProperty("InstalledVersion", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(installedVersionProperty);

            var installedVersion = installedVersionProperty!.GetValue(configuration) as string;
            Assert.Equal("1.0.13", installedVersion);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void ResolveAppcastUrl_RewritesLegacyPrivateFeedFromEnvironmentOverride()
    {
        using var appcastVariable = new EnvironmentVariableScope(
            "NWSHELPER_APPCAST_URL",
            "https://github.com/dmealo/NWSHelper/releases/latest/download/appcast.xml");

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"NWSHelperPublicUpdateTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, "gui-settings.json");

        try
        {
            using var service = new NetSparkleUpdateService(filePath: configPath, currentVersionOverride: "1.0.14+build.202603251822.30.sha.1150656a");
            var resolveAppcastUrl = typeof(NetSparkleUpdateService).GetMethod("ResolveAppcastUrl", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(resolveAppcastUrl);

            var resolved = resolveAppcastUrl!.Invoke(service, Array.Empty<object>()) as string;
            Assert.Equal("https://github.com/lumalilt/NWSHelper/releases/latest/download/appcast.xml", resolved);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void StoreRuntimeContextProvider_UsesEnvironmentOverrideForStoreUiTesting()
    {
        var provider = new StoreRuntimeContextProvider(
            environmentReader: name => name switch
            {
                "NWSHELPER_FORCE_STORE_CHANNEL" => "1",
                "NWSHELPER_STORE_PACKAGE_FAMILY_NAME" => "NWSHelper.Public.StoreUiTest",
                _ => null
            },
            processPathReader: () => @"D:\source\repos\dmealo\NWSHelper.Public\NWSHelper.Gui\bin\Debug\net10.0\NWSHelper.Gui.exe");

        var context = provider.GetCurrent();

        Assert.True(context.IsPackaged);
        Assert.True(context.IsStoreInstall);
        Assert.Equal(StoreProofAuthority.Heuristic, context.ProofAuthority);
        Assert.Equal("environment-override", context.DetectionSource);
        Assert.Equal("NWSHelper.Public.StoreUiTest", context.PackageFamilyName);
    }

    [Fact]
    public void StoreRuntimeContextProvider_UsesPersistedDeveloperSettingForStoreUiTesting()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"NWSHelperPublicStoreRuntimeTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, "gui-settings.json");

        try
        {
            var store = new GuiConfigurationStore(configPath);
            store.Save(new GuiConfigurationDocument
            {
                Setup = new GuiSetupSettings
                {
                    SimulateStoreInstallForUiTesting = true
                }
            });

            var provider = new StoreRuntimeContextProvider(
                processPathReader: () => @"D:\source\repos\dmealo\NWSHelper.Public\NWSHelper.Gui\bin\Debug\net10.0\NWSHelper.Gui.exe",
                filePath: configPath);

            var context = provider.GetCurrent();

            Assert.True(context.IsPackaged);
            Assert.True(context.IsStoreInstall);
            Assert.Equal(StoreProofAuthority.Heuristic, context.ProofAuthority);
            Assert.Equal("settings-override", context.DetectionSource);
            Assert.Equal(StoreRuntimeContextProvider.DefaultUiTestPackageFamilyName, context.PackageFamilyName);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildStatusMessage_ForStartupUpdateAvailable_PromptsManualReview()
    {
        var latest = new AppCastItem
        {
            Version = "1.0.16",
            ShortVersion = "1.0.16"
        };

        var message = NetSparkleUpdateService.BuildStatusMessage(
            UpdateStatus.UpdateAvailable,
            latest,
            fromStartupPolicy: true,
            interactiveUiAvailable: true);

        Assert.Equal("Update available: 1.0.16. Use Check for Updates to review and install.", message);
    }

    [Fact]
    public void BuildStatusMessage_ForManualUpdateWithInteractiveUi_ReflectsOpenedReview()
    {
        var latest = new AppCastItem
        {
            Version = "1.0.16",
            ShortVersion = "1.0.16"
        };

        var message = NetSparkleUpdateService.BuildStatusMessage(
            UpdateStatus.UpdateAvailable,
            latest,
            fromStartupPolicy: false,
            interactiveUiAvailable: true);

        Assert.Equal("Update review opened for 1.0.16. Follow the updater prompts to download and install.", message);
    }

    [Fact]
    public void BuildStatusMessage_ForManualUpdateWithoutInteractiveUi_DoesNotPromisePromptFlow()
    {
        var latest = new AppCastItem
        {
            Version = "1.0.16",
            ShortVersion = "1.0.16"
        };

        var message = NetSparkleUpdateService.BuildStatusMessage(
            UpdateStatus.UpdateAvailable,
            latest,
            fromStartupPolicy: false,
            interactiveUiAvailable: false);

        Assert.Equal("Update available: 1.0.16.", message);
    }

    [Theory]
    [InlineData(true, "#1F2937", "#F8FAFC")]
    [InlineData(false, "#F8FAFC", "#0F172A")]
    public void BuildReleaseNotesHtmlTemplate_UsesThemeColors(bool useDarkTheme, string expectedBackground, string expectedTextColor)
    {
        var template = NetSparkleUpdateService.BuildReleaseNotesHtmlTemplate(useDarkTheme);

        Assert.Contains(expectedBackground, template, StringComparison.Ordinal);
        Assert.Contains(expectedTextColor, template, StringComparison.Ordinal);
        Assert.Contains("{0} ({1})", template, StringComparison.Ordinal);
        Assert.Contains("{2}", template, StringComparison.Ordinal);
        Assert.Contains("{3}", template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "#1F2937", "#F8FAFC", "#93C5FD")]
    [InlineData(false, "#F8FAFC", "#0F172A", "#1D4ED8")]
    public void BuildReleaseNotesHeaderHtml_UsesThemeColors(bool useDarkTheme, string expectedBackground, string expectedTextColor, string expectedLinkColor)
    {
        var headerHtml = NetSparkleUpdateService.BuildReleaseNotesHeaderHtml(useDarkTheme);

        Assert.Contains(expectedBackground, headerHtml, StringComparison.Ordinal);
        Assert.Contains(expectedTextColor, headerHtml, StringComparison.Ordinal);
        Assert.Contains(expectedLinkColor, headerHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "#111827")]
    [InlineData(false, "#E2E8F0")]
    public void GetUpdateWindowBackgroundHex_ReturnsThemeSurface(bool useDarkTheme, string expected)
    {
        Assert.Equal(expected, NetSparkleUpdateService.GetUpdateWindowBackgroundHex(useDarkTheme));
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }
}