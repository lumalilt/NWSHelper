using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NWSHelper.Core.Models;
using NWSHelper.Core.Utils;
using NWSHelper.Gui.Services;

namespace NWSHelper.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string SmartFillNoneOption = "None";

    private readonly IExtractionOrchestrator extractionOrchestrator;
    private readonly IDatasetDownloadService datasetDownloadService;
    private readonly IOutputPathActions outputPathActions;
    private readonly IThemeService themeService;
    private readonly ISetupSettingsService setupSettingsService;
    private readonly IGuiSettingsMigrationService settingsMigrationService;
    private readonly IEntitlementService entitlementService;
    private readonly IAccountLinkService accountLinkService;
    private readonly IUpdateService updateService;
    private IReadOnlyList<DatasetCatalogItem> availableDatasets = [];
    private CancellationTokenSource? activeRunCancellation;
    private TerritoryExtractionPlan? lastPreviewPlan;
    private EntitlementSnapshot activeEntitlementSnapshot = EntitlementSnapshot.CreateDefaultFree();
    private AccountLinkSnapshot activeAccountLinkSnapshot = AccountLinkSnapshot.CreateSignedOut();
    private bool isApplyingInitialTheme;
    private bool isApplyingInitialSetup;
    private bool isApplyingInitialUpdateSettings;
    private bool isSynchronizingPreviewSelection;
    private WorkflowStage settingsReturnStage = WorkflowStage.Results;
    private static readonly IComparer<string> TerritoryLabelComparer = Comparer<string>.Create(CompareTerritoryLabels);

    private static readonly HashSet<string> PersistedSetupProperties =
    [
        nameof(SelectedDatasetProviderId),
        nameof(SelectedDatasetSourcesCsv),
        nameof(OpenAddressesApiBaseUrl),
        nameof(OpenAddressesApiToken),
        nameof(UseEmbeddedOpenAddressesOnboardingWebView),
        nameof(OpenAddressesApiOnboardingType),
        nameof(LastOpenAddressesOnboardingSuccessSummary),
        nameof(BoundaryCsvPath),
        nameof(ExistingAddressesCsvPath),
        nameof(DatasetRootPath),
        nameof(StatesFilter),
        nameof(ConsolidatedOutputPath),
        nameof(WarningThreshold),
        nameof(WhatIf),
        nameof(ListThresholdExceeding),
        nameof(OutputDespiteThreshold),
        nameof(PerTerritoryOutput),
        nameof(PerTerritoryDirectory),
        nameof(OutputSplitRows),
        nameof(GroupByCategory),
        nameof(NoPrompt),
        nameof(SmartSelect),
        nameof(SelectAll),
        nameof(OutputExistingNoneNew),
        nameof(NoneNewInConsolidated),
        nameof(OutputAllRows),
        nameof(ExcludeNormalizedRows),
        nameof(OverwriteExistingLatLong),
        nameof(OnlyMatchSingleState),
        nameof(OnlyMatchSingleCounty),
        nameof(PreserveRawState),
        nameof(PreserveRawStreet),
        nameof(SmartFillApartmentUnits),
        nameof(SelectedSmartFillMode),
        nameof(ForceWithoutAddressInput),
        nameof(EnableMapIncrementalDiffs),
        nameof(EnableMapRenderItemReuse),
        nameof(MapTileCacheLifeDays),
        nameof(EnableMapAddressPointDeduplication),
        nameof(IsOptionsExpanded)
    ];

    [ObservableProperty]
    private WorkflowStage currentStage = WorkflowStage.Setup;

    [ObservableProperty]
    private string selectedDatasetProviderId = "openaddresses";

    [ObservableProperty]
    private string selectedDatasetSourcesCsv = string.Empty;

    [ObservableProperty]
    private string openAddressesApiBaseUrl = "https://batch.openaddresses.io/api";

    [ObservableProperty]
    private string openAddressesApiToken = string.Empty;

    [ObservableProperty]
    private bool useEmbeddedOpenAddressesOnboardingWebView = true;

    [ObservableProperty]
    private string openAddressesApiOnboardingType = ApiOnboardingTypeLabels.Automated;

    [ObservableProperty]
    private string lastOpenAddressesOnboardingSuccessSummary = string.Empty;

    [ObservableProperty]
    private string boundaryCsvPath = string.Empty;

    [ObservableProperty]
    private string existingAddressesCsvPath = string.Empty;

    [ObservableProperty]
    private string datasetRootPath = "./datasets";

    [ObservableProperty]
    private string statesFilter = "";

    [ObservableProperty]
    private string consolidatedOutputPath = string.Empty;

    [ObservableProperty]
    private int warningThreshold = 350;

    [ObservableProperty]
    private bool whatIf;

    [ObservableProperty]
    private bool listThresholdExceeding;

    [ObservableProperty]
    private bool outputDespiteThreshold;

    [ObservableProperty]
    private bool perTerritoryOutput = true;

    [ObservableProperty]
    private string perTerritoryDirectory = string.Empty;

    [ObservableProperty]
    private int outputSplitRows;

    [ObservableProperty]
    private bool groupByCategory;

    [ObservableProperty]
    private bool noPrompt;

    [ObservableProperty]
    private bool smartSelect;

    [ObservableProperty]
    private bool selectAll;

    [ObservableProperty]
    private bool outputExistingNoneNew;

    [ObservableProperty]
    private bool noneNewInConsolidated;

    [ObservableProperty]
    private bool outputAllRows;

    [ObservableProperty]
    private bool excludeNormalizedRows;

    [ObservableProperty]
    private bool overwriteExistingLatLong;

    [ObservableProperty]
    private bool onlyMatchSingleState;

    [ObservableProperty]
    private bool onlyMatchSingleCounty;

    [ObservableProperty]
    private bool preserveRawState;

    [ObservableProperty]
    private bool preserveRawStreet;

    [ObservableProperty]
    private bool smartFillApartmentUnits;

    [ObservableProperty]
    private string selectedSmartFillMode = SmartFillNoneOption;

    [ObservableProperty]
    private bool forceWithoutAddressInput;

    [ObservableProperty]
    private bool enableMapIncrementalDiffs = true;

    [ObservableProperty]
    private bool enableMapRenderItemReuse = true;

    [ObservableProperty]
    private int mapTileCacheLifeDays = 7;

    [ObservableProperty]
    private bool enableMapAddressPointDeduplication = true;

    [ObservableProperty]
    private bool isOptionsExpanded;

    [ObservableProperty]
    private string activationKey = string.Empty;

    [ObservableProperty]
    private bool isActivatingEntitlement;

    [ObservableProperty]
    private string entitlementAddOnLabel = "None";

    [ObservableProperty]
    private string entitlementStatusMessage = "Free tier active (max 30 new addresses per territory).";

    [ObservableProperty]
    private string entitlementLimitLabel = "30 new addresses / territory";

    [ObservableProperty]
    private bool entitlementExpired;

    [ObservableProperty]
    private string cappedOutputMessage = string.Empty;

    [ObservableProperty]
    private string accountLinkEmail = string.Empty;

    [ObservableProperty]
    private string accountLinkStatusLabel = "Not linked";

    [ObservableProperty]
    private string accountLinkStatusMessage = "No unified Store/direct account is linked for this install yet.";

    [ObservableProperty]
    private string linkedAccountIdentity = "Not linked";

    [ObservableProperty]
    private string linkedPurchaseSourceLabel = "None";

    [ObservableProperty]
    private string linkedAccountLastSyncLabel = "Not yet synced";

    [ObservableProperty]
    private bool isAccountLinkBusy;

    [ObservableProperty]
    private bool canClearAccountLink;

    [ObservableProperty]
    private string currentVersion = AppVersionProvider.GetDisplayVersion();

    [ObservableProperty]
    private bool autoUpdateEnabled = true;

    [ObservableProperty]
    private bool isStoreInstall;

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private string updateStatusMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isLoadingAvailableDatasets;

    [ObservableProperty]
    private bool isTestingDatasetProviderConnection;

    [ObservableProperty]
    private bool isDatasetProviderConnectionVerified;

    [ObservableProperty]
    private bool isDatasetDownloadInProgress;

    [ObservableProperty]
    private double datasetDownloadPercent;

    [ObservableProperty]
    private string datasetDownloadMessage = string.Empty;

    [ObservableProperty]
    private string statusMessage = "Ready.";

    [ObservableProperty]
    private string? lastError;

    [ObservableProperty]
    private int selectedTerritoryCount;

    [ObservableProperty]
    private bool allPreviewRowsSelected;

    [ObservableProperty]
    private string runState = "IDLE";

    [ObservableProperty]
    private string elapsed = "00:00:00";

    [ObservableProperty]
    private int processedCount;

    [ObservableProperty]
    private int matchedCount;

    [ObservableProperty]
    private int newCount;

    [ObservableProperty]
    private int existingTotal;

    [ObservableProperty]
    private int foundTotal;

    [ObservableProperty]
    private int distinctTotal;

    [ObservableProperty]
    private int writtenTotal;

    [ObservableProperty]
    private int plannedTotal;

    [ObservableProperty]
    private int skippedNoneNewTotal;

    [ObservableProperty]
    private int skippedThresholdTotal;

    [ObservableProperty]
    private int warningCount;

    [ObservableProperty]
    private int coordinateBackfillCount;

    [ObservableProperty]
    private int coordinateOverwriteCount;

    [ObservableProperty]
    private int resultsTabIndex;

    [ObservableProperty]
    private string mapTerritorySelectionSummary = "Map territories: All selected";

    [ObservableProperty]
    private AppThemePreference selectedThemePreference = AppThemePreference.System;

    public MainWindowViewModel()
        : this(null, null, null, null, null, null, null, null, null, null)
    {
    }

    public MainWindowViewModel(
        IExtractionOrchestrator? extractionOrchestrator = null,
        IOutputPathActions? outputPathActions = null,
        IThemeService? themeService = null,
        ISetupSettingsService? setupSettingsService = null,
        GuiLaunchOptions? launchOptions = null,
        IDatasetDownloadService? datasetDownloadService = null,
        IEntitlementService? entitlementService = null,
        IAccountLinkService? accountLinkService = null,
        IUpdateService? updateService = null,
        IGuiSettingsMigrationService? settingsMigrationService = null)
    {
        var storeRuntimeContextProvider = new StoreRuntimeContextProvider();
        this.extractionOrchestrator = extractionOrchestrator ?? new ExtractionOrchestrator();
        this.datasetDownloadService = datasetDownloadService ?? new DatasetDownloadService();
        this.outputPathActions = outputPathActions ?? new OutputPathActions();
        this.themeService = themeService ?? new ThemeService();
        this.setupSettingsService = setupSettingsService ?? new SetupSettingsService();
        this.settingsMigrationService = settingsMigrationService ?? new GuiSettingsMigrationService();
        this.entitlementService = entitlementService ?? new SupabaseEntitlementService();
        this.accountLinkService = accountLinkService ?? new AccountLinkService(storeRuntimeContextProvider: storeRuntimeContextProvider);
        this.updateService = updateService ?? new NetSparkleUpdateService(storeRuntimeContextProvider: storeRuntimeContextProvider);

        DatasetProviders = this.datasetDownloadService.GetProviders();

        SmartFillModes = BuildSmartFillModeOptions();
        ThemePreferences = Enum.GetValues<AppThemePreference>();
        ApiOnboardingTypes = ApiOnboardingTypeLabels.All;

        TerritoryPreviewRows = [];
        MapTerritorySelections = [];

        ProgressLanes = [];
        TerritoryResults = [];
        OutputArtifacts = [];

        isApplyingInitialTheme = true;
        SelectedThemePreference = this.themeService.CurrentPreference;
        isApplyingInitialTheme = false;

        isApplyingInitialSetup = true;
        ApplyPersistedSetupSettings();
        var resolvedLaunchOptions = launchOptions ?? new GuiLaunchOptions();
        ApplyLaunchOptions(resolvedLaunchOptions);
        if (resolvedLaunchOptions.HasAnyOverrides)
        {
            PersistSetupSettings();
            StatusMessage = "Loaded settings from command-line arguments and saved as defaults.";
        }
        isApplyingInitialSetup = false;

        ApplyEntitlementSnapshot(this.entitlementService.GetSnapshot());
        ApplyAccountLinkSnapshot(this.accountLinkService.GetSnapshot());

        isApplyingInitialUpdateSettings = true;
        CurrentVersion = this.updateService.CurrentVersion;
        IsStoreInstall = this.updateService.IsStoreInstall;
        AutoUpdateEnabled = this.updateService.AutoUpdateEnabled;
        isApplyingInitialUpdateSettings = false;

        if (DatasetProviders.Count > 0 && !DatasetProviders.Any(provider => string.Equals(provider.Id, SelectedDatasetProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedDatasetProviderId = DatasetProviders[0].Id;
        }

        PropertyChanged += OnViewModelPropertyChanged;

        InitializeProgressLanes();
        ReloadMapTerritorySelections();
        RecalculateSelectedTerritories();
    }

    public IReadOnlyList<string> SmartFillModes { get; }

    public IReadOnlyList<AppThemePreference> ThemePreferences { get; }

    public IReadOnlyList<string> ApiOnboardingTypes { get; }

    public IReadOnlyList<DatasetProviderOption> DatasetProviders { get; }

    public ObservableCollection<TerritoryPreviewItemViewModel> TerritoryPreviewRows { get; }

    public ObservableCollection<MapTerritorySelectionItemViewModel> MapTerritorySelections { get; }

    public ObservableCollection<ProgressLaneViewModel> ProgressLanes { get; }

    public ObservableCollection<TerritoryResultItemViewModel> TerritoryResults { get; }

    public ObservableCollection<OutputArtifactItemViewModel> OutputArtifacts { get; }

    public bool IsSetupStage => CurrentStage == WorkflowStage.Setup;

    public bool IsPreviewStage => CurrentStage == WorkflowStage.Preview;

    public bool IsRunStage => CurrentStage == WorkflowStage.Run;

    public bool IsResultsStage => CurrentStage == WorkflowStage.Results;

    public bool IsSettingsStage => CurrentStage == WorkflowStage.Settings;

    public int TotalPreviewCount => TerritoryPreviewRows.Count;

    public bool HasMapTerritorySelections => MapTerritorySelections.Count > 0;

    public bool HasDatasetDownloadMessage => !string.IsNullOrWhiteSpace(DatasetDownloadMessage);

    public bool HasLastOpenAddressesOnboardingSuccessSummary => !string.IsNullOrWhiteSpace(LastOpenAddressesOnboardingSuccessSummary);

    public bool HasCappedOutputMessage => !string.IsNullOrWhiteSpace(CappedOutputMessage);

    public bool HasUnlimitedAddressesAddOn => activeEntitlementSnapshot.HasUnlimitedAddressesAddOn;

    public bool CanConfigureAutoUpdatePolicy => !IsStoreInstall;

    public bool CanStartAccountSignIn => !IsAccountLinkBusy;

    public bool CanRefreshAccountLink => !IsAccountLinkBusy && activeAccountLinkSnapshot.HasState;

    public bool CanRestoreStorePurchase => !IsAccountLinkBusy && IsStoreInstall && activeAccountLinkSnapshot.HasActiveSession;

    public bool HasStoreContinuityPrompt => IsStoreInstall && !HasVerifiedStoreContinuity(activeAccountLinkSnapshot);

    public string StoreContinuityPromptTitle => GetStoreContinuityPromptTitle();

    public string StoreContinuityPromptMessage => GetStoreContinuityPromptMessage();

    public string StatusLine => string.IsNullOrWhiteSpace(LastError) ? StatusMessage : LastError;

    partial void OnCurrentStageChanged(WorkflowStage value)
    {
        OnPropertyChanged(nameof(IsSetupStage));
        OnPropertyChanged(nameof(IsPreviewStage));
        OnPropertyChanged(nameof(IsRunStage));
        OnPropertyChanged(nameof(IsResultsStage));
        OnPropertyChanged(nameof(IsSettingsStage));
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(StatusLine));
    }

    partial void OnLastErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(StatusLine));
    }

    partial void OnDatasetDownloadMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasDatasetDownloadMessage));
    }

    partial void OnLastOpenAddressesOnboardingSuccessSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasLastOpenAddressesOnboardingSuccessSummary));
    }

    partial void OnCappedOutputMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasCappedOutputMessage));
    }

    partial void OnIsStoreInstallChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureAutoUpdatePolicy));
        OnPropertyChanged(nameof(CanRestoreStorePurchase));
        OnPropertyChanged(nameof(HasStoreContinuityPrompt));
        OnPropertyChanged(nameof(StoreContinuityPromptTitle));
        OnPropertyChanged(nameof(StoreContinuityPromptMessage));
    }

    partial void OnIsAccountLinkBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartAccountSignIn));
        OnPropertyChanged(nameof(CanRefreshAccountLink));
        OnPropertyChanged(nameof(CanRestoreStorePurchase));
    }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        if (isApplyingInitialUpdateSettings)
        {
            return;
        }

        updateService.AutoUpdateEnabled = value;
        UpdateStatusMessage = value
            ? "Automatic update checks enabled for this install channel."
            : "Automatic update checks disabled.";
    }

    partial void OnSelectedThemePreferenceChanged(AppThemePreference value)
    {
        themeService.ApplyTheme(value);

        if (!isApplyingInitialTheme)
        {
            LastError = null;
            StatusMessage = value switch
            {
                AppThemePreference.System => "Theme set to System.",
                AppThemePreference.Light => "Theme set to Light.",
                AppThemePreference.Dark => "Theme set to Dark.",
                _ => "Theme updated."
            };
        }
    }

    partial void OnBoundaryCsvPathChanged(string value)
    {
        ReloadMapTerritorySelections();
    }

    partial void OnAllPreviewRowsSelectedChanged(bool value)
    {
        if (isSynchronizingPreviewSelection)
        {
            return;
        }

        isSynchronizingPreviewSelection = true;
        foreach (var row in TerritoryPreviewRows)
        {
            row.IsSelected = value;
        }

        isSynchronizingPreviewSelection = false;
        RecalculateSelectedTerritories();
    }

    partial void OnEnableMapIncrementalDiffsChanged(bool value)
    {
        OutputMapPreviewViewModel.EnableIncrementalCollectionDiffs = value;
    }

    partial void OnEnableMapRenderItemReuseChanged(bool value)
    {
        OutputMapPreviewViewModel.EnableRenderItemReuse = value;
    }

    partial void OnMapTileCacheLifeDaysChanged(int value)
    {
        var normalizedDays = Math.Clamp(value, 1, 365);
        if (normalizedDays != value)
        {
            MapTileCacheLifeDays = normalizedDays;
            return;
        }

        OutputMapPreviewViewModel.SetPersistentTileCacheLifeDays(normalizedDays);
    }

    partial void OnEnableMapAddressPointDeduplicationChanged(bool value)
    {
        OutputMapPreviewViewModel.EnableAddressPointDeduplication = value;
    }

    partial void OnSelectedDatasetProviderIdChanged(string value)
    {
        IsDatasetProviderConnectionVerified = false;
    }

    partial void OnOpenAddressesApiBaseUrlChanged(string value)
    {
        IsDatasetProviderConnectionVerified = false;
    }

    partial void OnOpenAddressesApiTokenChanged(string value)
    {
        IsDatasetProviderConnectionVerified = false;
    }

    partial void OnSelectedSmartFillModeChanged(string value)
    {
        SmartFillApartmentUnits = IsSmartFillModeEnabled(value);
    }

    [RelayCommand]
    private void GoToSetup() => CurrentStage = WorkflowStage.Setup;

    [RelayCommand]
    private void GoToPreview() => CurrentStage = WorkflowStage.Preview;

    [RelayCommand]
    private void GoToRun() => CurrentStage = WorkflowStage.Run;

    [RelayCommand]
    private void GoToResults() => CurrentStage = WorkflowStage.Results;

    [RelayCommand]
    private void GoToSettings() => EnterSettingsStage();

    [RelayCommand]
    private async Task ExecutePrimaryActionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        switch (CurrentStage)
        {
            case WorkflowStage.Setup:
                await BuildPreviewAsync();
                break;
            case WorkflowStage.Preview:
                await StartExtractionAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task RunExtractionWithDefaultsAsync()
    {
        if (IsBusy || !IsSetupStage)
        {
            return;
        }

        await BuildPreviewAsync();

        if (IsBusy || CurrentStage != WorkflowStage.Preview || !string.IsNullOrWhiteSpace(LastError))
        {
            return;
        }

        await StartExtractionAsync();
    }

    [RelayCommand]
    private void GoToPreviousStage()
    {
        if (CurrentStage == WorkflowStage.Settings)
        {
            ExitSettingsStage();
            return;
        }

        CurrentStage = CurrentStage switch
        {
            WorkflowStage.Preview => WorkflowStage.Setup,
            WorkflowStage.Run => WorkflowStage.Preview,
            WorkflowStage.Results => WorkflowStage.Run,
            _ => CurrentStage
        };
    }

    [RelayCommand]
    private void GoToNextStage()
    {
        if (CurrentStage == WorkflowStage.Results)
        {
            EnterSettingsStage();
            return;
        }

        CurrentStage = CurrentStage switch
        {
            WorkflowStage.Setup => WorkflowStage.Preview,
            WorkflowStage.Preview => WorkflowStage.Run,
            WorkflowStage.Run => WorkflowStage.Results,
            _ => CurrentStage
        };
    }

    [RelayCommand]
    private void ResetSetupDefaults()
    {
        isApplyingInitialSetup = true;
        ApplySetupSettings(new GuiSetupSettings());
        isApplyingInitialSetup = false;

        PersistSetupSettings();
        LastError = null;
        StatusMessage = "Setup defaults restored.";
    }

    [RelayCommand]
    private void ResetMapTileCache()
    {
        OutputMapPreviewViewModel.ResetPersistentTileCache();
        LastError = null;
        StatusMessage = "Map tile cache reset.";
    }

    public async Task ExportSettingsMigrationAsync(string path)
    {
        LastError = null;

        try
        {
            var result = await settingsMigrationService.ExportAsync(path, CancellationToken.None);
            if (result.IsSuccess)
            {
                StatusMessage = result.Message;
                return;
            }

            LastError = result.Message;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    public async Task ImportSettingsMigrationAsync(string path)
    {
        LastError = null;

        try
        {
            var result = await settingsMigrationService.ImportAsync(path, CancellationToken.None);
            if (!result.IsSuccess)
            {
                LastError = result.Message;
                StatusMessage = result.Message;
                return;
            }

            ApplyImportedMigrationConfiguration(result.Configuration);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ActivateProductAddOnAsync()
    {
        if (IsActivatingEntitlement)
        {
            return;
        }

        IsActivatingEntitlement = true;
        LastError = null;

        try
        {
            var activationResult = await entitlementService.ActivateAsync(ActivationKey, CancellationToken.None);
            ApplyEntitlementSnapshot(activationResult.Snapshot);

            if (activationResult.IsSuccess)
            {
                ActivationKey = string.Empty;
                StatusMessage = activationResult.Message;
                EntitlementStatusMessage = activationResult.Message;
                return;
            }

            LastError = activationResult.Message;
            StatusMessage = activationResult.Message;
            EntitlementStatusMessage = activationResult.Message;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            EntitlementStatusMessage = ex.Message;
        }
        finally
        {
            IsActivatingEntitlement = false;
        }
    }

    [RelayCommand]
    private async Task RefreshEntitlementStatusAsync()
    {
        LastError = null;
        try
        {
            var snapshot = await entitlementService.RefreshAsync(CancellationToken.None, forceOnlineRevalidation: true);
            ApplyEntitlementSnapshot(snapshot);
            StatusMessage = "Entitlement status refreshed.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearLinkedAccountAsync()
    {
        if (!CanClearAccountLink)
        {
            StatusMessage = "No linked account is cached for this install.";
            return;
        }

        LastError = null;

        try
        {
            var result = await accountLinkService.ClearCachedLinkAsync(CancellationToken.None);
            ApplyAccountLinkSnapshot(result.Snapshot);

            if (result.IsSuccess)
            {
                StatusMessage = result.Message;
                return;
            }

            LastError = result.Message;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartAccountSignInAsync()
    {
        if (IsAccountLinkBusy)
        {
            return;
        }

        IsAccountLinkBusy = true;
        LastError = null;

        try
        {
            var result = await accountLinkService.StartSignInAsync(AccountLinkEmail, CancellationToken.None);
            ApplyAccountLinkOperationResult(result);
            await TryAutoRestoreStorePurchaseAsync(result.Snapshot, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AccountLinkStatusMessage = "Account sign-in request was canceled.";
            StatusMessage = "Account sign-in request was canceled.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AccountLinkStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsAccountLinkBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAccountLinkStatusAsync()
    {
        if (IsAccountLinkBusy)
        {
            return;
        }

        IsAccountLinkBusy = true;
        LastError = null;

        try
        {
            var result = await accountLinkService.RefreshStatusAsync(CancellationToken.None);
            ApplyAccountLinkOperationResult(result);
            await TryAutoRestoreStorePurchaseAsync(result.Snapshot, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AccountLinkStatusMessage = "Account link refresh was canceled.";
            StatusMessage = "Account link refresh was canceled.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AccountLinkStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsAccountLinkBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreStorePurchaseAsync()
    {
        if (IsAccountLinkBusy)
        {
            return;
        }

        IsAccountLinkBusy = true;
        LastError = null;

        try
        {
            await RestoreStorePurchaseCoreAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AccountLinkStatusMessage = "Store purchase restore was canceled.";
            StatusMessage = "Store purchase restore was canceled.";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AccountLinkStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsAccountLinkBusy = false;
        }
    }

    public Task RunStartupUpdatePolicyAsync()
    {
        return ExecuteUpdateCheckAsync(fromStartupPolicy: true, CancellationToken.None);
    }

    public async Task RunStartupStoreContinuityPolicyAsync()
    {
        if (!IsStoreInstall)
        {
            return;
        }

        if (ShouldPromptForStoreContinuityLink(activeAccountLinkSnapshot))
        {
            if (CurrentStage != WorkflowStage.Settings)
            {
                settingsReturnStage = CurrentStage;
                CurrentStage = WorkflowStage.Settings;
            }

            LastError = null;
            StatusMessage = "Store install detected. Link an email now to preserve later direct-download access.";
            return;
        }

        await TryAutoRestoreStorePurchaseAsync(activeAccountLinkSnapshot, CancellationToken.None);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync()
    {
        return ExecuteUpdateCheckAsync(fromStartupPolicy: false, CancellationToken.None);
    }

    private async Task ExecuteUpdateCheckAsync(bool fromStartupPolicy, CancellationToken cancellationToken)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        if (fromStartupPolicy && (IsStoreInstall || !AutoUpdateEnabled))
        {
            return;
        }

        IsCheckingForUpdates = true;
        if (!fromStartupPolicy)
        {
            LastError = null;
        }

        try
        {
            var result = fromStartupPolicy
                ? await updateService.CheckForUpdatesOnStartupAsync(cancellationToken)
                : await updateService.CheckForUpdatesAsync(cancellationToken);
            UpdateStatusMessage = result.Message;

            if (!fromStartupPolicy || result.IsUpdateAvailable)
            {
                StatusMessage = result.Message;
            }
        }
        catch (OperationCanceledException) when (fromStartupPolicy)
        {
            UpdateStatusMessage = "Automatic update check was canceled.";
        }
        catch (Exception ex)
        {
            if (fromStartupPolicy)
            {
                UpdateStatusMessage = $"Automatic update check failed: {ex.Message}";
                return;
            }

            LastError = ex.Message;
            StatusMessage = ex.Message;
            UpdateStatusMessage = ex.Message;
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task LoadAvailableDatasetsAsync()
    {
        await LoadAvailableDatasetsForSelectionAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<DatasetCatalogItem>> LoadAvailableDatasetsForSelectionAsync(CancellationToken cancellationToken)
    {
        if (IsLoadingAvailableDatasets)
        {
            return availableDatasets;
        }

        LastError = null;
        IsLoadingAvailableDatasets = true;
        StatusMessage = "Loading available datasets...";

        try
        {
            availableDatasets = await datasetDownloadService.GetDatasetsAsync(
                SelectedDatasetProviderId,
                OpenAddressesApiBaseUrl,
                cancellationToken);

            DatasetDownloadMessage = availableDatasets.Count == 0
                ? "No datasets were returned for the selected provider."
                : $"Loaded {availableDatasets.Count} dataset options.";
            StatusMessage = DatasetDownloadMessage;
            return availableDatasets;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            DatasetDownloadMessage = ex.Message;
            return [];
        }
        finally
        {
            IsLoadingAvailableDatasets = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedDatasetsAsync(IReadOnlyCollection<string>? selectedDatasetKeys)
    {
        if (IsDatasetDownloadInProgress)
        {
            return;
        }

        var selectedKeys = selectedDatasetKeys?
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (selectedKeys.Count == 0)
        {
            StatusMessage = "No datasets selected.";
            return;
        }

        LastError = null;
        IsDatasetDownloadInProgress = true;
        DatasetDownloadPercent = 0;
        DatasetDownloadMessage = "Preparing dataset downloads...";

        try
        {
            var progress = new Progress<DatasetDownloadProgress>(snapshot =>
            {
                DatasetDownloadPercent = snapshot.PercentComplete;
                DatasetDownloadMessage = snapshot.Message;
                StatusMessage = snapshot.Message;
            });

            var downloadResult = await datasetDownloadService.DownloadDatasetsAsync(
                new DatasetDownloadRequest(
                    SelectedDatasetProviderId,
                    DatasetRootPath,
                    selectedKeys,
                    OpenAddressesApiBaseUrl,
                    OpenAddressesApiToken),
                progress,
                CancellationToken.None);

            SelectedDatasetSourcesCsv = string.Join(',', selectedKeys);
            DatasetRootPath = downloadResult.ProviderDatasetRootPath;

            DatasetDownloadPercent = 100;
            DatasetDownloadMessage = $"Downloaded {downloadResult.DownloadedCount}/{downloadResult.RequestedCount} datasets.";
            StatusMessage = DatasetDownloadMessage;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DatasetDownloadMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsDatasetDownloadInProgress = false;
        }
    }

    public IReadOnlyCollection<string> GetSelectedDatasetSourceKeys()
    {
        return ParseSelectedDatasetSources(SelectedDatasetSourcesCsv);
    }

    [RelayCommand]
    private async Task TestDatasetProviderConnectionAsync()
    {
        if (IsTestingDatasetProviderConnection)
        {
            return;
        }

        LastError = null;
        IsTestingDatasetProviderConnection = true;
        StatusMessage = "Testing dataset provider connection...";

        try
        {
            var result = await datasetDownloadService.TestProviderConnectionAsync(
                SelectedDatasetProviderId,
                OpenAddressesApiBaseUrl,
                OpenAddressesApiToken,
                CancellationToken.None);

            DatasetDownloadMessage = result.Message;
            if (result.IsSuccess)
            {
                IsDatasetProviderConnectionVerified = true;
                LastError = null;
                StatusMessage = result.Message;
                return;
            }

            IsDatasetProviderConnectionVerified = false;
            LastError = result.Message;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            IsDatasetProviderConnectionVerified = false;
            LastError = ex.Message;
            StatusMessage = ex.Message;
            DatasetDownloadMessage = ex.Message;
        }
        finally
        {
            IsTestingDatasetProviderConnection = false;
        }
    }

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        LastError = null;
        IsBusy = true;
        StatusMessage = "Building preview from extraction plan...";

        try
        {
            if (!TryValidateSetupInputs(out var validationError))
            {
                LastError = validationError;
                StatusMessage = validationError;
                return;
            }

            var request = BuildExtractionRequest();
            var preview = await extractionOrchestrator.BuildPreviewAsync(request, CancellationToken.None);
            lastPreviewPlan = preview.Plan;
            UpdateCappedOutputMessage(preview.Result);

            if (request.ListThresholdExceeding)
            {
                ApplyThresholdExceedingListing(preview.Result, request.WarningThreshold);
                CurrentStage = WorkflowStage.Results;
                return;
            }

            LoadPreviewRows(preview.Plan, request.SelectAll || ((request.SmartSelect || request.NoPrompt) && !request.SelectAll));
            StatusMessage = $"Preview ready. {TerritoryPreviewRows.Count} territories evaluated.";
            CurrentStage = WorkflowStage.Preview;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartExtractionAsync()
    {
        LastError = null;

        if (!TryValidateSetupInputs(out var validationError))
        {
            LastError = validationError;
            StatusMessage = validationError;
            RunState = "FAILED";
            CurrentStage = WorkflowStage.Preview;
            return;
        }

        var selectedIds = TerritoryPreviewRows
            .Where(row => row.IsSelected)
            .Select(row => row.TerritoryId)
            .ToList();

        if (selectedIds.Count == 0)
        {
            LastError = "Select at least one territory in Preview before running extraction.";
            StatusMessage = "Extraction blocked: no territories selected.";
            RunState = "BLOCKED";
            CurrentStage = WorkflowStage.Preview;
            return;
        }

        IsBusy = true;
        RunState = "RUNNING";
        StatusMessage = "Extraction started...";
        CurrentStage = WorkflowStage.Run;
        InitializeProgressLanes();

        activeRunCancellation?.Dispose();
        activeRunCancellation = new CancellationTokenSource();

        try
        {
            var request = BuildExtractionRequest();

            var selectedMissingPreExisting = GetSelectedTerritoriesMissingPreExistingCount(selectedIds);
            if (selectedMissingPreExisting > 0 && !request.ForceWithoutAddressInput && !request.NoPrompt)
            {
                var promptDetail = string.IsNullOrWhiteSpace(request.ExistingAddressesCsvPath)
                    ? "No pre-existing addresses file was provided."
                    : "No pre-existing rows matched selected territories.";
                LastError = $"{promptDetail} Enable 'Force without address input' to proceed ({selectedMissingPreExisting} territories).";
                StatusMessage = "Extraction blocked pending force-without-address-input.";
                RunState = "BLOCKED";
                CurrentStage = WorkflowStage.Preview;
                return;
            }

            var execution = await extractionOrchestrator.ExecuteAsync(
                request,
                selectedIds,
                UpdateRunProgress,
                activeRunCancellation.Token);

            ApplyExtractionResult(execution.Result);
            RunState = execution.Result.WasWhatIf ? "WHAT_IF_COMPLETE" : "COMPLETED";
            CurrentStage = WorkflowStage.Results;
        }
        catch (OperationCanceledException)
        {
            RunState = "CANCELED";
            StatusMessage = "Extraction canceled.";
        }
        catch (Exception ex)
        {
            RunState = "FAILED";
            LastError = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        if (IsSettingsStage)
        {
            ExitSettingsStage();
            return;
        }

        if (!IsRunStage || !IsBusy)
        {
            return;
        }

        activeRunCancellation?.Cancel();
    }

    private void EnterSettingsStage()
    {
        if (CurrentStage != WorkflowStage.Settings)
        {
            settingsReturnStage = CurrentStage;
        }

        CurrentStage = WorkflowStage.Settings;
    }

    private void ExitSettingsStage()
    {
        CurrentStage = settingsReturnStage == WorkflowStage.Settings
            ? WorkflowStage.Results
            : settingsReturnStage;
    }

    [RelayCommand]
    private void ApplySmartSelection()
    {
        foreach (var row in TerritoryPreviewRows)
        {
            row.IsSelected = !row.HasWarning;
        }

        RecalculateSelectedTerritories();
    }

    [RelayCommand]
    private void SelectAllTerritories()
    {
        foreach (var row in TerritoryPreviewRows)
        {
            row.IsSelected = true;
        }

        RecalculateSelectedTerritories();
    }

    [RelayCommand]
    private void ClearTerritorySelection()
    {
        foreach (var row in TerritoryPreviewRows)
        {
            row.IsSelected = false;
        }

        RecalculateSelectedTerritories();
    }

    [RelayCommand]
    private void SelectAllMapTerritories()
    {
        foreach (var territory in MapTerritorySelections)
        {
            territory.IsSelected = true;
        }

        UpdateMapTerritorySelectionSummary();
    }

    [RelayCommand]
    private void ClearMapTerritorySelection()
    {
        foreach (var territory in MapTerritorySelections)
        {
            territory.IsSelected = false;
        }

        UpdateMapTerritorySelectionSummary();
    }

    [RelayCommand]
    private async Task OpenOutputPathAsync(string? path)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "No output path available.";
            return;
        }

        var opened = await outputPathActions.OpenPathAsync(path);
        if (opened)
        {
            StatusMessage = $"Opened output path: {path}";
            return;
        }

        LastError = $"Could not open output path: {path}";
        StatusMessage = "Open path failed.";
    }

    [RelayCommand]
    private async Task CopyOutputPathAsync(string? path)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "No output path available.";
            return;
        }

        var copied = await outputPathActions.CopyPathAsync(path);
        if (copied)
        {
            StatusMessage = $"Copied output path: {path}";
            return;
        }

        LastError = $"Could not copy output path: {path}";
        StatusMessage = "Copy path failed.";
    }

    [RelayCommand]
    private async Task PreviewOutputMapAsync(string? path)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "No output path available.";
            return;
        }

        var previewOpened = await outputPathActions.PreviewMapAsync(path, BoundaryCsvPath, "Output");
        if (previewOpened)
        {
            StatusMessage = $"Opened map preview: {path}";
            return;
        }

        LastError = $"Could not open map preview: {path}";
        StatusMessage = "Map preview failed.";
    }

    [RelayCommand]
    private async Task OpenInputMapAsync()
    {
        LastError = null;

        if (!IsSetupStage)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BoundaryCsvPath))
        {
            StatusMessage = "Boundary CSV is required for input map preview.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ExistingAddressesCsvPath))
        {
            StatusMessage = "Existing Addresses CSV is required for input map preview.";
            return;
        }

        var selectedMapTerritoryIds = MapTerritorySelections
            .Where(territory => territory.IsSelected)
            .Select(territory => territory.TerritoryId)
            .Where(territoryId => !string.IsNullOrWhiteSpace(territoryId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (MapTerritorySelections.Count > 0 && selectedMapTerritoryIds.Count == 0)
        {
            StatusMessage = "Select at least one map territory before opening Map Existing.";
            return;
        }

        var previewOpened = await outputPathActions.PreviewMapAsync(
            ExistingAddressesCsvPath,
            BoundaryCsvPath,
            "Map Existing",
            selectedMapTerritoryIds);
        if (previewOpened)
        {
            StatusMessage = $"Opened input map preview: {ExistingAddressesCsvPath}";
            return;
        }

        LastError = $"Could not open input map preview: {ExistingAddressesCsvPath}";
        StatusMessage = "Input map preview failed.";
    }

    private void OnPreviewRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerritoryPreviewItemViewModel.IsSelected))
        {
            RecalculateSelectedTerritories();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isApplyingInitialSetup || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (!PersistedSetupProperties.Contains(e.PropertyName))
        {
            return;
        }

        PersistSetupSettings();
    }

    private void RecalculateSelectedTerritories()
    {
        var selectedCount = TerritoryPreviewRows.Count(row => row.IsSelected);
        SelectedTerritoryCount = selectedCount;

        var allSelected = TerritoryPreviewRows.Count > 0 && selectedCount == TerritoryPreviewRows.Count;
        if (AllPreviewRowsSelected != allSelected)
        {
            isSynchronizingPreviewSelection = true;
            AllPreviewRowsSelected = allSelected;
            isSynchronizingPreviewSelection = false;
        }
    }

    private void ReloadMapTerritorySelections()
    {
        foreach (var territory in MapTerritorySelections)
        {
            territory.PropertyChanged -= OnMapTerritorySelectionPropertyChanged;
        }

        MapTerritorySelections.Clear();

        if (string.IsNullOrWhiteSpace(BoundaryCsvPath))
        {
            UpdateMapTerritorySelectionSummary();
            OnPropertyChanged(nameof(HasMapTerritorySelections));
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(BoundaryCsvPath);
            if (!File.Exists(fullPath))
            {
                UpdateMapTerritorySelectionSummary();
                OnPropertyChanged(nameof(HasMapTerritorySelections));
                return;
            }

            var items = new List<MapTerritorySelectionItemViewModel>();
            var seenTerritoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var stream = File.OpenRead(fullPath);
            using var reader = new StreamReader(stream);
            using var csv = CsvFactory.CreateReader(reader);

            if (!csv.Read() || !csv.ReadHeader())
            {
                UpdateMapTerritorySelectionSummary();
                OnPropertyChanged(nameof(HasMapTerritorySelections));
                return;
            }

            var headers = csv.HeaderRecord ?? [];

            while (csv.Read())
            {
                var territoryId = GetCsvField(csv, headers, "TerritoryID").Trim();
                if (string.IsNullOrWhiteSpace(territoryId) || !seenTerritoryIds.Add(territoryId))
                {
                    continue;
                }

                var categoryCode = GetCsvField(csv, headers, "CategoryCode");
                var number = GetCsvField(csv, headers, "Number");
                var suffix = GetCsvField(csv, headers, "Suffix");
                var territoryNumber = GetCsvField(csv, headers, "TerritoryNumber");

                items.Add(new MapTerritorySelectionItemViewModel(
                    territoryId,
                    BuildMapTerritoryDisplayLabel(categoryCode, number, suffix, territoryNumber, territoryId),
                    true));
            }

            var orderedItems = items
                .OrderBy(item => item.DisplayLabel, TerritoryLabelComparer)
                .ThenBy(item => item.TerritoryId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in orderedItems)
            {
                item.PropertyChanged += OnMapTerritorySelectionPropertyChanged;
                MapTerritorySelections.Add(item);
            }
        }
        catch
        {
        }

        UpdateMapTerritorySelectionSummary();
        OnPropertyChanged(nameof(HasMapTerritorySelections));
    }

    private void OnMapTerritorySelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapTerritorySelectionItemViewModel.IsSelected))
        {
            UpdateMapTerritorySelectionSummary();
        }
    }

    private void UpdateMapTerritorySelectionSummary()
    {
        var mapTerritoryTitle = "Map existing territories";
        if (MapTerritorySelections.Count == 0)
        {
            MapTerritorySelectionSummary = $"{mapTerritoryTitle}: All selected";
            return;
        }

        var selectedCount = MapTerritorySelections.Count(item => item.IsSelected);
        MapTerritorySelectionSummary = selectedCount switch
        {
            0 => $"{mapTerritoryTitle}: None selected (0/{MapTerritorySelections.Count})",
            _ when selectedCount == MapTerritorySelections.Count => $"{mapTerritoryTitle}: All selected ({selectedCount})",
            _ => $"{mapTerritoryTitle}: {selectedCount}/{MapTerritorySelections.Count} selected"
        };
    }

    private static string GetCsvField(CsvHelper.CsvReader csv, IReadOnlyList<string> headers, params string[] candidateHeaders)
    {
        foreach (var candidate in candidateHeaders)
        {
            var matched = headers.FirstOrDefault(header =>
                string.Equals(header, candidate, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matched))
            {
                return csv.GetField(matched) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string BuildMapTerritoryDisplayLabel(
        string categoryCode,
        string number,
        string suffix,
        string territoryNumber,
        string territoryId)
    {
        var normalizedCategoryCode = categoryCode.Trim();
        var normalizedNumber = number.Trim();
        var normalizedSuffix = suffix.Trim();
        var normalizedTerritoryId = territoryId.Trim();
        var normalizedTerritoryNumber = territoryNumber.Trim();

        var labelParts = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(normalizedCategoryCode))
        {
            labelParts.Add(normalizedCategoryCode);
        }

        if (!string.IsNullOrWhiteSpace(normalizedNumber))
        {
            labelParts.Add(normalizedNumber);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSuffix))
        {
            labelParts.Add(normalizedSuffix);
        }

        var coreLabel = labelParts.Count > 0
            ? string.Join("-", labelParts)
            : (!string.IsNullOrWhiteSpace(normalizedTerritoryNumber)
                ? normalizedTerritoryNumber
                : normalizedTerritoryId);

        return string.IsNullOrWhiteSpace(normalizedTerritoryId)
            ? coreLabel
            : $"{coreLabel} [ID:{normalizedTerritoryId}]";
    }

    private void ApplyPersistedSetupSettings()
    {
        var settings = setupSettingsService.Load();
        if (settings is null)
        {
            return;
        }

        ApplySetupSettings(settings);
    }

    private void ApplyLaunchOptions(GuiLaunchOptions launchOptions)
    {
        if (!string.IsNullOrWhiteSpace(launchOptions.BoundaryCsvPath))
        {
            BoundaryCsvPath = launchOptions.BoundaryCsvPath;
        }

        if (launchOptions.ExistingAddressesCsvPath is not null)
        {
            ExistingAddressesCsvPath = launchOptions.ExistingAddressesCsvPath;
        }

        if (!string.IsNullOrWhiteSpace(launchOptions.DatasetRootPath))
        {
            DatasetRootPath = launchOptions.DatasetRootPath;
        }

        if (!string.IsNullOrWhiteSpace(launchOptions.StatesFilter))
        {
            StatesFilter = launchOptions.StatesFilter;
        }

        if (!string.IsNullOrWhiteSpace(launchOptions.ConsolidatedOutputPath))
        {
            ConsolidatedOutputPath = NormalizeAlternativeOutputFolder(launchOptions.ConsolidatedOutputPath);
        }

        if (launchOptions.OutputSplitRows.HasValue)
        {
            OutputSplitRows = launchOptions.OutputSplitRows.Value;
        }

        if (launchOptions.WarningThreshold.HasValue)
        {
            WarningThreshold = launchOptions.WarningThreshold.Value;
        }

        if (launchOptions.PerTerritoryOutput.HasValue)
        {
            PerTerritoryOutput = launchOptions.PerTerritoryOutput.Value;
        }

        if (launchOptions.ForceWithoutAddressInput.HasValue)
        {
            ForceWithoutAddressInput = launchOptions.ForceWithoutAddressInput.Value;
        }

        if (launchOptions.SmartSelect.HasValue)
        {
            SmartSelect = launchOptions.SmartSelect.Value;
        }

        if (launchOptions.SelectAll.HasValue)
        {
            SelectAll = launchOptions.SelectAll.Value;
        }

        if (launchOptions.NoPrompt.HasValue)
        {
            NoPrompt = launchOptions.NoPrompt.Value;
        }

        if (launchOptions.WhatIf.HasValue)
        {
            WhatIf = launchOptions.WhatIf.Value;
        }

        if (launchOptions.OutputDespiteThreshold.HasValue)
        {
            OutputDespiteThreshold = launchOptions.OutputDespiteThreshold.Value;
        }

        if (launchOptions.OutputExistingNoneNew.HasValue)
        {
            OutputExistingNoneNew = launchOptions.OutputExistingNoneNew.Value;
        }

        if (launchOptions.GroupByCategory.HasValue)
        {
            GroupByCategory = launchOptions.GroupByCategory.Value;
        }

        if (launchOptions.NoneNewInConsolidated.HasValue)
        {
            NoneNewInConsolidated = launchOptions.NoneNewInConsolidated.Value;
        }

        if (launchOptions.OutputAllRows.HasValue)
        {
            OutputAllRows = launchOptions.OutputAllRows.Value;
        }

        if (launchOptions.ExcludeNormalizedRows.HasValue)
        {
            ExcludeNormalizedRows = launchOptions.ExcludeNormalizedRows.Value;
        }

        if (launchOptions.OverwriteExistingLatLong.HasValue)
        {
            OverwriteExistingLatLong = launchOptions.OverwriteExistingLatLong.Value;
        }

        if (launchOptions.OnlyMatchSingleState.HasValue)
        {
            OnlyMatchSingleState = launchOptions.OnlyMatchSingleState.Value;
        }

        if (launchOptions.OnlyMatchSingleCounty.HasValue)
        {
            OnlyMatchSingleCounty = launchOptions.OnlyMatchSingleCounty.Value;
        }

        if (launchOptions.PreserveRawState.HasValue)
        {
            PreserveRawState = launchOptions.PreserveRawState.Value;
        }

        if (launchOptions.PreserveRawStreet.HasValue)
        {
            PreserveRawStreet = launchOptions.PreserveRawStreet.Value;
        }

        if (launchOptions.SmartFillApartmentUnits.HasValue)
        {
            SmartFillApartmentUnits = launchOptions.SmartFillApartmentUnits.Value;

            if (!SmartFillApartmentUnits)
            {
                SelectedSmartFillMode = SmartFillNoneOption;
            }
            else if (!IsSmartFillModeEnabled(SelectedSmartFillMode))
            {
                SelectedSmartFillMode = SmartFillApartmentUnitsMode.TypeApartmentOnly.ToString();
            }
        }

        if (!string.IsNullOrWhiteSpace(launchOptions.SmartFillApartmentUnitsMode) &&
            Enum.TryParse<SmartFillApartmentUnitsMode>(launchOptions.SmartFillApartmentUnitsMode, ignoreCase: true, out var launchSmartFillMode))
        {
            SelectedSmartFillMode = launchSmartFillMode.ToString();
            SmartFillApartmentUnits = true;
        }

        if (launchOptions.ListThresholdExceeding.HasValue)
        {
            ListThresholdExceeding = launchOptions.ListThresholdExceeding.Value;
        }
    }

    private void ApplySetupSettings(GuiSetupSettings settings)
    {
        SelectedDatasetProviderId = settings.SelectedDatasetProviderId;
        SelectedDatasetSourcesCsv = settings.SelectedDatasetSourcesCsv;
        OpenAddressesApiBaseUrl = settings.OpenAddressesApiBaseUrl;
        OpenAddressesApiToken = settings.OpenAddressesApiToken;
        UseEmbeddedOpenAddressesOnboardingWebView = settings.UseEmbeddedOpenAddressesOnboardingWebView;
        if (ApiOnboardingTypeLabels.TryParse(settings.OpenAddressesApiOnboardingType, out var onboardingType))
        {
            OpenAddressesApiOnboardingType = ApiOnboardingTypeLabels.ToLabel(onboardingType);
        }
        else
        {
            OpenAddressesApiOnboardingType = settings.EnableOpenAddressesAdvancedDiagnostics
                ? ApiOnboardingTypeLabels.Debugging
                : ApiOnboardingTypeLabels.Automated;
        }

        LastOpenAddressesOnboardingSuccessSummary = settings.LastOpenAddressesOnboardingSuccessSummary;
        BoundaryCsvPath = settings.BoundaryCsvPath;
        ExistingAddressesCsvPath = settings.ExistingAddressesCsvPath;
        DatasetRootPath = settings.DatasetRootPath;
        StatesFilter = settings.StatesFilter;
        ConsolidatedOutputPath = NormalizeAlternativeOutputFolder(settings.ConsolidatedOutputPath);
        WarningThreshold = settings.WarningThreshold;
        WhatIf = settings.WhatIf;
        ListThresholdExceeding = settings.ListThresholdExceeding;
        OutputDespiteThreshold = settings.OutputDespiteThreshold;
        PerTerritoryOutput = settings.PerTerritoryOutput;
        PerTerritoryDirectory = settings.PerTerritoryDirectory;
        OutputSplitRows = settings.OutputSplitRows;
        GroupByCategory = settings.GroupByCategory;
        NoPrompt = settings.NoPrompt;
        SmartSelect = settings.SmartSelect;
        SelectAll = settings.SelectAll;
        OutputExistingNoneNew = settings.OutputExistingNoneNew;
        NoneNewInConsolidated = settings.NoneNewInConsolidated;
        OutputAllRows = settings.OutputAllRows;
        ExcludeNormalizedRows = settings.ExcludeNormalizedRows;
        OverwriteExistingLatLong = settings.OverwriteExistingLatLong;
        OnlyMatchSingleState = settings.OnlyMatchSingleState;
        OnlyMatchSingleCounty = settings.OnlyMatchSingleCounty;
        PreserveRawState = settings.PreserveRawState;
        PreserveRawStreet = settings.PreserveRawStreet;
        ForceWithoutAddressInput = settings.ForceWithoutAddressInput;
        EnableMapIncrementalDiffs = settings.EnableMapIncrementalDiffs;
        EnableMapRenderItemReuse = settings.EnableMapRenderItemReuse;
        MapTileCacheLifeDays = settings.MapTileCacheLifeDays;
        EnableMapAddressPointDeduplication = settings.EnableMapAddressPointDeduplication;
        IsOptionsExpanded = settings.IsOptionsExpanded;

        if (!string.IsNullOrWhiteSpace(settings.SelectedSmartFillMode) &&
            string.Equals(settings.SelectedSmartFillMode, SmartFillNoneOption, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSmartFillMode = SmartFillNoneOption;
        }
        else if (!string.IsNullOrWhiteSpace(settings.SelectedSmartFillMode) &&
            Enum.TryParse<SmartFillApartmentUnitsMode>(settings.SelectedSmartFillMode, ignoreCase: true, out var persistedSmartFillMode))
        {
            SelectedSmartFillMode = persistedSmartFillMode.ToString();
        }
        else
        {
            SelectedSmartFillMode = settings.SmartFillApartmentUnits
                ? SmartFillApartmentUnitsMode.TypeApartmentOnly.ToString()
                : SmartFillNoneOption;
        }

        SmartFillApartmentUnits = IsSmartFillModeEnabled(SelectedSmartFillMode);
    }

    private void ApplyImportedMigrationConfiguration(GuiConfigurationDocument? configuration)
    {
        if (configuration is null)
        {
            return;
        }

        isApplyingInitialTheme = true;
        themeService.ApplyTheme(configuration.Theme);
        SelectedThemePreference = themeService.CurrentPreference;
        isApplyingInitialTheme = false;

        if (configuration.Setup is not null)
        {
            isApplyingInitialSetup = true;
            ApplySetupSettings(configuration.Setup);
            isApplyingInitialSetup = false;
        }

        if (configuration.Updates is not null)
        {
            isApplyingInitialUpdateSettings = true;
            updateService.AutoUpdateEnabled = configuration.Updates.AutoUpdateEnabled;
            AutoUpdateEnabled = updateService.AutoUpdateEnabled;
            isApplyingInitialUpdateSettings = false;
        }

        if (configuration.Entitlement?.AccountLink is not null)
        {
            ApplyAccountLinkSnapshot(ToAccountLinkSnapshot(configuration.Entitlement.AccountLink));
        }
    }

    private void PersistSetupSettings()
    {
        setupSettingsService.Save(new GuiSetupSettings
        {
            SelectedDatasetProviderId = SelectedDatasetProviderId,
            SelectedDatasetSourcesCsv = SelectedDatasetSourcesCsv,
            OpenAddressesApiBaseUrl = OpenAddressesApiBaseUrl,
            OpenAddressesApiToken = OpenAddressesApiToken,
            UseEmbeddedOpenAddressesOnboardingWebView = UseEmbeddedOpenAddressesOnboardingWebView,
            OpenAddressesApiOnboardingType = OpenAddressesApiOnboardingType,
            EnableOpenAddressesAdvancedDiagnostics = ApiOnboardingTypeLabels.ParseOrDefault(OpenAddressesApiOnboardingType) == ApiOnboardingType.Debugging,
            LastOpenAddressesOnboardingSuccessSummary = LastOpenAddressesOnboardingSuccessSummary,
            BoundaryCsvPath = BoundaryCsvPath,
            ExistingAddressesCsvPath = ExistingAddressesCsvPath,
            DatasetRootPath = DatasetRootPath,
            StatesFilter = StatesFilter,
            ConsolidatedOutputPath = NormalizeAlternativeOutputFolder(ConsolidatedOutputPath),
            WarningThreshold = WarningThreshold,
            WhatIf = WhatIf,
            ListThresholdExceeding = ListThresholdExceeding,
            OutputDespiteThreshold = OutputDespiteThreshold,
            PerTerritoryOutput = PerTerritoryOutput,
            PerTerritoryDirectory = PerTerritoryDirectory,
            OutputSplitRows = OutputSplitRows,
            GroupByCategory = GroupByCategory,
            NoPrompt = NoPrompt,
            SmartSelect = SmartSelect,
            SelectAll = SelectAll,
            OutputExistingNoneNew = OutputExistingNoneNew,
            NoneNewInConsolidated = NoneNewInConsolidated,
            OutputAllRows = OutputAllRows,
            ExcludeNormalizedRows = ExcludeNormalizedRows,
            OverwriteExistingLatLong = OverwriteExistingLatLong,
            OnlyMatchSingleState = OnlyMatchSingleState,
            OnlyMatchSingleCounty = OnlyMatchSingleCounty,
            PreserveRawState = PreserveRawState,
            PreserveRawStreet = PreserveRawStreet,
            SmartFillApartmentUnits = IsSmartFillModeEnabled(SelectedSmartFillMode),
            SelectedSmartFillMode = IsSmartFillModeEnabled(SelectedSmartFillMode)
                ? SelectedSmartFillMode
                : SmartFillNoneOption,
            ForceWithoutAddressInput = ForceWithoutAddressInput,
            EnableMapIncrementalDiffs = EnableMapIncrementalDiffs,
            EnableMapRenderItemReuse = EnableMapRenderItemReuse,
            MapTileCacheLifeDays = MapTileCacheLifeDays,
            EnableMapAddressPointDeduplication = EnableMapAddressPointDeduplication,
            IsOptionsExpanded = IsOptionsExpanded
        });
    }

    private void ApplyEntitlementSnapshot(EntitlementSnapshot snapshot)
    {
        activeEntitlementSnapshot = snapshot ?? EntitlementSnapshot.CreateDefaultFree("Fallback");

        EntitlementExpired = activeEntitlementSnapshot.IsExpired;
        EntitlementAddOnLabel = activeEntitlementSnapshot.HasUnlimitedAddressesAddOn
            ? "Unlimited Addresses"
            : EntitlementExpired
                ? "Free (Unlimited Addresses expired)"
                : "Free";

        var freeCap = activeEntitlementSnapshot.MaxNewAddressesPerTerritory
            ?? 30;
        EntitlementLimitLabel = activeEntitlementSnapshot.HasUnlimitedAddressesAddOn
            ? "Unlimited new addresses / territory"
            : $"{freeCap} new addresses / territory";

        EntitlementStatusMessage = activeEntitlementSnapshot.HasUnlimitedAddressesAddOn
            ? "Unlimited Addresses entitlement active"
            : EntitlementExpired
                ? "Unlimited Addresses entitlement expired. Free-tier cap is active."
                : "Free tier active.";

        OnPropertyChanged(nameof(HasUnlimitedAddressesAddOn));
        UpdateAccountLinkDisplay();
    }

    private void ApplyAccountLinkOperationResult(AccountLinkOperationResult result)
    {
        if (result.EntitlementSnapshot is not null)
        {
            ApplyEntitlementSnapshot(result.EntitlementSnapshot);
        }

        ApplyAccountLinkSnapshot(result.Snapshot);
        AccountLinkStatusMessage = result.Message;

        if (result.IsSuccess)
        {
            LastError = null;
            StatusMessage = result.Message;
            return;
        }

        LastError = result.Message;
        StatusMessage = result.Message;
    }

    private void ApplyAccountLinkSnapshot(AccountLinkSnapshot snapshot)
    {
        activeAccountLinkSnapshot = snapshot ?? AccountLinkSnapshot.CreateSignedOut();
        if (!string.IsNullOrWhiteSpace(activeAccountLinkSnapshot.Email))
        {
            AccountLinkEmail = activeAccountLinkSnapshot.Email;
        }

        UpdateAccountLinkDisplay();
    }

    private void UpdateAccountLinkDisplay()
    {
        AccountLinkStatusLabel = activeAccountLinkSnapshot.Status switch
        {
            AccountLinkStateStatus.AwaitingConfirmation => "Check email",
            AccountLinkStateStatus.Linked when activeEntitlementSnapshot.HasUnlimitedAddressesAddOn => "Unlimited Addresses linked",
            AccountLinkStateStatus.Linked => "Linked",
            AccountLinkStateStatus.PendingReview => "Pending review",
            AccountLinkStateStatus.Failed => "Action required",
            AccountLinkStateStatus.SignedIn => "Signed in",
            _ => "Not linked"
        };

        AccountLinkStatusMessage = activeAccountLinkSnapshot.Status switch
        {
            AccountLinkStateStatus.AwaitingConfirmation
                => "Finish sign-in from the emailed link, then refresh account link status.",
            AccountLinkStateStatus.Linked when !string.IsNullOrWhiteSpace(activeAccountLinkSnapshot.PurchaseSource)
                => $"Account is linked to {FormatPurchaseSource(activeAccountLinkSnapshot.PurchaseSource)} purchase state.",
            AccountLinkStateStatus.Linked
                => "Account link cache is present for this install.",
            AccountLinkStateStatus.PendingReview
                => "Account claim is pending manual review.",
            AccountLinkStateStatus.Failed when !string.IsNullOrWhiteSpace(activeAccountLinkSnapshot.LastError)
                => activeAccountLinkSnapshot.LastError,
            AccountLinkStateStatus.Failed
                => "Account link action failed. Retry or contact support.",
            AccountLinkStateStatus.SignedIn
                => "Email is linked to this install. No Store/direct purchase claim is linked yet.",
            _ => "No unified Store/direct account is linked for this install yet."
        };

        LinkedAccountIdentity = !string.IsNullOrWhiteSpace(activeAccountLinkSnapshot.Email)
            ? activeAccountLinkSnapshot.Email
            : !string.IsNullOrWhiteSpace(activeAccountLinkSnapshot.AccountId)
                ? activeAccountLinkSnapshot.AccountId
                : "Not linked";

        LinkedPurchaseSourceLabel = FormatPurchaseSource(activeAccountLinkSnapshot.PurchaseSource);
        LinkedAccountLastSyncLabel = activeAccountLinkSnapshot.LastSyncUtc.HasValue
            ? activeAccountLinkSnapshot.LastSyncUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : "Not yet synced";
        CanClearAccountLink = activeAccountLinkSnapshot.HasState;
        OnPropertyChanged(nameof(CanRefreshAccountLink));
        OnPropertyChanged(nameof(CanRestoreStorePurchase));
        OnPropertyChanged(nameof(HasStoreContinuityPrompt));
        OnPropertyChanged(nameof(StoreContinuityPromptTitle));
        OnPropertyChanged(nameof(StoreContinuityPromptMessage));
    }

    private bool HasVerifiedStoreContinuity(AccountLinkSnapshot snapshot)
    {
        return IsStoreInstall
            && activeEntitlementSnapshot.HasUnlimitedAddressesAddOn
            && snapshot.Status == AccountLinkStateStatus.Linked
            && string.Equals(snapshot.PurchaseSource, "store", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldPromptForStoreContinuityLink(AccountLinkSnapshot snapshot)
    {
        return IsStoreInstall
            && !HasVerifiedStoreContinuity(snapshot)
            && !snapshot.HasActiveSession;
    }

    private bool ShouldAutoRestoreStorePurchase(AccountLinkSnapshot snapshot)
    {
        return IsStoreInstall
            && !HasVerifiedStoreContinuity(snapshot)
            && snapshot.HasActiveSession
            && !string.Equals(snapshot.PurchaseSource, "store", StringComparison.OrdinalIgnoreCase);
    }

    private string GetStoreContinuityPromptTitle()
    {
        if (!HasStoreContinuityPrompt)
        {
            return string.Empty;
        }

        return activeAccountLinkSnapshot.Status switch
        {
            AccountLinkStateStatus.AwaitingConfirmation => "Complete email sign-in",
            AccountLinkStateStatus.PendingReview => "Store claim pending review",
            AccountLinkStateStatus.Failed => "Store claim needs attention",
            _ when activeAccountLinkSnapshot.HasActiveSession => "Finish Store purchase restore",
            _ => "Protect Store purchase continuity"
        };
    }

    private string GetStoreContinuityPromptMessage()
    {
        if (!HasStoreContinuityPrompt)
        {
            return string.Empty;
        }

        return activeAccountLinkSnapshot.Status switch
        {
            AccountLinkStateStatus.AwaitingConfirmation => "Use the emailed link to finish sign-in. After the session is active, NWS Helper will try to attach your Store purchase automatically.",
            AccountLinkStateStatus.PendingReview => "Your Store purchase claim was submitted and is waiting for manual review. Keep using the same email so this purchase can be recovered on a direct install later.",
            AccountLinkStateStatus.Failed => "The Store continuity check did not complete. Retry Link Email or Restore Store Purchase to capture this install.",
            _ when activeAccountLinkSnapshot.HasActiveSession => "This install is signed in, but the Store purchase is not linked yet. NWS Helper will try to restore it automatically when possible, and you can still use Restore Store Purchase manually if needed.",
            _ => "Link an email now so this Store purchase can later be recovered on a direct install without depending on this device alone."
        };
    }

    private async Task TryAutoRestoreStorePurchaseAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!ShouldAutoRestoreStorePurchase(snapshot))
        {
            return;
        }

        AccountLinkStatusMessage = "Checking Store purchase continuity...";
        StatusMessage = "Checking Store purchase continuity...";
        await RestoreStorePurchaseCoreAsync(cancellationToken);
    }

    private async Task RestoreStorePurchaseCoreAsync(CancellationToken cancellationToken)
    {
        var result = await accountLinkService.RestoreStorePurchaseAsync(cancellationToken);
        ApplyAccountLinkOperationResult(result);
    }

    private static string FormatPurchaseSource(string purchaseSource)
    {
        if (string.IsNullOrWhiteSpace(purchaseSource))
        {
            return "None";
        }

        return purchaseSource.Trim() switch
        {
            var value when value.Equals("store", StringComparison.OrdinalIgnoreCase) => "Store",
            var value when value.Equals("direct", StringComparison.OrdinalIgnoreCase) => "Direct",
            var value when value.Equals("manual-support", StringComparison.OrdinalIgnoreCase) => "Manual Support",
            _ => purchaseSource.Trim()
        };
    }

    private static AccountLinkSnapshot ToAccountLinkSnapshot(GuiAccountLinkSettings settings)
    {
        if (settings.Status == AccountLinkStateStatus.SignedOut &&
            string.IsNullOrWhiteSpace(settings.AccountId) &&
            string.IsNullOrWhiteSpace(settings.Email) &&
            string.IsNullOrWhiteSpace(settings.PurchaseSource) &&
            !settings.LinkedAtUtc.HasValue &&
            !settings.LastSyncUtc.HasValue &&
            string.IsNullOrWhiteSpace(settings.LastError))
        {
            return AccountLinkSnapshot.CreateSignedOut();
        }

        return new AccountLinkSnapshot
        {
            Status = settings.Status,
            AccountId = settings.AccountId ?? string.Empty,
            Email = settings.Email ?? string.Empty,
            PurchaseSource = settings.PurchaseSource ?? string.Empty,
            LinkedAtUtc = settings.LinkedAtUtc,
            LastSyncUtc = settings.LastSyncUtc,
            LastError = settings.LastError ?? string.Empty
        };
    }

    private void UpdateCappedOutputMessage(ExtractionResult result)
    {
        if (result.TotalCappedNewAddresses <= 0 || !result.AppliedMaxNewAddressesPerTerritory.HasValue)
        {
            CappedOutputMessage = string.Empty;
            return;
        }

        CappedOutputMessage =
            $"Entitlement cap applied: {result.TotalCappedNewAddresses} new addresses were omitted at {result.AppliedMaxNewAddressesPerTerritory.Value} per territory. Existing rows and existing-row updates are still included, so total written rows per territory can exceed this cap.";
    }

    private ExtractionRequest BuildExtractionRequest()
    {
        var smartFillEnabled = TryResolveSmartFillMode(SelectedSmartFillMode, out var smartFillMode);

        return new ExtractionRequest
        {
            BoundaryCsvPath = BoundaryCsvPath,
            ExistingAddressesCsvPath = string.IsNullOrWhiteSpace(ExistingAddressesCsvPath) ? null : ExistingAddressesCsvPath,
            DatasetRootPath = DatasetRootPath,
            StatesFilterCsv = StatesFilter,
            ConsolidatedOutputPath = string.IsNullOrWhiteSpace(ConsolidatedOutputPath)
                ? null
                : NormalizeAlternativeOutputFolder(ConsolidatedOutputPath),
            OutputSplitRows = OutputSplitRows,
            WarningThreshold = WarningThreshold,
            WhatIf = WhatIf,
            ListThresholdExceeding = ListThresholdExceeding,
            OutputDespiteThreshold = OutputDespiteThreshold,
            OutputExistingNoneNew = OutputExistingNoneNew,
            GroupByCategory = GroupByCategory,
            NoneNewInConsolidated = NoneNewInConsolidated,
            OutputAllRows = OutputAllRows,
            ExcludeNormalizedRows = ExcludeNormalizedRows,
            OverwriteExistingLatLong = OverwriteExistingLatLong,
            OnlyMatchSingleState = OnlyMatchSingleState,
            OnlyMatchSingleCounty = OnlyMatchSingleCounty,
            PreserveRawState = PreserveRawState,
            PreserveRawStreet = PreserveRawStreet,
            SmartFillApartmentUnits = smartFillEnabled,
            SmartFillApartmentUnitsMode = smartFillMode,
            PerTerritoryOutput = PerTerritoryOutput,
            PerTerritoryDirectory = null,
            SmartSelect = SmartSelect,
            SelectAll = SelectAll,
            NoPrompt = NoPrompt,
            ForceWithoutAddressInput = ForceWithoutAddressInput,
            EntitlementContext = activeEntitlementSnapshot.ToCoreContext()
        };
    }

    private bool TryValidateSetupInputs(out string validationError)
    {
        validationError = string.Empty;

        if (string.IsNullOrWhiteSpace(BoundaryCsvPath))
        {
            validationError = "Boundary CSV is required.";
            return false;
        }

        var boundaryPath = Path.GetFullPath(BoundaryCsvPath);
        if (!File.Exists(boundaryPath))
        {
            validationError = $"Boundary CSV not found: {boundaryPath}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ExistingAddressesCsvPath))
        {
            var existingPath = Path.GetFullPath(ExistingAddressesCsvPath);
            if (!File.Exists(existingPath))
            {
                validationError = $"Existing addresses CSV not found: {existingPath}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(DatasetRootPath))
        {
            validationError = "Dataset root is required.";
            return false;
        }

        var datasetRoot = Path.GetFullPath(DatasetRootPath);
        if (!Directory.Exists(datasetRoot))
        {
            validationError = $"Dataset root not found: {datasetRoot}";
            return false;
        }

        if (OutputSplitRows < 0)
        {
            validationError = "Output split rows must be 0 or greater.";
            return false;
        }

        if (IsSmartFillModeEnabled(SelectedSmartFillMode)
            && !Enum.TryParse<SmartFillApartmentUnitsMode>(SelectedSmartFillMode, ignoreCase: true, out _))
        {
            validationError = "Invalid smart-fill mode.";
            return false;
        }

        return true;
    }

    private int GetSelectedTerritoriesMissingPreExistingCount(IReadOnlyCollection<string> selectedTerritoryIds)
    {
        var selectedSet = new HashSet<string>(selectedTerritoryIds, StringComparer.OrdinalIgnoreCase);

        if (lastPreviewPlan is not null)
        {
            return lastPreviewPlan.Items.Count(item =>
                selectedSet.Contains(item.TerritoryId) &&
                !item.HasPreExistingAddresses);
        }

        return TerritoryPreviewRows.Count(row => selectedSet.Contains(row.TerritoryId) && row.ExistingCount <= 0);
    }

    private static IReadOnlyList<string> BuildSmartFillModeOptions()
    {
        var options = new List<string> { SmartFillNoneOption };
        options.AddRange(Enum.GetNames<SmartFillApartmentUnitsMode>());
        return options;
    }

    private static bool IsSmartFillModeEnabled(string? selectedMode)
    {
        return !string.IsNullOrWhiteSpace(selectedMode)
            && !string.Equals(selectedMode, SmartFillNoneOption, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveSmartFillMode(string? selectedMode, out SmartFillApartmentUnitsMode mode)
    {
        if (IsSmartFillModeEnabled(selectedMode)
            && Enum.TryParse<SmartFillApartmentUnitsMode>(selectedMode, ignoreCase: true, out var parsedMode))
        {
            mode = parsedMode;
            return true;
        }

        mode = SmartFillApartmentUnitsMode.TypeApartmentOnly;
        return false;
    }

    private static string NormalizeAlternativeOutputFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (string.Equals(Path.GetExtension(trimmed), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(trimmed);
            return string.IsNullOrWhiteSpace(directory)
                ? "."
                : directory;
        }

        return trimmed;
    }

    private static IReadOnlyCollection<string> ParseSelectedDatasetSources(string? selectedDatasetSourcesCsv)
    {
        if (string.IsNullOrWhiteSpace(selectedDatasetSourcesCsv))
        {
            return [];
        }

        return selectedDatasetSourcesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ApplyThresholdExceedingListing(ExtractionResult result, int warningThreshold)
    {
        UpdateCappedOutputMessage(result);

        var effectiveThreshold = warningThreshold <= 0 ? 350 : warningThreshold;
        var evaluatedTotals = result.TerritoryAddressCountsEvaluated.Count > 0
            ? result.TerritoryAddressCountsEvaluated
            : result.TerritoryAddressCounts;

        var evaluatedNew = result.TerritoryNewAddressCountsEvaluated.Count > 0
            ? result.TerritoryNewAddressCountsEvaluated
            : result.TerritoryNewAddressCounts;

        TerritoryResults.Clear();
        OutputArtifacts.Clear();

        var exceeding = evaluatedTotals
            .Where(kvp => kvp.Value > effectiveThreshold)
            .OrderBy(kvp => result.TerritoryDisplayNames.TryGetValue(kvp.Key, out var label) ? label : kvp.Key, TerritoryLabelComparer)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (territoryId, total) in exceeding)
        {
            var label = result.TerritoryDisplayNames.TryGetValue(territoryId, out var display) ? display : territoryId;
            var newCount = evaluatedNew.TryGetValue(territoryId, out var added) ? added : 0;
            var existing = Math.Max(0, total - newCount);
            var found = result.TerritoryFoundAddressCounts.TryGetValue(territoryId, out var foundCount) ? foundCount : total;
            var distinct = result.TerritoryDistinctAddressCounts.TryGetValue(territoryId, out var distinctCount) ? distinctCount : total;

            TerritoryResults.Add(new TerritoryResultItemViewModel(
                label,
                existing,
                found,
                distinct,
                newCount,
                total,
                "!",
                "N",
                "N",
                "N",
                "N",
                string.Empty));
        }

        ExistingTotal = result.PreExistingAddressesCount;
        FoundTotal = result.MatchedAddresses;
        DistinctTotal = result.DistinctMatchedAddresses;
        NewCount = result.NewAddressesCount;
        WrittenTotal = 0;
        PlannedTotal = result.TotalOutputRows;
        SkippedNoneNewTotal = 0;
        SkippedThresholdTotal = TerritoryResults.Count;
        WarningCount = TerritoryResults.Count;
        CoordinateBackfillCount = result.CoordinatesBackfilled;
        CoordinateOverwriteCount = result.CoordinatesOverwritten;

        StatusMessage = TerritoryResults.Count == 0
            ? $"No territories exceed the warning threshold (> {effectiveThreshold})."
            : $"Territories exceeding the warning threshold (> {effectiveThreshold}): {TerritoryResults.Count}";
    }

    private void LoadPreviewRows(TerritoryExtractionPlan plan, bool applyDefaultSelection)
    {
        foreach (var row in TerritoryPreviewRows)
        {
            row.PropertyChanged -= OnPreviewRowPropertyChanged;
        }

        TerritoryPreviewRows.Clear();

        var items = plan.Items
            .OrderBy(item => item.Label, TerritoryLabelComparer)
            .ThenBy(item => item.TerritoryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var item in items)
        {
            var isSelected = applyDefaultSelection
                ? SelectAll || ((SmartSelect || NoPrompt) && !item.HasWarning)
                : false;

            var row = new TerritoryPreviewItemViewModel(
                item.TerritoryId,
                item.Label,
                item.PreExistingCount,
                item.AddedCount,
                item.TotalCount,
                item.HasWarning,
                item.ProposedPerTerritoryPath ?? string.Empty,
                isSelected);

            row.PropertyChanged += OnPreviewRowPropertyChanged;
            TerritoryPreviewRows.Add(row);
        }

        OnPropertyChanged(nameof(TotalPreviewCount));
        RecalculateSelectedTerritories();
    }

    private void InitializeProgressLanes()
    {
        ProgressLanes.Clear();
        ProgressLanes.Add(new ProgressLaneViewModel("Loading territories", 0, 0, 0));
        ProgressLanes.Add(new ProgressLaneViewModel("Loading pre-existing addresses", 0, 0, 0));
        ProgressLanes.Add(new ProgressLaneViewModel("Streaming & matching addresses", 0, 0, 0));
        ProgressLanes.Add(new ProgressLaneViewModel("Writing output files", 0, 0, 0));
        ProgressLanes.Add(new ProgressLaneViewModel("Writing per-territory files", 0, 0, 0));

        Elapsed = "00:00:00";
        ProcessedCount = 0;
        MatchedCount = 0;
        NewCount = 0;
    }

    private void UpdateRunProgress(ExtractionProgressSnapshot snapshot)
    {
        if (ProgressLanes.Count < 5)
        {
            return;
        }

        ProgressLanes[0].PercentComplete = CalculatePercent(snapshot.TerritoryCount, snapshot.TerritoryTotal);
        ProgressLanes[0].ProcessedCount = snapshot.TerritoryCount;

        ProgressLanes[1].PercentComplete = CalculatePercent(snapshot.PreExistingCount, snapshot.PreExistingTotal);
        ProgressLanes[1].ProcessedCount = snapshot.PreExistingCount;

        var streamState = snapshot.StreamState;
        if (streamState is not null)
        {
            ProgressLanes[2].PercentComplete = CalculatePercent(streamState.ProcessedRows, snapshot.StreamingTotal ?? streamState.TotalExpectedRows);
            ProgressLanes[2].ProcessedCount = streamState.ProcessedRows;
            ProgressLanes[2].MatchedCount = streamState.MatchedRows;

            ProcessedCount = streamState.ProcessedRows;
            MatchedCount = streamState.MatchedRows;
            NewCount = streamState.NewRows;
            Elapsed = streamState.Elapsed.ToString(@"hh\:mm\:ss");
        }

        ProgressLanes[3].PercentComplete = CalculatePercent(snapshot.OutputRows, snapshot.OutputTotalRows);
        ProgressLanes[3].ProcessedCount = snapshot.OutputRows;
        ProgressLanes[3].MatchedCount = snapshot.OutputFiles;

        ProgressLanes[4].PercentComplete = CalculatePercent(snapshot.PerTerritoryRows, snapshot.PerTerritoryTotalRows);
        ProgressLanes[4].ProcessedCount = snapshot.PerTerritoryRows;
        ProgressLanes[4].MatchedCount = snapshot.PerTerritoryFiles;
    }

    private static double CalculatePercent(int value, int? total)
    {
        if (!total.HasValue || total.Value <= 0)
        {
            return value > 0 ? 100 : 0;
        }

        return Math.Min(100, Math.Max(0, (value / (double)total.Value) * 100));
    }

    private void ApplyExtractionResult(ExtractionResult result)
    {
        UpdateCappedOutputMessage(result);

        ResultsTabIndex = result.WasWhatIf && WhatIf ? 1 : 0;

        ExistingTotal = result.PreExistingAddressesCount;
        FoundTotal = result.MatchedAddresses;
        DistinctTotal = result.DistinctMatchedAddresses;
        NewCount = result.NewAddressesCount;
        WrittenTotal = result.TotalOutputRows;
        PlannedTotal = result.TotalOutputRows;
        CoordinateBackfillCount = result.CoordinatesBackfilled;
        CoordinateOverwriteCount = result.CoordinatesOverwritten;

        var effectiveThreshold = WarningThreshold <= 0 ? 350 : WarningThreshold;
        var territoryIds = result.TerritoryDisplayNames.Keys
            .Concat(result.TerritoryAddressCounts.Keys)
            .Concat(result.TerritoryOutputFiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => result.TerritoryDisplayNames.TryGetValue(id, out var display) ? display : id, TerritoryLabelComparer)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WarningCount = territoryIds.Count(id => HasWarning(result, id, effectiveThreshold));
        SkippedThresholdTotal = WarningCount;
        SkippedNoneNewTotal = territoryIds.Count(id =>
        {
            var added = result.TerritoryNewAddressCounts.TryGetValue(id, out var addedCount)
                ? addedCount
                : result.TerritoryNewAddressCountsEvaluated.TryGetValue(id, out var evaluatedAdded) ? evaluatedAdded : 0;
            return added == 0 && !result.TerritoryOutputFiles.ContainsKey(id);
        });

        TerritoryResults.Clear();
        foreach (var id in territoryIds)
        {
            var label = result.TerritoryDisplayNames.TryGetValue(id, out var display) ? display : id;
            var existing = result.TerritoryExistingAddressCounts.TryGetValue(id, out var existingCount) ? existingCount : 0;
            var found = result.TerritoryFoundAddressCounts.TryGetValue(id, out var foundCount) ? foundCount : 0;
            var distinct = result.TerritoryDistinctAddressCounts.TryGetValue(id, out var distinctCount) ? distinctCount : 0;
            var added = result.TerritoryNewAddressCounts.TryGetValue(id, out var addedCount) ? addedCount : 0;
            var total = result.TerritoryAddressCounts.TryGetValue(id, out var totalCount) ? totalCount : existing + added;
            var hasWarning = HasWarning(result, id, effectiveThreshold);

            var hasOutput = result.TerritoryOutputFiles.TryGetValue(id, out var outputPath);
            var hasConsolidatedOutput = result.OutputFilePaths.Count > 0 || !string.IsNullOrWhiteSpace(result.OutputFilePath);
            var isNoneNewTerritory = result.NoneNewTerritoryIds.Contains(id);
            var consolidatedOnly = !hasOutput && hasConsolidatedOutput && (!isNoneNewTerritory || NoneNewInConsolidated);
            var thresholdSuppressed = !OutputDespiteThreshold && !result.WasWhatIf && !hasOutput && hasWarning;
            var writtenCount = thresholdSuppressed ? 0 : total;

            TerritoryResults.Add(new TerritoryResultItemViewModel(
                label,
                existing,
                found,
                distinct,
                added,
                writtenCount,
                hasWarning ? "!" : "-",
                result.TerritoryStatePostalBackfilled.TryGetValue(id, out var addfill) && addfill > 0 ? "Y" : "N",
                result.TerritoryCoordinatesBackfilled.TryGetValue(id, out var geofill) && geofill > 0 ? "Y" : "N",
                result.TerritoryCoordinatesOverwritten.TryGetValue(id, out var overwritten) && overwritten > 0 ? "Y" : "N",
                consolidatedOnly ? "Y" : "N",
                hasOutput ? outputPath ?? string.Empty : string.Empty));
        }

        OutputArtifacts.Clear();

        if (result.OutputFilePaths.Count > 0)
        {
            for (var i = 0; i < result.OutputFilePaths.Count; i++)
            {
                var name = result.OutputFilePaths.Count == 1 ? "Consolidated CSV" : $"Split Output {i + 1}";
                OutputArtifacts.Add(new OutputArtifactItemViewModel("Consolidated", name, result.OutputFilePaths[i]));
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.OutputFilePath))
        {
            OutputArtifacts.Add(new OutputArtifactItemViewModel("Consolidated", "Consolidated CSV", result.OutputFilePath));
        }

        if (!string.IsNullOrWhiteSpace(result.NoneNewOutputFilePath))
        {
            OutputArtifacts.Add(new OutputArtifactItemViewModel("None-New", "None-New CSV", result.NoneNewOutputFilePath));
        }

        foreach (var categoryFile in result.CategoryOutputFiles.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            OutputArtifacts.Add(new OutputArtifactItemViewModel("Category", Path.GetFileName(categoryFile.Value), categoryFile.Value));
        }

        foreach (var territoryFile in result.TerritoryOutputFiles.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = result.TerritoryDisplayNames.TryGetValue(territoryFile.Key, out var display) ? display : territoryFile.Key;
            OutputArtifacts.Add(new OutputArtifactItemViewModel("Per-Territory", label, territoryFile.Value));
        }

        var thresholdSuppressionMessage = (!OutputDespiteThreshold && !result.WasWhatIf && SkippedThresholdTotal > 0)
            ? $" Per-territory outputs suppressed for {SkippedThresholdTotal} warning territories."
            : string.Empty;

        StartMapTilePreload();

        StatusMessage = result.WasWhatIf
            ? $"Extraction complete (what-if). No files were written.{thresholdSuppressionMessage}"
            : $"Extraction completed.{thresholdSuppressionMessage}";
    }

    private void StartMapTilePreload()
    {
        var outputPaths = OutputArtifacts
            .Where(artifact => artifact.HasPath)
            .Select(artifact => artifact.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (outputPaths.Length == 0)
        {
            return;
        }

        _ = outputPathActions.PreloadMapTilesAsync(outputPaths, BoundaryCsvPath);
    }

    private static bool HasWarning(ExtractionResult result, string territoryId, int threshold)
    {
        var total = result.TerritoryAddressCounts.TryGetValue(territoryId, out var totalCount) ? totalCount : 0;
        var label = result.TerritoryDisplayNames.TryGetValue(territoryId, out var display) ? display : territoryId;
        var hasThresholdWarning = total >= threshold;
        var hasTextWarning = result.Warnings.Any(w =>
            w.IndexOf(territoryId, StringComparison.OrdinalIgnoreCase) >= 0 ||
            w.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
        return hasThresholdWarning || hasTextWarning;
    }

    private static int CompareTerritoryLabels(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? 0 : -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        var (leftCore, leftIdSuffix) = SplitTerritoryLabelParts(left);
        var (rightCore, rightIdSuffix) = SplitTerritoryLabelParts(right);

        var coreComparison = CompareDashSeparatedSegments(leftCore, rightCore);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        var idComparison = CompareNaturalChunks(leftIdSuffix, rightIdSuffix);
        if (idComparison != 0)
        {
            return idComparison;
        }

        var insensitiveComparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        if (insensitiveComparison != 0)
        {
            return insensitiveComparison;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static (string CoreLabel, string IdSuffix) SplitTerritoryLabelParts(string label)
    {
        var markerIndex = label.IndexOf(" [ID:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return (label.Trim(), string.Empty);
        }

        var core = label[..markerIndex].Trim();
        var idStart = markerIndex + " [ID:".Length;
        var idSuffix = idStart <= label.Length
            ? label[idStart..].TrimEnd(']').Trim()
            : string.Empty;

        return (core, idSuffix);
    }

    private static int CompareDashSeparatedSegments(string left, string right)
    {
        var leftSegments = left.Split('-', StringSplitOptions.TrimEntries);
        var rightSegments = right.Split('-', StringSplitOptions.TrimEntries);
        var max = Math.Min(leftSegments.Length, rightSegments.Length);

        for (var i = 0; i < max; i++)
        {
            var comparison = CompareNaturalChunks(leftSegments[i], rightSegments[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftSegments.Length.CompareTo(rightSegments.Length);
    }

    private static int CompareNaturalChunks(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsDigit(left[leftIndex]);
            var rightIsDigit = char.IsDigit(right[rightIndex]);

            if (leftIsDigit && rightIsDigit)
            {
                var leftDigitStart = leftIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                {
                    leftIndex++;
                }

                var rightDigitStart = rightIndex;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                {
                    rightIndex++;
                }

                var digitComparison = CompareDigitChunks(
                    left[leftDigitStart..leftIndex],
                    right[rightDigitStart..rightIndex]);

                if (digitComparison != 0)
                {
                    return digitComparison;
                }

                continue;
            }

            if (!leftIsDigit && !rightIsDigit)
            {
                var leftTextStart = leftIndex;
                while (leftIndex < left.Length && !char.IsDigit(left[leftIndex]))
                {
                    leftIndex++;
                }

                var rightTextStart = rightIndex;
                while (rightIndex < right.Length && !char.IsDigit(right[rightIndex]))
                {
                    rightIndex++;
                }

                var textComparison = string.Compare(
                    left[leftTextStart..leftIndex],
                    right[rightTextStart..rightIndex],
                    StringComparison.OrdinalIgnoreCase);

                if (textComparison != 0)
                {
                    return textComparison;
                }

                continue;
            }

            var characterComparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterComparison != 0)
            {
                return characterComparison;
            }

            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareDigitChunks(string leftDigits, string rightDigits)
    {
        var leftTrimmed = leftDigits.TrimStart('0');
        var rightTrimmed = rightDigits.TrimStart('0');

        if (leftTrimmed.Length == 0)
        {
            leftTrimmed = "0";
        }

        if (rightTrimmed.Length == 0)
        {
            rightTrimmed = "0";
        }

        var lengthComparison = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
        if (lengthComparison != 0)
        {
            return lengthComparison;
        }

        var valueComparison = string.Compare(leftTrimmed, rightTrimmed, StringComparison.Ordinal);
        if (valueComparison != 0)
        {
            return valueComparison;
        }

        return leftDigits.Length.CompareTo(rightDigits.Length);
    }
}

