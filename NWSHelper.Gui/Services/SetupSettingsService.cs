using System;

namespace NWSHelper.Gui.Services;

public sealed class GuiSetupSettings
{
    public string SelectedDatasetProviderId { get; set; } = "openaddresses";

    public string SelectedDatasetSourcesCsv { get; set; } = string.Empty;

    public string OpenAddressesApiBaseUrl { get; set; } = "https://batch.openaddresses.io/api";

    public string OpenAddressesApiToken { get; set; } = string.Empty;

    public bool UseEmbeddedOpenAddressesOnboardingWebView { get; set; } = true;

    public bool EnableOpenAddressesAdvancedDiagnostics { get; set; }

    public string OpenAddressesApiOnboardingType { get; set; } = ApiOnboardingTypeLabels.Automated;

    public string LastOpenAddressesOnboardingSuccessSummary { get; set; } = string.Empty;

    public string BoundaryCsvPath { get; set; } = string.Empty;

    public string ExistingAddressesCsvPath { get; set; } = string.Empty;

    public string DatasetRootPath { get; set; } = "./datasets";

    public string StatesFilter { get; set; } = "MD";

    public string ConsolidatedOutputPath { get; set; } = string.Empty;

    public int WarningThreshold { get; set; } = 350;

    public bool WhatIf { get; set; }

    public bool ListThresholdExceeding { get; set; }

    public bool OutputDespiteThreshold { get; set; }

    public bool PerTerritoryOutput { get; set; } = true;

    public string PerTerritoryDirectory { get; set; } = string.Empty;

    public int OutputSplitRows { get; set; }

    public bool GroupByCategory { get; set; }

    public bool NoPrompt { get; set; }

    public bool SmartSelect { get; set; }

    public bool SelectAll { get; set; } = true;

    public bool OutputExistingNoneNew { get; set; }

    public bool NoneNewInConsolidated { get; set; }

    public bool OutputAllRows { get; set; }

    public bool ExcludeNormalizedRows { get; set; }

    public bool OverwriteExistingLatLong { get; set; }

    public bool OnlyMatchSingleState { get; set; }

    public bool OnlyMatchSingleCounty { get; set; }

    public bool PreserveRawState { get; set; }

    public bool PreserveRawStreet { get; set; }

    public bool SmartFillApartmentUnits { get; set; }

    public string SelectedSmartFillMode { get; set; } = "None";

    public bool ForceWithoutAddressInput { get; set; }

    public bool EnableMapIncrementalDiffs { get; set; } = true;

    public bool EnableMapRenderItemReuse { get; set; } = true;

    public int MapTileCacheLifeDays { get; set; } = 7;

    public bool EnableMapAddressPointDeduplication { get; set; } = true;

    public bool IgnoreUnlimitedAddressesEntitlement { get; set; }

    public bool SimulateStoreInstallForUiTesting { get; set; }

    public bool IsSetupGuidanceExpanded { get; set; } = true;

    public bool IsResultsGuidanceExpanded { get; set; } = true;

    public bool IsOptionsExpanded { get; set; }
}

public interface ISetupSettingsService
{
    GuiSetupSettings? Load();

    void Save(GuiSetupSettings settings);
}

public sealed class SetupSettingsService : ISetupSettingsService
{
    private readonly GuiConfigurationStore configurationStore;

    public SetupSettingsService(string? filePath = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
    }

    public GuiSetupSettings? Load()
    {
        try
        {
            return configurationStore.Load().Setup;
        }
        catch
        {
            return null;
        }
    }

    public void Save(GuiSetupSettings settings)
    {
        try
        {
            var current = configurationStore.Load();
            current.Setup = settings;
            configurationStore.Save(current);
        }
        catch
        {
        }
    }
}

