using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using Xunit;

namespace NWSHelper.Tests;

public class StoreAddOnCatalogViewModelTests
{
    [Fact]
    public async Task RefreshStoreAddOnCatalog_LoadsAvailableOffersForStoreInstall()
    {
        var catalogService = new FakeStoreAddOnCatalogService
        {
            CatalogResult = StoreAddOnCatalogResult.CreateAvailable(
            [
                new StoreAddOnOffer
                {
                    StoreId = "9TEST0000001",
                    InAppOfferToken = "unlimited_addresses",
                    Title = "Unlimited Addresses",
                    Description = "Remove the new-address cap.",
                    PriceText = "$19.99",
                    IsOwned = false
                }
            ],
            "1 Microsoft Store add-on is available for this install.")
        };

        var viewModel = CreateViewModel(
            updateService: new FakeUpdateService { IsStoreInstall = true },
            storeAddOnCatalogService: catalogService);

        await viewModel.RefreshStoreAddOnCatalogCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasStoreAddOnOffers);
        Assert.Single(viewModel.StoreAddOnOffers);
        Assert.Equal("Unlimited Addresses", viewModel.StoreAddOnOffers[0].Title);
        Assert.Equal("$19.99", viewModel.StoreAddOnOffers[0].PriceLabel);
        Assert.Equal("1 Microsoft Store add-on is available for this install.", viewModel.StoreAddOnCatalogMessage);
        Assert.Equal(1, catalogService.GetCatalogCalls);
    }

    [Fact]
    public async Task PurchaseStoreAddOn_WithLinkedStoreSession_RunsRestoreFlow()
    {
        var catalogService = new FakeStoreAddOnCatalogService
        {
            CatalogResult = StoreAddOnCatalogResult.CreateAvailable(
            [
                new StoreAddOnOffer
                {
                    StoreId = "9TEST0000001",
                    InAppOfferToken = "unlimited_addresses",
                    Title = "Unlimited Addresses",
                    Description = "Remove the new-address cap.",
                    PriceText = "$19.99",
                    IsOwned = false
                }
            ],
            "1 Microsoft Store add-on is available for this install."),
            PurchaseResult = StoreAddOnPurchaseResult.CreateSuccess("Purchased Unlimited Addresses from Microsoft Store.")
        };

        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = new AccountLinkSnapshot
            {
                Status = AccountLinkStateStatus.SignedIn,
                AccountId = "acct_store",
                Email = "store@example.com",
                LastSyncUtc = DateTimeOffset.UtcNow
            },
            RestoreStorePurchaseResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Store purchase linked.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LastSyncUtc = DateTimeOffset.UtcNow
                },
                EntitlementSnapshot = new EntitlementSnapshot
                {
                    BasePlanCode = EntitlementProductCodes.FreeBasePlan,
                    AddOnCodes = [EntitlementProductCodes.UnlimitedAddressesAddOn],
                    MaxNewAddressesPerTerritory = null,
                    LastValidatedUtc = DateTimeOffset.UtcNow,
                    ValidationSource = "Online"
                }
            }
        };

        var viewModel = CreateViewModel(
            accountLinkService: accountLinkService,
            updateService: new FakeUpdateService { IsStoreInstall = true },
            storeAddOnCatalogService: catalogService);

        await viewModel.RefreshStoreAddOnCatalogCommand.ExecuteAsync(null);
        await viewModel.PurchaseStoreAddOnCommand.ExecuteAsync(viewModel.StoreAddOnOffers[0]);

        Assert.Equal(1, catalogService.PurchaseCalls);
        Assert.Equal(1, accountLinkService.RestoreStorePurchaseCalls);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.Equal("Store purchase linked.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task PurchaseStoreAddOn_WithoutLinkedSession_UnlocksLocallyFromVerifiedStoreOwnership()
    {
        var catalogService = new FakeStoreAddOnCatalogService
        {
            CatalogResult = StoreAddOnCatalogResult.CreateAvailable(
            [
                new StoreAddOnOffer
                {
                    StoreId = "9TEST0000001",
                    InAppOfferToken = "unlimited_addresses",
                    Title = "Unlimited Addresses",
                    Description = "Remove the new-address cap.",
                    PriceText = "$19.99",
                    IsOwned = false
                }
            ],
            "1 Microsoft Store add-on is available for this install."),
            PurchaseResult = StoreAddOnPurchaseResult.CreateSuccess("Purchased Unlimited Addresses from Microsoft Store.")
        };

        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        var viewModel = CreateViewModel(
            accountLinkService: accountLinkService,
            updateService: new FakeUpdateService { IsStoreInstall = true },
            storeAddOnCatalogService: catalogService,
            storeOwnershipVerifier: FakeStoreOwnershipVerifier.VerifiedOwned());

        await viewModel.RefreshStoreAddOnCatalogCommand.ExecuteAsync(null);
        await viewModel.PurchaseStoreAddOnCommand.ExecuteAsync(viewModel.StoreAddOnOffers[0]);

        Assert.Equal(1, catalogService.PurchaseCalls);
        Assert.Equal(0, accountLinkService.RestoreStorePurchaseCalls);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.Contains("Link an email", viewModel.AccountLinkStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowseUnlimitedAddressesAddOn_WhenCapApplies_OpensSettingsAndLoadsStoreOffers()
    {
        var catalogService = new FakeStoreAddOnCatalogService
        {
            CatalogResult = StoreAddOnCatalogResult.CreateAvailable(
            [
                new StoreAddOnOffer
                {
                    StoreId = "9TEST0000001",
                    InAppOfferToken = "unlimited_addresses",
                    Title = "Unlimited Addresses",
                    Description = "Remove the new-address cap.",
                    PriceText = "$19.99",
                    IsOwned = false
                }
            ],
            "1 Microsoft Store add-on is available for this install.")
        };

        var viewModel = CreateViewModel(
            updateService: new FakeUpdateService { IsStoreInstall = true },
            storeAddOnCatalogService: catalogService);
        viewModel.CurrentStage = WorkflowStage.Preview;
        viewModel.CappedOutputMessage = "Entitlement cap applied.";

        Assert.True(viewModel.ShowUnlimitedAddressesResultCallToAction);
        Assert.Equal("View Available Add-ons", viewModel.UnlimitedAddressesActionButtonLabel);
        Assert.False(viewModel.HasUnlimitedAddressesActionCaption);

        await viewModel.BrowseUnlimitedAddressesAddOnCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowStage.Settings, viewModel.CurrentStage);
        Assert.True(viewModel.HasStoreAddOnOffers);
        Assert.Equal(1, catalogService.GetCatalogCalls);
    }

    [Fact]
    public async Task BrowseUnlimitedAddressesAddOn_ForDirectDownload_OpensStoreListingInsteadOfSettings()
    {
        var catalogService = new FakeStoreAddOnCatalogService();
        var outputPathActions = new FakeOutputPathActions();
        var viewModel = CreateViewModel(
            updateService: new FakeUpdateService { IsStoreInstall = false },
            storeAddOnCatalogService: catalogService,
            outputPathActions: outputPathActions,
            storeListingOptions: new StoreListingOptions
            {
                AppProductId = "9TESTAPP0001",
                WebUrl = "https://apps.microsoft.com/detail/9TESTAPP0001"
            });
        viewModel.CurrentStage = WorkflowStage.Preview;
        viewModel.CappedOutputMessage = "Entitlement cap applied.";

        Assert.False(viewModel.CanBrowseUnlimitedAddressesInStore);
        Assert.True(viewModel.ShowUnlimitedAddressesResultCallToAction);
        Assert.True(viewModel.ShowUnlimitedAddressesSettingsCallToAction);
        Assert.Equal("Download in Store", viewModel.UnlimitedAddressesActionButtonLabel);
        Assert.Equal("View in Store (web)", viewModel.UnlimitedAddressesWebActionButtonLabel);
        Assert.True(viewModel.ShowUnlimitedAddressesWebActionButton);
        Assert.True(viewModel.HasUnlimitedAddressesActionCaption);
        Assert.Equal("View add-ons in Store version", viewModel.UnlimitedAddressesActionCaption);

        await viewModel.BrowseUnlimitedAddressesAddOnCommand.ExecuteAsync(null);

        Assert.Equal(WorkflowStage.Preview, viewModel.CurrentStage);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9TESTAPP0001", outputPathActions.LastOpenedUrl);
        Assert.Equal("Install from Microsoft Store to browse and purchase add-ons in-app.", viewModel.StoreAddOnCatalogMessage);
        Assert.Equal("Opened Microsoft Store app listing for the Store version.", viewModel.StatusMessage);
        Assert.Equal(0, catalogService.GetCatalogCalls);
    }

    [Fact]
    public async Task OpenStoreVersionInWeb_ForDirectDownload_OpensWebListingByProductId()
    {
        var outputPathActions = new FakeOutputPathActions();
        var viewModel = CreateViewModel(
            updateService: new FakeUpdateService { IsStoreInstall = false },
            outputPathActions: outputPathActions,
            storeListingOptions: new StoreListingOptions
            {
                AppProductId = "9TESTAPP0001"
            });

        await viewModel.OpenStoreVersionInWebCommand.ExecuteAsync(null);

        Assert.Equal("https://apps.microsoft.com/detail/9TESTAPP0001", outputPathActions.LastOpenedUrl);
        Assert.Equal("Opened Microsoft Store web listing for the Store version.", viewModel.StatusMessage);
    }

    [Fact]
    public void StoreListingOptions_UsesProductIdFromWebUrlForStoreAppLaunch()
    {
        var options = new StoreListingOptions
        {
            WebUrl = "https://apps.microsoft.com/detail/9TESTAPP0001"
        };

        Assert.Equal("9TESTAPP0001", options.ResolvedAppProductId);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9TESTAPP0001", options.BuildStoreAppUrl());
        Assert.Equal("https://apps.microsoft.com/detail/9TESTAPP0001", options.BuildWebUrl());
    }

    [Fact]
    public void StoreListingOptions_UsesAssemblyMetadataProductIdByDefault()
    {
        var options = new StoreListingOptions();

        Assert.Equal("9ppkpp8r8865", options.ResolvedAppProductId);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9ppkpp8r8865", options.BuildStoreAppUrl());
        Assert.Equal("https://apps.microsoft.com/detail/9ppkpp8r8865", options.BuildWebUrl());
    }

    [Fact]
    public void ResultsCallToAction_RemainsVisibleWhenCurrentEntitlementIsActiveButOutputWasCapped()
    {
        var entitlementService = new FakeEntitlementService
        {
            Snapshot = new EntitlementSnapshot
            {
                BasePlanCode = EntitlementProductCodes.FreeBasePlan,
                AddOnCodes = [EntitlementProductCodes.UnlimitedAddressesAddOn],
                MaxNewAddressesPerTerritory = null,
                LastValidatedUtc = DateTimeOffset.UtcNow,
                ValidationSource = "Test"
            }
        };

        var viewModel = CreateViewModel(
            entitlementService: entitlementService,
            updateService: new FakeUpdateService { IsStoreInstall = true });
        viewModel.CappedOutputMessage = "Entitlement cap applied.";

        Assert.False(viewModel.CanBrowseUnlimitedAddressesInStore);
        Assert.True(viewModel.ShowUnlimitedAddressesResultCallToAction);
        Assert.False(viewModel.ShowUnlimitedAddressesSettingsCallToAction);
    }

    [Fact]
    public void StoreRuntimeDiagnostics_ReflectPersistedDeveloperOverride()
    {
        var provider = new FakeStoreRuntimeContextProvider(new StoreRuntimeContext
        {
            IsPackaged = true,
            IsStoreInstall = true,
            ProofAuthority = StoreProofAuthority.Heuristic,
            DetectionSource = "settings-override",
            PackageFamilyName = StoreRuntimeContextProvider.DefaultUiTestPackageFamilyName
        });

        var viewModel = CreateViewModel(
            updateService: new FakeUpdateService { IsStoreInstall = false },
            storeRuntimeContextProvider: provider);

        Assert.True(viewModel.IsStoreInstall);
        Assert.Equal("Store", viewModel.StoreRuntimeChannelLabel);
        Assert.Contains("Developer Settings override", viewModel.StoreRuntimeDiagnosticsMessage, StringComparison.Ordinal);
        Assert.Contains(StoreRuntimeContextProvider.DefaultUiTestPackageFamilyName, viewModel.StoreRuntimeDiagnosticsMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicGuiMarkup_ContainsStoreAddOnCatalogBindings()
    {
        var previewMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "PreviewStageView.axaml"));
        var resultsMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "ResultsStageView.axaml"));
        var settingsMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml"));
        var appBootstrap = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "App.axaml.cs"));
        var viewModelCode = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "ViewModels", "MainWindowViewModel.cs"));
        var storeListingCode = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Services", "StoreListingOptions.cs"));
        var storeRuntimeCode = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Services", "StoreRuntimeContext.cs"));

        Assert.Contains("ShowUnlimitedAddressesResultCallToAction", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("BrowseUnlimitedAddressesAddOnCommand", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionInWebCommand", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionButtonLabel", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionButtonLabel", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionToolTip", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionToolTip", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionCaption", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"🔓\"", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesCallToActionMessage", previewMarkup, StringComparison.Ordinal);
        Assert.Contains("ShowUnlimitedAddressesResultCallToAction", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("BrowseUnlimitedAddressesAddOnCommand", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionInWebCommand", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionButtonLabel", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionButtonLabel", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionToolTip", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionToolTip", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionCaption", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"🔓\"", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesCallToActionMessage", resultsMarkup, StringComparison.Ordinal);
        Assert.Contains("Microsoft Store Add-Ons", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("ShowUnlimitedAddressesSettingsCallToAction", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("BrowseUnlimitedAddressesAddOnCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionInWebCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionButtonLabel", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionButtonLabel", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionToolTip", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesWebActionToolTip", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesActionCaption", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("Text=\"🔓\"", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("UnlimitedAddressesSettingsCallToActionMessage", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("RefreshStoreAddOnCatalogCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("PurchaseStoreAddOnCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreAddOnOffers", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("ShowStoreAddOnRefreshControls", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreAddOnCatalogSectionDescription", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreRuntimeChannelLabel", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreRuntimeDiagnosticsMessage", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("SimulateStoreInstallForUiTesting", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreAddOnCatalogServiceFactory.CreateDefault", appBootstrap, StringComparison.Ordinal);
        Assert.Contains("PurchaseStoreAddOnAsync", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("BrowseUnlimitedAddressesAddOnAsync", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionInStoreAppAsync", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionInWebAsync", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("OpenStoreVersionListingAsync", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("Download in Store", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("View in Store (web)", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("View add-ons in Store version", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("ShowUnlimitedAddressesResultCallToAction", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("BuildStoreRuntimeDiagnosticsMessage", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("NWSHELPER_STORE_APP_PRODUCT_ID", storeListingCode, StringComparison.Ordinal);
        Assert.Contains("NWSHelperStoreAppProductId", storeListingCode, StringComparison.Ordinal);
        Assert.Contains("ResolvedAppProductId", storeListingCode, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=", storeListingCode, StringComparison.Ordinal);
        Assert.Contains("https://apps.microsoft.com/detail/", storeListingCode, StringComparison.Ordinal);
        Assert.Contains("settings-override", storeRuntimeCode, StringComparison.Ordinal);
        Assert.Contains("SimulateStoreInstallForUiTesting", storeRuntimeCode, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IEntitlementService? entitlementService = null,
        IAccountLinkService? accountLinkService = null,
        IUpdateService? updateService = null,
        IStoreAddOnCatalogService? storeAddOnCatalogService = null,
        IStoreOwnershipVerifier? storeOwnershipVerifier = null,
        IOutputPathActions? outputPathActions = null,
        StoreListingOptions? storeListingOptions = null,
        IStoreRuntimeContextProvider? storeRuntimeContextProvider = null)
    {
        var resolvedUpdateService = updateService ?? new FakeUpdateService();
        return new MainWindowViewModel(
            outputPathActions: outputPathActions ?? new FakeOutputPathActions(),
            themeService: new FakeThemeService(),
            setupSettingsService: new FakeSetupSettingsService(),
            entitlementService: entitlementService ?? new FakeEntitlementService(),
            accountLinkService: accountLinkService ?? new FakeAccountLinkService(),
            updateService: resolvedUpdateService,
            settingsMigrationService: new FakeGuiSettingsMigrationService(),
            storeAddOnCatalogService: storeAddOnCatalogService ?? new FakeStoreAddOnCatalogService(),
            supportDiagnosticsExportService: new FakeSupportDiagnosticsExportService(),
            storeOwnershipVerifier: storeOwnershipVerifier ?? FakeStoreOwnershipVerifier.NotOwned(),
            storeListingOptions: storeListingOptions ?? new StoreListingOptions(),
            storeRuntimeContextProvider: storeRuntimeContextProvider ?? new FakeStoreRuntimeContextProvider(new StoreRuntimeContext
            {
                IsStoreInstall = resolvedUpdateService.IsStoreInstall,
                DetectionSource = resolvedUpdateService.IsStoreInstall ? "windowsapps-path" : "none"
            }));
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private sealed class FakeThemeService : IThemeService
    {
        public AppThemePreference CurrentPreference { get; set; } = AppThemePreference.System;

        public void ApplyTheme(AppThemePreference preference)
        {
            CurrentPreference = preference;
        }
    }

    private sealed class FakeOutputPathActions : IOutputPathActions
    {
        public string? LastOpenedUrl { get; private set; }

        public Task<bool> OpenPathAsync(string path)
        {
            return Task.FromResult(true);
        }

        public Task<bool> OpenUrlAsync(string url)
        {
            LastOpenedUrl = url;
            return Task.FromResult(true);
        }

        public Task<bool> CopyPathAsync(string path)
        {
            return Task.FromResult(true);
        }

        public Task<bool> PreviewMapAsync(string path, string? boundaryCsvPath, string? previewContextLabel = null, System.Collections.Generic.IReadOnlyCollection<string>? selectedTerritoryIds = null)
        {
            return Task.FromResult(true);
        }

        public Task PreloadMapTilesAsync(System.Collections.Generic.IReadOnlyCollection<string> outputPaths, string? boundaryCsvPath)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStoreRuntimeContextProvider : IStoreRuntimeContextProvider
    {
        private readonly StoreRuntimeContext context;

        public FakeStoreRuntimeContextProvider(StoreRuntimeContext context)
        {
            this.context = context;
        }

        public StoreRuntimeContext GetCurrent() => context;
    }

    private sealed class FakeStoreOwnershipVerifier : IStoreOwnershipVerifier
    {
        private readonly StoreOwnershipVerificationResult result;

        private FakeStoreOwnershipVerifier(StoreOwnershipVerificationResult result)
        {
            this.result = result;
        }

        public static FakeStoreOwnershipVerifier VerifiedOwned()
        {
            return new FakeStoreOwnershipVerifier(StoreOwnershipVerificationResult.CreateVerified(
                new StoreOwnershipEvidence
                {
                    ProductKind = StoreOwnedProductKind.DurableAddOn,
                    ProductStoreId = "9TEST0000001",
                    InAppOfferToken = "unlimited_addresses",
                    SkuStoreId = "SKU-0010",
                    IsOwned = true,
                    VerificationSource = "windows-store-license"
                },
                "owned"));
        }

        public static FakeStoreOwnershipVerifier NotOwned()
        {
            return new FakeStoreOwnershipVerifier(StoreOwnershipVerificationResult.CreateFallback("not owned"));
        }

        public Task<StoreOwnershipVerificationResult> VerifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSetupSettingsService : ISetupSettingsService
    {
        public GuiSetupSettings? Load() => null;

        public void Save(GuiSetupSettings settings)
        {
        }
    }

    private sealed class FakeGuiSettingsMigrationService : IGuiSettingsMigrationService
    {
        public Task<GuiSettingsMigrationResult> ExportAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GuiSettingsMigrationResult { IsSuccess = true, Message = "ok" });
        }

        public Task<GuiSettingsMigrationResult> ImportAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GuiSettingsMigrationResult { IsSuccess = true, Message = "ok" });
        }
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        public EntitlementSnapshot Snapshot { get; set; } = EntitlementSnapshot.CreateDefaultFree("Test");

        public EntitlementSnapshot GetSnapshot() => Snapshot;

        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken, bool forceOnlineRevalidation = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task<EntitlementActivationResult> ActivateAsync(string activationKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EntitlementActivationResult
            {
                IsSuccess = false,
                Message = "not used",
                Snapshot = Snapshot
            });
        }
    }

    private sealed class FakeAccountLinkService : IAccountLinkService
    {
        public AccountLinkSnapshot Snapshot { get; set; } = AccountLinkSnapshot.CreateSignedOut();

        public AccountLinkOperationResult RestoreStorePurchaseResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Store purchase linked.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public int RestoreStorePurchaseCalls { get; private set; }

        public AccountLinkSnapshot GetSnapshot() => Snapshot;

        public Task<AccountLinkOperationResult> SaveSnapshotAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "saved", Snapshot = Snapshot });
        }

        public Task<AccountLinkOperationResult> StartSignInAsync(string email, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "ok", Snapshot = Snapshot });
        }

        public Task<AccountLinkOperationResult> RefreshStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "ok", Snapshot = Snapshot });
        }

        public Task<AccountLinkOperationResult> RestoreStorePurchaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreStorePurchaseCalls++;
            Snapshot = RestoreStorePurchaseResult.Snapshot;
            return Task.FromResult(RestoreStorePurchaseResult);
        }

        public Task<AccountLinkOperationResult> ClearCachedLinkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = AccountLinkSnapshot.CreateSignedOut();
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "cleared", Snapshot = Snapshot });
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public string CurrentVersion { get; set; } = "1.0.0";

        public bool AutoUpdateEnabled { get; set; }

        public bool IsStoreInstall { get; set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "noop" });
        }

        public Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "noop" });
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStoreAddOnCatalogService : IStoreAddOnCatalogService
    {
        public StoreAddOnCatalogResult CatalogResult { get; set; } = StoreAddOnCatalogResult.CreateAvailable([], "No add-ons.");

        public StoreAddOnPurchaseResult PurchaseResult { get; set; } = StoreAddOnPurchaseResult.CreateSuccess("Purchased.");

        public int GetCatalogCalls { get; private set; }

        public int PurchaseCalls { get; private set; }

        public Task<StoreAddOnCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCatalogCalls++;
            return Task.FromResult(CatalogResult);
        }

        public Task<StoreAddOnPurchaseResult> PurchaseAsync(string storeId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PurchaseCalls++;
            return Task.FromResult(PurchaseResult);
        }
    }

    private sealed class FakeSupportDiagnosticsExportService : ISupportDiagnosticsExportService
    {
        public Task<SupportDiagnosticsExportResult> ExportAsync(string path, SupportDiagnosticsSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SupportDiagnosticsExportResult
            {
                IsSuccess = true,
                Message = "ok"
            });
        }
    }
}
