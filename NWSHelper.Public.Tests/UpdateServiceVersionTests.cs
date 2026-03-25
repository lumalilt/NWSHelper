using System;
using System.Reflection;
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
}