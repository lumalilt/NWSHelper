using System;
using System.IO;
using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class GuidanceWorkflowTests
{
    [Fact]
    public void PublicGuiSetupSettings_DefaultGuidanceFlagsToExpanded()
    {
        var settings = new GuiSetupSettings();

        Assert.True(settings.IsSetupGuidanceExpanded);
        Assert.True(settings.IsResultsGuidanceExpanded);
    }

    [Fact]
    public void PublicGuiSetupSettings_RoundTripsGuidanceFlags()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"NWSHelperPublicGuidanceSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var unifiedPath = Path.Combine(tempDirectory, "gui-settings.json");

        try
        {
            var setupService = new SetupSettingsService(unifiedPath);
            setupService.Save(new GuiSetupSettings
            {
                BoundaryCsvPath = "boundary.csv",
                IsSetupGuidanceExpanded = false,
                IsResultsGuidanceExpanded = false
            });

            var loaded = setupService.Load();

            Assert.NotNull(loaded);
            Assert.Equal("boundary.csv", loaded!.BoundaryCsvPath);
            Assert.False(loaded.IsSetupGuidanceExpanded);
            Assert.False(loaded.IsResultsGuidanceExpanded);
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
    public void PublicGuiMarkup_ContainsSetupAndResultsGuidance()
    {
        var repositoryRoot = GetRepositoryRoot();
        var setupMarkup = File.ReadAllText(Path.Combine(repositoryRoot, "NWSHelper.Gui", "Views", "Stages", "SetupStageView.axaml"));
        var resultsMarkup = File.ReadAllText(Path.Combine(repositoryRoot, "NWSHelper.Gui", "Views", "Stages", "ResultsStageView.axaml"));

        Assert.Contains("IsExpanded=\"{Binding IsSetupGuidanceExpanded, Mode=TwoWay}\"", setupMarkup, StringComparison.Ordinal);
        Assert.Contains("How this fits your territory workflow", setupMarkup, StringComparison.Ordinal);
        Assert.Contains("Recommended: use the exported Territory Addresses file so existing statuses and notes can be preserved.", setupMarkup, StringComparison.Ordinal);

        Assert.Contains("IsExpanded=\"{Binding IsResultsGuidanceExpanded, Mode=TwoWay}\"", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("Next step: import back into your territory app", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("Use the consolidated file for one full import, or per-territory/category files when needed.", resultsMarkup, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}