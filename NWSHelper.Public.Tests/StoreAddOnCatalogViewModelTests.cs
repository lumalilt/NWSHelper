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
    public void PublicGuiMarkup_ContainsStoreAddOnCatalogBindings()
    {
        var settingsMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml"));
        var appBootstrap = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "App.axaml.cs"));
        var viewModelCode = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("Microsoft Store Add-Ons", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("RefreshStoreAddOnCatalogCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("PurchaseStoreAddOnCommand", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreAddOnOffers", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreAddOnCatalogServiceFactory.CreateDefault", appBootstrap, StringComparison.Ordinal);
        Assert.Contains("PurchaseStoreAddOnAsync", viewModelCode, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IEntitlementService? entitlementService = null,
        IAccountLinkService? accountLinkService = null,
        IUpdateService? updateService = null,
        IStoreAddOnCatalogService? storeAddOnCatalogService = null)
    {
        return new MainWindowViewModel(
            themeService: new FakeThemeService(),
            setupSettingsService: new FakeSetupSettingsService(),
            entitlementService: entitlementService ?? new FakeEntitlementService(),
            accountLinkService: accountLinkService ?? new FakeAccountLinkService(),
            updateService: updateService ?? new FakeUpdateService(),
            settingsMigrationService: new FakeGuiSettingsMigrationService(),
            storeAddOnCatalogService: storeAddOnCatalogService ?? new FakeStoreAddOnCatalogService());
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
}