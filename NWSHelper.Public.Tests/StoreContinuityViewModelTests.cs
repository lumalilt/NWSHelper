using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using Xunit;

namespace NWSHelper.Tests;

public class StoreContinuityViewModelTests
{
    [Fact]
    public void Constructor_DefaultsSelectAllToEnabled()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.SelectAll);
    }

    [Fact]
    public void ResetSetupDefaults_RestoresSelectAllToEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectAll = false;

        viewModel.ResetSetupDefaultsCommand.Execute(null);

        Assert.True(viewModel.SelectAll);
    }

    [Fact]
    public async Task StartupPolicy_WhenStoreInstallIsUnlinked_PromptsFromSettingsAndRequestsAttention()
    {
        var viewModel = CreateViewModel(updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(WorkflowStage.Settings, viewModel.CurrentStage);
        Assert.True(viewModel.HasStoreContinuityPrompt);
        Assert.Equal(1, viewModel.StoreContinuityAttentionRequestId);
        Assert.Equal("Protect Store purchase continuity", viewModel.StoreContinuityPromptTitle);
        Assert.Contains("direct install", viewModel.StoreContinuityPromptMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct-download access", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupPolicy_WhenStoreSessionExists_AutoRestoresPurchase()
    {
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
            updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(1, accountLinkService.RestoreStorePurchaseCalls);
        Assert.Equal(0, viewModel.StoreContinuityAttentionRequestId);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.False(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Store purchase linked.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task BuildPreview_WhenStoreOwnershipVerifiedLocally_UsesUnlimitedEntitlementWithoutClaim()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-store-local-unlock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var boundaryPath = Path.Combine(tempDirectory, "Territories.csv");
            File.WriteAllText(boundaryPath, "placeholder");

            var extractionOrchestrator = new CapturingExtractionOrchestrator();
            var viewModel = CreateViewModel(
                extractionOrchestrator: extractionOrchestrator,
                accountLinkService: new FakeAccountLinkService { Snapshot = AccountLinkSnapshot.CreateSignedOut() },
                updateService: new FakeUpdateService { IsStoreInstall = true },
                storeOwnershipVerifier: FakeStoreOwnershipVerifier.VerifiedOwned());

            viewModel.BoundaryCsvPath = boundaryPath;
            viewModel.DatasetRootPath = tempDirectory;

            await viewModel.BuildPreviewCommand.ExecuteAsync(null);

            Assert.NotNull(extractionOrchestrator.LastRequest);
            Assert.NotNull(extractionOrchestrator.LastRequest!.EntitlementContext);
            Assert.Null(extractionOrchestrator.LastRequest.EntitlementContext!.MaxNewAddressesPerTerritory);
            Assert.Contains(EntitlementProductCodes.UnlimitedAddressesAddOn, extractionOrchestrator.LastRequest.EntitlementContext.AddOnCodes);
            Assert.True(viewModel.HasUnlimitedAddressesAddOn);
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
    public async Task BuildPreview_WhenIgnoreUnlimitedAddressesEntitlementEnabled_UsesFreeTierEvenIfStoreOwnershipIsVerified()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-store-ignore-unlimited", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var boundaryPath = Path.Combine(tempDirectory, "Territories.csv");
            File.WriteAllText(boundaryPath, "placeholder");

            var extractionOrchestrator = new CapturingExtractionOrchestrator();
            var viewModel = CreateViewModel(
                extractionOrchestrator: extractionOrchestrator,
                accountLinkService: new FakeAccountLinkService { Snapshot = AccountLinkSnapshot.CreateSignedOut() },
                updateService: new FakeUpdateService { IsStoreInstall = true },
                storeOwnershipVerifier: FakeStoreOwnershipVerifier.VerifiedOwned());

            viewModel.IgnoreUnlimitedAddressesEntitlement = true;
            viewModel.BoundaryCsvPath = boundaryPath;
            viewModel.DatasetRootPath = tempDirectory;

            await viewModel.BuildPreviewCommand.ExecuteAsync(null);

            Assert.NotNull(extractionOrchestrator.LastRequest);
            Assert.NotNull(extractionOrchestrator.LastRequest!.EntitlementContext);
            Assert.Equal(30, extractionOrchestrator.LastRequest.EntitlementContext!.MaxNewAddressesPerTerritory);
            Assert.DoesNotContain(EntitlementProductCodes.UnlimitedAddressesAddOn, extractionOrchestrator.LastRequest.EntitlementContext.AddOnCodes);
            Assert.False(viewModel.HasUnlimitedAddressesAddOn);
            Assert.Equal("Free", viewModel.EntitlementAddOnLabel);
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
    public async Task BuildPreview_WhenDatasetRootAlreadyPointsAtProviderFolder_NormalizesToBaseRoot()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-provider-root-normalize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var providerRoot = Path.Combine(tempDirectory, "openaddresses");
            Directory.CreateDirectory(providerRoot);

            var boundaryPath = Path.Combine(tempDirectory, "Territories.csv");
            File.WriteAllText(boundaryPath, "placeholder");

            var extractionOrchestrator = new CapturingExtractionOrchestrator();
            var viewModel = CreateViewModel(extractionOrchestrator: extractionOrchestrator);
            viewModel.BoundaryCsvPath = boundaryPath;
            viewModel.DatasetRootPath = providerRoot;

            await viewModel.BuildPreviewCommand.ExecuteAsync(null);

            Assert.NotNull(extractionOrchestrator.LastRequest);
            Assert.Equal(Path.GetFullPath(tempDirectory), extractionOrchestrator.LastRequest!.DatasetRootPath);
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
    public async Task DownloadSelectedDatasetsAsync_KeepsDatasetRootAtBaseFolder()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-download-base-root", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var datasetDownloadService = new FakeDatasetDownloadService
            {
                DownloadResult = new DatasetDownloadResult(Path.Combine(Path.GetFullPath(tempDirectory), "openaddresses"), 1, 1)
            };

            var viewModel = CreateViewModel(datasetDownloadService: datasetDownloadService);
            viewModel.DatasetRootPath = tempDirectory;

            await viewModel.DownloadSelectedDatasetsCommand.ExecuteAsync(new[] { "us/md/harford" });

            Assert.Equal(Path.GetFullPath(tempDirectory), viewModel.DatasetRootPath);
            Assert.Equal(tempDirectory, datasetDownloadService.LastDownloadRequest?.DatasetRootPath);
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
    public async Task StartupPolicy_WhenStoreLinkExistsButEntitlementIsFree_RefreshesLinkedEntitlement()
    {
        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = new AccountLinkSnapshot
            {
                Status = AccountLinkStateStatus.Linked,
                AccountId = "acct_store",
                Email = "store@example.com",
                PurchaseSource = "store",
                LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastSyncUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            },
            RefreshStatusResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Account link status refreshed.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
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
            updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(1, accountLinkService.RefreshStatusCalls);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.False(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Account link status refreshed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task StartupPolicy_WhenDirectInstallHasLinkedAccountButFreeSnapshot_RefreshesLinkedEntitlement()
    {
        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = new AccountLinkSnapshot
            {
                Status = AccountLinkStateStatus.Linked,
                AccountId = "acct_direct",
                Email = "direct@example.com",
                PurchaseSource = "direct",
                LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastSyncUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            },
            RefreshStatusResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Account link status refreshed.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_direct",
                    Email = "direct@example.com",
                    PurchaseSource = "direct",
                    LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
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
            updateService: new FakeUpdateService { IsStoreInstall = false });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(1, accountLinkService.RefreshStatusCalls);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.Equal("Unlimited Addresses", viewModel.EntitlementAddOnLabel);
        Assert.Equal("Unlimited Addresses linked", viewModel.AccountLinkStatusLabel);
        Assert.Equal("Account link status refreshed.", viewModel.StatusMessage);
    }

    [Fact]
    public void LinkedStoreSnapshot_HidesPromptWithoutEntitlementHydration()
    {
        var viewModel = CreateViewModel(
            accountLinkService: new FakeAccountLinkService
            {
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LastSyncUtc = DateTimeOffset.UtcNow
                }
            },
            updateService: new FakeUpdateService { IsStoreInstall = true });

        Assert.False(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Linked", viewModel.AccountLinkStatusLabel);
    }

    [Fact]
    public async Task StartupPrompt_AfterSuccessfulRestore_ReturnsToSetup()
    {
        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = AccountLinkSnapshot.CreateSignedOut(),
            StartSignInResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Sign-in link requested.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.SignedIn,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    LastSyncUtc = DateTimeOffset.UtcNow
                }
            },
            RestoreStorePurchaseResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Store purchase verified and linked.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LastSyncUtc = DateTimeOffset.UtcNow
                }
            }
        };

        var viewModel = CreateViewModel(
            accountLinkService: accountLinkService,
            updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(WorkflowStage.Settings, viewModel.CurrentStage);

        viewModel.AccountLinkEmail = "store@example.com";
        await viewModel.StartAccountSignInCommand.ExecuteAsync(null);

        Assert.Equal(1, accountLinkService.RestoreStorePurchaseCalls);
        Assert.Equal(WorkflowStage.Setup, viewModel.CurrentStage);
        Assert.False(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Store purchase verified and linked.", viewModel.StatusMessage);
    }

    [Fact]
    public void PendingReviewSession_HidesSignInRequiredStoreHintAndKeepsRestoreAvailable()
    {
        var viewModel = CreateViewModel(
            accountLinkService: new FakeAccountLinkService
            {
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.PendingReview,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LastSyncUtc = DateTimeOffset.UtcNow
                }
            },
            updateService: new FakeUpdateService { IsStoreInstall = true });

        Assert.True(viewModel.CanRestoreStorePurchase);
        Assert.False(viewModel.ShowStoreRestoreRequiresSignInHint);
        Assert.True(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Store claim pending review", viewModel.StoreContinuityPromptTitle);
    }

    [Fact]
    public async Task ExportSupportDiagnosticsAsync_UsesConfiguredExporter()
    {
        var diagnosticsService = new FakeSupportDiagnosticsExportService();
        var viewModel = CreateViewModel(
            accountLinkService: new FakeAccountLinkService
            {
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.PendingReview,
                    Email = "store@example.com",
                    PurchaseSource = "store"
                }
            },
            updateService: new FakeUpdateService { IsStoreInstall = true },
            supportDiagnosticsExportService: diagnosticsService);

        await viewModel.ExportSupportDiagnosticsAsync("C:\\temp\\support.json");

        Assert.Equal("C:\\temp\\support.json", diagnosticsService.LastPath);
        Assert.Equal("Support diagnostics exported.", viewModel.StatusMessage);
        Assert.True(diagnosticsService.LastSnapshot?.IsStoreInstall);
    }

    [Fact]
    public async Task OpenSupportLinkAsync_UsesConfiguredOutputActions()
    {
        var outputPathActions = new FakeOutputPathActions();
        var viewModel = CreateViewModel(outputPathActions: outputPathActions);

        await viewModel.OpenSupportLinkCommand.ExecuteAsync(null);

        Assert.Equal("https://lumalilt.com/nwshelper/support", outputPathActions.LastOpenedUrl);
        Assert.Equal("Opened support page.", viewModel.StatusMessage);
        Assert.Null(viewModel.LastError);
    }

    [Fact]
    public void PublicGuiMarkup_ContainsStoreContinuityAttentionBindingsAndStartupHook()
    {
        var settingsMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml"));
        var settingsCodeBehind = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml.cs"));
        var viewModelCode = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "ViewModels", "MainWindowViewModel.cs"));
        var appMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "App.axaml"));
        var mainWindowMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "MainWindow.axaml"));
        var mainWindowCodeBehind = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("HasStoreContinuityPrompt", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptTitle", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptMessage", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("ShowStoreRestoreRequiresSignInHint", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("Export Support Diagnostics", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptBanner", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("AccountLinkSection", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("AccountLinkEmailTextBox", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("Developer Settings", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("IgnoreUnlimitedAddressesEntitlementCheckBox", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("IgnoreUnlimitedAddressesEntitlement", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("store-continuity-prompt", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptBackgroundBrush", appMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityAttentionRequestId", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("DeveloperSettingsChordPressesRequired = 6", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("RevealDeveloperSettingsAsync", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Key.G", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Loaded += OnLoaded", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Unloaded += OnUnloaded", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("BringIntoView", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("PulseStoreContinuityPromptAsync", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("IgnoreUnlimitedAddressesEntitlement", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("OpenSupportLinkCommand", mainWindowMarkup, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"Support\"", mainWindowMarkup, StringComparison.Ordinal);
        Assert.True(mainWindowMarkup.IndexOf("OpenSupportLinkCommand", StringComparison.Ordinal) < mainWindowMarkup.LastIndexOf("GoToSettingsCommand", StringComparison.Ordinal));
        Assert.Contains("RunStartupStoreContinuityPolicyAsync", mainWindowCodeBehind, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IExtractionOrchestrator? extractionOrchestrator = null,
        IOutputPathActions? outputPathActions = null,
        IDatasetDownloadService? datasetDownloadService = null,
        IEntitlementService? entitlementService = null,
        IAccountLinkService? accountLinkService = null,
        IUpdateService? updateService = null,
        ISupportDiagnosticsExportService? supportDiagnosticsExportService = null,
        IStoreOwnershipVerifier? storeOwnershipVerifier = null)
    {
        return new MainWindowViewModel(
            extractionOrchestrator: extractionOrchestrator ?? new CapturingExtractionOrchestrator(),
            outputPathActions: outputPathActions ?? new FakeOutputPathActions(),
            datasetDownloadService: datasetDownloadService ?? new FakeDatasetDownloadService(),
            themeService: new FakeThemeService(),
            setupSettingsService: new FakeSetupSettingsService(),
            entitlementService: entitlementService ?? new FakeEntitlementService(),
            accountLinkService: accountLinkService ?? new FakeAccountLinkService(),
            updateService: updateService ?? new FakeUpdateService(),
            settingsMigrationService: new FakeGuiSettingsMigrationService(),
            supportDiagnosticsExportService: supportDiagnosticsExportService ?? new FakeSupportDiagnosticsExportService(),
            storeOwnershipVerifier: storeOwnershipVerifier ?? FakeStoreOwnershipVerifier.NotOwned());
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

    private sealed class FakeDatasetDownloadService : IDatasetDownloadService
    {
        public DatasetDownloadRequest? LastDownloadRequest { get; private set; }

        public DatasetDownloadResult DownloadResult { get; set; } = new(Path.GetFullPath("./datasets/openaddresses"), 0, 0);

        public IReadOnlyList<DatasetProviderOption> GetProviders()
        {
            return [new DatasetProviderOption("openaddresses", "OpenAddresses", "openaddresses")];
        }

        public string ResolveBaseDatasetRoot(string datasetRootPath, string providerId)
        {
            var fullRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(datasetRootPath) ? "./datasets" : datasetRootPath);
            var rootName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(rootName, "openaddresses", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(fullRoot)?.FullName ?? fullRoot
                : fullRoot;
        }

        public string ResolveProviderDatasetRoot(string datasetRootPath, string providerId)
        {
            return Path.Combine(ResolveBaseDatasetRoot(datasetRootPath, providerId), "openaddresses");
        }

        public Task<IReadOnlyList<DatasetCatalogItem>> GetDatasetsAsync(string providerId, string? openAddressesApiBaseUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DatasetCatalogItem>>([]);
        }

        public Task<DatasetDownloadResult> DownloadDatasetsAsync(DatasetDownloadRequest request, IProgress<DatasetDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            LastDownloadRequest = request;
            return Task.FromResult(DownloadResult);
        }

        public Task<DatasetProviderConnectionTestResult> TestProviderConnectionAsync(string providerId, string? openAddressesApiBaseUrl, string? openAddressesApiToken, CancellationToken cancellationToken)
        {
            return Task.FromResult(new DatasetProviderConnectionTestResult(true, "ok"));
        }
    }

    private sealed class CapturingExtractionOrchestrator : IExtractionOrchestrator
    {
        public ExtractionRequest? LastRequest { get; private set; }

        public Task<ExtractionPreviewData> BuildPreviewAsync(ExtractionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new ExtractionPreviewData
            {
                Plan = new TerritoryExtractionPlan(),
                Result = new ExtractionResult()
            });
        }

        public Task<ExtractionExecutionData> ExecuteAsync(ExtractionRequest request, IReadOnlyCollection<string> selectedTerritoryIds, Action<ExtractionProgressSnapshot>? onProgress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new ExtractionExecutionData
            {
                Result = new ExtractionResult()
            });
        }
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

        public AccountLinkOperationResult StartSignInResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Sign-in link requested.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public AccountLinkOperationResult RefreshStatusResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Account link status refreshed.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public AccountLinkOperationResult RestoreStorePurchaseResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Store purchase restore completed.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public int RestoreStorePurchaseCalls { get; private set; }

        public int RefreshStatusCalls { get; private set; }

        public AccountLinkSnapshot GetSnapshot() => Snapshot;

        public Task<AccountLinkOperationResult> SaveSnapshotAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "saved", Snapshot = snapshot });
        }

        public Task<AccountLinkOperationResult> ClearCachedLinkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = AccountLinkSnapshot.CreateSignedOut();
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "cleared", Snapshot = Snapshot });
        }

        public Task<AccountLinkOperationResult> StartSignInAsync(string email, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = StartSignInResult.Snapshot;
            return Task.FromResult(StartSignInResult);
        }

        public Task<AccountLinkOperationResult> RefreshStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshStatusCalls++;
            Snapshot = RefreshStatusResult.Snapshot;
            return Task.FromResult(RefreshStatusResult);
        }

        public Task<AccountLinkOperationResult> RestoreStorePurchaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreStorePurchaseCalls++;
            Snapshot = RestoreStorePurchaseResult.Snapshot;
            return Task.FromResult(RestoreStorePurchaseResult);
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public string CurrentVersion { get; set; } = "1.0.0-test";

        public bool IsStoreInstall { get; set; }

        public bool AutoUpdateEnabled { get; set; } = true;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "No updates." });
        }

        public Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "No updates." });
        }
    }

    private sealed class FakeSupportDiagnosticsExportService : ISupportDiagnosticsExportService
    {
        public string? LastPath { get; private set; }

        public SupportDiagnosticsSnapshot? LastSnapshot { get; private set; }

        public Task<SupportDiagnosticsExportResult> ExportAsync(string path, SupportDiagnosticsSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPath = path;
            LastSnapshot = snapshot;
            return Task.FromResult(new SupportDiagnosticsExportResult
            {
                IsSuccess = true,
                Message = "Support diagnostics exported."
            });
        }
    }
}